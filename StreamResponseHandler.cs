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
        private const int MaxDiscordMessageLength = 1950;

        public async Task<string> HandleStreamAsync(
            IAsyncEnumerable<StreamChunk> stream,
            IUserMessage statusMessage,
            ModelMetadata modelMeta,
            int historyCount,
            int imageCount,
            int initialContextTokens,
            ulong contextId, // 追加: 呼び出し元のコンテキストID
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
                        string textToRender = displayTextBuffer.ToString();
                        
                        if (reasoningStartTime.HasValue)
                        {
                            var lines = reasoningBuffer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
                            var last3Lines = lines.Skip(Math.Max(0, lines.Length - 3));
                            string reasoningPreview = $"\n\n💭 **thinking**\n> {string.Join("\n> ", last3Lines)}";
                            textToRender += reasoningPreview;
                        }

                        // 修正箇所: contextId を渡す
                        var result = await UpdateDiscordMessageAsync(textToRender, currentDiscordMsg, currentStartIndex, contextId);
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
            
            string footer = $"\n\n`Model: {modelMeta.ModelId}` | `📊 {tokensPerSec:F1} t/s` | `[Context: {totalContextTokens} / {modelMeta.ContextWindow}]` | `🖼️ Images: {imageCount}` | `Reason: {lastFinishReason}` | `History: {historyCount}`";
            
            displayTextBuffer.Append(footer);

            // 修正箇所: contextId を渡し、isFinal を true とする
            var finalResult = await UpdateDiscordMessageAsync(
                displayTextBuffer.ToString(), 
                currentDiscordMsg, 
                currentStartIndex, 
                contextId,
                isFinal: true);
                
            currentDiscordMsg = finalResult.Message;
            currentStartIndex = finalResult.NextIndex;

            return rawTextBuffer.ToString();
        }

        private async Task<(IUserMessage Message, int NextIndex)> UpdateDiscordMessageAsync(
            string currentFullText, 
            IUserMessage currentMsg, 
            int startIndex,
            ulong contextId, // 追加: ボタン再構築用コンテキストID
            bool isFinal = false)
        {
            string formattedText = currentFullText
                .Replace("<image_memory>", "\n\n||🖼️ **画像メモリ:** ")
                .Replace("</image_memory>", "||\n\n");

            if (formattedText.Length <= startIndex) return (currentMsg, startIndex);

            // 動的に停止ボタンを再構築
            var stopButton = new ComponentBuilder()
                .WithButton("🛑 生成を停止", $"stop_{contextId}", ButtonStyle.Danger)
                .Build();

            while (formattedText.Length - startIndex > MaxDiscordMessageLength)
            {
                string chunkToDisplay = formattedText.Substring(startIndex, MaxDiscordMessageLength);
                
                bool isCodeBlockOpen = ((chunkToDisplay.Length - chunkToDisplay.Replace("```", "").Length) / 3) % 2 != 0;
                if (isCodeBlockOpen)
                {
                    chunkToDisplay += "\n```"; 
                    formattedText = formattedText.Insert(startIndex + MaxDiscordMessageLength, "\n```\n");
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

            string finalChunk = formattedText.Substring(startIndex);
            
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