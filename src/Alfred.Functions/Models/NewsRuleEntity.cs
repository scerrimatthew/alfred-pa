using Azure;
using Azure.Data.Tables;

namespace Alfred.Functions.Models;

public class NewsRuleEntity : ITableEntity
{
    public string PartitionKey { get; set; } = "rules";
    public string RowKey { get; set; } = string.Empty;
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    // Natural-language standing preference for the AI news digest ("stop covering funding
    // rounds", "more on EU AI Act enforcement"); applied by Claude with reasoning
    public string Instruction { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}
