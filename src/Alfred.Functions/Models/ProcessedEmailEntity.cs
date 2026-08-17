using Azure;
using Azure.Data.Tables;

namespace Alfred.Functions.Models;

public class ProcessedEmailEntity : ITableEntity
{
    public string PartitionKey { get; set; } = "emails";
    public string RowKey { get; set; } = string.Empty;
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public string Subject { get; set; } = string.Empty;
    public DateTimeOffset ProcessedAt { get; set; }
    public string Summary { get; set; } = string.Empty;
    public string SenderName { get; set; } = string.Empty;
    public string? Homework { get; set; }
    public string? Category { get; set; }
    public bool Suppressed { get; set; }
    public string? GmailThreadId { get; set; }
}
