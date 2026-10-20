using System;
using System.Collections.Generic;
using System.Linq;

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
                    Description = "デフォルトモデル-日常会話用-約60tok/sec (画像対応 / ローカル推論)",
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
                    Description = "フィルタ緩和モデル-日常会話用-約100tok/sec (画像対応 / ローカル推論)",
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
                    Description = "難問推論・複雑な設計用-約10tok/sec (画像対応 / ローカル推論)",
                    IsVlm = true,
                    Provider = ApiProvider.LmStudio
                }
            },
            {
                "ornith-1.5-9b",
                new ModelMetadata
                {
                    DisplayName = "Ornith-1.5-9B",
                    ModelId = "ornith-1.5-9b",
                    // LM Studio `/api/v0/models`の理論上限は262144だが、qwen3.8-27bの前例(023)を
                    // 踏まえ理論値は採用せず、ユーザーのデスクトップPCで実際にロードされている値
                    // (loaded_context_length=65536、ユーザー申告と一致)を登録
                    ContextWindow = 65536,
                    MaxOutputTokens = -1,
                    Description = "コーディング特化モデル-約80tok/sec (画像対応 / ローカル推論)",
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
                "gemini-4-argon",
                new ModelMetadata
                {
                    DisplayName = "Gemini 4 Argon",
                    ModelId = "gemini-4-argon",
                    ContextWindow = 1048576,
                    // Gemini 3.8 Flash(8192)より推論が深い上位モデルのため、思考で上限を使い切らないよう
                    // OpenAIと同じ32,000とした(Vertexには無料枠の事前ブロックが無く、予約量の制約も無い)
                    MaxOutputTokens = 32000,
                    Description = "Google最上位モデル (Vertex AI)",
                    IsVlm = true,
                    Provider = ApiProvider.VertexGemini,
                    // MINIMAL・XHIGHは実機で拒否(400)を確認(075)。3.8 Flashと同じ3段階
                    SupportedEfforts = new[] { EffortLevel.Low, EffortLevel.Medium, EffortLevel.High }
                }
            },
            {
                "xai/grok-4.7",
                new ModelMetadata
                {
                    DisplayName = "Grok 4.7",
                    ModelId = "xai/grok-4.7",
                    ContextWindow = 500000,
                    MaxOutputTokens = 8192,
                    Description = "xAI Grok (Vertex AI / プレビュー)",
                    // xAI公式は画像入力対応だが、VertexGrokProviderが画像をリクエストに含めない実装のため無効
                    IsVlm = false,
                    Provider = ApiProvider.VertexGrok,
                    // Noneは"minimal"にマップ。xhigh・maxはVertex経由では実機で拒否(075、4.6と同じ)
                    SupportedEfforts = new[] { EffortLevel.None, EffortLevel.Low, EffortLevel.Medium, EffortLevel.High }
                }
            },
            {
                "gpt-6.1-sol",
                new ModelMetadata
                {
                    DisplayName = "GPT-6.1 Sol",
                    ModelId = "gpt-6.1-sol",
                    ContextWindow = 1050000,
                    // max_completion_tokensとして送る値。OpenAIでは推論トークンもこの上限に含まれ、
                    // 8192ではeffort=High等で本文が空・途中切れになる実害があった(provisioning/057)。
                    // 公式上限は128,000だが、Program.csの無料枠事前ブロックがこの値を1回分の予約量として
                    // 使うため、大型枠(25万/日)で実際に使える量との兼ね合いで32,000とした
                    MaxOutputTokens = 32000,
                    Description = "OpenAI標準モデル (無料枠 25万トークン/日、Astraと共有)",
                    IsVlm = true,
                    Provider = ApiProvider.OpenAi,
                    // noneは実機で拒否(400)を確認(075)
                    SupportedEfforts = new[] { EffortLevel.Low, EffortLevel.Medium, EffortLevel.High, EffortLevel.XHigh }
                }
            },
            {
                "gpt-6-astra",
                new ModelMetadata
                {
                    DisplayName = "GPT-6 Astra",
                    ModelId = "gpt-6-astra",
                    ContextWindow = 1050000,
                    // 推論トークン込みの上限。6.1 Solと揃えて32,000(理由は6.1 Solのコメント参照)
                    MaxOutputTokens = 32000,
                    Description = "OpenAI最上位モデル (無料枠 25万トークン/日、6.1 Solと共有)",
                    IsVlm = true,
                    Provider = ApiProvider.OpenAi,
                    // noneは実機で拒否(400)を確認(075)
                    SupportedEfforts = new[] { EffortLevel.Low, EffortLevel.Medium, EffortLevel.High, EffortLevel.XHigh }
                }
            },
            {
                "gpt-5.6-luna",
                new ModelMetadata
                {
                    DisplayName = "GPT-5.6 Luna",
                    ModelId = "gpt-5.6-luna",
                    ContextWindow = 1050000,
                    // 推論トークン込みの上限。6.1 Solと揃えて32,000(理由は6.1 Solのコメント参照)
                    MaxOutputTokens = 32000,
                    Description = "OpenAI高速・低コストモデル (無料枠 250万トークン/日)",
                    IsVlm = true,
                    Provider = ApiProvider.OpenAi,
                    // maxのみ実機で拒否(400)を確認。それ以外の5段階は受理される
                    SupportedEfforts = new[] { EffortLevel.None, EffortLevel.Low, EffortLevel.Medium, EffortLevel.High, EffortLevel.XHigh }
                }
            }
        };

        // SupportedEffortsに無い段階を、対応範囲内で最も近い段階に丸める(同距離なら低い方)。
        // チャンネルのeffortはモデルを切り替えても残るため、例えばNoneのままgpt-6.1-solに
        // 切り替えると、そのままでは400になる(075)。SupportedEffortsが未指定のモデルは
        // 各Provider側のクランプに任せ、そのまま返す
        public static EffortLevel ClampEffort(string modelId, EffortLevel requested)
        {
            if (!AvailableModels.TryGetValue(modelId, out var meta) || meta.SupportedEfforts is not { Count: > 0 } supported)
            {
                return requested;
            }
            if (supported.Contains(requested))
            {
                return requested;
            }
            return supported
                .OrderBy(level => Math.Abs((int)level - (int)requested))
                .ThenBy(level => (int)level)
                .First();
        }
    }
}
