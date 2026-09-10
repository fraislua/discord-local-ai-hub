using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DiscordAIBot
{
    // MCPサーバー経由で、同一プロンプトを複数モデルに順番に投げて比較するツール。
    // askツール(AskTool.cs)と同じProviderFactory/コスト記録/無料枠事前ブロックを再利用するが、
    // 以下の点でaskとは独立した設計とする:
    // - session_id(短期履歴)には非対応。各モデルへの呼び出しはaskのsession_id省略時と同じ
    //   単発リクエスト。複数モデル×複数ターンの履歴管理は複雑さに見合わないため見送り
    // - モデルIDのバリデーション・無料枠切れ・コンテキスト超過は、他モデルの結果に影響しない
    //   よう、そのモデルのエラーとして結果配列に含めて処理を継続する(全体を失敗させない)
    // - OpenAI(データ共有前提)モデルを比較対象に含めるかどうかは、askのmodel引数と同じく
    //   完全に呼び出し側の判断に委ねる(ツール側でのガード・除外は行わない。ユーザーとの
    //   合意に基づく設計判断)
    [McpServerToolType]
    public class CompareModelsTool
    {
        private readonly Func<ApiProvider, IAiProvider> _providerFactory;
        private readonly Func<string, Task<bool>> _hasOpenAiBudgetAsync;
        private readonly ILogger<CompareModelsTool> _logger;
        private readonly IHttpContextAccessor _httpContextAccessor;

        // 日本語等の非ASCII文字を\uXXXXにエスケープしない。既定のエンコーダーはエスケープするため、
        // 呼び出し元エージェントが読むテキストが読みにくく、トークン数も数倍に膨らんでいた。
        // 結果はHTMLに埋め込まずMCPのテキストとして返すだけなので、緩いエスケープで問題ない
        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        private class ModelComparisonResult
        {
            [JsonPropertyName("modelId")] public string ModelId { get; init; } = "";
            [JsonPropertyName("answer")] public string? Answer { get; init; }
            [JsonPropertyName("error")] public string? Error { get; init; }
            // 本文はあるが出力上限等で正常終了しなかった場合の注記(正常終了時はnull)
            [JsonPropertyName("warning")] public string? Warning { get; init; }
            [JsonPropertyName("finishReason")] public string? FinishReason { get; init; }
            [JsonPropertyName("durationMs")] public long DurationMs { get; init; }
            [JsonPropertyName("estimatedCostUsd")] public double? EstimatedCostUsd { get; init; }
        }

        public CompareModelsTool(
            Func<ApiProvider, IAiProvider> providerFactory,
            Func<string, Task<bool>> hasOpenAiBudgetAsync,
            ILogger<CompareModelsTool> logger,
            IHttpContextAccessor httpContextAccessor)
        {
            _providerFactory = providerFactory;
            _hasOpenAiBudgetAsync = hasOpenAiBudgetAsync;
            _logger = logger;
            _httpContextAccessor = httpContextAccessor;
        }

        [McpServerTool, Description("同じプロンプトを複数のモデルに順番に投げ、それぞれの応答をJSON配列で返して比較する。1モデルの失敗(無効なID・無料枠切れ・コンテキスト超過・呼び出しエラー)は他モデルの結果に影響しない(そのモデルのerrorフィールドに理由が入るのみ)。各モデルの結果にはfinishReasonが含まれ、出力上限などで途中で終わった場合はwarningに注記が入り、本文が空の場合はerrorになる。data sharing前提のOpenAIモデル(gpt-5.6-*)を含めるかどうかはaskツールのmodel引数と同様に呼び出し側の判断に委ねる(ツール側では除外しない)。session_idによる短期履歴には非対応(毎回独立したリクエスト)。モデル数が多い・effortが高いと時間がかかるため、呼び出し中はProgress notificationsで進捗を通知する。")]
        public async Task<string> Compare(
            [Description("全モデル共通の質問・依頼内容")] string prompt,
            [Description("比較したいモデルIDの配列(1個以上)。/modelで選択可能なIDと同じ値")] string[] models,
            [Description("思考の深さ。省略時はMedium。全モデル共通で適用され、モデルが非対応の場合は自動調整される")] string? effort,
            [Description("生成のランダム性(0.0〜2.0程度)。省略時は0.7。OpenAIモデルでは無視される")] double? temperature,
            IProgress<ProgressNotificationValue> progress,
            CancellationToken cancellationToken)
        {
            if (models is null || models.Length == 0)
            {
                throw new McpException("modelsには1個以上のモデルIDを指定してください。");
            }

            EffortLevel effortLevel = EffortLevel.Medium;
            if (!string.IsNullOrWhiteSpace(effort) && !Enum.TryParse(effort, ignoreCase: true, out effortLevel))
            {
                string validEfforts = string.Join(", ", Enum.GetNames(typeof(EffortLevel)));
                throw new McpException($"未知のeffortです: '{effort}'。有効な値: {validEfforts}");
            }

            double resolvedTemperature = temperature ?? 0.7;
            string callerIp = _httpContextAccessor.HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown";

            _logger.LogInformation(
                "MCP compare 開始: caller={CallerIp} models={Models} effort={Effort} promptChars={PromptChars}",
                callerIp, string.Join(",", models), effort ?? "(default)", prompt.Length);

            var results = new List<ModelComparisonResult>();

            // progressの値はcompare全体の経過秒数とする。MCP仕様上progressは通知ごとに増加する必要があり、
            // モデルをまたいでも単調増加させるため。何モデル目を処理中かはMessageに含める
            var compareStopwatch = Stopwatch.StartNew();

            for (int i = 0; i < models.Length; i++)
            {
                string modelId = models[i];
                string position = $"{i + 1}/{models.Length}";
                progress.Report(new ProgressNotificationValue
                {
                    Progress = (float)compareStopwatch.Elapsed.TotalSeconds,
                    Message = $"{modelId} を呼び出し中...({position})"
                });

                // 1モデルの生成中も約3秒ごとに通知する(provisioning/057: 以前はモデル切り替え時にしか
                // 通知せず、遅いモデルの生成中にクライアントに中断された)
                Action<TimeSpan, int> reportModelProgress = (elapsed, receivedChars) => progress.Report(new ProgressNotificationValue
                {
                    Progress = (float)compareStopwatch.Elapsed.TotalSeconds,
                    Message = $"{modelId} 応答生成中...({position}、経過{elapsed.TotalSeconds:F0}秒、{receivedChars}文字受信済み)"
                });

                results.Add(await CallSingleModelAsync(modelId, prompt, effortLevel, resolvedTemperature, reportModelProgress, cancellationToken));
            }

            progress.Report(new ProgressNotificationValue { Progress = (float)compareStopwatch.Elapsed.TotalSeconds, Message = "全モデル完了" });

            _logger.LogInformation(
                "MCP compare 完了: caller={CallerIp} models={Models} errorCount={ErrorCount}",
                callerIp, string.Join(",", models), results.FindAll(r => r.Error is not null).Count);

            return JsonSerializer.Serialize(results, _jsonOptions);
        }

        private async Task<ModelComparisonResult> CallSingleModelAsync(
            string modelId, string prompt, EffortLevel effortLevel, double temperature,
            Action<TimeSpan, int> reportProgress, CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();

            if (!ModelRegistry.AvailableModels.TryGetValue(modelId, out var modelMeta))
            {
                string validIds = string.Join(", ", ModelRegistry.AvailableModels.Keys);
                return new ModelComparisonResult { ModelId = modelId, Error = $"未知のモデルIDです。有効な値: {validIds}" };
            }

            if (modelMeta.Provider == ApiProvider.OpenAi && !await _hasOpenAiBudgetAsync(modelMeta.ModelId))
            {
                return new ModelComparisonResult { ModelId = modelId, Error = "本日の無料枠上限に達しているため、現在使用できません。" };
            }

            int estimatedTokens = TokenManager.CountTokens(prompt);
            int contextBudget = modelMeta.ContextWindow - 2000;
            if (contextBudget > 0 && estimatedTokens > contextBudget)
            {
                return new ModelComparisonResult
                {
                    ModelId = modelId,
                    Error = $"プロンプトが推定{estimatedTokens}トークンで、コンテキスト上限(安全マージン込み実質{contextBudget}トークン)を超えています。"
                };
            }

            try
            {
                var request = new AiRequest(
                    SystemPrompt: "",
                    ModelId: modelMeta.ModelId,
                    History: Array.Empty<ChatTurn>(),
                    CurrentUserText: prompt,
                    CurrentImagesBase64: Array.Empty<string>(),
                    Temperature: temperature,
                    MaxOutputTokens: modelMeta.MaxOutputTokens,
                    Effort: effortLevel
                );

                // ストリーム受信・終了理由の収集・進捗通知・コスト記録はaskと共通(McpModelCall)
                var result = await McpModelCall.RunAsync(
                    _providerFactory(modelMeta.Provider), request, modelMeta, reportProgress, cancellationToken);

                // モデル単位の所要時間・終了理由を残す(provisioning/057の調査時、compareの中断が
                // どのモデルの処理中に起きたかをログから直接特定できなかったため)
                _logger.LogInformation(
                    "MCP compare モデル完了: model={ModelId} durationMs={DurationMs} finishReason={FinishReason} answerChars={AnswerChars} completionTokens={CompletionTokens} reasoningTokens={ReasoningTokens}",
                    modelId, stopwatch.ElapsedMilliseconds, result.FinishReason ?? "(none)", result.Answer.Length, result.CompletionTokens, result.ReasoningTokens);

                // 空応答はaskと同じくエラー扱い(使用量はRunAsync内で記録済み)
                if (string.IsNullOrWhiteSpace(result.Answer))
                {
                    return new ModelComparisonResult
                    {
                        ModelId = modelId,
                        Error = McpModelCall.BuildEmptyAnswerMessage(result, modelMeta),
                        FinishReason = result.FinishReason,
                        DurationMs = stopwatch.ElapsedMilliseconds,
                        EstimatedCostUsd = result.EstimatedCostUsd
                    };
                }

                return new ModelComparisonResult
                {
                    ModelId = modelId,
                    Answer = result.Answer,
                    Warning = McpModelCall.BuildIncompleteNotice(result, modelMeta),
                    FinishReason = result.FinishReason,
                    DurationMs = stopwatch.ElapsedMilliseconds,
                    EstimatedCostUsd = result.EstimatedCostUsd
                };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 1モデルの呼び出しエラー(プロバイダー側の一時的な障害等)で比較全体を
                // 失敗させないよう、そのモデルのエラーとして結果に含めて処理を継続する
                _logger.LogWarning(ex, "MCP compare: モデル呼び出し失敗 model={ModelId}", modelId);
                return new ModelComparisonResult
                {
                    ModelId = modelId,
                    Error = $"呼び出し中にエラーが発生しました: {ex.Message}",
                    DurationMs = stopwatch.ElapsedMilliseconds
                };
            }
        }
    }
}
