using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace DiscordAIBot
{
    public class LmStudioProvider : IAiProvider
    {
        private readonly HttpClient _httpClient;
        private readonly string _endpointUrl;
        
        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public LmStudioProvider(HttpClient httpClient, string endpointUrl)
        {
            _httpClient = httpClient;
            _endpointUrl = endpointUrl;
        }

        public async IAsyncEnumerable<StreamChunk> StreamChatAsync(
            AiRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var messages = new List<LmMessageDto>();

            if (!string.IsNullOrEmpty(request.SystemPrompt))
            {
                messages.Add(new LmMessageDto("system", request.SystemPrompt));
            }

            foreach (var turn in request.History)
            {
                messages.Add(new LmMessageDto(turn.Role, turn.Content));
            }

            if (request.CurrentImagesBase64 != null && request.CurrentImagesBase64.Count > 0)
            {
                var contentList = new List<LmContentPartDto>
                {
                    new LmContentPartDto(Type: "text", Text: request.CurrentUserText)
                };

                foreach (var imgBase64 in request.CurrentImagesBase64)
                {
                    contentList.Add(new LmContentPartDto(
                        Type: "image_url",
                        ImageUrl: new LmImageUrlDto(Url: $"data:image/jpeg;base64,{imgBase64}") // プレフィックス付与
                    ));
                }
                messages.Add(new LmMessageDto("user", contentList));
            }
            else
            {
                messages.Add(new LmMessageDto("user", request.CurrentUserText));
            }

            var requestBodyObj = new LmRequestDto(
                Model: request.ModelId,
                Messages: messages,
                Temperature: request.Temperature,
                MaxTokens: request.MaxOutputTokens > 0 ? request.MaxOutputTokens : -1,
                Stream: true
            );

            string requestJson = JsonSerializer.Serialize(requestBodyObj, _jsonOptions);
            using var httpContent = new StringContent(requestJson, Encoding.UTF8, "application/json");

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, _endpointUrl)
            {
                Content = httpContent
            };

            using var response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

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
                    bool isReasoning = false;

                    if (delta.TryGetProperty("reasoning_content", out var reasoningElement) && reasoningElement.ValueKind == JsonValueKind.String)
                    {
                        textDelta = reasoningElement.GetString();
                        isReasoning = true;
                    }
                    else if (delta.TryGetProperty("content", out var contentElement) && contentElement.ValueKind == JsonValueKind.String)
                    {
                        textDelta = contentElement.GetString();
                    }

                    if (string.IsNullOrEmpty(textDelta) && finishReason == null)
                    {
                        continue;
                    }

                    yield return new StreamChunk(textDelta, isReasoning, finishReason);
                }
            }
        }

        private record LmRequestDto(
            [property: JsonPropertyName("model")] string Model,
            [property: JsonPropertyName("messages")] IReadOnlyList<LmMessageDto> Messages,
            [property: JsonPropertyName("temperature")] double Temperature,
            [property: JsonPropertyName("max_tokens")] int MaxTokens,
            [property: JsonPropertyName("stream")] bool Stream
        );

        private record LmMessageDto(
            [property: JsonPropertyName("role")] string Role,
            [property: JsonPropertyName("content")] object Content
        );

        private record LmContentPartDto(
            [property: JsonPropertyName("type")] string Type,
            [property: JsonPropertyName("text")] string? Text = null,
            [property: JsonPropertyName("image_url")] LmImageUrlDto? ImageUrl = null
        );

        private record LmImageUrlDto(
            [property: JsonPropertyName("url")] string Url,
            [property: JsonPropertyName("detail")] string Detail = "auto"
        );
    }
}