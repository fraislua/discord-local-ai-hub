using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;

namespace DiscordAIBot
{
    public class ChatOrchestrator
    {
        private readonly Func<ApiProvider, IAiProvider> _providerFactory;
        private readonly AttachmentProcessor _attachmentProcessor;
        private readonly StreamResponseHandler _streamResponseHandler;

        public ChatOrchestrator(
            Func<ApiProvider, IAiProvider> providerFactory,
            AttachmentProcessor attachmentProcessor,
            StreamResponseHandler streamResponseHandler)
        {
            _providerFactory = providerFactory ?? throw new ArgumentNullException(nameof(providerFactory));
            _attachmentProcessor = attachmentProcessor;
            _streamResponseHandler = streamResponseHandler;
        }

        public async Task ProcessUserMessageAsync(
            SocketUserMessage userMessage,
            IUserMessage statusMessage,
            ulong threadId,
            ModelMetadata modelMeta,
            string systemPrompt,
            CancellationToken cancellationToken)
        {
            int currentBaseTokens = TokenManager.CountTokens(systemPrompt) + TokenManager.CountTokens(userMessage.Content);
            int availableTokensForAttachments = modelMeta.ContextWindow - 2000 - currentBaseTokens;
            if (availableTokensForAttachments < 0) availableTokensForAttachments = 0;

            var (extractedText, base64Images) = await _attachmentProcessor.ProcessAttachmentsAsync(
                userMessage.Attachments, 
                modelMeta, 
                availableTokensForAttachments, 
                cancellationToken);
            
            string userTextForDb = userMessage.Content;
            string userTextForAi = userMessage.Content;

            if (!string.IsNullOrEmpty(extractedText))
            {
                userTextForDb += $"\n\n{extractedText}";
                userTextForAi += $"\n\n{extractedText}";
            }

            if (modelMeta.IsVlm && base64Images.Count > 0)
            {
                userTextForDb += "\n\n[📸 画像が送信されました]";
                userTextForAi += "\n\n<image_memory>\n添付された画像の内容を詳細に分析し、重要な情報（UI配置、コードエラーの内容、キャラクターの見た目など）を漏れなくこのタグ内にテキストとして書き出して言語化し、今後の推論の前提知識として記憶せよ。\n</image_memory>";
            }

            long currentUserMessageId = await SaveUserMessageAsync(threadId, userTextForDb);

            string statusText = base64Images.Count > 0 
                ? $"🖼️ [2/3] 画像({base64Images.Count}枚)をエンコード中... Model: `{modelMeta.ModelId}`"
                : $"🚀 [2/3] Loading AI model (`{modelMeta.ModelId}`)...";
            await statusMessage.ModifyAsync(m => m.Content = statusText);

            var history = await GetAndTrimHistoryAsync(threadId, modelMeta.ContextWindow, currentUserMessageId, userTextForAi, systemPrompt, cancellationToken);

            var request = new AiRequest(
                SystemPrompt: systemPrompt,
                ModelId: modelMeta.ModelId,
                History: history,
                CurrentUserText: userTextForAi,
                CurrentImagesBase64: base64Images,
                Temperature: 0.7,
                MaxOutputTokens: modelMeta.MaxOutputTokens
            );

            int initialContextTokens = TokenManager.CountTokens(systemPrompt) + TokenManager.CountTokens(userTextForAi);
            foreach (var turn in history)
            {
                initialContextTokens += TokenManager.CountTokens(turn.Content);
            }

            // 動的プロバイダーの解決
            var aiProvider = _providerFactory(modelMeta.Provider);
            var stream = aiProvider.StreamChatAsync(request, cancellationToken);
            
            // 修正箇所: threadId を contextId として引き渡す
            var streamResult = await _streamResponseHandler.HandleStreamAsync(
                stream,
                statusMessage,
                modelMeta,
                history.Count,
                base64Images.Count,
                initialContextTokens,
                threadId,
                cancellationToken);

            await SaveAiMessageAsync(threadId, streamResult.RawText);

            if (streamResult.PromptTokens.HasValue)
            {
                double? estimatedCost = CostEstimator.EstimateCostUsd(
                    modelMeta.Provider,
                    streamResult.PromptTokens.Value,
                    streamResult.CompletionTokens ?? 0,
                    streamResult.ReasoningTokens ?? 0);

                if (estimatedCost.HasValue)
                {
                    await SaveUsageRecordAsync(
                        modelMeta.ModelId,
                        streamResult.PromptTokens.Value,
                        streamResult.CompletionTokens ?? 0,
                        streamResult.ReasoningTokens ?? 0,
                        estimatedCost.Value);
                }
            }
        }

        private async Task<IReadOnlyList<ChatTurn>> GetAndTrimHistoryAsync(
            ulong threadId,
            int maxContextWindow,
            long excludeMessageId,
            string currentUserText,
            string systemPrompt,
            CancellationToken cancellationToken)
        {
            using var db = new ChatDbContext();
            
            var rawHistory = await db.Messages
                .Where(m => m.ThreadId == threadId && m.Id != excludeMessageId)
                .OrderByDescending(m => m.Id)
                .Take(50)
                .ToListAsync(cancellationToken);

            var chatTurns = new List<ChatTurn>();
            int currentTokens = TokenManager.CountTokens(systemPrompt) + TokenManager.CountTokens(currentUserText);

            int tokenLimit = maxContextWindow - 2000; 
            if (tokenLimit < 0) tokenLimit = maxContextWindow;

            foreach (var msg in rawHistory)
            {
                int msgTokens = TokenManager.CountTokens(msg.Content);
                if (currentTokens + msgTokens > tokenLimit) break;

                currentTokens += msgTokens;
                chatTurns.Add(new ChatTurn(msg.Role, msg.Content));
            }

            chatTurns.Reverse(); 

            return chatTurns;
        }

        private async Task<long> SaveUserMessageAsync(ulong threadId, string userText)
        {
            using var db = new ChatDbContext();
            var msg = new ChatMessage
            {
                ThreadId = threadId,
                Role = "user",
                Content = userText,
                CreatedAt = DateTime.UtcNow
            };
            db.Messages.Add(msg);
            await db.SaveChangesAsync();
            
            return msg.Id; 
        }

        private async Task SaveAiMessageAsync(ulong threadId, string aiText)
        {
            if (string.IsNullOrWhiteSpace(aiText)) return;

            using var db = new ChatDbContext();
            db.Messages.Add(new ChatMessage
            {
                ThreadId = threadId,
                Role = "assistant",
                Content = aiText,
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        private async Task SaveUsageRecordAsync(string modelId, int promptTokens, int completionTokens, int reasoningTokens, double estimatedCostUsd)
        {
            using var db = new ChatDbContext();
            db.UsageRecords.Add(new UsageRecord
            {
                CreatedAt = DateTime.UtcNow,
                ModelId = modelId,
                PromptTokens = promptTokens,
                CompletionTokens = completionTokens,
                ReasoningTokens = reasoningTokens,
                EstimatedCostUsd = estimatedCostUsd
            });
            await db.SaveChangesAsync();
        }
    }
}