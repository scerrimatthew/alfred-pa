using Azure;
using Azure.Data.Tables;

namespace Alfred.Functions.Models;

// One claimed Telegram update_id. Telegram re-delivers updates it thinks failed (slow
// web-search answers, long /news runs), so each update is claimed exactly once at the
// webhook entrance and duplicates are dropped instead of double-processed.
public class ProcessedUpdateEntity : ITableEntity
{
    public string PartitionKey { get; set; } = "personal";
    public string RowKey { get; set; } = string.Empty; // the Telegram update_id
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public DateTimeOffset ClaimedAt { get; set; }
}
