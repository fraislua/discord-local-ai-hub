using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.AspNetCore;

namespace DiscordAIBot
{
    class Program
    {
        private DiscordSocketClient _client = null!;
        private static readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        private readonly ConcurrentDictionary<ulong, CancellationTokenSource> _activeGenerations = new();
        private readonly ConcurrentDictionary<ulong, string> _channelModels = new();
        private readonly ConcurrentDictionary<ulong, EffortLevel> _channelEfforts = new();
        private readonly ConcurrentDictionary<ulong, string> _channelPrompts = new();

        private ChatOrchestrator _orchestrator = null!;
        private string _discordToken = string.Empty;
        private string _lmStudioEndpoint = string.Empty;
        private string _geminiApiKey = string.Empty;
        private string _googleAdcCredentialPath = string.Empty;
        private string _vertexProjectId = string.Empty;
        private string _vertexRegion = string.Empty;
        private string _openAiApiKey = string.Empty;
        private string _mcpListenUrl = string.Empty;

        private ulong _chatAiChannelId;
        private bool _usageTopicUpdaterStarted = false;
        private double _lastDisplayedCostUsd = -1;
        private int _lastDisplayedSolTokensToday = -1;
        private int _lastDisplayedLightTokensToday = -1;

        // 連投防止(アカウント乗っ取り等への対策)。ユーザー単位で管理
        // (チャンネル本体への投稿は毎回新規スレッドになるため、スレッド単位では検知できない)
        private const int BurstWindowSeconds = 15;
        private const int BurstThresholdCount = 3;
        private const int DailyThresholdCount = 30;
        private readonly ConcurrentDictionary<ulong, UserActivityState> _userActivity = new();

        private class UserActivityState
        {
            public List<DateTime> RecentMessageTimestamps { get; } = new();
            public int DailyMessageCount { get; set; }
            public DateTime DailyCountResetAtJst { get; set; }
            public bool DailyThresholdAcknowledgedToday { get; set; }
            public bool ConfirmationPending { get; set; }
        }

        // クラウドモデル選択の自動失効(アカウント乗っ取り等への根本対策)。
        // 一定時間操作が無いスレッドは、選択中のモデルがクラウドなら自動でローカルの
        // デフォルトモデルに戻し、通知を送る
        private const int IdleRevertMinutes = 30;
        private readonly ConcurrentDictionary<ulong, DateTime> _lastActivityAt = new();
        private bool _idleRevertLoopStarted = false;

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
                _openAiApiKey = config["BotSettings:OpenAiApiKey"] ?? throw new Exception("OpenAiApiKeyが設定されていません。");
                _mcpListenUrl = config["BotSettings:McpListenUrl"] ?? throw new Exception("McpListenUrlが設定されていません。");

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
                await db.Database.ExecuteSqlRawAsync(@"
                    CREATE TABLE IF NOT EXISTS ThreadDriveFiles (
                        ThreadId INTEGER NOT NULL PRIMARY KEY,
                        DriveFileId TEXT NOT NULL,
                        DriveFileLink TEXT NOT NULL,
                        CreatedAt TEXT NOT NULL
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
            IAiProvider openAiProvider = new OpenAiProvider(_httpClient, _openAiApiKey);

            Func<ApiProvider, IAiProvider> providerFactory = providerType => providerType switch
            {
                ApiProvider.LmStudio => lmStudioProvider,
                ApiProvider.GoogleAiStudio => googleAiProvider,
                ApiProvider.VertexGemini => vertexGeminiProvider,
                ApiProvider.VertexGrok => vertexGrokProvider,
                ApiProvider.OpenAi => openAiProvider,
                _ => throw new ArgumentException($"未対応のプロバイダーです: {providerType}")
            };

            var attachmentProcessor = new AttachmentProcessor(_httpClient);
            var streamHandler = new StreamResponseHandler();
            var driveUploader = new GoogleDriveUploader(_httpClient, googleAdcTokenProvider, _vertexProjectId);

            _orchestrator = new ChatOrchestrator(providerFactory, attachmentProcessor, streamHandler, driveUploader);

            await _client.LoginAsync(TokenType.Bot, _discordToken);
            await _client.StartAsync();

            // MCPサーバー機能(Step 2: MCP SDK組み込み、接続疎通確認用。ツール本体は未実装)。
            // McpListenUrlはTailscale IPのみを指す想定(例: http://100.x.x.x:5100)。
            // 0.0.0.0で待ち受けるとLAN内からもアクセス可能になるため、appsettings.json側で
            // Tailscale IP限定のURLを設定する運用とする
            var webBuilder = WebApplication.CreateBuilder();
            webBuilder.WebHost.UseUrls(_mcpListenUrl);

            // StatefulForInitializeClients: initialize handshakeを使う現行クライアントには
            // セッション付きで応答しつつ、将来のセッションレスプロトコル(2026-07-28以降)の
            // クライアントも同一エンドポイントで受け付けられるようにする(単一プロセス構成
            // のためセッションアフィニティは問題にならない)
            webBuilder.Services.AddMcpServer()
                .WithHttpTransport(o => o.SessionMode = HttpServerSessionMode.StatefulForInitializeClients);

            var webApp = webBuilder.Build();
            webApp.MapGet("/health", () => Results.Ok("ok"));
            webApp.MapMcp("/mcp");

            await webApp.RunAsync();
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

            var promptCommand = new SlashCommandBuilder()
                .WithName("prompt")
                .WithDescription("このチャンネル・スレッドで使用するシステムプロンプトを選択します。");

            try
            {
                await _client.CreateGlobalApplicationCommandAsync(slashCommand.Build());
                await _client.CreateGlobalApplicationCommandAsync(effortCommand.Build());
                await _client.CreateGlobalApplicationCommandAsync(promptCommand.Build());
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

            if (!_idleRevertLoopStarted)
            {
                _idleRevertLoopStarted = true;
                _ = Task.Run(IdleCloudModelRevertLoopAsync);
            }
        }

        // 一定時間操作の無いスレッドのクラウドモデル選択を、セキュリティ対策として
        // ローカルのデフォルトモデルへ自動的に戻す
        private async Task IdleCloudModelRevertLoopAsync()
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));

            while (await timer.WaitForNextTickAsync())
            {
                await RevertIdleCloudModelsAsync();
            }
        }

        private async Task RevertIdleCloudModelsAsync()
        {
            DateTime now = DateTime.UtcNow;

            foreach (var kvp in _channelModels)
            {
                ulong contextId = kvp.Key;
                string modelId = kvp.Value;

                if (!ModelRegistry.AvailableModels.TryGetValue(modelId, out var modelMeta)) continue;
                if (modelMeta.Provider == ApiProvider.LmStudio) continue; // 既にローカルなら対象外

                if (!_lastActivityAt.TryGetValue(contextId, out var lastActivity)) continue;
                if ((now - lastActivity).TotalMinutes < IdleRevertMinutes) continue;

                _channelModels[contextId] = ModelRegistry.DefaultModelId;
                _lastActivityAt.TryRemove(contextId, out _);

                try
                {
                    if (_client.GetChannel(contextId) is IMessageChannel channel)
                    {
                        string defaultModelName = ModelRegistry.AvailableModels[ModelRegistry.DefaultModelId].DisplayName;
                        await channel.SendMessageAsync(
                            $"🔒 **[セキュリティ]** {IdleRevertMinutes}分間操作が無かったため、このスレッドの使用モデルを自動的に **{defaultModelName}**（ローカル）に戻しました。クラウドモデルを使い続ける場合は `/model` で再度選択してください。");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Warning] アイドル復帰通知の送信に失敗しました: {ex.Message}");
                }
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
                int solUsedToday = await GetOpenAiPoolUsageTodayAsync(OpenAiQuota.LargePoolModelIds);
                int lightUsedToday = await GetOpenAiPoolUsageTodayAsync(OpenAiQuota.LightPoolModelIds);

                bool costChanged = Math.Abs(totalCostUsd - _lastDisplayedCostUsd) >= 0.001;
                bool solChanged = solUsedToday != _lastDisplayedSolTokensToday;
                bool lightChanged = lightUsedToday != _lastDisplayedLightTokensToday;

                if (!costChanged && !solChanged && !lightChanged)
                {
                    return;
                }

                if (_client.GetChannel(_chatAiChannelId) is ITextChannel channel)
                {
                    string topic = $"💰 今月のクラウドAI利用額(推定): ${totalCostUsd:F2} / $10.00 (Google Developer Program枠) " +
                        $"| 🆓 OpenAI無料枠(本日) Sol {solUsedToday / 1000}K/{OpenAiQuota.LargePoolDailyCap / 1000}K " +
                        $"Terra+Luna {lightUsedToday / 1000}K/{OpenAiQuota.LightPoolDailyCap / 1000}K";
                    await channel.ModifyAsync(x => x.Topic = topic);
                    _lastDisplayedCostUsd = totalCostUsd;
                    _lastDisplayedSolTokensToday = solUsedToday;
                    _lastDisplayedLightTokensToday = lightUsedToday;
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

            // このトピック行はGoogle Developer Programの$10枠専用の表示のため、
            // OpenAI(別プログラム、日次無料枠は別途🆓表示)のUsageRecordsは合算対象外にする
            var googleProgramModelIds = ModelRegistry.AvailableModels.Values
                .Where(m => m.Provider == ApiProvider.VertexGemini || m.Provider == ApiProvider.VertexGrok)
                .Select(m => m.ModelId)
                .ToList();

            using var db = new ChatDbContext();
            return await db.UsageRecords
                .Where(u => u.CreatedAt >= startOfMonthUtc && googleProgramModelIds.Contains(u.ModelId))
                .SumAsync(u => u.EstimatedCostUsd);
        }

        // OpenAIデータ共有プログラムの日次無料枠、当日(JST暦日)の該当プール累計消費量
        private async Task<int> GetOpenAiPoolUsageTodayAsync(IReadOnlyList<string> poolModelIds)
        {
            DateTime nowJst = DateTime.UtcNow.AddHours(9);
            DateTime startOfDayUtc = new DateTime(nowJst.Year, nowJst.Month, nowJst.Day, 0, 0, 0, DateTimeKind.Utc).AddHours(-9);

            using var db = new ChatDbContext();
            return await db.UsageRecords
                .Where(u => u.CreatedAt >= startOfDayUtc && poolModelIds.Contains(u.ModelId))
                .SumAsync(u => u.PromptTokens + u.CompletionTokens);
        }

        // 送信前の事前ブロック判定。「当日の該当プール累計 + このモデルの最大出力トークン数」が
        // 日次無料枠上限を超える場合はfalse(実際に課金が発生するリクエストは絶対に送らない)
        private async Task<bool> HasOpenAiBudgetAsync(string modelId)
        {
            var pool = OpenAiQuota.GetPool(modelId);
            if (pool == null) return true;

            int usedToday = await GetOpenAiPoolUsageTodayAsync(pool.Value.PoolModelIds);

            int reserve = ModelRegistry.AvailableModels.TryGetValue(modelId, out var meta) && meta.MaxOutputTokens > 0
                ? meta.MaxOutputTokens
                : 8192;

            return usedToday + reserve <= pool.Value.Cap;
        }

        // /effortの選択メニュー用のラベル・説明文(ユーザー希望により英語表記。
        // 他のUIテキストは引き続き日本語のまま)
        private static (string Label, string Description) GetEffortDisplay(EffortLevel level) => level switch
        {
            EffortLevel.None => ("None", "No reasoning, fastest & cheapest (supported models only)"),
            EffortLevel.Low => ("Low", "Prioritizes speed. Good for everyday chat"),
            EffortLevel.Medium => ("Medium (Default)", "Balanced"),
            EffortLevel.High => ("High", "For complex problems. Takes longer"),
            EffortLevel.XHigh => ("XHigh", "For very complex problems. Significantly higher latency & cost (supported models only)"),
            _ => (level.ToString(), "")
        };

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
                ulong contextId = command.Channel.Id;
                string currentModelId = _channelModels.TryGetValue(contextId, out var cm) && ModelRegistry.AvailableModels.ContainsKey(cm)
                    ? cm
                    : ModelRegistry.DefaultModelId;
                var currentModel = ModelRegistry.AvailableModels[currentModelId];

                // モデルごとに実際に対応するエフォート段階だけを表示する(未指定モデルは
                // 従来通りLow/Medium/Highの3段階にフォールバック)
                IReadOnlyList<EffortLevel> supportedEfforts = currentModel.SupportedEfforts
                    ?? new[] { EffortLevel.Low, EffortLevel.Medium, EffortLevel.High };

                var menuBuilder = new SelectMenuBuilder()
                    .WithPlaceholder("エフォートを選択してください")
                    .WithCustomId("effort_select_menu");

                foreach (var level in supportedEfforts)
                {
                    var (label, description) = GetEffortDisplay(level);
                    menuBuilder.AddOption(label, level.ToString(), description);
                }

                var builder = new ComponentBuilder().WithSelectMenu(menuBuilder);
                await command.RespondAsync($"👇 `{currentModel.DisplayName}`のエフォートを選択してください（この場所での会話に適用されます。ローカルモデルには影響しません）:", components: builder.Build());
            }
            else if (command.Data.Name == "prompt")
            {
                var menuBuilder = new SelectMenuBuilder()
                    .WithPlaceholder("システムプロンプトを選択してください")
                    .WithCustomId("prompt_select_menu");

                foreach (var kvp in SystemPromptRegistry.AvailablePrompts)
                {
                    menuBuilder.AddOption(kvp.Value.DisplayName, kvp.Key, kvp.Value.Description);
                }

                var builder = new ComponentBuilder().WithSelectMenu(menuBuilder);
                await command.RespondAsync("👇 使用するシステムプロンプトを選択してください（この場所での会話に適用されます）:", components: builder.Build());
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
            else if (component.Data.CustomId == "prompt_select_menu")
            {
                string selectedPromptId = component.Data.Values.First();
                ulong contextId = component.Channel.Id;
                _channelPrompts[contextId] = selectedPromptId;

                string promptName = SystemPromptRegistry.AvailablePrompts[selectedPromptId].DisplayName;
                await component.RespondAsync($"✅ この場所でのシステムプロンプトを **{promptName}** に変更しました。");
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

        // 連投検知。トリップした場合はConfirmationPendingを立て、以降のメッセージを
        // ユーザーがボタンで解除するまで保留する(解除方法はResetUserActivity参照)
        private (bool needsConfirmation, string? reason) RecordActivityAndCheck(ulong userId)
        {
            var state = _userActivity.GetOrAdd(userId, _ => new UserActivityState());

            lock (state)
            {
                if (state.ConfirmationPending)
                {
                    return (true, "連続送信を検知したため一時停止中です");
                }

                DateTime now = DateTime.UtcNow;

                // 日次カウントのリセット判定(JST暦日基準)
                DateTime nowJst = now.AddHours(9);
                DateTime todayStartJst = new DateTime(nowJst.Year, nowJst.Month, nowJst.Day, 0, 0, 0, DateTimeKind.Utc);
                if (state.DailyCountResetAtJst != todayStartJst)
                {
                    state.DailyMessageCount = 0;
                    state.DailyThresholdAcknowledgedToday = false;
                    state.DailyCountResetAtJst = todayStartJst;
                }

                state.RecentMessageTimestamps.Add(now);
                state.RecentMessageTimestamps.RemoveAll(t => (now - t).TotalSeconds > BurstWindowSeconds);
                state.DailyMessageCount++;

                if (state.RecentMessageTimestamps.Count > BurstThresholdCount)
                {
                    state.ConfirmationPending = true;
                    return (true, $"{BurstWindowSeconds}秒以内に{BurstThresholdCount}通を超える送信を検知しました");
                }

                if (!state.DailyThresholdAcknowledgedToday && state.DailyMessageCount > DailyThresholdCount)
                {
                    state.ConfirmationPending = true;
                    return (true, $"本日の送信数が{DailyThresholdCount}通を超えました");
                }

                return (false, null);
            }
        }

        private void ResetUserActivity(ulong userId)
        {
            if (_userActivity.TryGetValue(userId, out var state))
            {
                lock (state)
                {
                    state.ConfirmationPending = false;
                    state.RecentMessageTimestamps.Clear();
                    // 日次上限は「実際に超過していた場合のみ」確認済み扱いにする。
                    // バースト検知起因の解除で日次チェックまで無効化してしまわないようにする
                    if (state.DailyMessageCount > DailyThresholdCount)
                    {
                        state.DailyThresholdAcknowledgedToday = true;
                    }
                }
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

            var (needsConfirmation, warningReason) = RecordActivityAndCheck(userMessage.Author.Id);
            if (needsConfirmation)
            {
                var confirmBuilder = new ComponentBuilder()
                    .WithButton("✅ 続行する", $"spam_confirm_{userMessage.Author.Id}", ButtonStyle.Primary);
                await userMessage.Channel.SendMessageAsync(
                    $"⚠️ **[セキュリティ確認]** {warningReason}。\nアカウントの不正利用防止のため、一時的に送信を保留しています。続行するには下のボタンを押してください（このメッセージ自体は処理されません。押した後、あらためて送信してください）。",
                    components: confirmBuilder.Build());
                return;
            }

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

            // クラウドモデル自動失効(アイドルタイマー)のための最終活動時刻を更新
            _lastActivityAt[contextId] = DateTime.UtcNow;

            // Google Drive記録ファイルの確保・ピン留めは、このスレッドで最初に送る
            // ステータス/応答メッセージより前に行う(スレッド最上部に来るようにするため)
            await _orchestrator.EnsureThreadDriveFileAsync(contextId, targetThread, CancellationToken.None);

            // 安全なフォールバックロジック (KeyNotFoundExceptionの完全防止)
            if (!_channelModels.TryGetValue(contextId, out string? targetModelId) || targetModelId == null || !ModelRegistry.AvailableModels.ContainsKey(targetModelId))
            {
                targetModelId = ModelRegistry.DefaultModelId;
                _channelModels[contextId] = targetModelId;
            }

            var targetModel = ModelRegistry.AvailableModels[targetModelId];

            EffortLevel effort = _channelEfforts.TryGetValue(contextId, out var customEffort) ? customEffort : EffortLevel.Medium;

            string promptId = _channelPrompts.TryGetValue(contextId, out var customPromptId) && SystemPromptRegistry.AvailablePrompts.ContainsKey(customPromptId)
                ? customPromptId
                : SystemPromptRegistry.DefaultPromptId;
            string systemPrompt = SystemPromptRegistry.AvailablePrompts[promptId].Prompt;

            if (targetModel.Provider == ApiProvider.LmStudio)
            {
                bool isOnline = await CheckLmStudioOnlineAsync();
                if (!isOnline)
                {
                    await targetThread.SendMessageAsync("❌ **[通信失敗]** デスクトップPCのAIエンジンがスリープ中、または未起動です。\n💡 `/model` コマンドを使用して、別のモデルに切り替えてください。");
                    return;
                }
            }
            else if (targetModel.Provider == ApiProvider.OpenAi)
            {
                bool hasBudget = await HasOpenAiBudgetAsync(targetModel.ModelId);
                if (!hasBudget)
                {
                    await targetThread.SendMessageAsync($"❌ **[無料枠超過]** 本日の`{targetModel.DisplayName}`無料枠(データ共有プログラム)を使い切りました。課金を避けるため送信を中止しました。\n💡 `/model` コマンドでローカルモデル、または別のモデルに切り替えてください。");
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
            if (component.Data.CustomId.StartsWith("spam_confirm_"))
            {
                string idString = component.Data.CustomId.Replace("spam_confirm_", "");
                if (ulong.TryParse(idString, out ulong userId))
                {
                    // 本人以外が解除できないようにする
                    if (component.User.Id != userId)
                    {
                        await component.RespondAsync("これはあなた宛の確認ではありません。", ephemeral: true);
                        return;
                    }

                    ResetUserActivity(userId);
                    await component.RespondAsync("✅ 制限を解除しました。続けてメッセージを送信してください。", ephemeral: true);
                }
                return;
            }

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
