using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol;

namespace DiscordAIBot
{
    // MCPツール(ask/compare)共通の「1モデル呼び出し」処理。ストリーム受信・終了理由の収集・
    // コスト記録を1か所にまとめ、両ツールで同じ受信ループを二重に持たないようにする。
    // Discord側(StreamResponseHandler)はフッターの`Reason: length`等で人間に切り捨てを知らせるが、
    // MCP経由では終了理由を捨てていたため、呼び出し元のAIエージェントが「書き終えた」のか
    // 「上限で切られた」のかを区別できなかった(provisioning/057)
    public static class McpModelCall
    {
        public enum Completion
        {
            Complete,  // 正常終了(stop / STOP)
            Truncated, // 出力上限に到達(length / MAX_TOKENS)
            Abnormal,  // 上記以外の終了理由(content_filter / SAFETY等)
            Unknown    // 終了理由を受信しないままストリームが終わった
        }

        public sealed record Result(
            string Answer,
            string? FinishReason,
            int? PromptTokens,
            int? CompletionTokens,
            int? ReasoningTokens,
            double? EstimatedCostUsd)
        {
            public Completion Completion => Classify(FinishReason);
        }

        // progressがnullでなければ、受信中に約3秒間隔で進捗を通知する
        public static async Task<Result> RunAsync(
            IAiProvider provider,
            AiRequest request,
            ModelMetadata modelMeta,
            IProgress<ProgressNotificationValue>? progress,
            CancellationToken cancellationToken)
        {
            var responseText = new StringBuilder();
            string? finishReason = null;
            int? promptTokens = null;
            int? completionTokens = null;
            int? reasoningTokens = null;

            // クライアントがprogressTokenを送っていない場合、MCP SDKはNullProgressを注入する
            // ため、常時Report()を呼んでも安全(その場合は単に無視される)。長時間かかる
            // クラウド呼び出し(High/XHigh effort等)でMCPクライアント側のタイムアウトを
            // 回避する狙いで、経過時間ベースで一定間隔ごとに進捗を通知する
            const int ProgressIntervalMs = 3000;
            var stopwatch = Stopwatch.StartNew();
            long lastProgressReportMs = 0;
            progress?.Report(new ProgressNotificationValue { Progress = 0, Message = "モデル呼び出しを開始しました" });

            await foreach (var chunk in provider.StreamChatAsync(request, cancellationToken))
            {
                if (!chunk.IsReasoning && chunk.TextDelta is { Length: > 0 })
                {
                    responseText.Append(chunk.TextDelta);
                }

                // 終了理由は最後に届いた非空の値を採用する
                if (!string.IsNullOrEmpty(chunk.FinishReason)) finishReason = chunk.FinishReason;
                if (chunk.PromptTokens.HasValue) promptTokens = chunk.PromptTokens;
                if (chunk.CompletionTokens.HasValue) completionTokens = chunk.CompletionTokens;
                if (chunk.ReasoningTokens.HasValue) reasoningTokens = chunk.ReasoningTokens;

                if (progress != null && stopwatch.ElapsedMilliseconds - lastProgressReportMs >= ProgressIntervalMs)
                {
                    lastProgressReportMs = stopwatch.ElapsedMilliseconds;
                    int elapsedSeconds = (int)stopwatch.Elapsed.TotalSeconds;
                    progress.Report(new ProgressNotificationValue
                    {
                        Progress = elapsedSeconds,
                        Message = $"応答生成中...(経過{elapsedSeconds}秒、{responseText.Length}文字受信済み)"
                    });
                }
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

            return new Result(responseText.ToString(), finishReason, promptTokens, completionTokens, reasoningTokens, estimatedCost);
        }

        // プロバイダーごとに終了理由の表記が異なる(OpenAI互換API: stop/length、Gemini: STOP/MAX_TOKENS)
        public static Completion Classify(string? finishReason) => finishReason?.ToLowerInvariant() switch
        {
            null or "" => Completion.Unknown,
            "stop" => Completion.Complete,
            "length" or "max_tokens" => Completion.Truncated,
            _ => Completion.Abnormal
        };

        // 本文はあるが正常終了ではない場合に呼び出し元へ伝える注記。正常終了時はnull
        public static string? BuildIncompleteNotice(Result result, ModelMetadata modelMeta)
        {
            string breakdown = FormatBreakdown(result, modelMeta);
            return result.Completion switch
            {
                Completion.Complete => null,
                Completion.Truncated => $"応答は出力上限に達して途中で切れています({breakdown})。",
                Completion.Abnormal => $"応答は通常とは異なる理由で終了しました({breakdown})。内容が不完全な可能性があります。",
                _ => $"終了理由を受信しないまま応答が終了しました({breakdown})。内容が不完全な可能性があります。"
            };
        }

        public static string BuildEmptyAnswerMessage(Result result, ModelMetadata modelMeta)
        {
            string message = $"モデル '{modelMeta.ModelId}' の応答本文が空でした({FormatBreakdown(result, modelMeta)})。";
            if (result.Completion == Completion.Truncated)
            {
                message += "推論(思考)だけで出力上限を使い切った可能性が高いです。effortを下げるか、質問を分割して再試行してください。";
            }
            return message;
        }

        private static string FormatBreakdown(Result result, ModelMetadata modelMeta)
        {
            var parts = new List<string> { $"finish_reason={result.FinishReason ?? "(なし)"}" };
            if (result.CompletionTokens.HasValue) parts.Add($"出力トークン={result.CompletionTokens}");
            if (result.ReasoningTokens.HasValue) parts.Add($"推論トークン={result.ReasoningTokens}");
            if (modelMeta.MaxOutputTokens > 0) parts.Add($"出力上限={modelMeta.MaxOutputTokens}");
            return string.Join("、", parts);
        }
    }
}
