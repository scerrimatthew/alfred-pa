using Azure;
using Azure.Data.Tables;

namespace Alfred.Functions.Models;

// A personal email Matthew asked to be reminded about later. Carries denormalized
// email details so the reminder can be sent without re-reading Gmail.
public class SnoozedEmailEntity : ITableEntity
{
    public string PartitionKey { get; set; } = "personal";
    public string RowKey { get; set; } = string.Empty; // Gmail message id
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public string Subject { get; set; } = string.Empty;
    public string SenderName { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string? ThreadId { get; set; }
    public DateTimeOffset DueAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
