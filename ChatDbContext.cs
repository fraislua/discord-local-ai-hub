using System;
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

    public class ChatDbContext : DbContext
    {
        public DbSet<ChatMessage> Messages { get; set; } = null!;

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            optionsBuilder.UseSqlite("Data Source=chat_history.db;Cache=Shared");
        }
    }
}