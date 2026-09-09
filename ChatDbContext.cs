using System;
using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace DiscordAIBot
{
    public class ChatMessage
    {
        // 🔴 復元・統一: インデックスの効率的な利用のため long 型に修正
        public long Id { get; set; }
        public ulong ThreadId { get; set; }
        public string Role { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
    }

    // Vertex AI経由のクラウドモデル利用ごとのトークン内訳・推定コスト(USD)を記録
    public class UsageRecord
    {
        public long Id { get; set; }
        public DateTime CreatedAt { get; set; }
        public string ModelId { get; set; } = string.Empty;
        public int PromptTokens { get; set; }
        public int CompletionTokens { get; set; }
        public int ReasoningTokens { get; set; }
        public double EstimatedCostUsd { get; set; }
        // 利用経路の可視化用("discord"/"mcp")。既存コード(ChatOrchestrator)は未指定のまま
        // Addするため、デフォルト値により後方互換で"discord"扱いになる
        public string Source { get; set; } = "discord";
    }

    // スレッド(セッション)ごとに紐づくGoogle Drive上の会話記録ファイル
    public class ThreadDriveFile
    {
        [Key]
        public ulong ThreadId { get; set; }
        public string DriveFileId { get; set; } = string.Empty;
        public string DriveFileLink { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
    }

    public class ChatDbContext : DbContext
    {
        public DbSet<ChatMessage> Messages { get; set; } = null!;
        public DbSet<UsageRecord> UsageRecords { get; set; } = null!;
        public DbSet<ThreadDriveFile> ThreadDriveFiles { get; set; } = null!;

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            optionsBuilder.UseSqlite("Data Source=chat_history.db;Cache=Shared");
        }
    }
}