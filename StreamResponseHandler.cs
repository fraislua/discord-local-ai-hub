using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Discord;

namespace DiscordAIBot
{
    public class StreamResponseHandler
    {
        private readonly TimeSpan _updateInterval = TimeSpan.FromSeconds(1.5);
        public const int MaxDiscordMessageLength = 1950;

        public async Task<StreamResult> HandleStreamAsync(
            IAsyncEnumerable<StreamChunk> stream,
            IUserMessage statusMessage,
            ModelMetadata modelMeta,
            int historyCount,
            int imageCount,
            int initialContextTokens,
            ulong contextId, // 追加: 呼び出し元のコンテキストID
            EffortLevel effort,
            CancellationToken cancellationToken)
        {
            var rawTextBuffer = new StringBuilder();      
            var displayTextBuffer = new StringBuilder();  
            var reasoningBuffer = new StringBuilder();    
            
            var stopwatch = new Stopwatch();
            
            DateTime lastUpdateTime = DateTime.UtcNow;
            DateTime? reasoningStartTime = null;
            
            IUserMessage currentDiscordMsg = statusMessage;
            int currentStartIndex = 0; 
            
            bool isFirstChunk = true;
            bool isInlineReasoning = false;
            bool wasReasoning = false;
            string lastFinishReason = "stop";

            int? usagePromptTokens = null;
            int? usageCompletionTokens = null;
            int? usageReasoningTokens = null;

            try
            {
                await foreach (var chunk in stream.WithCancellation(cancellationToken))
                {
                    if (isFirstChunk)
                    {
                        stopwatch.Start();
                        await currentDiscordMsg.ModifyAsync(m => m.Content = "🧠 [3/3] Generating...");
                        isFirstChunk = false;
                    }

                    if (!string.IsNullOrEmpty(chunk.FinishReason))
                    {
                        lastFinishReason = chunk.FinishReason;
                    }

                    if (chunk.PromptTokens.HasValue || chunk.CompletionTokens.HasValue || chunk.ReasoningTokens.HasValue)
                    {
                        usagePromptTokens = chunk.PromptTokens;
                        usageCompletionTokens = chunk.CompletionTokens;
                        usageReasoningTokens = chunk.ReasoningTokens;
                    }

                    if (chunk.TextDelta != null)
                    {
                        string delta = chunk.TextDelta;
                        rawTextBuffer.Append(delta);

                        if (delta.Contains("<think>"))
                        {
                            isInlineReasoning = true;
                            delta = delta.Replace("<think>", "");
                        }
                        if (delta.Contains("</think>"))
                        {
                            isInlineReasoning = false;
                            delta = delta.Replace("</think>", "");
                        }

                        bool currentIsReasoning = chunk.IsReasoning || isInlineReasoning;

                        if (wasReasoning && !currentIsReasoning)
                        {
                            if (reasoningStartTime.HasValue)
                            {
                                double thinkingSeconds = (DateTime.UtcNow - reasoningStartTime.Value).TotalSeconds;
                                displayTextBuffer.Append($"\n💭 *Thought for {thinkingSeconds:F1} seconds*\n\n");
                                reasoningStartTime = null;
                                reasoningBuffer.Clear();
                            }
                        }
                        
                        wasReasoning = currentIsReasoning; 

                        if (currentIsReasoning)
                        {
                            if (reasoningStartTime == null) reasoningStartTime = DateTime.UtcNow;
                            reasoningBuffer.Append(delta);
                        }
                        else
                        {
                            displayTextBuffer.Append(delta);
                        }
                    }

                    if ((DateTime.UtcNow - lastUpdateTime) > _updateInterval)
                    {
                        lastUpdateTime = DateTime.UtcNow;
                        string permanentText = displayTextBuffer.ToString();
                        string textToRender = permanentText;

                        if (reasoningStartTime.HasValue)
                        {
                            var lines = reasoningBuffer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
                            var last3Lines = lines.Skip(Math.Max(0, lines.Length - 3));
                            string reasoningPreview = $"\n\n💭 **thinking**\n> {string.Join("\n> ", last3Lines)}";
                            textToRender += reasoningPreview;
                        }

                        // 修正箇所: contextIdに加え、確定済み(permanent)テキストの長さを渡す。
                        // ページ送り(startIndexの前進)は確定済みテキストの長さのみを基準に行い、
                        // 一時的なthinkingプレビュー(改行が少ないと1950字を超えうる)の伸縮で
                        // ページ境界がずれて後続の本回答が描画スキップされる不具合を防ぐ
                        var result = await UpdateDiscordMessageAsync(textToRender, permanentText.Length, currentDiscordMsg, currentStartIndex, contextId);
                        currentDiscordMsg = result.Message;
                        currentStartIndex = result.NextIndex;
                    }

                    if (!string.IsNullOrEmpty(chunk.FinishReason))
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                rawTextBuffer.Append("\n\n[🛑 ユーザーにより生成が停止されました]");
                displayTextBuffer.Append("\n\n**[🛑 生成停止]**");
                lastFinishReason = "cancelled";
            }

            if (stopwatch.IsRunning)
            {
                stopwatch.Stop();
            }

            if (reasoningStartTime.HasValue)
            {
                double thinkingSeconds = (DateTime.UtcNow - reasoningStartTime.Value).TotalSeconds;
                displayTextBuffer.Append($"\n💭 *Thought for {thinkingSeconds:F1} seconds*\n\n");
            }

            int outputTokens = TokenManager.CountTokens(rawTextBuffer.ToString());
            double elapsedSeconds = stopwatch.Elapsed.TotalSeconds;
            double tokensPerSec = elapsedSeconds > 0 ? outputTokens / elapsedSeconds : 0;
            int totalContextTokens = initialContextTokens + outputTokens; 
            
            // エフォートはクラウドモデルのみ意味を持つ(ローカルモデルは無視されるため表示しない)
            string effortSegment = modelMeta.Provider != ApiProvider.LmStudio ? $" | `Effort: {effort}`" : "";
            string footer = $"\n\n`Model: {modelMeta.ModelId}`{effortSegment} | `📊 {tokensPerSec:F1} t/s` | `[Context: {totalContextTokens} / {modelMeta.ContextWindow}]` | `🖼️ Images: {imageCount}` | `Reason: {lastFinishReason}` | `History: {historyCount}`";
            
            displayTextBuffer.Append(footer);

            // 修正箇所: contextId を渡し、isFinal を true とする。最終テキストはこの時点で
            // 全て確定済みのため、permanentLengthは全文の長さと同一
            string finalText = displayTextBuffer.ToString();
            var finalResult = await UpdateDiscordMessageAsync(
                finalText,
                finalText.Length,
                currentDiscordMsg,
                currentStartIndex,
                contextId,
                isFinal: true);
                
            currentDiscordMsg = finalResult.Message;
            currentStartIndex = finalResult.NextIndex;

            return new StreamResult(rawTextBuffer.ToString(), usagePromptTokens, usageCompletionTokens, usageReasoningTokens, lastFinishReason == "cancelled");
        }

        private async Task<(IUserMessage Message, int NextIndex)> UpdateDiscordMessageAsync(
            string currentFullText,
            int permanentLength, // 追加: currentFullTextのうち確定済み(non-preview)部分の長さ
            IUserMessage currentMsg,
            int startIndex,
            ulong contextId, // 追加: ボタン再構築用コンテキストID
            bool isFinal = false)
        {
            string formattedText = currentFullText
                .Replace("<image_memory>", "\n\n||🖼️ **画像メモリ:** ")
                .Replace("</image_memory>", "||\n\n");

            if (formattedText.Length <= startIndex) return (currentMsg, startIndex);

            // ページ送り判定・分割は確定済み部分のみを対象に行う(タグ置換前の生の
            // permanentLengthで一旦切り出してから同じ置換をかける)
            string formattedPermanent = currentFullText
                .Substring(0, Math.Min(permanentLength, currentFullText.Length))
                .Replace("<image_memory>", "\n\n||🖼️ **画像メモリ:** ")
                .Replace("</image_memory>", "||\n\n");

            // 動的に停止ボタンを再構築
            var stopButton = new ComponentBuilder()
                .WithButton("🛑 生成を停止", $"stop_{contextId}", ButtonStyle.Danger)
                .Build();

            // 修正箇所: ページ送り(新規メッセージへの分割・startIndexの前進)は確定済み
            // テキスト(formattedPermanent)の長さのみを基準に行う。thinkingプレビュー
            // (改行が少ないと長くなりうる、かつ思考終了後は消える一時的な文字列)の
            // 伸縮でページ境界がずれないようにするための変更
            while (formattedPermanent.Length - startIndex > MaxDiscordMessageLength)
            {
                string chunkToDisplay = formattedPermanent.Substring(startIndex, MaxDiscordMessageLength);

                bool isCodeBlockOpen = ((chunkToDisplay.Length - chunkToDisplay.Replace("```", "").Length) / 3) % 2 != 0;
                if (isCodeBlockOpen)
                {
                    chunkToDisplay += "\n```";
                    formattedPermanent = formattedPermanent.Insert(startIndex + MaxDiscordMessageLength, "\n```\n");
                }

                // 修正箇所: 文字数上限を超過した場合、古いメッセージのボタンを剥奪する
                await currentMsg.ModifyAsync(m =>
                {
                    m.Content = chunkToDisplay;
                    m.Components = null; // 古いメッセージからボタンを削除
                });

                startIndex += MaxDiscordMessageLength;

                // 修正箇所: 新しいメッセージに停止ボタンを付与して送信
                currentMsg = await currentMsg.Channel.SendMessageAsync("*(続きを出力中...)*", components: stopButton);
            }

            // 表示だけはプレビュー分を含むformattedTextの末尾から。確定分がまだページ境界に
            // 届いていない間にプレビューが表示上限を超える場合、ページ送りはせず表示のみ切り詰める
            // (プレビューはどうせ思考終了後に消えるため、ここで確定させる必要がない)
            string finalChunk = formattedText.Substring(startIndex);
            if (finalChunk.Length > MaxDiscordMessageLength)
            {
                finalChunk = finalChunk.Substring(0, MaxDiscordMessageLength);
            }

            // 修正箇所: isFinalの真偽に応じてボタンの有無を制御
            await currentMsg.ModifyAsync(m =>
            {
                m.Content = finalChunk;
                m.Components = isFinal ? null : stopButton;
            });

            return (currentMsg, startIndex);
        }
    }
}