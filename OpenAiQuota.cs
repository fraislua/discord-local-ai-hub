using System.Collections.Generic;
using System.Linq;

namespace DiscordAIBot
{
    // OpenAIのデータ共有プログラムによる日次無料トークン枠の定義。
    // Buildプランの値(2026-10時点、ユーザーがOpenAI管理画面で確認): 大型枠 250,000トークン/日、
    // 軽量枠 2,500,000トークン/日。どちらも枠内の全モデルで1つのプールを共有する。
    // gpt-6-lunaは軽量枠ではなく大型枠のため、軽量枠用にgpt-5.6-lunaを残している(075)。
    // 超過分は通常料金で課金されるため、Program.cs側の事前ブロックでこの上限を
    // 絶対に超えないようにする(実利用量の追跡はUsageRecordsテーブルを流用)
    public static class OpenAiQuota
    {
        public const int LargePoolDailyCap = 250_000;
        public const int LightPoolDailyCap = 2_500_000;

        // 登録から外したgpt-5.6-sol/gpt-5.6-terraも、同じ日に使った分を枠の集計に含めるため残している
        public static readonly IReadOnlyList<string> LargePoolModelIds = new List<string> { "gpt-6.1-sol", "gpt-6-astra", "gpt-5.6-sol" };
        public static readonly IReadOnlyList<string> LightPoolModelIds = new List<string> { "gpt-5.6-luna", "gpt-5.6-terra" };

        public static (int Cap, IReadOnlyList<string> PoolModelIds)? GetPool(string modelId)
        {
            if (LargePoolModelIds.Contains(modelId))
            {
                return (LargePoolDailyCap, LargePoolModelIds);
            }
            if (LightPoolModelIds.Contains(modelId))
            {
                return (LightPoolDailyCap, LightPoolModelIds);
            }
            return null;
        }
    }
}
