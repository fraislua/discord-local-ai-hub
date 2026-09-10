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
    // Vertex AIネイティブエンドポイント経由でGeminiを呼び出すProvider。
    // リクエスト/レスポンスのJSONスキーマはGoogle AI Studio(Generative Language API)と同一だが、
    // 認証がAPIキーではなくADC OAuth2 Bearerトークンである点、エンドポイントがproject/region付きの
    // Vertex URLである点が異なる（実機検証済み: role交互制約・空文字禁止・thoughtSignatureの扱いは
    // GoogleAiStudioProviderと同じロジックがそのまま通用する）。
    public class VertexGeminiProvider : IAiProvider
    {
        private readonly HttpClient _httpClient;
        private readonly GoogleAdcTokenProvider _tokenProvider;
        private readonly string _projectId;
        private readonly string _region;

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        public VertexGeminiProvider(HttpClient httpClient, GoogleAdcTokenProvider tokenProvider, string projectId, string region)
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
            // "global"はリージョン無しホスト、それ以外は"{region}-aiplatform.googleapis.com"
            string host = _region == "global" ? "aiplatform.googleapis.com" : $"{_region}-aiplatform.googleapis.com";
            string endpointUrl = $"https://{host}/v1/projects/{_projectId}/locations/{_region}/publishers/google/models/{request.ModelId}:streamGenerateContent?alt=sse";

            var contents = new List<GeminiContentDto>();

            // 履歴のマッピングとロール交互制約の対応(GoogleAiStudioProviderと同一ロジック)
            foreach (var turn in request.History)
            {
                string role = turn.Role == "assistant" ? "model" : "user";

                if (contents.Count > 0 && contents[^1].Role == role)
                {
                    var lastContent = contents[^1];
                    if (lastContent.Parts.Count > 0 && lastContent.Parts[0].Text != null)
                    {
                        lastContent.Parts[0].Text += $"\n\n{turn.Content}";
                    }
                    else
                    {
                        lastContent.Parts.Add(new GeminiPartDto { Text = turn.Content });
                    }
                }
                else
                {
                    contents.Add(new GeminiContentDto
                    {
                        Role = role,
                        Parts = new List<GeminiPartDto> { new GeminiPartDto { Text = turn.Content } }
                    });
                }
            }

            // Gemini APIの制約準拠: 履歴の先頭は必ず「user」でなければならない
            if (contents.Count > 0 && contents[0].Role != "user")
            {
                contents.Insert(0, new GeminiContentDto
                {
                    Role = "user",
                    Parts = new List<GeminiPartDto> { new GeminiPartDto { Text = "（これまでの会話を続けます）" } }
                });
            }

            // 履歴の最後が "user" で終わっている場合、次の現在の入力 ("user") と衝突するためダミーを挿入
            if (contents.Count > 0 && contents[^1].Role == "user")
            {
                contents.Add(new GeminiContentDto
                {
                    Role = "model",
                    Parts = new List<GeminiPartDto> { new GeminiPartDto { Text = "承知いたしました。続けてください。" } }
                });
            }

            // Gemini APIの制約準拠: Textが空文字だとエラーになるためプレースホルダーを配置
            string safeUserText = string.IsNullOrWhiteSpace(request.CurrentUserText)
                ? "（添付内容を確認してください）"
                : request.CurrentUserText;

            var currentParts = new List<GeminiPartDto>();
            if (request.CurrentImagesBase64 != null && request.CurrentImagesBase64.Count > 0)
            {
                foreach (var base64 in request.CurrentImagesBase64)
                {
                    currentParts.Add(new GeminiPartDto
                    {
                        InlineData = new GeminiInlineDataDto
                        {
                            MimeType = "image/jpeg",
                            Data = base64
                        }
                    });
                }
            }
            currentParts.Add(new GeminiPartDto { Text = safeUserText });
            contents.Add(new GeminiContentDto { Role = "user", Parts = currentParts });

            GeminiSystemInstructionDto? systemInstruction = null;
            if (!string.IsNullOrEmpty(request.SystemPrompt))
            {
                systemInstruction = new GeminiSystemInstructionDto(
                    new List<GeminiPartDto> { new GeminiPartDto { Text = request.SystemPrompt } }
                );
            }

            // GeminiのthinkingLevelはLOW/MEDIUM/HIGHの3段階のみ(026で実機確認済み)。
            // Noneは下限のLOWに、XHighは上限のHIGHにクランプする
            string thinkingLevel = request.Effort switch
            {
                EffortLevel.None => "LOW",
                EffortLevel.Low => "LOW",
                EffortLevel.High => "HIGH",
                EffortLevel.XHigh => "HIGH",
                _ => "MEDIUM"
            };

            var requestBodyObj = new GeminiRequestDto(
                Contents: contents,
                SystemInstruction: systemInstruction,
                GenerationConfig: new GeminiGenerationConfigDto(
                    Temperature: request.Temperature,
                    MaxOutputTokens: request.MaxOutputTokens > 0 ? request.MaxOutputTokens : 8192,
                    ThinkingConfig: new GeminiThinkingConfigDto(ThinkingLevel: thinkingLevel)
                )
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
                    Console.WriteLine($"[Error] Vertex Gemini API Request Failed: {(int)response.StatusCode} {response.ReasonPhrase}");
                    Console.WriteLine($"[Error] Response Details: {errorContent}");

                    if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    {
                        throw new AiRateLimitException(response.StatusCode, "Vertex AI (Gemini)のレートリミットに到達しました。");
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
            catch (OperationCanceledException)
            {
                // キャンセル・タイムアウトは包まずに伝播させる(理由はOpenAiProviderと同じ、provisioning/060)
                throw;
            }
            catch (Exception ex)
            {
                throw new Exception($"Vertex Gemini API 通信エラー: {ex.Message}", ex);
            }

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);

            while (true)
            {
                string? line = await reader.ReadLineAsync(cancellationToken);

                if (line == null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;
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
                    if (!root.TryGetProperty("candidates", out var candidates) || candidates.GetArrayLength() == 0)
                    {
                        continue;
                    }

                    var candidate = candidates[0];

                    string? finishReason = null;
                    if (candidate.TryGetProperty("finishReason", out var frElement) && frElement.ValueKind == JsonValueKind.String)
                    {
                        finishReason = frElement.GetString();
                    }

                    string? textDelta = null;
                    if (candidate.TryGetProperty("content", out var contentElement) &&
                        contentElement.TryGetProperty("parts", out var parts) &&
                        parts.GetArrayLength() > 0 &&
                        parts[0].TryGetProperty("text", out var textElement) &&
                        textElement.ValueKind == JsonValueKind.String)
                    {
                        textDelta = textElement.GetString();
                    }

                    // usageMetadataは生成完了時のチャンク(finishReasonと同じチャンク)にのみ
                    // トークン内訳が入る。thoughtsTokenCountは思考(reasoning)トークン数
                    int? promptTokens = null;
                    int? completionTokens = null;
                    int? reasoningTokens = null;
                    if (root.TryGetProperty("usageMetadata", out var usageMetadata))
                    {
                        if (usageMetadata.TryGetProperty("promptTokenCount", out var ptElement) && ptElement.ValueKind == JsonValueKind.Number)
                        {
                            promptTokens = ptElement.GetInt32();
                        }
                        if (usageMetadata.TryGetProperty("candidatesTokenCount", out var ctElement) && ctElement.ValueKind == JsonValueKind.Number)
                        {
                            completionTokens = ctElement.GetInt32();
                        }
                        if (usageMetadata.TryGetProperty("thoughtsTokenCount", out var rtElement) && rtElement.ValueKind == JsonValueKind.Number)
                        {
                            reasoningTokens = rtElement.GetInt32();
                        }
                    }

                    if (string.IsNullOrEmpty(textDelta) && finishReason == null)
                    {
                        continue;
                    }

                    yield return new StreamChunk(textDelta, false, finishReason, promptTokens, completionTokens, reasoningTokens);
                }
            }
        }

        private record GeminiRequestDto(
            IReadOnlyList<GeminiContentDto> Contents,
            GeminiSystemInstructionDto? SystemInstruction,
            GeminiGenerationConfigDto GenerationConfig
        );

        private class GeminiContentDto
        {
            public string Role { get; set; } = string.Empty;
            public List<GeminiPartDto> Parts { get; set; } = new();
        }

        private record GeminiSystemInstructionDto(
            IReadOnlyList<GeminiPartDto> Parts
        );

        private class GeminiPartDto
        {
            public string? Text { get; set; }
            public GeminiInlineDataDto? InlineData { get; set; }
        }

        private class GeminiInlineDataDto
        {
            public string MimeType { get; set; } = string.Empty;
            public string Data { get; set; } = string.Empty;
        }

        private record GeminiGenerationConfigDto(
            double Temperature,
            int MaxOutputTokens,
            GeminiThinkingConfigDto ThinkingConfig
        );

        // Gemini 3系のthinkingLevel(LOW/MEDIUM/HIGH)。実機検証済み(旧thinkingBudgetの
        // 数値指定も動くが非推奨のため、新方式のenumを採用)
        private record GeminiThinkingConfigDto(
            string ThinkingLevel
        );
    }
}
