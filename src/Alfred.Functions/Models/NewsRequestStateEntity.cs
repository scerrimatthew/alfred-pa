using Azure;
using Azure.Data.Tables;

namespace Alfred.Functions.Models;

// Single-row marker while an on-demand /news research run is in flight. Research takes
// minutes, so Telegram may re-deliver the webhook update before the run finishes — the
// marker lets duplicates (and impatient re-sends) be dropped instead of double-researched.
public class NewsRequestStateEntity : ITableEntity
{
    public string PartitionKey { get; set; } = "personal";
    public string RowKey { get; set; } = "news-request";
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public DateTimeOffset RequestedAt { get; set; }
    public string? Topic { get; set; }
}
