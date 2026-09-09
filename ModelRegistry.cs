using System.Collections.Generic;

namespace DiscordAIBot
{
    public enum ApiProvider
    {
        LmStudio,
        GoogleAiStudio, // 当面残置（現在は未使用のモデルエントリ無し）
        VertexGemini,
        VertexGrok,
        OpenAi
    }

    public struct ModelMetadata
    {
        public string DisplayName { get; init; }
        public string ModelId { get; init; }
        public int ContextWindow { get; init; }
        public int MaxOutputTokens { get; init; }
        public string Description { get; init; }
        public bool IsVlm { get; init; }
        public ApiProvider Provider { get; init; }
        // /effortコマンドで選択可能なエフォート段階(実機検証済みの値のみ)。
        // 未指定(null)の場合はProgram.cs側でLow/Medium/Highの3段階にフォールバックする
        public IReadOnlyList<EffortLevel>? SupportedEfforts { get; init; }
    }

    public static class ModelRegistry
    {
        // セキュリティ対策として、デフォルトは課金の発生しないローカルモデルとする。
        // クラウドモデルは/modelコマンドで都度明示的に選択する運用（アカウント乗っ取り等の
        // リスクを踏まえ、既定で高コストなクラウドモデルが有効にならないようにする）
        public const string DefaultModelId = "google/gemma-4-12b-qat";

        public static readonly Dictionary<string, ModelMetadata> AvailableModels = new()
        {
            {
                "google/gemma-4-12b-qat",
                new ModelMetadata
                {
                    DisplayName = "Gemma-4-12B-VLM",
                    ModelId = "google/gemma-4-12b-qat",
                    ContextWindow = 32768,
                    MaxOutputTokens = -1,
                    Description = "デフォルトモデル (画像対応 / ローカル推論)",
                    IsVlm = true,
                    Provider = ApiProvider.LmStudio
                }
            },
            {
                "gemma-4-e4b-uncensored-hauhaucs-aggressive",
                new ModelMetadata
                {
                    DisplayName = "Gemma-4-E4B-Uncensored",
                    ModelId = "gemma-4-e4b-uncensored-hauhaucs-aggressive",
                    ContextWindow = 131072,
                    MaxOutputTokens = -1,
                    Description = "フィルタ緩和モデル (画像対応 / ローカル推論)",
                    IsVlm = true,
                    Provider = ApiProvider.LmStudio
                }
            },
            {
                "qwen/qwen3.8-27b",
                new ModelMetadata
                {
                    DisplayName = "Qwen3.8-27B",
                    ModelId = "qwen/qwen3.8-27b",
                    ContextWindow = 8192,
                    MaxOutputTokens = -1,
                    Description = "高精度-低速推論 (画像対応 / ローカル推論)",
                    IsVlm = true,
                    Provider = ApiProvider.LmStudio
                }
            },
            {
                "gemini-3.8-flash",
                new ModelMetadata
                {
                    DisplayName = "Gemini 3.8 Flash",
                    ModelId = "gemini-3.8-flash",
                    ContextWindow = 1048576,
                    MaxOutputTokens = 8192,
                    Description = "高速・長文脈モデル (Vertex AI)",
                    IsVlm = true,
                    Provider = ApiProvider.VertexGemini,
                    SupportedEfforts = new[] { EffortLevel.Low, EffortLevel.Medium, EffortLevel.High }
                }
            },
            {
                "xai/grok-4.6",
                new ModelMetadata
                {
                    DisplayName = "Grok 4.6",
                    ModelId = "xai/grok-4.6",
                    ContextWindow = 500000,
                    MaxOutputTokens = 8192,
                    Description = "xAI Grok (Vertex AI)",
                    IsVlm = false,
                    Provider = ApiProvider.VertexGrok,
                    // Noneは実機検証済みの"minimal"にマップ(026)。XHigh/Maxは実機で拒否済みのため含めない
                    SupportedEfforts = new[] { EffortLevel.None, EffortLevel.Low, EffortLevel.Medium, EffortLevel.High }
                }
            },
            {
                "gpt-5.6-sol",
                new ModelMetadata
                {
                    DisplayName = "GPT-5.6 Sol",
                    ModelId = "gpt-5.6-sol",
                    ContextWindow = 1050000,
                    MaxOutputTokens = 8192,
                    Description = "OpenAI最上位モデル (無料枠 25万トークン/日)",
                    IsVlm = true,
                    Provider = ApiProvider.OpenAi,
                    // maxのみ実機で拒否(400)を確認。それ以外の5段階は3モデル共通で受理される
                    SupportedEfforts = new[] { EffortLevel.None, EffortLevel.Low, EffortLevel.Medium, EffortLevel.High, EffortLevel.XHigh }
                }
            },
            {
                "gpt-5.6-terra",
                new ModelMetadata
                {
                    DisplayName = "GPT-5.6 Terra",
                    ModelId = "gpt-5.6-terra",
                    ContextWindow = 1050000,
                    MaxOutputTokens = 8192,
                    Description = "OpenAIバランス型モデル (無料枠 250万トークン/日、Lunaと共有)",
                    IsVlm = true,
                    Provider = ApiProvider.OpenAi,
                    // maxのみ実機で拒否(400)を確認。それ以外の5段階は3モデル共通で受理される
                    SupportedEfforts = new[] { EffortLevel.None, EffortLevel.Low, EffortLevel.Medium, EffortLevel.High, EffortLevel.XHigh }
                }
            },
            {
                "gpt-5.6-luna",
                new ModelMetadata
                {
                    DisplayName = "GPT-5.6 Luna",
                    ModelId = "gpt-5.6-luna",
                    ContextWindow = 1050000,
                    MaxOutputTokens = 8192,
                    Description = "OpenAI高速・低コストモデル (無料枠 250万トークン/日、Terraと共有)",
                    IsVlm = true,
                    Provider = ApiProvider.OpenAi,
                    // maxのみ実機で拒否(400)を確認。それ以外の5段階は3モデル共通で受理される
                    SupportedEfforts = new[] { EffortLevel.None, EffortLevel.Low, EffortLevel.Medium, EffortLevel.High, EffortLevel.XHigh }
                }
            }
        };
    }
}
