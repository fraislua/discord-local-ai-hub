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

            var requestBodyObj = new GrokRequestDto(
                Model: request.ModelId,
                Messages: messages,
                Temperature: request.Temperature,
                MaxTokens: request.MaxOutputTokens > 0 ? request.MaxOutputTokens : -1,
                Stream: true
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
                    if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
                    {
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

                    if (string.IsNullOrEmpty(textDelta) && finishReason == null)
                    {
                        continue;
                    }

                    yield return new StreamChunk(textDelta, false, finishReason);
                }
            }
        }

        private record GrokRequestDto(
            [property: JsonPropertyName("model")] string Model,
            [property: JsonPropertyName("messages")] IReadOnlyList<GrokMessageDto> Messages,
            [property: JsonPropertyName("temperature")] double Temperature,
            [property: JsonPropertyName("max_tokens")] int MaxTokens,
            [property: JsonPropertyName("stream")] bool Stream
        );

        private record GrokMessageDto(
            [property: JsonPropertyName("role")] string Role,
            [property: JsonPropertyName("content")] string Content
        );
    }
}
