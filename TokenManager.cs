using System;
using System.IO;
using System.Threading.Tasks;
using Tokenizers.HuggingFace.Tokenizer;

namespace DiscordAIBot
{
    // 1コア/1GBメモリ環境を守るためのシングルトントークン管理者（スレッドセーフ・ゼロアロケーション版）
    public static class TokenManager
    {
        private static Tokenizer? _tokenizer;
        private static bool _isInitialized = false;
        private static readonly object _lock = new object();

        public static async Task InitializeAsync()
        {
            if (_isInitialized) return;

            Console.WriteLine("⏳ [TokenManager] Tokenizers.HuggingFaceを使用して Gemma 4 の辞書データをロードしています...");
            try
            {
                await Task.Run(() => 
                {
                    string tokenizerPath = Path.Combine(AppContext.BaseDirectory, "tokenizer.json");
                    if (!File.Exists(tokenizerPath))
                    {
                        throw new FileNotFoundException($"ファイルが見つかりません: {tokenizerPath}");
                    }
                    _tokenizer = Tokenizer.FromFile(tokenizerPath);
                });
                
                _isInitialized = true;
                Console.WriteLine("✅ [TokenManager] Gemma 4 専用トークナイザーの初期化が完了しました。");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ [TokenManager] 初期化に失敗しました。文字数ベースの概算モードで稼働します。詳細: {ex.Message}");
                _tokenizer = null;
            }
        }

        public static int CountTokens(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            if (_tokenizer != null)
            {
                // ネイティブバインディングの競合を防ぐための排他制御
                lock (_lock)
                {
                    var encodings = _tokenizer.Encode(text, addSpecialTokens: false);
                    // LINQの GC Alloc を回避するため、直接列挙して最初の要素を取得
                    foreach (var encoding in encodings)
                    {
                        return encoding.Ids.Count;
                    }
                }
            }
            return (int)(text.Length * 1.5);
        }
    }
}