using System;
using System.ComponentModel;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace DiscordAIBot
{
    // MCPサーバー経由(Tailnet内の他端末のClaude Code等)で呼び出す、単発・ステートレスな
    // 一問一答ツール。Discord側のChatOrchestrator/StreamResponseHandler(スレッド履歴・
    // フッター整形・Drive記録)は経由せず、IAiProviderを直接呼び出す薄いラッパー。
    // Discord bot本体と同じProviderインスタンス・コスト記録/無料枠事前ブロックを共有する
    [McpServerToolType]
    public class AskTool
    {
        private readonly Func<ApiProvider, IAiProvider> _providerFactory;
        private readonly Func<string, Task<bool>> _hasOpenAiBudgetAsync;

        public AskTool(Func<ApiProvider, IAiProvider> providerFactory, Func<string, Task<bool>> hasOpenAiBudgetAsync)
        {
            _providerFactory = providerFactory;
            _hasOpenAiBudgetAsync = hasOpenAiBudgetAsync;
        }

        [McpServerTool, Description("単発の質問・軽いコード生成をAIモデルに投げ、応答テキストを1回だけ返します。会話履歴は保持されません(呼び出しごとに独立したリクエストです)。")]
        public async Task<string> Ask(
            [Description("モデルへの質問・依頼内容")] string prompt,
            [Description("使用するモデルID(省略時はローカルの既定モデル)。/modelで選択可能なIDと同じ値(例: gemini-3.8-flash, xai/grok-4.6, gpt-5.6-sol)")] string? model,
            CancellationToken cancellationToken)
        {
            string modelId = string.IsNullOrWhiteSpace(model) ? ModelRegistry.DefaultModelId : model;

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

            var request = new AiRequest(
                SystemPrompt: "",
                ModelId: modelMeta.ModelId,
                History: Array.Empty<ChatTurn>(),
                CurrentUserText: prompt,
                CurrentImagesBase64: Array.Empty<string>(),
                Temperature: 0.7,
                MaxOutputTokens: modelMeta.MaxOutputTokens,
                Effort: EffortLevel.Medium
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

            if (promptTokens.HasValue)
            {
                double? estimatedCost = CostEstimator.EstimateCostUsd(
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
                        EstimatedCostUsd = estimatedCost.Value
                    });
                    await db.SaveChangesAsync(cancellationToken);
                }
            }

            return responseText.ToString();
        }
    }
}
