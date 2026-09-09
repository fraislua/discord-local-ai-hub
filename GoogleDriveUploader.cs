using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DiscordAIBot
{
    // スレッド(セッション)ごとの会話記録をGoogle Driveに保存する。
    // GoogleAdcTokenProviderを共有インスタンスとして受け取り(drive.fileスコープが
    // 付与されたADCが前提)、Drive API v3を直接HttpClientで呼び出す。
    // drive.fileスコープのため、このアプリが作成したフォルダ・ファイルのみ扱える
    public class GoogleDriveUploader
    {
        private readonly HttpClient _httpClient;
        private readonly GoogleAdcTokenProvider _tokenProvider;
        private readonly string _quotaProjectId;

        // プロセス生存中はフォルダIDをキャッシュし、スレッドごとの検索/作成コストを避ける
        private string? _cachedFolderId;

        public GoogleDriveUploader(HttpClient httpClient, GoogleAdcTokenProvider tokenProvider, string quotaProjectId)
        {
            _httpClient = httpClient;
            _tokenProvider = tokenProvider;
            _quotaProjectId = quotaProjectId;
        }

        // 指定名のフォルダを検索し、無ければ作成してIDを返す
        public async Task<string> GetOrCreateFolderAsync(string folderName, CancellationToken cancellationToken)
        {
            if (_cachedFolderId != null) return _cachedFolderId;

            string accessToken = await _tokenProvider.GetAccessTokenAsync(cancellationToken);

            string query = $"name='{folderName.Replace("'", "\\'")}' and mimeType='application/vnd.google-apps.folder' and trashed=false";
            string encodedQuery = Uri.EscapeDataString(query);

            using (var listRequest = new HttpRequestMessage(HttpMethod.Get, $"https://www.googleapis.com/drive/v3/files?q={encodedQuery}&fields=files(id,name)"))
            {
                listRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                listRequest.Headers.Add("X-Goog-User-Project", _quotaProjectId);

                using var listResponse = await _httpClient.SendAsync(listRequest, cancellationToken);
                if (listResponse.IsSuccessStatusCode)
                {
                    string listJson = await listResponse.Content.ReadAsStringAsync(cancellationToken);
                    using var listDoc = JsonDocument.Parse(listJson);
                    if (listDoc.RootElement.TryGetProperty("files", out var filesArray) && filesArray.GetArrayLength() > 0)
                    {
                        string? existingId = filesArray[0].GetProperty("id").GetString();
                        if (existingId != null)
                        {
                            _cachedFolderId = existingId;
                            return existingId;
                        }
                    }
                }
            }

            var metadata = new { name = folderName, mimeType = "application/vnd.google-apps.folder" };
            string metadataJson = JsonSerializer.Serialize(metadata);

            using var createRequest = new HttpRequestMessage(HttpMethod.Post, "https://www.googleapis.com/drive/v3/files?fields=id")
            {
                Content = new StringContent(metadataJson, Encoding.UTF8, "application/json")
            };
            createRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            createRequest.Headers.Add("X-Goog-User-Project", _quotaProjectId);

            using var createResponse = await _httpClient.SendAsync(createRequest, cancellationToken);
            createResponse.EnsureSuccessStatusCode();

            string createJson = await createResponse.Content.ReadAsStringAsync(cancellationToken);
            using var createDoc = JsonDocument.Parse(createJson);
            string folderId = createDoc.RootElement.GetProperty("id").GetString()
                ?? throw new Exception("フォルダ作成に失敗しました(idが取得できません)");

            _cachedFolderId = folderId;
            return folderId;
        }

        // 指定フォルダ内に新規ファイルを作成する。(fileId, webViewLink)を返す
        public async Task<(string fileId, string webViewLink)> CreateFileInFolderAsync(string fileName, string content, string folderId, CancellationToken cancellationToken)
        {
            string accessToken = await _tokenProvider.GetAccessTokenAsync(cancellationToken);

            var metadata = new { name = fileName, mimeType = "text/markdown", parents = new[] { folderId } };
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
            response.EnsureSuccessStatusCode();

            string responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(responseJson);

            string fileId = doc.RootElement.GetProperty("id").GetString()
                ?? throw new Exception("ファイル作成に失敗しました(idが取得できません)");
            string webViewLink = doc.RootElement.TryGetProperty("webViewLink", out var linkEl)
                ? (linkEl.GetString() ?? string.Empty)
                : string.Empty;

            return (fileId, webViewLink);
        }

        // 既存ファイルの内容を丸ごと上書きする
        public async Task UpdateFileContentAsync(string fileId, string content, CancellationToken cancellationToken)
        {
            string accessToken = await _tokenProvider.GetAccessTokenAsync(cancellationToken);

            using var request = new HttpRequestMessage(
                HttpMethod.Patch,
                $"https://www.googleapis.com/upload/drive/v3/files/{fileId}?uploadType=media")
            {
                Content = new StringContent(content, Encoding.UTF8, "text/markdown")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.Add("X-Goog-User-Project", _quotaProjectId);

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
        }
    }
}
