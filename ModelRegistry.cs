using System.Collections.Generic;

namespace DiscordAIBot
{
    public enum ApiProvider 
    { 
        LmStudio, 
        GoogleAiStudio 
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
    }

    public static class ModelRegistry
    {
        // プロンプトエンジニアリング・Unity実装など、用途ごとのデフォルトモデル定数
        public const string DefaultModelId = "google/gemma-4-12b-qat";
        public const string ChatAiDefaultModelId = "gemini-3.5-flash";

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
                "gemma-4-26b-a4b-it", 
                new ModelMetadata 
                { 
                    DisplayName = "Gemma-4-26B-A4B", 
                    ModelId = "gemma-4-26b-a4b-it", 
                    ContextWindow = 65536, 
                    MaxOutputTokens = -1, 
                    Description = "高精度-低速推論 (ローカル推論)", 
                    IsVlm = false,
                    Provider = ApiProvider.LmStudio
                }
            },
            {
                "gemini-3.5-flash", 
                new ModelMetadata 
                { 
                    DisplayName = "Gemini 3.5 Flash", 
                    ModelId = "gemini-3.5-flash", 
                    ContextWindow = 1048576, 
                    MaxOutputTokens = 8192, 
                    Description = "高速・長文脈モデル (Google AI Studio)", 
                    IsVlm = true,
                    Provider = ApiProvider.GoogleAiStudio
                }
            }
        };
    }
}