using System.Collections.Generic;
using System.Threading;

namespace DiscordAIBot
{
    // AIプロバイダーとの通信を抽象化するインターフェース
    public interface IAiProvider
    {
        /// <summary>
        /// ストリーミングでAI応答を取得します。
        /// プロバイダー固有のJSONフォーマット差異などは内部で吸収し、
        /// 正規化された StreamChunk として yield return します。
        /// </summary>
        IAsyncEnumerable<StreamChunk> StreamChatAsync(
            AiRequest request,
            CancellationToken cancellationToken);
    }
}