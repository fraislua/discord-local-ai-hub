using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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
        private readonly GoogleDriveUploader _driveUploader;
        private readonly Func<string, int, Task<bool>> _hasOpenAiBudgetAsync;

        // スレッド(セッション)ごとの会話記録を保存するGoogle Drive上のフォルダ名
        private const string DriveFolderName = "Discord-AIChatbot";

        public ChatOrchestrator(
            Func<ApiProvider, IAiProvider> providerFactory,
            AttachmentProcessor attachmentProcessor,
            StreamResponseHandler streamResponseHandler,
            GoogleDriveUploader driveUploader,
            Func<string, int, Task<bool>> hasOpenAiBudgetAsync)
        {
            _providerFactory = providerFactory ?? throw new ArgumentNullException(nameof(providerFactory));
            _attachmentProcessor = attachmentProcessor;
            _streamResponseHandler = streamResponseHandler;
            _driveUploader = driveUploader;
            _hasOpenAiBudgetAsync = hasOpenAiBudgetAsync;
        }

        public async Task ProcessUserMessageAsync(
            SocketUserMessage userMessage,
            IUserMessage statusMessage,
            ulong threadId,
            ModelMetadata modelMeta,
            string systemPrompt,
            EffortLevel effort,
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

            // Vertex経由のクラウドモデルは思考完了までテキストを一切ストリームしないため、
            // 「Loading」ではなく「思考中」と明示する(ローカルモデルのロード待ちと区別)
            bool isCloudProvider = modelMeta.Provider == ApiProvider.VertexGemini || modelMeta.Provider == ApiProvider.VertexGrok;

            string statusText;
            if (base64Images.Count > 0)
            {
                statusText = $"🖼️ [2/3] 画像({base64Images.Count}枚)をエンコード中... Model: `{modelMeta.ModelId}`";
            }
            else if (isCloudProvider)
            {
                statusText = $"💭 [2/3] 思考中... (`{modelMeta.ModelId}`)";
            }
            else
            {
                statusText = $"🚀 [2/3] Loading AI model (`{modelMeta.ModelId}`)...";
            }
            await statusMessage.ModifyAsync(m => m.Content = statusText);

            var history = await GetAndTrimHistoryAsync(threadId, modelMeta.ContextWindow, currentUserMessageId, userTextForAi, systemPrompt, cancellationToken);

            var request = new AiRequest(
                SystemPrompt: systemPrompt,
                ModelId: modelMeta.ModelId,
                History: history,
                CurrentUserText: userTextForAi,
                CurrentImagesBase64: base64Images,
                Temperature: 0.7,
                MaxOutputTokens: modelMeta.MaxOutputTokens,
                Effort: effort
            );

            int initialContextTokens = TokenManager.CountTokens(systemPrompt) + TokenManager.CountTokens(userTextForAi);
            foreach (var turn in history)
            {
                initialContextTokens += TokenManager.CountTokens(turn.Content);
            }

            // 無料枠の事前ブロックはProgram.cs側でも行っているが、そちらは履歴・添付を組み立てる前のため
            // 今回送るプロンプト分を含められない。長いスレッドでは毎回数十万トークンの履歴を送りうるため、
            // 組み立て後に推定プロンプト分込みで再判定し、超える場合は送信しない(provisioning/060)
            int conservativePromptTokens = CostEstimator.EstimatePromptTokensConservatively(initialContextTokens, base64Images.Count);
            if (modelMeta.Provider == ApiProvider.OpenAi && !await _hasOpenAiBudgetAsync(modelMeta.ModelId, conservativePromptTokens))
            {
                // 応答しない発言を履歴に残さない(次の発言の履歴に未回答のまま混ざるのを防ぐ)
                await DeleteMessageAsync(currentUserMessageId);
                await statusMessage.ModifyAsync(m =>
                {
                    m.Content = $"❌ **[無料枠超過]** 本日の`{modelMeta.DisplayName}`無料枠(データ共有プログラム)の残りでは、今回のメッセージ(履歴・添付を含む推定{conservativePromptTokens}トークン+出力上限{modelMeta.MaxOutputTokens}トークン)を送れません。課金を避けるため送信を中止しました。\n💡 新しいスレッドで会話を始めて履歴を減らすか、`/model` コマンドでローカルモデル、または別のモデルに切り替えてください。";
                    m.Components = null;
                });
                return;
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
                effort,
                cancellationToken);

            await SaveAiMessageAsync(threadId, streamResult.RawText);

            if (streamResult.PromptTokens.HasValue)
            {
                double? estimatedCost = CostEstimator.EstimateCostUsd(
                    modelMeta.Provider,
                    modelMeta.ModelId,
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
            else if (streamResult.WasCancelled)
            {
                // 停止ボタン等で途中で打ち切るとusageが届かないが、上流では生成済み分が消費されうる。
                // 無料枠の集計から漏らさないよう、出力上限まで使った前提の推定値で記録する(provisioning/060)
                int estimatedCompletionTokens = modelMeta.MaxOutputTokens > 0 ? modelMeta.MaxOutputTokens : 8192;
                double? estimatedCost = CostEstimator.EstimateCostUsd(
                    modelMeta.Provider, modelMeta.ModelId, conservativePromptTokens, estimatedCompletionTokens, 0);

                if (estimatedCost.HasValue)
                {
                    await SaveUsageRecordAsync(
                        modelMeta.ModelId, conservativePromptTokens, estimatedCompletionTokens, 0, estimatedCost.Value, source: "discord-cancelled");
                }
            }

            // 今回のやり取りを反映してGoogle Drive上の会話記録を更新する
            await UpdateThreadDriveFileAsync(threadId, cancellationToken);
        }

        // スレッドのGoogle Drive記録ファイルが未作成なら作成し、リンクをピン留めする。
        // 既に作成済み(DBにレコードあり)なら何もしない。
        // 呼び出し元(Program.cs)が、そのスレッドで最初のステータス/応答メッセージを
        // 送る前に呼ぶことで、Drive記録リンクがスレッドの一番上(ユーザーの初回投稿の
        // 直後)に来るようにしている
        public async Task EnsureThreadDriveFileAsync(ulong threadId, SocketThreadChannel thread, CancellationToken cancellationToken)
        {
            using var db = new ChatDbContext();
            bool exists = await db.ThreadDriveFiles.AnyAsync(t => t.ThreadId == threadId, cancellationToken);
            if (exists) return;

            try
            {
                string threadTitle = thread.Name;

                string folderId = await _driveUploader.GetOrCreateFolderAsync(DriveFolderName, cancellationToken);

                string initialContent = $"# {threadTitle}\n\n(会話開始: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC)\n";
                string createdDateJst = DateTime.Now.ToString("yyyyMMdd");
                var (fileId, webViewLink) = await _driveUploader.CreateFileInFolderAsync(
                    $"{threadTitle}[{createdDateJst}]({threadId}).md", initialContent, folderId, cancellationToken);

                db.ThreadDriveFiles.Add(new ThreadDriveFile
                {
                    ThreadId = threadId,
                    DriveFileId = fileId,
                    DriveFileLink = webViewLink,
                    CreatedAt = DateTime.UtcNow
                });
                await db.SaveChangesAsync(cancellationToken);

                var pinnedMsg = await thread.SendMessageAsync($"📄 このスレッドの記録: {webViewLink}");

                // ピン留めの失敗(「メッセージの管理」権限不足等)は、Drive記録自体は
                // 成功しているため区別してログに残す。DBレコードは既に保存済みなので
                // ここで失敗してもファイル作成の再試行対象にはしない
                try
                {
                    await pinnedMsg.PinAsync();
                }
                catch (Exception pinEx)
                {
                    Console.WriteLine($"[Warning] Drive記録リンクのピン留めに失敗しました(botに「メッセージの管理」権限が必要です): {pinEx.Message}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Warning] Google Driveスレッドファイルの作成に失敗しました: {ex.Message}");
                // 失敗時はDBにレコードを残さず、次回のメッセージで再試行する
            }
        }

        // スレッドのGoogle Drive記録ファイルを、DB内の全会話履歴で丸ごと上書きする
        private async Task UpdateThreadDriveFileAsync(ulong threadId, CancellationToken cancellationToken)
        {
            using var db = new ChatDbContext();
            var driveFile = await db.ThreadDriveFiles.FirstOrDefaultAsync(t => t.ThreadId == threadId, cancellationToken);
            if (driveFile == null) return; // 作成に失敗している場合はスキップ

            try
            {
                string transcript = await BuildTranscriptAsync(threadId, cancellationToken);
                await _driveUploader.UpdateFileContentAsync(driveFile.DriveFileId, transcript, cancellationToken);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Warning] Google Driveスレッドファイルの更新に失敗しました: {ex.Message}");
            }
        }

        private async Task<string> BuildTranscriptAsync(ulong threadId, CancellationToken cancellationToken)
        {
            using var db = new ChatDbContext();
            var messages = await db.Messages
                .Where(m => m.ThreadId == threadId)
                .OrderBy(m => m.Id)
                .ToListAsync(cancellationToken);

            var sb = new StringBuilder();
            sb.AppendLine($"# Discord AI Chat Log (Thread ID: {threadId})");
            sb.AppendLine();

            foreach (var msg in messages)
            {
                string roleLabel = msg.Role == "user" ? "User" : "AI";
                sb.AppendLine($"## {roleLabel} ({msg.CreatedAt:yyyy-MM-dd HH:mm:ss} UTC)");
                sb.AppendLine();
                sb.AppendLine(msg.Content);
                sb.AppendLine();
            }

            return sb.ToString();
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

        private async Task SaveUsageRecordAsync(string modelId, int promptTokens, int completionTokens, int reasoningTokens, double estimatedCostUsd, string source = "discord")
        {
            using var db = new ChatDbContext();
            db.UsageRecords.Add(new UsageRecord
            {
                CreatedAt = DateTime.UtcNow,
                ModelId = modelId,
                PromptTokens = promptTokens,
                CompletionTokens = completionTokens,
                ReasoningTokens = reasoningTokens,
                EstimatedCostUsd = estimatedCostUsd,
                Source = source
            });
            await db.SaveChangesAsync();
        }

        private async Task DeleteMessageAsync(long messageId)
        {
            using var db = new ChatDbContext();
            var message = await db.Messages.FindAsync(messageId);
            if (message == null) return;

            db.Messages.Remove(message);
            await db.SaveChangesAsync();
        }
    }
}