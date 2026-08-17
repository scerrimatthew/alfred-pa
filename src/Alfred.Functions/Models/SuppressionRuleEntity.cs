using Azure;
using Azure.Data.Tables;

namespace Alfred.Functions.Models;

public class SuppressionRuleEntity : ITableEntity
{
    public string PartitionKey { get; set; } = "rules";
    public string RowKey { get; set; } = string.Empty;
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    // Natural-language description of what to suppress; matched by Claude with reasoning, not literally
    public string Pattern { get; set; } = string.Empty;
    public string? ExampleSender { get; set; }
    public string? ExampleSubject { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
