using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DiscordAIBot
{
    // MCPツール(ask/compare)共通の「1モデル呼び出し」処理。ストリーム受信・終了理由の収集・
    // 進捗通知・コスト記録を1か所にまとめ、両ツールで同じ受信ループを二重に持たないようにする。
    // Discord側(StreamResponseHandler)はフッターの`Reason: length`等で人間に切り捨てを知らせるが、
    // MCP経由では終了理由を捨てていたため、呼び出し元のAIエージェントが「書き終えた」のか
    // 「上限で切られた」のかを区別できなかった(provisioning/057)
    public static class McpModelCall
    {
        // MCPクライアント(Claude Code等)は無応答・無進捗が続くとツール呼び出しを中断するため、
        // この間隔で進捗を通知する
        private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(3);

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

        // reportProgressがnullでなければ、呼び出し中は約3秒ごとに(経過時間, 受信済み文字数)で呼び出す。
        // 通知はチャンクの到着とは無関係なタイマーで行う。以前はチャンク受信ループの中で通知していたため、
        // 最初のトークンが届くまでの待ち(ローカルモデルのロード等)や、本文チャンクを流さない推論フェーズ
        // (OpenAI/Grok)の間は無通知になり、compareでは1モデルの生成中ずっと無通知だった
        // (provisioning/057: 326秒無通知でクライアントに中断された)
        public static async Task<Result> RunAsync(
            IAiProvider provider,
            AiRequest request,
            ModelMetadata modelMeta,
            Action<TimeSpan, int>? reportProgress,
            CancellationToken cancellationToken)
        {
            var responseText = new StringBuilder();
            int receivedChars = 0; // タイマー側から読むため、StringBuilderとは別にInterlockedで更新する
            string? finishReason = null;
            int? promptTokens = null;
            int? completionTokens = null;
            int? reasoningTokens = null;

            var stopwatch = Stopwatch.StartNew();
            using var progressCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task progressLoop = Task.CompletedTask;
            if (reportProgress is { } report)
            {
                progressLoop = ReportPeriodicallyAsync(
                    () => report(stopwatch.Elapsed, Volatile.Read(ref receivedChars)), progressCts.Token);
            }

            try
            {
                await foreach (var chunk in provider.StreamChatAsync(request, cancellationToken))
                {
                    if (!chunk.IsReasoning && chunk.TextDelta is { Length: > 0 })
                    {
                        responseText.Append(chunk.TextDelta);
                        Interlocked.Add(ref receivedChars, chunk.TextDelta.Length);
                    }

                    // 終了理由は最後に届いた非空の値を採用する
                    if (!string.IsNullOrEmpty(chunk.FinishReason)) finishReason = chunk.FinishReason;
                    if (chunk.PromptTokens.HasValue) promptTokens = chunk.PromptTokens;
                    if (chunk.CompletionTokens.HasValue) completionTokens = chunk.CompletionTokens;
                    if (chunk.ReasoningTokens.HasValue) reasoningTokens = chunk.ReasoningTokens;
                }
            }
            finally
            {
                // 正常終了・例外・キャンセルのいずれでも通知を止め、停止を待ってから抜ける
                // (ツール呼び出しの完了後に進捗通知が送られないようにする)
                progressCts.Cancel();
                await progressLoop;
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

        private static async Task ReportPeriodicallyAsync(Action report, CancellationToken cancellationToken)
        {
            using var timer = new PeriodicTimer(ProgressInterval);
            try
            {
                while (await timer.WaitForNextTickAsync(cancellationToken))
                {
                    report();
                }
            }
            catch (OperationCanceledException)
            {
                // ストリームの終了、または呼び出し元のキャンセルで停止する
            }
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
