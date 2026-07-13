using System;
using System.Collections.Generic;
using System.Net;

namespace DiscordAIBot
{
    // レートリミット（429等）用のカスタム例外
    public class AiRateLimitException : Exception
    {
        public HttpStatusCode StatusCode { get; }

        public AiRateLimitException(HttpStatusCode statusCode, string message) : base(message)
        {
            StatusCode = statusCode;
        }
    }

    // 正規化済みリクエスト
    // （どのAIプロバイダーを呼び出す際にも、この形でデータを渡します）
    public record AiRequest(
        string SystemPrompt,
        string ModelId,
        IReadOnlyList<ChatTurn> History,
        string CurrentUserText,
        IReadOnlyList<string> CurrentImagesBase64,
        double Temperature,
        int MaxOutputTokens
    );

    // 履歴用のロールとコンテンツ
    public record ChatTurn(string Role, string Content);

    // 正規化済みストリームチャンク
    // （GC Allocを避けるため readonly record struct を採用し、スタックに確保させます）
    public readonly record struct StreamChunk(
        string? TextDelta,
        bool IsReasoning,
        string? FinishReason
    );
}