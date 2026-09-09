using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DiscordAIBot
{
    // 長文のAI応答をGoogle Driveにテキストファイルとして保存する。
    // GoogleAdcTokenProviderを共有インスタンスとして受け取り(drive.fileスコープが
    // 付与されたADCが前提)、Drive API v3のmultipartアップロードを直接呼び出す。
    // drive.fileスコープのため、アップロード(新規作成)のみ可能で既存ファイルは扱わない
    public class GoogleDriveUploader
    {
        private readonly HttpClient _httpClient;
        private readonly GoogleAdcTokenProvider _tokenProvider;
        private readonly string _quotaProjectId;

        public GoogleDriveUploader(HttpClient httpClient, GoogleAdcTokenProvider tokenProvider, string quotaProjectId)
        {
            _httpClient = httpClient;
            _tokenProvider = tokenProvider;
            _quotaProjectId = quotaProjectId;
        }

        // 成功時はwebViewLink(ブラウザで開けるURL)を返す。失敗時はnull
        public async Task<string?> UploadTextFileAsync(string fileName, string content, CancellationToken cancellationToken)
        {
            string accessToken = await _tokenProvider.GetAccessTokenAsync(cancellationToken);

            var metadata = new { name = fileName, mimeType = "text/markdown" };
            string metadataJson = JsonSerializer.Serialize(metadata);

            using var multipart = new MultipartContent("related");
            multipart.Add(new StringContent(metadataJson, Encoding.UTF8, "application/json"));
            multipart.Add(new StringContent(content, Encoding.UTF8, "text/markdown"));

            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                "https://www.googleapis.com/upload/drive/v3/files?uploadType=multipart&fields=id,webViewLink")
            {
                Content = multipart
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.Add("X-Goog-User-Project", _quotaProjectId);

            using var response = await _httpClient.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                string errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                Console.WriteLine($"[Error] Google Drive アップロード失敗: {(int)response.StatusCode} {errorContent}");
                return null;
            }

            string responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(responseJson);

            if (doc.RootElement.TryGetProperty("webViewLink", out var linkElement) && linkElement.ValueKind == JsonValueKind.String)
            {
                return linkElement.GetString();
            }

            return null;
        }
    }
}
