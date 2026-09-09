using System.Collections.Generic;

namespace DiscordAIBot
{
    public struct SystemPromptMetadata
    {
        public string DisplayName { get; init; }
        public string Description { get; init; }
        public string Prompt { get; init; }
    }

    // /promptコマンドで切り替え可能なシステムプロンプトの一覧。
    // 用途が増えたらここにエントリを追加していく想定
    public static class SystemPromptRegistry
    {
        public const string DefaultPromptId = "creative";

        public static readonly Dictionary<string, SystemPromptMetadata> AvailablePrompts = new()
        {
            {
                "none",
                new SystemPromptMetadata
                {
                    DisplayName = "プロンプトなし",
                    Description = "システムプロンプトを設定しません（モデル標準の応答）",
                    Prompt = ""
                }
            },
            {
                "creative",
                new SystemPromptMetadata
                {
                    DisplayName = "創作アシスタント",
                    Description = "ゲーム企画・アイデア出し向け（デフォルト）",
                    Prompt = "優秀な創作アシスタントとして、ゲームのアイデア、コアループ、システム設計、企画書のブラッシュアップを支援してください。ステップバイステップで深く思考し、クリエイティブな提案を行ってください。"
                }
            }
        };
    }
}
