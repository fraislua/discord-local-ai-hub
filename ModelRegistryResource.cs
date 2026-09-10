using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;

namespace DiscordAIBot
{
    // ModelRegistry.AvailableModelsをMCP Resourceとして公開する。呼び出し側エージェントが
    // MCP_AGENT_GUIDE.mdのMarkdown表を都度読む代わりに、構造化データとして最新のモデル一覧
    // (コンテキスト長・画像対応可否・コスト特性・データ共有有無)を取得できるようにする。
    // costTier/dataSharingProgramはModelMetadata自体には持たせず、既存のProvider別の
    // 業務ルール(Program.cs/OpenAiQuota.cs/MCP_AGENT_GUIDE.mdに実装済み・記載済みの内容)を
    // このクラス内でのみ再現する(Discord bot本体のコアデータ型には手を入れない)
    [McpServerResourceType]
    public class ModelRegistryResource
    {
        private class ModelInfo
        {
            [JsonPropertyName("modelId")] public string ModelId { get; init; } = "";
            [JsonPropertyName("displayName")] public string DisplayName { get; init; } = "";
            [JsonPropertyName("provider")] public string Provider { get; init; } = "";
            [JsonPropertyName("location")] public string Location { get; init; } = "";
            [JsonPropertyName("contextWindow")] public int ContextWindow { get; init; }
            [JsonPropertyName("maxOutputTokens")] public int MaxOutputTokens { get; init; }
            [JsonPropertyName("supportsImages")] public bool SupportsImages { get; init; }
            [JsonPropertyName("supportedEfforts")] public IReadOnlyList<string>? SupportedEfforts { get; init; }
            [JsonPropertyName("costTier")] public string CostTier { get; init; } = "";
            [JsonPropertyName("dataSharingProgram")] public bool DataSharingProgram { get; init; }
            [JsonPropertyName("usageNote")] public string UsageNote { get; init; } = "";
            [JsonPropertyName("description")] public string Description { get; init; } = "";
        }

        private class Registry
        {
            [JsonPropertyName("defaultModelId")] public string DefaultModelId { get; init; } = "";
            [JsonPropertyName("models")] public IReadOnlyList<ModelInfo> Models { get; init; } = System.Array.Empty<ModelInfo>();
        }

        [McpServerResource(UriTemplate = "models://registry", Name = "Available Models", MimeType = "application/json")]
        [Description("askツールのmodel引数に指定できる全モデルの一覧。コンテキスト長・画像対応可否・思考深さの対応段階に加え、コスト特性(free/paid_shared_pool/free_within_daily_quota)とデータ共有プログラムの有無を含む。model引数を選ぶ前にこれを参照すれば、MCP_AGENT_GUIDE.mdを都度読まなくても最新の一覧を取得できる。")]
        public static string GetRegistry()
        {
            var models = ModelRegistry.AvailableModels.Values.Select(meta =>
            {
                var (costTier, dataSharing, note) = DescribeCost(meta.Provider);
                return new ModelInfo
                {
                    ModelId = meta.ModelId,
                    DisplayName = meta.DisplayName,
                    Provider = meta.Provider.ToString(),
                    Location = meta.Provider == ApiProvider.LmStudio ? "local" : "cloud",
                    ContextWindow = meta.ContextWindow,
                    MaxOutputTokens = meta.MaxOutputTokens,
                    SupportsImages = meta.IsVlm,
                    SupportedEfforts = meta.SupportedEfforts?.Select(e => e.ToString()).ToArray(),
                    CostTier = costTier,
                    DataSharingProgram = dataSharing,
                    UsageNote = note,
                    Description = meta.Description
                };
            }).ToList();

            var registry = new Registry
            {
                DefaultModelId = ModelRegistry.DefaultModelId,
                Models = models
            };

            // 日本語(usageNote等)を\uXXXXにエスケープしない(CompareModelsToolと同じ理由)
            return JsonSerializer.Serialize(registry, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
        }

        private static (string CostTier, bool DataSharing, string Note) DescribeCost(ApiProvider provider) => provider switch
        {
            ApiProvider.LmStudio =>
                ("free", false, "ローカル推論のため金銭コスト・データ共有なし。迷ったらこれを使う"),
            ApiProvider.VertexGemini or ApiProvider.VertexGrok =>
                ("paid_shared_pool", false, "無料枠・自動ブロックなし。利用の都度、共有の月$10枠(Google Developer Program)を静かに消費する"),
            ApiProvider.OpenAi =>
                ("free_within_daily_quota", true, "データ共有(Data Sharing)プログラム前提。日次無料枠内は金銭コストなし、上限到達で送信は自動ブロックされる。機密性の高い内容には不向き"),
            _ => ("unknown", false, "")
        };
    }
}
