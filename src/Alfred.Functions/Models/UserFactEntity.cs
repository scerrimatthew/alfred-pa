using Azure;
using Azure.Data.Tables;

namespace Alfred.Functions.Models;

public class UserFactEntity : ITableEntity
{
    public string PartitionKey { get; set; } = "facts";
    public string RowKey { get; set; } = string.Empty;
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    // A durable fact about Matthew he asked Alfred to remember ("my apartment at Hillcrest
    // is A5 in Block A"); injected into personal triage and chat prompts, applied by Claude
    public string Fact { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}
