using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DiscordAIBot
{
    // MCPの session_id ごとの短期履歴を保持する軽量ストア。Discordのスレッド履歴(SQLite永続化)
    // とは別物で、プロセスメモリ上のみで完結する使い捨ての履歴(TTL経過またはプロセス再起動で消える)
    public class AskSessionStore
    {
        private const int SessionTtlMinutes = 20;
        private const int MaxTurnsPerSession = 40; // 20往復。無制限成長を防ぐ安全弁

        private class Session
        {
            public readonly List<ChatTurn> History = new();
            public DateTime LastActivityUtc = DateTime.UtcNow;
        }

        private readonly ConcurrentDictionary<string, Session> _sessions = new();

        public IReadOnlyList<ChatTurn> GetHistory(string sessionId)
        {
            SweepExpired();

            if (!_sessions.TryGetValue(sessionId, out var session)) return Array.Empty<ChatTurn>();

            lock (session)
            {
                return session.History.ToArray();
            }
        }

        public void AppendTurn(string sessionId, string userText, string assistantText)
        {
            var session = _sessions.GetOrAdd(sessionId, _ => new Session());

            lock (session)
            {
                session.History.Add(new ChatTurn("user", userText));
                session.History.Add(new ChatTurn("assistant", assistantText));

                while (session.History.Count > MaxTurnsPerSession)
                {
                    session.History.RemoveAt(0);
                }

                session.LastActivityUtc = DateTime.UtcNow;
            }
        }

        private void SweepExpired()
        {
            var cutoff = DateTime.UtcNow.AddMinutes(-SessionTtlMinutes);
            foreach (var kvp in _sessions)
            {
                if (kvp.Value.LastActivityUtc < cutoff)
                {
                    _sessions.TryRemove(kvp.Key, out _);
                }
            }
        }
    }

    // MCPサーバー経由(Tailnet内の他端末のClaude Code等)で呼び出す、単発またはsession_id指定時
    // のみ短期履歴を持つ一問一答ツール。Discord側のChatOrchestrator/StreamResponseHandler
    // (スレッド履歴・フッター整形・Drive記録)は経由せず、IAiProviderを直接呼び出す薄いラッパー。
    // Discord bot本体と同じProviderインスタンス・コスト記録/無料枠事前ブロックを共有する
    [McpServerToolType]
    public class AskTool
    {
        private readonly Func<ApiProvider, IAiProvider> _providerFactory;
        private readonly Func<string, Task<bool>> _hasOpenAiBudgetAsync;
        private readonly AskSessionStore _sessionStore;
        private readonly ILogger<AskTool> _logger;
        private readonly IHttpContextAccessor _httpContextAccessor;

        public AskTool(
            Func<ApiProvider, IAiProvider> providerFactory,
            Func<string, Task<bool>> hasOpenAiBudgetAsync,
            AskSessionStore sessionStore,
            ILogger<AskTool> logger,
            IHttpContextAccessor httpContextAccessor)
        {
            _providerFactory = providerFactory;
            _hasOpenAiBudgetAsync = hasOpenAiBudgetAsync;
            _sessionStore = sessionStore;
            _logger = logger;
            _httpContextAccessor = httpContextAccessor;
        }

        [McpServerTool, Description("質問・軽いコード生成をAIモデルに投げ、応答テキストを返します。session_id省略時は毎回独立したリクエスト(履歴なし)。session_idを指定すると、同じIDでの呼び出し間で直前までの会話を踏まえて応答します(有効期限20分、サーバー再起動でも消える使い捨ての短期履歴)。応答が出力上限などで途中で終わった場合は本文の末尾に「[⚠ ...]」の注記(finish_reason・トークン内訳)が付き、本文が空の場合はエラーになります。出力上限はmodels://registryのmaxOutputTokensで確認でき、OpenAIモデル(gpt-5.6-*)では推論トークンもこの上限に含まれます。")]
        public async Task<string> Ask(
            [Description("モデルへの質問・依頼内容")] string prompt,
            [Description("使用するモデルID(省略時はローカルの既定モデル)。/modelで選択可能なIDと同じ値(例: gemini-3.8-flash, xai/grok-4.6, gpt-5.6-sol)")] string? model,
            [Description("短期セッションID(任意の文字列)。同じIDを指定して続けて呼ぶと会話が継続する。省略時は毎回独立したリクエスト")] string? session_id,
            [Description("思考の深さ。省略時はMedium。選択肢: None, Low, Medium, High, XHigh(モデルが非対応の場合は自動調整される)")] string? effort,
            [Description("生成のランダム性(0.0〜2.0程度)。省略時は0.7。OpenAIモデルではAPI仕様上デフォルト値のみ受理されるため無視される")] double? temperature,
            IProgress<ProgressNotificationValue> progress,
            CancellationToken cancellationToken)
        {
            var callStopwatch = Stopwatch.StartNew();
            string callerIp = _httpContextAccessor.HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown";
            string modelId = string.IsNullOrWhiteSpace(model) ? ModelRegistry.DefaultModelId : model;

            // 呼び出し元(Tailscale IP)・session_id・モデルを記録。Tailscale限定バインドで
            // ネットワークレベルの信頼はあるが、「どの端末から」呼ばれたかはこれまで未記録
            // だったため、コスト記録(UsageRecords)を補完する形で構造化ログとして残す
            _logger.LogInformation(
                "MCP ask 開始: caller={CallerIp} session={SessionId} model={ModelId} effort={Effort} promptChars={PromptChars}",
                callerIp, session_id ?? "(none)", modelId, effort ?? "(default)", prompt.Length);

            if (!ModelRegistry.AvailableModels.TryGetValue(modelId, out var modelMeta))
            {
                string validIds = string.Join(", ", ModelRegistry.AvailableModels.Keys);
                throw new McpException($"未知のモデルIDです: '{modelId}'。有効な値: {validIds}");
            }

            // OpenAIは日次無料枠の事前ブロックをDiscord経由と全く同じロジックで通す
            // (二重管理を避け、MCP経由の消費もUsageRecordsで一元的に把握する)
            if (modelMeta.Provider == ApiProvider.OpenAi && !await _hasOpenAiBudgetAsync(modelMeta.ModelId))
            {
                throw new McpException($"モデル '{modelId}' は本日の無料枠上限に達しているため、現在使用できません。");
            }

            EffortLevel effortLevel = EffortLevel.Medium;
            if (!string.IsNullOrWhiteSpace(effort) && !Enum.TryParse(effort, ignoreCase: true, out effortLevel))
            {
                string validEfforts = string.Join(", ", Enum.GetNames(typeof(EffortLevel)));
                throw new McpException($"未知のeffortです: '{effort}'。有効な値: {validEfforts}");
            }

            double resolvedTemperature = temperature ?? 0.7;

            IReadOnlyList<ChatTurn> history = string.IsNullOrWhiteSpace(session_id)
                ? Array.Empty<ChatTurn>()
                : _sessionStore.GetHistory(session_id);

            // ContextWindow超過の事前チェック(セッション履歴+今回のプロンプトの合計)。
            // Discordチャットと異なり呼び出し元はAIエージェントのため、黙って履歴を
            // 間引くより、明確なエラーで判断を委ねる方が適切と判断
            int estimatedTokens = TokenManager.CountTokens(prompt);
            foreach (var turn in history)
            {
                estimatedTokens += TokenManager.CountTokens(turn.Content);
            }
            int contextBudget = modelMeta.ContextWindow - 2000; // Discord側と同じ安全マージン
            if (contextBudget > 0 && estimatedTokens > contextBudget)
            {
                throw new McpException(
                    $"プロンプト(+session_idの履歴)が推定{estimatedTokens}トークンで、モデル '{modelId}' の" +
                    $"コンテキスト上限(安全マージン込み実質{contextBudget}トークン、総枠{modelMeta.ContextWindow}トークン)を" +
                    "超えています。新しいsession_idで開始するか、プロンプトを短くしてください。");
            }

            var request = new AiRequest(
                SystemPrompt: "",
                ModelId: modelMeta.ModelId,
                History: history,
                CurrentUserText: prompt,
                CurrentImagesBase64: Array.Empty<string>(),
                Temperature: resolvedTemperature,
                MaxOutputTokens: modelMeta.MaxOutputTokens,
                Effort: effortLevel
            );

            var provider = _providerFactory(modelMeta.Provider);

            // クライアントがprogressTokenを送っていない場合、MCP SDKはNullProgressを注入する
            // ため、常時Report()を呼んでも安全(その場合は単に無視される)
            progress.Report(new ProgressNotificationValue { Progress = 0, Message = "モデル呼び出しを開始しました" });

            // ストリーム受信・終了理由の収集・コスト記録はcompareと共通(McpModelCall)。
            // 進捗はチャンクの到着とは無関係に約3秒ごとに通知される
            var result = await McpModelCall.RunAsync(provider, request, modelMeta,
                (elapsed, receivedChars) => progress.Report(new ProgressNotificationValue
                {
                    Progress = (float)elapsed.TotalSeconds,
                    Message = $"応答生成中...(経過{elapsed.TotalSeconds:F0}秒、{receivedChars}文字受信済み)"
                }),
                cancellationToken);

            _logger.LogInformation(
                "MCP ask 完了: caller={CallerIp} session={SessionId} model={ModelId} durationMs={DurationMs} finishReason={FinishReason} answerChars={AnswerChars} promptTokens={PromptTokens} completionTokens={CompletionTokens} reasoningTokens={ReasoningTokens}",
                callerIp, session_id ?? "(none)", modelMeta.ModelId, callStopwatch.ElapsedMilliseconds, result.FinishReason ?? "(none)", result.Answer.Length, result.PromptTokens, result.CompletionTokens, result.ReasoningTokens);

            // 本文が空の応答を成功として返すと、呼び出し元は「成功したが中身が無い」を失敗と区別
            // できない(provisioning/057: 推論トークンが出力上限を使い切り、本文0トークンのまま
            // IsError=Falseで返っていた)。使用量はRunAsync内で記録済みのため、ここでエラーにして
            // session_idの履歴にも残さない
            if (string.IsNullOrWhiteSpace(result.Answer))
            {
                throw new McpException(McpModelCall.BuildEmptyAnswerMessage(result, modelMeta));
            }

            // 途中で切れた応答も、同じsession_idで「続きを」と依頼できるよう本文はそのまま履歴に残す
            // (注記は呼び出し元へのメタ情報のため履歴には含めない)
            if (!string.IsNullOrWhiteSpace(session_id))
            {
                _sessionStore.AppendTurn(session_id, prompt, result.Answer);
            }

            string? notice = McpModelCall.BuildIncompleteNotice(result, modelMeta);
            if (notice is null)
            {
                return result.Answer;
            }

            if (result.Completion == McpModelCall.Completion.Truncated)
            {
                notice += string.IsNullOrWhiteSpace(session_id)
                    ? "続きが必要な場合は、質問を分割して呼び出し直してください。"
                    : "続きが必要な場合は、同じsession_idで続きを依頼するか、質問を分割してください。";
            }

            return $"{result.Answer}\n\n[⚠ {notice}]";
        }
    }
}
