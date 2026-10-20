using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DiscordAIBot
{
    // Open WebUI等のOpenAI互換クライアント向けの窓口(/v1/models, /v1/chat/completions)。
    // クライアントから各APIやLM Studioへ直接つながせず、MCPと同じMcpModelCall経由で呼ぶことで、
    // 無料枠の事前ブロック・コスト記録(Source=webui)・LM Studio要求の直列化をDiscord/MCPと共有する。
    // 会話履歴はクライアント側が持ち、毎回messagesに会話全体が入って届く前提のステートレス実装(provisioning/076)
    public static class OpenAiCompatEndpoints
    {
        private const string Source = "webui";

        // Vertex経由のGeminiは思考が終わるまで何も流さず、Argonは200秒近く無言のことがある(075)。
        // クライアントやプロキシの無通信タイムアウトで切られないよう、この間隔でSSEのコメント行を送る
        private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(15);

        // 1リクエストで受け付ける画像の枚数とサイズ(デコード後)。サイズはDiscord添付と同じ10MB
        private const int MaxImagesPerRequest = 5;
        private const long MaxImageBytes = 10 * 1024 * 1024;

        // ChatOrchestratorの1枚あたりの見積もり(ImageTokenBudgetOffset)と同じ値で、履歴を削る量を決める
        private const int ImageTokenBudget = 1000;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        // apiKeyが空の場合は呼び出し側で/v1を公開しない
        public static void Map(WebApplication app, string apiKey)
        {
            byte[] expectedKey = Encoding.UTF8.GetBytes(apiKey);

            var group = app.MapGroup("/v1");
            group.AddEndpointFilter(async (context, next) =>
            {
                string header = context.HttpContext.Request.Headers.Authorization.ToString();
                byte[] presented = Encoding.UTF8.GetBytes(header.StartsWith("Bearer ", StringComparison.Ordinal) ? header[7..] : "");
                if (!CryptographicOperations.FixedTimeEquals(presented, expectedKey))
                {
                    return Error(StatusCodes.Status401Unauthorized, "APIキーが正しくありません。", "invalid_request_error", "invalid_api_key");
                }
                return await next(context);
            });

            group.MapGet("/models", () => Results.Json(new
            {
                @object = "list",
                data = ModelRegistry.AvailableModels.Values.Select(m => new
                {
                    id = m.ModelId,
                    @object = "model",
                    created = 0,
                    owned_by = m.Provider.ToString(),
                    name = m.DisplayName
                })
            }, JsonOptions));

            group.MapPost("/chat/completions", HandleChatCompletionsAsync);
        }

        private static IResult Error(int status, string message, string type, string? code = null) =>
            Results.Json(new { error = new { message, type, code } }, JsonOptions, statusCode: status);

        private sealed class BadRequest : Exception
        {
            public BadRequest(string message) : base(message) { }
        }

        private static async Task HandleChatCompletionsAsync(HttpContext http)
        {
            var services = http.RequestServices;
            var providerFactory = services.GetRequiredService<Func<ApiProvider, IAiProvider>>();
            var hasOpenAiBudgetAsync = services.GetRequiredService<Func<string, int, Task<bool>>>();
            var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("OpenAiCompat");
            string callerIp = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            CancellationToken cancellationToken = http.RequestAborted;

            JsonNode? body;
            try
            {
                body = await JsonNode.ParseAsync(http.Request.Body, cancellationToken: cancellationToken);
            }
            catch (JsonException)
            {
                await Error(400, "リクエスト本文がJSONとして解釈できません。", "invalid_request_error").ExecuteAsync(http);
                return;
            }

            ModelMetadata modelMeta;
            AiRequest request;
            int estimatedPromptTokens;
            int trimmedTurns;
            bool stream;
            bool includeUsage;
            try
            {
                string? modelId = body?["model"]?.GetValue<string>();
                if (string.IsNullOrEmpty(modelId) || !ModelRegistry.AvailableModels.TryGetValue(modelId, out modelMeta))
                {
                    string validIds = string.Join(", ", ModelRegistry.AvailableModels.Keys);
                    await Error(404, $"未知のモデルIDです: '{modelId}'。有効な値: {validIds}", "invalid_request_error", "model_not_found").ExecuteAsync(http);
                    return;
                }

                stream = body!["stream"]?.GetValue<bool>() ?? false;
                includeUsage = body["stream_options"]?["include_usage"]?.GetValue<bool>() ?? false;
                (request, estimatedPromptTokens, trimmedTurns) = await BuildRequestAsync(body, modelMeta, cancellationToken);
            }
            catch (BadRequest ex)
            {
                await Error(400, ex.Message, "invalid_request_error").ExecuteAsync(http);
                return;
            }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException)
            {
                // JsonNode.GetValue<T>は型が合わないと例外になる(例: streamに文字列)
                await Error(400, $"リクエストの項目の型が不正です: {ex.Message}", "invalid_request_error").ExecuteAsync(http);
                return;
            }

            logger.LogInformation(
                "WebUI 開始: caller={CallerIp} model={ModelId} effort={Effort} stream={Stream} historyTurns={HistoryTurns} trimmedTurns={TrimmedTurns} images={Images} estimatedPromptTokens={EstimatedPromptTokens}",
                callerIp, modelMeta.ModelId, request.Effort, stream, request.History.Count, trimmedTurns, request.CurrentImagesBase64.Count, estimatedPromptTokens);

            // 無料枠の事前ブロックはDiscord・MCPと同じロジック(履歴・画像込みのプロンプトを安全側に見積もる、060)
            int conservativePromptTokens = CostEstimator.EstimatePromptTokensConservatively(estimatedPromptTokens, request.CurrentImagesBase64.Count);
            if (modelMeta.Provider == ApiProvider.OpenAi && !await hasOpenAiBudgetAsync(modelMeta.ModelId, conservativePromptTokens))
            {
                await Error(429,
                    $"{modelMeta.DisplayName} は本日の無料枠の残りでは、今回のリクエスト(履歴・画像込みの推定プロンプト{conservativePromptTokens}トークン" +
                    $"+出力上限{modelMeta.MaxOutputTokens}トークン)を送れないため使用できません。課金を避けるため送信を中止しました。" +
                    "新しいチャットで履歴を減らすか、別のモデルを選んでください。",
                    "insufficient_quota", "insufficient_quota").ExecuteAsync(http);
                return;
            }

            var provider = providerFactory(modelMeta.Provider);
            string completionId = $"chatcmpl-{Guid.NewGuid():N}";
            long created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            if (stream)
            {
                await StreamAsync(http, provider, request, modelMeta, conservativePromptTokens, completionId, created, includeUsage, logger, callerIp, stopwatch);
                return;
            }

            McpModelCall.Result result;
            try
            {
                result = await McpModelCall.RunAsync(provider, request, modelMeta, conservativePromptTokens, null, cancellationToken, Source);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                logger.LogInformation("WebUI 切断: caller={CallerIp} model={ModelId} durationMs={DurationMs}", callerIp, modelMeta.ModelId, stopwatch.ElapsedMilliseconds);
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning("WebUI 失敗: caller={CallerIp} model={ModelId} error={Error}", callerIp, modelMeta.ModelId, ex.Message);
                await Error(502, $"モデル呼び出しに失敗しました: {ex.Message}", "upstream_error").ExecuteAsync(http);
                return;
            }

            LogCompleted(logger, callerIp, modelMeta, stopwatch, result);

            if (string.IsNullOrWhiteSpace(result.Answer))
            {
                await Error(502, McpModelCall.BuildEmptyAnswerMessage(result, modelMeta), "upstream_error").ExecuteAsync(http);
                return;
            }

            string? notice = McpModelCall.BuildIncompleteNotice(result, modelMeta);
            string content = notice is null ? result.Answer : $"{result.Answer}\n\n[⚠ {notice}]";
            await Results.Json(new
            {
                id = completionId,
                @object = "chat.completion",
                created,
                model = modelMeta.ModelId,
                choices = new[]
                {
                    new { index = 0, message = new { role = "assistant", content }, finish_reason = MapFinishReason(result) }
                },
                usage = BuildUsage(result)
            }, JsonOptions).ExecuteAsync(http);
        }

        private static async Task StreamAsync(
            HttpContext http, IAiProvider provider, AiRequest request, ModelMetadata modelMeta, int conservativePromptTokens,
            string completionId, long created, bool includeUsage, ILogger logger, string callerIp, System.Diagnostics.Stopwatch stopwatch)
        {
            CancellationToken cancellationToken = http.RequestAborted;
            http.Response.ContentType = "text/event-stream; charset=utf-8";
            http.Response.Headers.CacheControl = "no-cache";
            http.Response.Headers["X-Accel-Buffering"] = "no";

            // チャンクの中継とキープアライブが同時に書き込まないよう直列化する
            var writeLock = new SemaphoreSlim(1, 1);
            async Task WriteRawAsync(string text, CancellationToken token)
            {
                await writeLock.WaitAsync(token);
                try
                {
                    await http.Response.WriteAsync(text, token);
                    await http.Response.Body.FlushAsync(token);
                }
                finally
                {
                    writeLock.Release();
                }
            }
            Task WriteChunkAsync(object delta, string? finishReason, object? usage, CancellationToken token)
            {
                var chunk = new
                {
                    id = completionId,
                    @object = "chat.completion.chunk",
                    created,
                    model = modelMeta.ModelId,
                    choices = usage is null
                        ? new object[] { new { index = 0, delta, finish_reason = finishReason } }
                        : Array.Empty<object>(),
                    usage
                };
                return WriteRawAsync($"data: {JsonSerializer.Serialize(chunk, JsonOptions)}\n\n", token);
            }

            using var keepAliveCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task keepAliveLoop = Task.Run(async () =>
            {
                using var timer = new PeriodicTimer(KeepAliveInterval);
                try
                {
                    while (await timer.WaitForNextTickAsync(keepAliveCts.Token))
                    {
                        await WriteRawAsync(": keep-alive\n\n", keepAliveCts.Token);
                    }
                }
                catch (OperationCanceledException)
                {
                    // ストリームの終了、またはクライアントの切断で停止する
                }
            });

            try
            {
                await WriteChunkAsync(new { role = "assistant", content = "" }, null, null, cancellationToken);

                McpModelCall.Result? result = null;
                string? failure = null;
                try
                {
                    // LM Studioのreasoningモデルの思考はreasoning_contentとして中継する(Open WebUIは折りたたみ表示する)
                    result = await McpModelCall.RunAsync(provider, request, modelMeta, conservativePromptTokens, null, cancellationToken, Source,
                        (chunk, token) => string.IsNullOrEmpty(chunk.TextDelta)
                            ? Task.CompletedTask
                            : WriteChunkAsync(chunk.IsReasoning
                                ? new { reasoning_content = chunk.TextDelta }
                                : (object)new { content = chunk.TextDelta }, null, null, token));
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    // ヘッダー送信後はHTTPステータスでエラーを返せないため、本文としてエラーを伝える
                    failure = ex.Message;
                    logger.LogWarning("WebUI 失敗: caller={CallerIp} model={ModelId} error={Error}", callerIp, modelMeta.ModelId, ex.Message);
                }

                string? tail = null;
                if (failure != null)
                {
                    tail = $"⚠ モデル呼び出しに失敗しました: {failure}";
                }
                else if (string.IsNullOrWhiteSpace(result!.Answer))
                {
                    tail = $"⚠ {McpModelCall.BuildEmptyAnswerMessage(result, modelMeta)}";
                }
                else if (McpModelCall.BuildIncompleteNotice(result, modelMeta) is { } notice)
                {
                    tail = $"\n\n[⚠ {notice}]";
                }

                if (result != null) LogCompleted(logger, callerIp, modelMeta, stopwatch, result);

                keepAliveCts.Cancel();
                await keepAliveLoop;

                if (tail != null) await WriteChunkAsync(new { content = tail }, null, null, cancellationToken);
                await WriteChunkAsync(new { }, result != null ? MapFinishReason(result) : "stop", null, cancellationToken);
                if (includeUsage && result != null && BuildUsage(result) is { } usage)
                {
                    await WriteChunkAsync(new { }, null, usage, cancellationToken);
                }
                await WriteRawAsync("data: [DONE]\n\n", cancellationToken);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                // クライアントの切断(Open WebUIの停止ボタン等)。切断後の書き込みはOperationCanceledException以外
                // (IOException等)になることもあるため、型ではなく切断状態で判定する。使用量はMcpModelCall側で記録済み
                logger.LogInformation("WebUI 切断: caller={CallerIp} model={ModelId} durationMs={DurationMs}", callerIp, modelMeta.ModelId, stopwatch.ElapsedMilliseconds);
            }
            finally
            {
                keepAliveCts.Cancel();
                await keepAliveLoop;
            }
        }

        private static void LogCompleted(ILogger logger, string callerIp, ModelMetadata modelMeta, System.Diagnostics.Stopwatch stopwatch, McpModelCall.Result result) =>
            logger.LogInformation(
                "WebUI 完了: caller={CallerIp} model={ModelId} durationMs={DurationMs} finishReason={FinishReason} answerChars={AnswerChars} promptTokens={PromptTokens} completionTokens={CompletionTokens} reasoningTokens={ReasoningTokens} costUsd={CostUsd}",
                callerIp, modelMeta.ModelId, stopwatch.ElapsedMilliseconds, result.FinishReason ?? "(none)", result.Answer.Length,
                result.PromptTokens, result.CompletionTokens, result.ReasoningTokens, result.EstimatedCostUsd);

        private static string MapFinishReason(McpModelCall.Result result) =>
            result.Completion == McpModelCall.Completion.Truncated ? "length" : "stop";

        // ローカルモデル等usageを返さないプロバイダーではnull(usageを省く)
        private static object? BuildUsage(McpModelCall.Result result)
        {
            if (!result.PromptTokens.HasValue) return null;
            int prompt = result.PromptTokens.Value;
            int completion = result.CompletionTokens ?? 0;
            return new
            {
                prompt_tokens = prompt,
                completion_tokens = completion,
                total_tokens = prompt + completion,
                completion_tokens_details = new { reasoning_tokens = result.ReasoningTokens ?? 0 }
            };
        }

        // messagesを AiRequest に変換する。戻り値の estimatedPromptTokens は削った後の履歴・システムプロンプト・
        // 今回の入力の合計(TokenManagerの推定値、画像は含まない)
        private static async Task<(AiRequest Request, int EstimatedPromptTokens, int TrimmedTurns)> BuildRequestAsync(
            JsonNode body, ModelMetadata modelMeta, CancellationToken cancellationToken)
        {
            if (body["messages"] is not JsonArray messages || messages.Count == 0)
            {
                throw new BadRequest("messagesが空です。");
            }

            var systemTexts = new List<string>();
            var turns = new List<(string Role, string Text, List<string> ImageUrls)>();
            foreach (var message in messages)
            {
                string role = message?["role"]?.GetValue<string>() ?? "";
                var (text, imageUrls) = ExtractContent(message?["content"]);
                switch (role)
                {
                    case "system":
                    case "developer":
                        if (!string.IsNullOrWhiteSpace(text)) systemTexts.Add(text);
                        break;
                    case "user":
                    case "assistant":
                        turns.Add((role, text, imageUrls));
                        break;
                    // tool等は使わない(ツール呼び出しには非対応)
                }
            }

            if (turns.Count == 0 || turns[^1].Role != "user")
            {
                throw new BadRequest("最後のメッセージがuserではありません。");
            }

            var current = turns[^1];
            if (current.ImageUrls.Count > 0 && !modelMeta.IsVlm)
            {
                throw new BadRequest($"{modelMeta.DisplayName} は画像入力に対応していません。画像対応のモデルを選んでください。");
            }
            if (current.ImageUrls.Count > MaxImagesPerRequest)
            {
                throw new BadRequest($"画像は1回に{MaxImagesPerRequest}枚までです。");
            }

            var images = new List<string>();
            foreach (var url in current.ImageUrls)
            {
                images.Add(await DecodeImageAsync(url, cancellationToken));
            }

            // 履歴中の画像は送らない(Discordと同様、今回の入力の画像のみ)。画像だけの発言が空にならないよう印を残す
            var history = turns.Take(turns.Count - 1)
                .Select(t => new ChatTurn(t.Role, t.ImageUrls.Count > 0 ? $"{t.Text}\n[画像]".Trim() : t.Text))
                .Where(t => !string.IsNullOrWhiteSpace(t.Content))
                .ToList();

            string systemPrompt = string.Join("\n\n", systemTexts);
            int fixedTokens = TokenManager.CountTokens(systemPrompt) + TokenManager.CountTokens(current.Text) + images.Count * ImageTokenBudget;
            int budget = modelMeta.ContextWindow - 2000; // Discord・MCPと同じ安全マージン
            if (budget > 0 && fixedTokens > budget)
            {
                throw new BadRequest(
                    $"今回のメッセージ(システムプロンプト・画像を含む推定{fixedTokens}トークン)だけで、{modelMeta.DisplayName} の" +
                    $"コンテキスト上限(安全マージン込み実質{budget}トークン)を超えています。");
            }

            // 新しい発言から順に、上限に収まるところまで履歴に残す(古い発言から削る)
            var historyTokens = history.Select(t => TokenManager.CountTokens(t.Content)).ToList();
            int keepFrom = history.Count;
            int used = fixedTokens;
            while (keepFrom > 0 && (budget <= 0 || used + historyTokens[keepFrom - 1] <= budget))
            {
                keepFrom--;
                used += historyTokens[keepFrom];
            }

            double temperature = body["temperature"]?.GetValue<double>() ?? 0.7;

            var request = new AiRequest(
                SystemPrompt: systemPrompt,
                ModelId: modelMeta.ModelId,
                History: history.Skip(keepFrom).ToList(),
                CurrentUserText: current.Text,
                CurrentImagesBase64: images,
                Temperature: temperature,
                MaxOutputTokens: modelMeta.MaxOutputTokens,
                Effort: ParseEffort(body["reasoning_effort"]?.GetValue<string>())
            );
            return (request, used - images.Count * ImageTokenBudget, keepFrom);
        }

        // contentは文字列か、{type:"text"}/{type:"image_url"}のパーツ配列
        private static (string Text, List<string> ImageUrls) ExtractContent(JsonNode? content)
        {
            var imageUrls = new List<string>();
            if (content is JsonValue value)
            {
                return (value.GetValue<string>(), imageUrls);
            }
            if (content is not JsonArray parts)
            {
                return ("", imageUrls);
            }

            var texts = new List<string>();
            foreach (var part in parts)
            {
                switch (part?["type"]?.GetValue<string>())
                {
                    case "text":
                        texts.Add(part["text"]?.GetValue<string>() ?? "");
                        break;
                    case "image_url":
                        // {image_url:{url:"..."}} が仕様だが、文字列で直接入れるクライアントもある
                        string? url = part["image_url"] is JsonObject obj ? obj["url"]?.GetValue<string>() : part["image_url"]?.GetValue<string>();
                        if (!string.IsNullOrEmpty(url)) imageUrls.Add(url);
                        break;
                }
            }
            return (string.Join("\n", texts), imageUrls);
        }

        // data URIのみ受け付ける。外部URLはハブ側から取りに行かない(任意のURLへアクセスさせないため)
        private static async Task<string> DecodeImageAsync(string url, CancellationToken cancellationToken)
        {
            int comma = url.IndexOf(',');
            if (!url.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) || comma < 0 || !url[..comma].EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
            {
                throw new BadRequest("画像はdata URI(base64)形式のみ対応しています。");
            }
            string base64 = url[(comma + 1)..];
            if ((long)base64.Length * 3 / 4 > MaxImageBytes)
            {
                throw new BadRequest($"画像のサイズが制限({MaxImageBytes / 1024 / 1024}MB)を超えています。");
            }

            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(base64);
            }
            catch (FormatException)
            {
                throw new BadRequest("画像のbase64が不正です。");
            }

            try
            {
                using var stream = new MemoryStream(bytes);
                return await AttachmentProcessor.NormalizeImageToJpegBase64Async(stream, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new BadRequest($"画像を読み込めませんでした: {ex.Message}");
            }
        }

        // OpenAIのreasoning_effort表記をEffortLevelに対応させる。未指定はDiscord・MCPと同じMedium。
        // モデルが対応しない段階は各Provider(OpenAIはModelRegistry.ClampEffort)が丸める
        private static EffortLevel ParseEffort(string? value) => value?.ToLowerInvariant() switch
        {
            null or "" => EffortLevel.Medium,
            "none" or "minimal" => EffortLevel.None,
            "low" => EffortLevel.Low,
            "medium" => EffortLevel.Medium,
            "high" => EffortLevel.High,
            "xhigh" or "max" => EffortLevel.XHigh,
            _ => throw new BadRequest($"未知のreasoning_effortです: '{value}'。有効な値: none, minimal, low, medium, high, xhigh")
        };
    }
}
