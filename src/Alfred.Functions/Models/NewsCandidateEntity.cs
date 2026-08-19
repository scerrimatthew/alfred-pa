using Azure;
using Azure.Data.Tables;

namespace Alfred.Functions.Models;

// A story lead mined from an AI newsletter that landed in the personal inbox — fed to the
// evening news research run as candidate material web search might miss
public class NewsCandidateEntity : ITableEntity
{
    public string PartitionKey { get; set; } = "news";
    public string RowKey { get; set; } = string.Empty; // hash of the lead URL, or headline when no URL
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public string Headline { get; set; } = string.Empty;
    public string? Url { get; set; }
    public string? Note { get; set; }
    public string Source { get; set; } = string.Empty; // newsletter sender name
    public DateTimeOffset SeenAt { get; set; }
}
