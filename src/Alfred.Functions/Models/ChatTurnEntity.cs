using Azure;
using Azure.Data.Tables;

namespace Alfred.Functions.Models;

public class ChatTurnEntity : ITableEntity
{
    public string PartitionKey { get; set; } = string.Empty; // Telegram chat id
    public string RowKey { get; set; } = string.Empty;       // inverted ticks, so queries return newest first
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public string Question { get; set; } = string.Empty;
    public string Answer { get; set; } = string.Empty;
    public DateTimeOffset AskedAt { get; set; }
}
