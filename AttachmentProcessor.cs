using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Discord;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace DiscordAIBot
{
    public class AttachmentProcessor
    {
        private readonly HttpClient _httpClient;
        private const int MaxImageDimension = 1024;
        private const long MaxFileSizeLimitBytes = 10 * 1024 * 1024; // LXC環境のメモリ枯渇対策のため10MB制限
        private const long MaxPixelsLimit = 16777216; // 約1677万ピクセル (Decompression Bomb対策)
        private const int ImageTokenBudgetOffset = 1000; // 画像1枚あたりの概算トークン消費

        public AttachmentProcessor(HttpClient httpClient)
        {
            _httpClient = httpClient;
        }

        public async Task<(string extractedText, List<string> base64Images)> ProcessAttachmentsAsync(
            IReadOnlyCollection<IAttachment> attachments,
            ModelMetadata modelMeta,
            int availableTokensForUser,
            CancellationToken cancellationToken)
        {
            var textBuilder = new StringBuilder();
            var base64Images = new List<string>();
            int currentConsumedTokens = 0;

            if (attachments == null || attachments.Count == 0)
            {
                return (string.Empty, base64Images);
            }

            foreach (var attachment in attachments)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (attachment.Size > MaxFileSizeLimitBytes)
                {
                    textBuilder.AppendLine($"\n[⚠️ ファイル {attachment.Filename} は10MBを超えているためスキップされました]");
                    continue;
                }

                string lowerFilename = attachment.Filename.ToLower();

                if (lowerFilename.EndsWith(".txt") || lowerFilename.EndsWith(".cs") || 
                    lowerFilename.EndsWith(".md") || lowerFilename.EndsWith(".json") || lowerFilename.EndsWith(".py"))
                {
                    try
                    {
                        using var response = await _httpClient.GetAsync(attachment.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                        response.EnsureSuccessStatusCode();
                        string fileContent = await response.Content.ReadAsStringAsync(cancellationToken);
                        
                        int fileTokens = TokenManager.CountTokens(fileContent);
                        
                        if (currentConsumedTokens + fileTokens > availableTokensForUser)
                        {
                            textBuilder.AppendLine($"\n❌ **容量オーバー:** ファイル `{attachment.Filename}` がコンテキスト上限を超過したため読み込みを中断しました。");
                            break;
                        }

                        currentConsumedTokens += fileTokens;
                        textBuilder.AppendLine($"\n\n【添付ファイル: {attachment.Filename}】\n```\n{fileContent}\n```");
                    }
                    catch (Exception ex)
                    {
                        textBuilder.AppendLine($"\n[❌ ファイル {attachment.Filename} の読み込みに失敗しました: {ex.Message}]");
                    }
                }
                else if (lowerFilename.EndsWith(".png") || lowerFilename.EndsWith(".jpg") || 
                         lowerFilename.EndsWith(".jpeg") || lowerFilename.EndsWith(".webp"))
                {
                    if (!modelMeta.IsVlm)
                    {
                        textBuilder.AppendLine($"\n⚠️ 現在のモデル (`{modelMeta.DisplayName}`) は画像認識に対応していません。画像 `{attachment.Filename}` は無視されました。");
                        continue;
                    }

                    if (currentConsumedTokens + ImageTokenBudgetOffset > availableTokensForUser)
                    {
                        textBuilder.AppendLine($"\n❌ **容量オーバー:** 画像 `{attachment.Filename}` を追加するとコンテキスト上限を超過するため読み込みを中断しました。");
                        break;
                    }

                    try
                    {
                        string base64 = await ProcessImageToBase64Async(attachment.Url, cancellationToken);
                        if (!string.IsNullOrEmpty(base64))
                        {
                            base64Images.Add(base64);
                            currentConsumedTokens += ImageTokenBudgetOffset;
                        }
                    }
                    catch (Exception ex)
                    {
                        textBuilder.AppendLine($"\n[❌ 画像 {attachment.Filename} の処理に失敗しました: {ex.Message}]");
                    }
                }
            }

            return (textBuilder.ToString(), base64Images);
        }

        private async Task<string> ProcessImageToBase64Async(string url, CancellationToken cancellationToken)
        {
            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            // 防御1: ヘッダによるサイズ検証
            if (response.Content.Headers.ContentLength.HasValue && 
                response.Content.Headers.ContentLength.Value > MaxFileSizeLimitBytes)
            {
                throw new Exception($"ファイルサイズが制限（{MaxFileSizeLimitBytes / 1024 / 1024}MB）を超過しています。");
            }
            
            using var httpStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var memoryStream = new MemoryStream();
            
            byte[] buffer = new byte[8192];
            int bytesRead;
            long totalBytesRead = 0;
            
            // 防御2: バッファリング時のサイズ監視（不正なContent-Lengthやチャンク転送からの保護）
            while ((bytesRead = await httpStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
            {
                totalBytesRead += bytesRead;
                if (totalBytesRead > MaxFileSizeLimitBytes)
                {
                    throw new Exception($"実際のファイルサイズが制限（{MaxFileSizeLimitBytes / 1024 / 1024}MB）を超過しています。");
                }
                await memoryStream.WriteAsync(buffer, 0, bytesRead, cancellationToken);
            }
            
            memoryStream.Position = 0;

            using var image = await SixLabors.ImageSharp.Image.LoadAsync(memoryStream, cancellationToken);

            // 防御3: ピクセル総数制限（Decompression Bomb対策）
            long totalPixels = (long)image.Width * image.Height;
            if (totalPixels > MaxPixelsLimit)
            {
                throw new Exception("画像ファイルの解像度が制限を超過しており、メモリ不足の恐れがあります（Decompression Bomb保護）。");
            }

            if (image.Width > MaxImageDimension || image.Height > MaxImageDimension)
            {
                image.Mutate(x => x.Resize(new ResizeOptions
                {
                    Size = new Size(MaxImageDimension, MaxImageDimension),
                    Mode = ResizeMode.Max
                }));
            }

            using var ms = new MemoryStream();
            await image.SaveAsync(ms, new JpegEncoder { Quality = 85 }, cancellationToken);
            
            // プレフィックスを含まない純粋なBase64文字列を返す
            return Convert.ToBase64String(ms.ToArray());
        }
    }
}