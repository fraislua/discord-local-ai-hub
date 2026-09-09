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
    // Vertex AIのOpenAI互換エンドポイント(.../endpoints/openapi/chat/completions)経由でGrokを呼び出すProvider。
    // SSEパースの構造はLmStudioProviderと類似するが、既存LmStudioProviderの挙動を一切変えないため
    // 意図的に共有化せず、独立したクラスとして実装する（コード重複は許容）。
    // 実機検証: reasoning_contentフィールドは出現せず(reasoning_tokensはusageのみに計上)、
    // 現時点ではIsVlm非対応モデルとして登録するため画像入力は扱わない。
    public class VertexGrokProvider : IAiProvider
    {
        private readonly HttpClient _httpClient;
        private readonly GoogleAdcTokenProvider _tokenProvider;
        private readonly string _projectId;
        private readonly string _region;

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public VertexGrokProvider(HttpClient httpClient, GoogleAdcTokenProvider tokenProvider, string projectId, string region)
        {
            _httpClient = httpClient;
            _tokenProvider = tokenProvider;
            _projectId = projectId;
            _region = region;
        }

        public async IAsyncEnumerable<StreamChunk> StreamChatAsync(
            AiRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            string host = _region == "global" ? "aiplatform.googleapis.com" : $"{_region}-aiplatform.googleapis.com";
            string endpointUrl = $"https://{host}/v1/projects/{_projectId}/locations/{_region}/endpoints/openapi/chat/completions";

            var messages = new List<GrokMessageDto>();

            if (!string.IsNullOrEmpty(request.SystemPrompt))
            {
                messages.Add(new GrokMessageDto("system", request.SystemPrompt));
            }

            foreach (var turn in request.History)
            {
                messages.Add(new GrokMessageDto(turn.Role, turn.Content));
            }

            messages.Add(new GrokMessageDto("user", request.CurrentUserText));

            // 実機検証済み: Vertex経由で受理される値は minimal/low/medium/high の4種
            // (xAI公式ドキュメント記載のxhigh、Vertexのエラーメッセージが例示するmaxは
            // いずれも実際には拒否される)。XHighはHighにクランプする
            string reasoningEffort = request.Effort switch
            {
                EffortLevel.None => "minimal",
                EffortLevel.Low => "low",
                EffortLevel.High => "high",
                EffortLevel.XHigh => "high",
                _ => "medium"
            };

            var requestBodyObj = new GrokRequestDto(
                Model: request.ModelId,
                Messages: messages,
                Temperature: request.Temperature,
                MaxTokens: request.MaxOutputTokens > 0 ? request.MaxOutputTokens : -1,
                Stream: true,
                ReasoningEffort: reasoningEffort
            );

            string requestJson = JsonSerializer.Serialize(requestBodyObj, _jsonOptions);
            using var httpContent = new StringContent(requestJson, Encoding.UTF8, "application/json");
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpointUrl) { Content = httpContent };

            string accessToken = await _tokenProvider.GetAccessTokenAsync(cancellationToken);
            httpRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    string errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                    Console.WriteLine($"[Error] Vertex Grok API Request Failed: {(int)response.StatusCode} {response.ReasonPhrase}");
                    Console.WriteLine($"[Error] Response Details: {errorContent}");

                    if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    {
                        throw new AiRateLimitException(response.StatusCode, "Vertex AI (Grok)のレートリミットに到達しました。");
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
                throw new Exception($"Vertex Grok API 通信エラー: {ex.Message}", ex);
            }

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);

            // Grokのストリームは「finish_reasonを含むチャンク」と「usage(トークン内訳)を含む
            // choices空のチャンク」が別々に届く。StreamResponseHandler側はFinishReasonが
            // 非nullのチャンクを見た時点でストリーム読み取りを終了するため、finish_reasonは
            // 一旦保留し、直後のusageチャンクとマージした1チャンクとしてyieldする
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
                        // choices空のusage専用チャンク。保留中のfinishReasonがあればここで確定
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
                        // usageチャンクを待つため、finishReasonはまだ確定させない
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

            // usageチャンクが届かないままストリームが終了した場合のフォールバック
            if (pendingFinishReason != null)
            {
                yield return new StreamChunk(null, false, pendingFinishReason);
            }
        }

        private record GrokRequestDto(
            [property: JsonPropertyName("model")] string Model,
            [property: JsonPropertyName("messages")] IReadOnlyList<GrokMessageDto> Messages,
            [property: JsonPropertyName("temperature")] double Temperature,
            [property: JsonPropertyName("max_tokens")] int MaxTokens,
            [property: JsonPropertyName("stream")] bool Stream,
            [property: JsonPropertyName("reasoning_effort")] string ReasoningEffort
        );

        private record GrokMessageDto(
            [property: JsonPropertyName("role")] string Role,
            [property: JsonPropertyName("content")] string Content
        );
    }
}
