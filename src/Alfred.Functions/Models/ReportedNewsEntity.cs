using Azure;
using Azure.Data.Tables;

namespace Alfred.Functions.Models;

// One news story the AI digest has already reported — passed back into later runs so the
// same story is never re-reported unless its implication changed
public class ReportedNewsEntity : ITableEntity
{
    public string PartitionKey { get; set; } = "news";
    public string RowKey { get; set; } = string.Empty; // hash of the story URL
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public string Headline { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string? Category { get; set; }
    public string? Summary { get; set; }
    public string? WhyItMatters { get; set; }
    public DateTimeOffset ReportedAt { get; set; }
}
