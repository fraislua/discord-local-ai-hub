using System.Collections.Generic;
using System.Linq;

namespace DiscordAIBot
{
    // OpenAIのデータ共有プログラムによる日次無料トークン枠の定義。
    // Usage Tier 1-2の値(2026-09時点、ユーザー申告・OpenAI Helpセンター記載値・
    // コミュニティ報告で確認): 大型枠(Sol) 250,000トークン/日、
    // 軽量枠(Terra+Luna、同一プールを共有) 2,500,000トークン/日。
    // 超過分は通常料金で課金されるため、Program.cs側の事前ブロックでこの上限を
    // 絶対に超えないようにする(実利用量の追跡はUsageRecordsテーブルを流用)
    public static class OpenAiQuota
    {
        public const int LargePoolDailyCap = 250_000;
        public const int LightPoolDailyCap = 2_500_000;

        public static readonly IReadOnlyList<string> LargePoolModelIds = new List<string> { "gpt-5.6-sol" };
        public static readonly IReadOnlyList<string> LightPoolModelIds = new List<string> { "gpt-5.6-terra", "gpt-5.6-luna" };

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
