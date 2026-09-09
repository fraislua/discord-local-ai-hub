using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace DiscordAIBot
{
    class Program
    {
        private DiscordSocketClient _client = null!;
        private static readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        private readonly ConcurrentDictionary<ulong, CancellationTokenSource> _activeGenerations = new();
        private readonly ConcurrentDictionary<ulong, string> _channelModels = new();
        private readonly ConcurrentDictionary<ulong, EffortLevel> _channelEfforts = new();

        private ChatOrchestrator _orchestrator = null!;
        private string _discordToken = string.Empty;
        private string _lmStudioEndpoint = string.Empty;
        private string _geminiApiKey = string.Empty;
        private string _googleAdcCredentialPath = string.Empty;
        private string _vertexProjectId = string.Empty;
        private string _vertexRegion = string.Empty;

        private ulong _chatAiChannelId;
        private bool _usageTopicUpdaterStarted = false;
        private double _lastDisplayedCostUsd = -1;

        static async Task Main(string[] args) => await new Program().MainAsync();

        public async Task MainAsync()
        {
            try
            {
                var config = new ConfigurationBuilder()
                    .SetBasePath(Directory.GetCurrentDirectory())
                    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                    .Build();

                _discordToken = config["BotSettings:Token"] ?? throw new Exception("Tokenが設定されていません。");
                _lmStudioEndpoint = config["BotSettings:LmStudioEndpoint"] ?? throw new Exception("LmStudioEndpointが設定されていません。");
                _geminiApiKey = config["BotSettings:GeminiApiKey"] ?? throw new Exception("GeminiApiKeyが設定されていません。");
                _googleAdcCredentialPath = config["BotSettings:GoogleAdcCredentialPath"] ?? throw new Exception("GoogleAdcCredentialPathが設定されていません。");
                _vertexProjectId = config["BotSettings:VertexProjectId"] ?? throw new Exception("VertexProjectIdが設定されていません。");
                _vertexRegion = config["BotSettings:VertexRegion"] ?? throw new Exception("VertexRegionが設定されていません。");

                if (!ulong.TryParse(config["BotSettings:ChatAiChannelId"], out _chatAiChannelId))
                {
                    throw new Exception("チャンネルIDの形式が不正か、設定されていません。");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[エラー] 設定ファイルの読み込みに失敗しました。\n詳細: {ex.Message}");
                return;
            }

            using (var db = new ChatDbContext())
            {
                await db.Database.EnsureCreatedAsync();
                // EnsureCreatedAsyncは既存DBに新規テーブルを追加しないため、明示的に作成
                await db.Database.ExecuteSqlRawAsync(@"
                    CREATE TABLE IF NOT EXISTS UsageRecords (
                        Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                        CreatedAt TEXT NOT NULL,
                        ModelId TEXT NOT NULL,
                        PromptTokens INTEGER NOT NULL,
                        CompletionTokens INTEGER NOT NULL,
                        ReasoningTokens INTEGER NOT NULL,
                        EstimatedCostUsd REAL NOT NULL
                    );");
            }
            await TokenManager.InitializeAsync();

            var discordConfig = new DiscordSocketConfig
            {
                GatewayIntents = GatewayIntents.AllUnprivileged | GatewayIntents.MessageContent
            };

            _client = new DiscordSocketClient(discordConfig);
            _client.Log += LogAsync;

            _client.Ready += Client_ReadyAsync;
            _client.MessageReceived += MessageReceivedAsync;
            _client.ButtonExecuted += ButtonExecutedAsync;
            _client.SlashCommandExecuted += SlashCommandHandlerAsync;
            _client.SelectMenuExecuted += SelectMenuExecutedAsync;
            _client.ThreadDeleted += ThreadDeletedAsync;

            // プロバイダーのインスタンス化と動的ルーティング用のFactory登録
            IAiProvider lmStudioProvider = new LmStudioProvider(_httpClient, _lmStudioEndpoint);
            IAiProvider googleAiProvider = new GoogleAiStudioProvider(_httpClient, _geminiApiKey);

            var googleAdcTokenProvider = new GoogleAdcTokenProvider(_googleAdcCredentialPath);
            IAiProvider vertexGeminiProvider = new VertexGeminiProvider(_httpClient, googleAdcTokenProvider, _vertexProjectId, _vertexRegion);
            IAiProvider vertexGrokProvider = new VertexGrokProvider(_httpClient, googleAdcTokenProvider, _vertexProjectId, _vertexRegion);

            Func<ApiProvider, IAiProvider> providerFactory = providerType => providerType switch
            {
                ApiProvider.LmStudio => lmStudioProvider,
                ApiProvider.GoogleAiStudio => googleAiProvider,
                ApiProvider.VertexGemini => vertexGeminiProvider,
                ApiProvider.VertexGrok => vertexGrokProvider,
                _ => throw new ArgumentException($"未対応のプロバイダーです: {providerType}")
            };

            var attachmentProcessor = new AttachmentProcessor(_httpClient);
            var streamHandler = new StreamResponseHandler();

            _orchestrator = new ChatOrchestrator(providerFactory, attachmentProcessor, streamHandler);

            await _client.LoginAsync(TokenType.Bot, _discordToken);
            await _client.StartAsync();

            await Task.Delay(-1);
        }

        private Task LogAsync(LogMessage log)
        {
            Console.WriteLine(log.ToString());
            return Task.CompletedTask;
        }

        private async Task Client_ReadyAsync()
        {
            var slashCommand = new SlashCommandBuilder()
                .WithName("model")
                .WithDescription("このチャンネル・スレッドで使用するAIモデルを選択します。");

            var effortCommand = new SlashCommandBuilder()
                .WithName("effort")
                .WithDescription("このチャンネル・スレッドでのクラウドAIモデルの思考の深さ(エフォート)を設定します。");

            try
            {
                await _client.CreateGlobalApplicationCommandAsync(slashCommand.Build());
                await _client.CreateGlobalApplicationCommandAsync(effortCommand.Build());
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Error] コマンド登録エラー: {ex.Message}");
            }

            if (!_usageTopicUpdaterStarted)
            {
                _usageTopicUpdaterStarted = true;
                _ = Task.Run(UsageTopicUpdateLoopAsync);
            }
        }

        // チャンネルトピックにクラウドAI利用額(今月・推定)を表示する。
        // Discordのチャンネル編集にはレート制限(目安10分に2回程度)があるため、
        // 一定間隔でのポーリング+差分がある場合のみ更新する方式にしている
        private async Task UsageTopicUpdateLoopAsync()
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));

            await UpdateUsageTopicAsync();
            while (await timer.WaitForNextTickAsync())
            {
                await UpdateUsageTopicAsync();
            }
        }

        private async Task UpdateUsageTopicAsync()
        {
            try
            {
                double totalCostUsd = await GetCurrentMonthCostUsdAsync();

                if (Math.Abs(totalCostUsd - _lastDisplayedCostUsd) < 0.001)
                {
                    return;
                }

                if (_client.GetChannel(_chatAiChannelId) is ITextChannel channel)
                {
                    string topic = $"💰 今月のクラウドAI利用額(推定): ${totalCostUsd:F2} / $10.00 (Google Developer Program枠)";
                    await channel.ModifyAsync(x => x.Topic = topic);
                    _lastDisplayedCostUsd = totalCostUsd;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Warning] チャンネルトピックの更新に失敗しました: {ex.Message}");
            }
        }

        private async Task<double> GetCurrentMonthCostUsdAsync()
        {
            // JST(UTC+9、DST無し)の暦月境界をUTCに変換してクエリする
            DateTime nowJst = DateTime.UtcNow.AddHours(9);
            DateTime startOfMonthUtc = new DateTime(nowJst.Year, nowJst.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(-9);

            using var db = new ChatDbContext();
            return await db.UsageRecords
                .Where(u => u.CreatedAt >= startOfMonthUtc)
                .SumAsync(u => u.EstimatedCostUsd);
        }

        private async Task SlashCommandHandlerAsync(SocketSlashCommand command)
        {
            if (command.Data.Name == "model")
            {
                var menuBuilder = new SelectMenuBuilder()
                    .WithPlaceholder("モデルを選択してください")
                    .WithCustomId("model_select_menu");

                foreach (var kvp in ModelRegistry.AvailableModels)
                {
                    menuBuilder.AddOption(kvp.Value.DisplayName, kvp.Key, kvp.Value.Description);
                }

                var builder = new ComponentBuilder().WithSelectMenu(menuBuilder);
                await command.RespondAsync("👇 使用するモデルを選択してください（この場所での会話に適用されます）:", components: builder.Build());
            }
            else if (command.Data.Name == "effort")
            {
                var menuBuilder = new SelectMenuBuilder()
                    .WithPlaceholder("エフォートを選択してください")
                    .WithCustomId("effort_select_menu")
                    .AddOption("低", EffortLevel.Low.ToString(), "応答速度重視。日常会話向け")
                    .AddOption("中（デフォルト）", EffortLevel.Medium.ToString(), "バランス型")
                    .AddOption("高", EffortLevel.High.ToString(), "複雑な問題向け。処理時間が長くなります");

                var builder = new ComponentBuilder().WithSelectMenu(menuBuilder);
                await command.RespondAsync("👇 クラウドAIモデル(Gemini/Grok)のエフォートを選択してください（ローカルモデルには影響しません）:", components: builder.Build());
            }
        }

        private async Task SelectMenuExecutedAsync(SocketMessageComponent component)
        {
            if (component.Data.CustomId == "model_select_menu")
            {
                string selectedModelId = component.Data.Values.First();
                ulong contextId = component.Channel.Id;
                _channelModels[contextId] = selectedModelId;

                string modelName = ModelRegistry.AvailableModels[selectedModelId].DisplayName;
                await component.RespondAsync($"✅ この場所での使用モデルを **{modelName}** に変更しました。");
            }
            else if (component.Data.CustomId == "effort_select_menu")
            {
                string selectedEffort = component.Data.Values.First();
                ulong contextId = component.Channel.Id;

                if (Enum.TryParse<EffortLevel>(selectedEffort, out var effort))
                {
                    _channelEfforts[contextId] = effort;
                    await component.RespondAsync($"✅ この場所でのエフォートを **{effort}** に変更しました。");
                }
            }
        }

        private async Task ThreadDeletedAsync(Cacheable<SocketThreadChannel, ulong> thread)
        {
            try
            {
                using var db = new ChatDbContext();
                var messagesToDelete = db.Messages.Where(m => m.ThreadId == thread.Id);
                db.Messages.RemoveRange(messagesToDelete);
                await db.SaveChangesAsync();
                Console.WriteLine($"[DB] スレッド {thread.Id} の履歴を削除しました。");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DB Error] 履歴削除に失敗: {ex.Message}");
            }
        }

        private async Task<bool> CheckLmStudioOnlineAsync()
        {
            try
            {
                string healthCheckUrl = _lmStudioEndpoint.Replace("/chat/completions", "/models");
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                var response = await _httpClient.GetAsync(healthCheckUrl, cts.Token);
                return response.IsSuccessStatusCode;
            }
            catch { return false; }
        }

        private async Task MessageReceivedAsync(SocketMessage arg)
        {
            if (arg is not SocketUserMessage userMessage || userMessage.Author.IsBot) return;

            ulong logicalChannelId = (userMessage.Channel is SocketThreadChannel t) ? t.ParentChannel.Id : userMessage.Channel.Id;

            if (logicalChannelId != _chatAiChannelId) return;

            SocketThreadChannel targetThread;
            ulong contextId;

            if (userMessage.Channel is SocketThreadChannel threadChannel)
            {
                targetThread = threadChannel;
                contextId = targetThread.Id;
            }
            else if (userMessage.Channel is SocketTextChannel textChannel)
            {
                string parentModelId = ModelRegistry.DefaultModelId;

                if (_channelModels.TryGetValue(textChannel.Id, out string? customParentModelId) && customParentModelId != null)
                {
                    parentModelId = customParentModelId;
                }

                string threadTitle = userMessage.Content.Length > 20 ? userMessage.Content.Substring(0, 20) + "..." : userMessage.Content;
                if (string.IsNullOrWhiteSpace(threadTitle)) threadTitle = "AI対話セッション";

                targetThread = await textChannel.CreateThreadAsync(
                    name: threadTitle,
                    autoArchiveDuration: ThreadArchiveDuration.OneDay,
                    message: userMessage);

                contextId = targetThread.Id;
                _channelModels.TryAdd(contextId, parentModelId);
            }
            else
            {
                return;
            }

            // 安全なフォールバックロジック (KeyNotFoundExceptionの完全防止)
            if (!_channelModels.TryGetValue(contextId, out string? targetModelId) || targetModelId == null || !ModelRegistry.AvailableModels.ContainsKey(targetModelId))
            {
                targetModelId = ModelRegistry.DefaultModelId;
                _channelModels[contextId] = targetModelId;
            }

            var targetModel = ModelRegistry.AvailableModels[targetModelId];

            EffortLevel effort = _channelEfforts.TryGetValue(contextId, out var customEffort) ? customEffort : EffortLevel.Medium;

            string systemPrompt = "優秀な創作アシスタントとして、ゲームのアイデア、コアループ、システム設計、企画書のブラッシュアップを支援してください。ステップバイステップで深く思考し、クリエイティブな提案を行ってください。";

            if (targetModel.Provider == ApiProvider.LmStudio)
            {
                bool isOnline = await CheckLmStudioOnlineAsync();
                if (!isOnline)
                {
                    await targetThread.SendMessageAsync("❌ **[通信失敗]** デスクトップPCのAIエンジンがスリープ中、または未起動です。\n💡 `/model` コマンドを使用して、別のモデルに切り替えてください。");
                    return;
                }
            }

            if (_activeGenerations.TryRemove(contextId, out var oldCts))
            {
                oldCts.Cancel();
                oldCts.Dispose();
            }

            var cts = new CancellationTokenSource();
            _activeGenerations[contextId] = cts;

            var builder = new ComponentBuilder()
                .WithButton("🛑 生成を停止", $"stop_{contextId}", ButtonStyle.Danger);

            var statusMsg = await targetThread.SendMessageAsync("🔍 [1/3] ファイルを解析中...", components: builder.Build());

            _ = Task.Run(async () =>
            {
                try
                {
                    await _orchestrator.ProcessUserMessageAsync(
                        userMessage,
                        statusMsg,
                        contextId,
                        targetModel,
                        systemPrompt,
                        effort,
                        cts.Token
                    );
                }
                catch (OperationCanceledException)
                {
                    if (!cts.IsCancellationRequested)
                    {
                        await statusMsg.ModifyAsync(m =>
                        {
                            m.Content = "⚠️ AIサーバーからの応答がタイムアウト（通信切断）しました。";
                            m.Components = null;
                        });
                    }
                }
                catch (AiRateLimitException)
                {
                    await statusMsg.ModifyAsync(m =>
                    {
                        m.Content = "❌ **[通信失敗]** APIのリクエスト制限（429 Too Many Requests）に達しました。\n💡 `/model` コマンドを使用して、別のモデルに切り替えてください。";
                        m.Components = null;
                    });
                }
                catch (HttpRequestException ex) when (ex.Message.Contains("429") || ex.Message.Contains("Too Many Requests"))
                {
                    // LmStudio等の他のプロバイダー起因の429エラーをキャッチするためのフォールバック
                    await statusMsg.ModifyAsync(m =>
                    {
                        m.Content = "❌ **[通信失敗]** APIのリクエスト制限（429 Too Many Requests）に達しました。\n💡 `/model` コマンドを使用して、別のモデルに切り替えてください。";
                        m.Components = null;
                    });
                }
                catch (Exception ex)
                {
                    await statusMsg.ModifyAsync(m =>
                    {
                        m.Content = $"❌ **[通信失敗]** ローカルAIが未起動、または予期せぬエラーが発生しました。\n💡 `/model` コマンドを使用して、別のモデルに切り替えてください。\n*(詳細: {ex.Message})*";
                        m.Components = null;
                    });
                }
                finally
                {
                    if (_activeGenerations.TryRemove(contextId, out var finishedCts))
                    {
                        finishedCts.Dispose();
                    }
                }
            });
        }

        private async Task ButtonExecutedAsync(SocketMessageComponent component)
        {
            if (component.Data.CustomId.StartsWith("stop_"))
            {
                string idString = component.Data.CustomId.Replace("stop_", "");
                if (ulong.TryParse(idString, out ulong contextId))
                {
                    if (_activeGenerations.TryGetValue(contextId, out var cts))
                    {
                        cts.Cancel();
                        await component.RespondAsync("🛑 生成停止シグナルを送信しました。", ephemeral: true);
                    }
                    else
                    {
                        await component.RespondAsync("既に生成は完了しているか、停止されています。", ephemeral: true);
                    }
                }
            }
        }
    }
}
