using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace DiscordAIBot
{
    // OpenAI Chat Completions APIを直接呼び出すProvider(gpt-5.6 Sol/Terra/Luna用)。
    // 実機検証済み(2026-09-09):
    // - 通常の max_tokens は 400 Bad Request で拒否される。max_completion_tokens が必須
    // - reasoning_effort はVertex Grokと同じフラット文字列パラメータ(low/medium/high)で
    //   通る。ネストされた reasoning:{effort:...} は Unknown parameter で拒否される
    //   (Responses API専用の形式であり、Chat Completionsでは使えない)
    // - ストリーミングでusageを取得するには stream_options.include_usage=true が必須
    //   (指定しないとusageチャンクが一切来ない。Vertex Grok経由との相違点)
    // - usage.completion_tokens には reasoning_tokens が既に含まれる(内訳として
    //   completion_tokens_details.reasoning_tokens が別途返るが、加算対象ではなく内訳)。
    //   Gemini/Grok(Vertex)とは異なり、コスト計算時に completion_tokens に
    //   reasoning_tokens を再度加算してはならない(二重計上になる) → CostEstimator側で対応
    // - レスポンスに無料枠(データ共有プログラム)消化を示すフィールドは見当たらなかった。
    //   日次無料枠の管理はOpenAiQuotaによる自前集計(事前ブロック)のみに依拠する
    // - SSEの構造(finish_reasonチャンクとusageチャンクが別々に届く)はVertex Grokと同じ
    //   ため、そのマージ処理をそのまま流用している
    public class OpenAiProvider : IAiProvider
    {
        private const string EndpointUrl = "https://api.openai.com/v1/chat/completions";

        private readonly HttpClient _httpClient;
        private readonly string _apiKey;

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public OpenAiProvider(HttpClient httpClient, string apiKey)
        {
            _httpClient = httpClient;
            _apiKey = apiKey;
        }

        public async IAsyncEnumerable<StreamChunk> StreamChatAsync(
            AiRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var messages = new List<OpenAiMessageDto>();

            if (!string.IsNullOrEmpty(request.SystemPrompt))
            {
                messages.Add(new OpenAiMessageDto("system", request.SystemPrompt));
            }

            foreach (var turn in request.History)
            {
                messages.Add(new OpenAiMessageDto(turn.Role, turn.Content));
            }

            // 画像が添付されている場合はcontentをパーツ配列(text + image_url)にする。
            // 無い場合は通常の文字列contentにする(単純化・トークン節約)
            if (request.CurrentImagesBase64 != null && request.CurrentImagesBase64.Count > 0)
            {
                var parts = new List<object>
                {
                    new OpenAiTextPartDto("text", request.CurrentUserText)
                };
                foreach (var base64 in request.CurrentImagesBase64)
                {
                    parts.Add(new OpenAiImagePartDto("image_url", new OpenAiImageUrlDto($"data:image/jpeg;base64,{base64}")));
                }
                messages.Add(new OpenAiMessageDto("user", parts));
            }
            else
            {
                messages.Add(new OpenAiMessageDto("user", request.CurrentUserText));
            }

            // 実機検証済み(2026-09-09): none/low/medium/high/xhighの5段階は
            // Sol/Terra/Luna全モデル共通で受理される。maxのみ400で拒否される
            // (モデル仕様ページの記載はmaxまで対応と誤っていた)
            string reasoningEffort = request.Effort switch
            {
                EffortLevel.None => "none",
                EffortLevel.Low => "low",
                EffortLevel.High => "high",
                EffortLevel.XHigh => "xhigh",
                _ => "medium"
            };

            // gpt-5.6系はtemperatureのデフォルト値(1)以外を受け付けない(実機で400確認済み)
            // ため、Vertex Gemini/Grokと異なりtemperatureパラメータ自体を送らない
            var requestBodyObj = new OpenAiRequestDto(
                Model: request.ModelId,
                Messages: messages,
                MaxCompletionTokens: request.MaxOutputTokens > 0 ? request.MaxOutputTokens : 8192,
                Stream: true,
                StreamOptions: new OpenAiStreamOptionsDto(true),
                ReasoningEffort: reasoningEffort
            );

            string requestJson = JsonSerializer.Serialize(requestBodyObj, _jsonOptions);
            using var httpContent = new StringContent(requestJson, Encoding.UTF8, "application/json");
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, EndpointUrl) { Content = httpContent };
            httpRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);

            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    string errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                    Console.WriteLine($"[Error] OpenAI API Request Failed: {(int)response.StatusCode} {response.ReasonPhrase}");
                    Console.WriteLine($"[Error] Response Details: {errorContent}");

                    if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    {
                        throw new AiRateLimitException(response.StatusCode, "OpenAI APIのレートリミットに到達しました。");
                    }
                }

                response.EnsureSuccessStatusCode();
            }
            catch (HttpRequestException)
            {
                throw;
            }
            catch (AiRateLimitException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new Exception($"OpenAI API 通信エラー: {ex.Message}", ex);
            }

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);

            // finish_reasonを含むチャンクと、usage(トークン内訳)を含むchoices空のチャンクが
            // 別々に届く(Vertex Grokと同じ形、実機検証済み)。StreamResponseHandler側は
            // FinishReasonが非nullのチャンクを見た時点で読み取りを終了するため、finish_reason
            // は一旦保留し、直後のusageチャンクとマージした1チャンクとしてyieldする
            string? pendingFinishReason = null;

            while (true)
            {
                string? line = await reader.ReadLineAsync(cancellationToken);

                if (line == null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (line.StartsWith("data: [DONE]")) break;
                if (!line.StartsWith("data: ")) continue;

                string dataJson = line.Substring(6);

                JsonDocument doc;
                try
                {
                    doc = JsonDocument.Parse(dataJson);
                }
                catch (JsonException)
                {
                    continue;
                }

                using (doc)
                {
                    var root = doc.RootElement;

                    int? promptTokens = null;
                    int? completionTokens = null;
                    int? reasoningTokens = null;
                    if (root.TryGetProperty("usage", out var usageElement) && usageElement.ValueKind == JsonValueKind.Object)
                    {
                        if (usageElement.TryGetProperty("prompt_tokens", out var ptElement) && ptElement.ValueKind == JsonValueKind.Number)
                        {
                            promptTokens = ptElement.GetInt32();
                        }
                        if (usageElement.TryGetProperty("completion_tokens", out var ctElement) && ctElement.ValueKind == JsonValueKind.Number)
                        {
                            completionTokens = ctElement.GetInt32();
                        }
                        if (usageElement.TryGetProperty("completion_tokens_details", out var ctdElement) &&
                            ctdElement.TryGetProperty("reasoning_tokens", out var rtElement) && rtElement.ValueKind == JsonValueKind.Number)
                        {
                            reasoningTokens = rtElement.GetInt32();
                        }
                    }

                    if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
                    {
                        if (promptTokens.HasValue || completionTokens.HasValue || reasoningTokens.HasValue)
                        {
                            yield return new StreamChunk(null, false, pendingFinishReason ?? "stop", promptTokens, completionTokens, reasoningTokens);
                            pendingFinishReason = null;
                        }
                        continue;
                    }

                    var choice = choices[0];
                    var delta = choice.GetProperty("delta");

                    string? finishReason = null;
                    if (choice.TryGetProperty("finish_reason", out var frElement) && frElement.ValueKind == JsonValueKind.String)
                    {
                        finishReason = frElement.GetString();
                    }

                    string? textDelta = null;
                    if (delta.TryGetProperty("content", out var contentElement) && contentElement.ValueKind == JsonValueKind.String)
                    {
                        textDelta = contentElement.GetString();
                    }

                    if (finishReason != null)
                    {
                        pendingFinishReason = finishReason;
                        if (!string.IsNullOrEmpty(textDelta))
                        {
                            yield return new StreamChunk(textDelta, false, null);
                        }
                        continue;
                    }

                    if (string.IsNullOrEmpty(textDelta))
                    {
                        continue;
                    }

                    yield return new StreamChunk(textDelta, false, null);
                }
            }

            if (pendingFinishReason != null)
            {
                yield return new StreamChunk(null, false, pendingFinishReason);
            }
        }

        private record OpenAiRequestDto(
            [property: JsonPropertyName("model")] string Model,
            [property: JsonPropertyName("messages")] IReadOnlyList<OpenAiMessageDto> Messages,
            [property: JsonPropertyName("max_completion_tokens")] int MaxCompletionTokens,
            [property: JsonPropertyName("stream")] bool Stream,
            [property: JsonPropertyName("stream_options")] OpenAiStreamOptionsDto StreamOptions,
            [property: JsonPropertyName("reasoning_effort")] string ReasoningEffort
        );

        private record OpenAiStreamOptionsDto(
            [property: JsonPropertyName("include_usage")] bool IncludeUsage
        );

        // Content は文字列(通常時)、または List<object>(画像添付時、text/image_urlパーツの配列)
        private record OpenAiMessageDto(
            [property: JsonPropertyName("role")] string Role,
            [property: JsonPropertyName("content")] object Content
        );

        private record OpenAiTextPartDto(
            [property: JsonPropertyName("type")] string Type,
            [property: JsonPropertyName("text")] string Text
        );

        private record OpenAiImagePartDto(
            [property: JsonPropertyName("type")] string Type,
            [property: JsonPropertyName("image_url")] OpenAiImageUrlDto ImageUrl
        );

        private record OpenAiImageUrlDto(
            [property: JsonPropertyName("url")] string Url
        );
    }
}
