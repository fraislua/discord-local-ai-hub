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
    public class GoogleAiStudioProvider : IAiProvider
    {
        private readonly HttpClient _httpClient;
        private readonly string _apiKey;

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        public GoogleAiStudioProvider(HttpClient httpClient, string apiKey)
        {
            _httpClient = httpClient;
            _apiKey = apiKey;
        }

        public async IAsyncEnumerable<StreamChunk> StreamChatAsync(
            AiRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            string endpointUrl = $"https://generativelanguage.googleapis.com/v1beta/models/{request.ModelId}:streamGenerateContent?key={_apiKey}&alt=sse";
            var contents = new List<GeminiContentDto>();

            // 履歴のマッピングとロール交互制約の対応
            foreach (var turn in request.History)
            {
                string role = turn.Role == "assistant" ? "model" : "user";

                if (contents.Count > 0 && contents[^1].Role == role)
                {
                    // 同一ロールが連続する場合はテキストをマージ
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
                    // 新しいロールを追加
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

            // 現在の入力のマッピング (純粋なBase64をinlineDataとしてアタッチ)
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

            // システムプロンプトの設定
            GeminiSystemInstructionDto? systemInstruction = null;
            if (!string.IsNullOrEmpty(request.SystemPrompt))
            {
                systemInstruction = new GeminiSystemInstructionDto(
                    new List<GeminiPartDto> { new GeminiPartDto { Text = request.SystemPrompt } }
                );
            }

            var requestBodyObj = new GeminiRequestDto(
                Contents: contents,
                SystemInstruction: systemInstruction,
                GenerationConfig: new GeminiGenerationConfigDto(
                    Temperature: request.Temperature,
                    MaxOutputTokens: request.MaxOutputTokens > 0 ? request.MaxOutputTokens : 8192
                )
            );

            string requestJson = JsonSerializer.Serialize(requestBodyObj, _jsonOptions);
            using var httpContent = new StringContent(requestJson, Encoding.UTF8, "application/json");
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpointUrl) { Content = httpContent };

            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                
                if (!response.IsSuccessStatusCode)
                {
                    // デバッグ用にレスポンス内容を読み取りログ出力
                    string errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                    Console.WriteLine($"[Error] Gemini API Request Failed: {(int)response.StatusCode} {response.ReasonPhrase}");
                    Console.WriteLine($"[Error] Response Details: {errorContent}");
                    
                    if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    {
                        throw new AiRateLimitException(response.StatusCode, "Google AI Studioのレートリミットに到達しました。");
                    }
                }
                
                response.EnsureSuccessStatusCode();
            }
            catch (HttpRequestException)
            {
                // 上位層（Program.cs等）でのハンドリングを阻害しないよう、そのままスローする
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
                throw new Exception($"Gemini API 通信エラー: {ex.Message}", ex);
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
                    continue; // 不完全なチャンクはスキップ
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

                    if (string.IsNullOrEmpty(textDelta) && finishReason == null)
                    {
                        continue;
                    }

                    yield return new StreamChunk(textDelta, false, finishReason);
                }
            }
        }

        // GC Alloc削減とシリアライズを目的としたDTO群
        private record GeminiRequestDto(
            IReadOnlyList<GeminiContentDto> Contents,
            GeminiSystemInstructionDto? SystemInstruction,
            GeminiGenerationConfigDto GenerationConfig
        );

        // 要素のマージが行えるようレコードからクラスに変更
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
            int MaxOutputTokens
        );
    }
}