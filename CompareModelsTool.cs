using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
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

        private class ModelComparisonResult
        {
            [JsonPropertyName("modelId")] public string ModelId { get; init; } = "";
            [JsonPropertyName("answer")] public string? Answer { get; init; }
            [JsonPropertyName("error")] public string? Error { get; init; }
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

        [McpServerTool, Description("同じプロンプトを複数のモデルに順番に投げ、それぞれの応答をJSON配列で返して比較する。1モデルの失敗(無効なID・無料枠切れ・コンテキスト超過・呼び出しエラー)は他モデルの結果に影響しない(そのモデルのerrorフィールドに理由が入るのみ)。data sharing前提のOpenAIモデル(gpt-5.6-*)を含めるかどうかはaskツールのmodel引数と同様に呼び出し側の判断に委ねる(ツール側では除外しない)。session_idによる短期履歴には非対応(毎回独立したリクエスト)。モデル数が多い・effortが高いと時間がかかるため、呼び出し中はProgress notificationsで進捗を通知する。")]
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

            for (int i = 0; i < models.Length; i++)
            {
                string modelId = models[i];
                progress.Report(new ProgressNotificationValue
                {
                    Progress = i,
                    Total = models.Length,
                    Message = $"{modelId} を呼び出し中...({i + 1}/{models.Length})"
                });

                results.Add(await CallSingleModelAsync(modelId, prompt, effortLevel, resolvedTemperature, cancellationToken));
            }

            progress.Report(new ProgressNotificationValue { Progress = models.Length, Total = models.Length, Message = "全モデル完了" });

            _logger.LogInformation(
                "MCP compare 完了: caller={CallerIp} models={Models} errorCount={ErrorCount}",
                callerIp, string.Join(",", models), results.FindAll(r => r.Error is not null).Count);

            return JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true });
        }

        private async Task<ModelComparisonResult> CallSingleModelAsync(
            string modelId, string prompt, EffortLevel effortLevel, double temperature, CancellationToken cancellationToken)
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

                var provider = _providerFactory(modelMeta.Provider);
                var responseText = new StringBuilder();
                int? promptTokens = null;
                int? completionTokens = null;
                int? reasoningTokens = null;

                await foreach (var chunk in provider.StreamChatAsync(request, cancellationToken))
                {
                    if (!chunk.IsReasoning && chunk.TextDelta is { Length: > 0 })
                    {
                        responseText.Append(chunk.TextDelta);
                    }

                    if (chunk.PromptTokens.HasValue) promptTokens = chunk.PromptTokens;
                    if (chunk.CompletionTokens.HasValue) completionTokens = chunk.CompletionTokens;
                    if (chunk.ReasoningTokens.HasValue) reasoningTokens = chunk.ReasoningTokens;
                }

                double? estimatedCost = null;
                if (promptTokens.HasValue)
                {
                    estimatedCost = CostEstimator.EstimateCostUsd(
                        modelMeta.Provider, modelMeta.ModelId, promptTokens.Value, completionTokens ?? 0, reasoningTokens ?? 0);

                    if (estimatedCost.HasValue)
                    {
                        using var db = new ChatDbContext();
                        db.UsageRecords.Add(new UsageRecord
                        {
                            CreatedAt = DateTime.UtcNow,
                            ModelId = modelMeta.ModelId,
                            PromptTokens = promptTokens.Value,
                            CompletionTokens = completionTokens ?? 0,
                            ReasoningTokens = reasoningTokens ?? 0,
                            EstimatedCostUsd = estimatedCost.Value,
                            Source = "mcp"
                        });
                        await db.SaveChangesAsync(cancellationToken);
                    }
                }

                return new ModelComparisonResult
                {
                    ModelId = modelId,
                    Answer = responseText.ToString(),
                    DurationMs = stopwatch.ElapsedMilliseconds,
                    EstimatedCostUsd = estimatedCost
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
