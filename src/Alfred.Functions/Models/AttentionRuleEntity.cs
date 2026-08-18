using Azure;
using Azure.Data.Tables;

namespace Alfred.Functions.Models;

// The positive counterpart to SuppressionRuleEntity: emails matching one of these
// patterns ALWAYS notify, regardless of the triage bar or any suppression rule.
// Matched by Claude with reasoning, not literally.
public class AttentionRuleEntity : ITableEntity
{
    public string PartitionKey { get; set; } = "rules";
    public string RowKey { get; set; } = string.Empty;
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public string Pattern { get; set; } = string.Empty;
    public string? ExampleSender { get; set; }
    public string? ExampleSubject { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
