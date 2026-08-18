using Azure;
using Azure.Data.Tables;

namespace Alfred.Functions.Models;

// Single-row marker for an in-progress quiet backfill of the personal inbox.
// While present, each PersonalEmailMonitor run works through one batch of
// historical emails (oldest first); the row is deleted when the sweep completes.
public class BackfillStateEntity : ITableEntity
{
    public string PartitionKey { get; set; } = "personal";
    public string RowKey { get; set; } = "backfill";
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public DateTimeOffset OldestDate { get; set; } // fixed window start, set when requested
    public DateTimeOffset RequestedAt { get; set; }
    public int ProcessedCount { get; set; }
}
