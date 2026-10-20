namespace DiscordAIBot
{
    // Vertex AI経由のクラウドモデル利用額をUSD建てで推定する。
    // 為替レートの影響を受けないよう、円建てではなくUSD単価をそのまま使用する。
    //
    // 単価の出所:
    // - Gemini 3.8 Flash: Google Cloud Billing Catalog API (services/C7E2-9256-1C43 "Vertex AI")
    //   から実際に取得したSKU単価。確認日: 2026-09-09、有効時刻: 2026-09-08T07:00:00Z
    //   (2026-10-20に再取得し同額であることを確認)
    // - Gemini 4 Argon: 同APIのGlobal Online PredictionsのSKU単価。確認日: 2026-10-20、
    //   有効時刻: 2026-10-20T07:00:00Z。長文脈の別単価SKUは無い(075)
    // - Grok 4.6/4.7: xAI公式ドキュメント(docs.x.ai/developers/models/grok-4.6, grok-4.7)の単価。
    //   4.7も4.6と同額(075で確認)。4.6については、Vertex AI経由の実際の請求(GCPコンソールの
    //   JPY建て価格)と、想定為替レート(JPY 159.375 / USD)で逆算した結果が完全一致することを
    //   確認済み(2026-09-09時点)。
    // - reasoning/thoughtsトークンは、visible出力トークンと同じ出力単価で課金される
    //   前提（両社とも明示的に別単価を示していないため、業界標準的な扱いとして採用）
    public static class CostEstimator
    {
        private const double GeminiFlashInputPerMillionUsd = 1.50;
        private const double GeminiFlashOutputPerMillionUsd = 7.50; // thoughtsトークン含む
        private const double GeminiArgonInputPerMillionUsd = 4.00;
        private const double GeminiArgonOutputPerMillionUsd = 20.00; // thoughtsトークン含む

        private const double GrokLongContextThresholdTokens = 200_000; // プロンプトがこれ以上で高単価適用
        private const double GrokInputPerMillionUsdStandard = 2.00;
        private const double GrokInputPerMillionUsdLong = 4.00;
        private const double GrokOutputPerMillionUsdStandard = 6.00; // reasoningトークン含む
        private const double GrokOutputPerMillionUsdLong = 12.00;

        // OpenAI単価(2026-10-20時点、developers.openai.com/api/docs/pricingのStandard)。
        // 日次無料枠(OpenAiQuota)内で完結する限りは実際には課金されないが、
        // 万一超過した場合の実額を正しく記録するため、無料枠の有無に関わらず
        // 名目コストとして常に計算する(Gemini/Grokと同じ方針)。
        // gpt-5.6-lunaは2026-09時点の1.00/6.00から値下げされていた(075)
        private const double OpenAi61SolInputPerMillionUsd = 2.00;
        private const double OpenAi61SolOutputPerMillionUsd = 10.00;
        private const double OpenAi6AstraInputPerMillionUsd = 10.00;
        private const double OpenAi6AstraOutputPerMillionUsd = 50.00;
        private const double OpenAi56LunaInputPerMillionUsd = 0.20;
        private const double OpenAi56LunaOutputPerMillionUsd = 1.20;

        // 入力がこれを超えるリクエストは、リクエスト全体の入力が2倍・出力が1.5倍になる(全モデル共通)
        private const int OpenAiLongContextThresholdTokens = 272_000;
        private const double OpenAiLongContextInputMultiplier = 2.0;
        private const double OpenAiLongContextOutputMultiplier = 1.5;

        // TokenManager(ローカルのGemma用トークナイザー)の推定トークン数を、上流APIで実際に数えられる
        // トークン数の安全側の見積もりに換算する係数。実測(provisioning/060)では、日本語プロンプトで
        // OpenAIの実数(チャット形式の固定分約7トークンを除く)は推定値の0.9〜1.35倍だった。
        // 無料枠の事前ブロック・中断時の推定記録は「実際より少なく見積もらない」ことを優先し、1.5倍+固定分とする
        private const double PromptEstimateSafetyFactor = 1.5;
        private const int PromptEstimateFixedOverheadTokens = 100;

        // 添付画像1枚あたりの見積もり。AttachmentProcessorが長辺1024pxに縮小するため実際は数百トークン程度だが、
        // モデルごとの画像トークン計算の違いを吸収するため多めに見積もる
        private const int EstimatedTokensPerImage = 2000;

        public static int EstimatePromptTokensConservatively(int localTokenEstimate, int imageCount = 0) =>
            (int)Math.Ceiling(localTokenEstimate * PromptEstimateSafetyFactor)
            + PromptEstimateFixedOverheadTokens
            + imageCount * EstimatedTokensPerImage;

        // ローカルモデル等、コスト計算対象外のプロバイダーはnullを返す
        public static double? EstimateCostUsd(ApiProvider provider, string modelId, int promptTokens, int completionTokens, int reasoningTokens)
        {
            switch (provider)
            {
                case ApiProvider.VertexGemini:
                {
                    var (inputRate, outputRate) = modelId switch
                    {
                        "gemini-4-argon" => (GeminiArgonInputPerMillionUsd, GeminiArgonOutputPerMillionUsd),
                        _ => (GeminiFlashInputPerMillionUsd, GeminiFlashOutputPerMillionUsd)
                    };
                    double inputCost = promptTokens / 1_000_000.0 * inputRate;
                    double outputCost = (completionTokens + reasoningTokens) / 1_000_000.0 * outputRate;
                    return inputCost + outputCost;
                }
                case ApiProvider.VertexGrok:
                {
                    bool isLongContext = promptTokens >= GrokLongContextThresholdTokens;
                    double inputRate = isLongContext ? GrokInputPerMillionUsdLong : GrokInputPerMillionUsdStandard;
                    double outputRate = isLongContext ? GrokOutputPerMillionUsdLong : GrokOutputPerMillionUsdStandard;
                    double inputCost = promptTokens / 1_000_000.0 * inputRate;
                    double outputCost = (completionTokens + reasoningTokens) / 1_000_000.0 * outputRate;
                    return inputCost + outputCost;
                }
                case ApiProvider.OpenAi:
                {
                    var (inputRate, outputRate) = modelId switch
                    {
                        "gpt-6.1-sol" => (OpenAi61SolInputPerMillionUsd, OpenAi61SolOutputPerMillionUsd),
                        "gpt-6-astra" => (OpenAi6AstraInputPerMillionUsd, OpenAi6AstraOutputPerMillionUsd),
                        "gpt-5.6-luna" => (OpenAi56LunaInputPerMillionUsd, OpenAi56LunaOutputPerMillionUsd),
                        _ => (0.0, 0.0)
                    };
                    if (promptTokens > OpenAiLongContextThresholdTokens)
                    {
                        inputRate *= OpenAiLongContextInputMultiplier;
                        outputRate *= OpenAiLongContextOutputMultiplier;
                    }
                    double inputCost = promptTokens / 1_000_000.0 * inputRate;
                    // OpenAIのcompletion_tokensには既にreasoning_tokensが含まれるため
                    // (completion_tokens_detailsは内訳であり加算対象ではない)、
                    // Gemini/Grokと異なりreasoningTokensを別途加算しない(二重計上防止)
                    double outputCost = completionTokens / 1_000_000.0 * outputRate;
                    return inputCost + outputCost;
                }
                default:
                    return null;
            }
        }
    }
}
