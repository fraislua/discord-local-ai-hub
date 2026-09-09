namespace DiscordAIBot
{
    // Vertex AI経由のクラウドモデル利用額をUSD建てで推定する。
    // 為替レートの影響を受けないよう、円建てではなくUSD単価をそのまま使用する。
    //
    // 単価の出所:
    // - Gemini 3.8 Flash: Google Cloud Billing Catalog API (services/C7E2-9256-1C43 "Vertex AI")
    //   から実際に取得したSKU単価。確認日: 2026-09-09、有効時刻: 2026-09-08T07:00:00Z
    // - Grok 4.6: xAI公式ドキュメント(docs.x.ai/developers/models/grok-4.6)の単価。
    //   Vertex AI経由の実際の請求(GCPコンソールのJPY建て価格)と、想定為替レート
    //   (JPY 159.375 / USD)で逆算した結果が完全一致することを確認済み(2026-09-09時点)。
    // - reasoning/thoughtsトークンは、visible出力トークンと同じ出力単価で課金される
    //   前提（両社とも明示的に別単価を示していないため、業界標準的な扱いとして採用）
    public static class CostEstimator
    {
        private const double GeminiInputPerMillionUsd = 1.50;
        private const double GeminiOutputPerMillionUsd = 7.50; // thoughtsトークン含む

        private const double GrokLongContextThresholdTokens = 200_000; // プロンプトがこれ以上で高単価適用
        private const double GrokInputPerMillionUsdStandard = 2.00;
        private const double GrokInputPerMillionUsdLong = 4.00;
        private const double GrokOutputPerMillionUsdStandard = 6.00; // reasoningトークン含む
        private const double GrokOutputPerMillionUsdLong = 12.00;

        // OpenAI GPT-5.6 Sol/Terra/Luna単価(2026-09時点、OpenAI公式発表値)。
        // 日次無料枠(OpenAiQuota)内で完結する限りは実際には課金されないが、
        // 万一超過した場合の実額を正しく記録するため、無料枠の有無に関わらず
        // 名目コストとして常に計算する(Gemini/Grokと同じ方針)
        private const double OpenAiSolInputPerMillionUsd = 5.00;
        private const double OpenAiSolOutputPerMillionUsd = 30.00;
        private const double OpenAiTerraInputPerMillionUsd = 2.50;
        private const double OpenAiTerraOutputPerMillionUsd = 15.00;
        private const double OpenAiLunaInputPerMillionUsd = 1.00;
        private const double OpenAiLunaOutputPerMillionUsd = 6.00;

        // ローカルモデル等、コスト計算対象外のプロバイダーはnullを返す
        public static double? EstimateCostUsd(ApiProvider provider, string modelId, int promptTokens, int completionTokens, int reasoningTokens)
        {
            switch (provider)
            {
                case ApiProvider.VertexGemini:
                {
                    double inputCost = promptTokens / 1_000_000.0 * GeminiInputPerMillionUsd;
                    double outputCost = (completionTokens + reasoningTokens) / 1_000_000.0 * GeminiOutputPerMillionUsd;
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
                        "gpt-5.6-sol" => (OpenAiSolInputPerMillionUsd, OpenAiSolOutputPerMillionUsd),
                        "gpt-5.6-terra" => (OpenAiTerraInputPerMillionUsd, OpenAiTerraOutputPerMillionUsd),
                        "gpt-5.6-luna" => (OpenAiLunaInputPerMillionUsd, OpenAiLunaOutputPerMillionUsd),
                        _ => (0.0, 0.0)
                    };
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
