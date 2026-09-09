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
    // PromptTokens/CompletionTokens/ReasoningTokensは、クラウドプロバイダーがusage情報を
    // 返す最終チャンクでのみ値が入る（ローカルモデル等、対応しない場合はnullのまま）
    public readonly record struct StreamChunk(
        string? TextDelta,
        bool IsReasoning,
        string? FinishReason,
        int? PromptTokens = null,
        int? CompletionTokens = null,
        int? ReasoningTokens = null
    );

    // StreamResponseHandlerの結果。PromptTokens等はクラウドプロバイダーがusageを
    // 返した場合のみ値が入る（ローカルモデルはnullのまま＝コスト計算対象外）
    public record StreamResult(
        string RawText,
        int? PromptTokens,
        int? CompletionTokens,
        int? ReasoningTokens
    );
}