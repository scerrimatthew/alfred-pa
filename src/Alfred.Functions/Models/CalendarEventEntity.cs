using Azure;
using Azure.Data.Tables;

namespace Alfred.Functions.Models;

public class CalendarEventEntity : ITableEntity
{
    public string PartitionKey { get; set; } = "events";
    public string RowKey { get; set; } = string.Empty;
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public string GoogleEventId { get; set; } = string.Empty;
    public string OriginalEmailId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public DateTimeOffset EventDate { get; set; }
    public DateTimeOffset LastUpdatedAt { get; set; }
}
