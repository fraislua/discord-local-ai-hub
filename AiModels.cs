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

    // 思考(reasoning)の深さ。ローカルモデルはSupportedEffortsを持つもの(Qwen3.8)にだけ渡し、
    // それ以外のローカルモデルでは無視する(082)。
    // 実際に対応する段階数はモデルごとに異なる(ModelRegistry.SupportedEffortsで定義)。
    // 対応しない段階が指定された場合、各Providerが自身の対応範囲にクランプする
    // (例: Gemini/GrokはXHighに非対応のためHighにクランプ)
    public enum EffortLevel
    {
        None,
        Low,
        Medium,
        High,
        XHigh
    }

    // 正規化済みリクエスト
    // （どのAIプロバイダーを呼び出す際にも、この形でデータを渡します）
    public record AiRequest(
        string SystemPrompt,
        string ModelId,
        IReadOnlyList<ChatTurn> History,
        string CurrentUserText,
        IReadOnlyList<string> CurrentImagesBase64,
        // nullは「指定なし」。ローカルモデルには送らずllama-swap側のモデルごとの推奨値に任せ、
        // クラウドモデルはDefaultTemperatureを使う(082)
        double? Temperature,
        int MaxOutputTokens,
        EffortLevel Effort = EffortLevel.Medium
    )
    {
        public const double DefaultTemperature = 0.7;
    }

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
    // 返した場合のみ値が入る（ローカルモデルはnullのまま＝コスト計算対象外）。
    // WasCancelledは停止ボタン等でストリームが途中で打ち切られた場合にtrue（usageは届かない）
    public record StreamResult(
        string RawText,
        int? PromptTokens,
        int? CompletionTokens,
        int? ReasoningTokens,
        bool WasCancelled
    );
}