using Alfred.Functions.Models;
using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging;

namespace Alfred.Functions.Services.State;

public class TableStorageStateService : IStateService
{
    private const string ProcessedEmailsTable = "ProcessedEmails";
    private const string CalendarEventsTable = "CalendarEvents";

    private readonly TableServiceClient _tableServiceClient;
    private readonly ILogger<TableStorageStateService> _logger;

    public TableStorageStateService(TableServiceClient tableServiceClient, ILogger<TableStorageStateService> logger)
    {
        _tableServiceClient = tableServiceClient;
        _logger = logger;
    }

    public async Task<bool> IsEmailProcessedAsync(string messageId)
    {
        var tableClient = _tableServiceClient.GetTableClient(ProcessedEmailsTable);
        await tableClient.CreateIfNotExistsAsync();

        try
        {
            await tableClient.GetEntityAsync<ProcessedEmailEntity>("emails", messageId);
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return false;
        }
    }

    public async Task MarkEmailProcessedAsync(string messageId, string subject, string senderName, string summary)
    {
        var tableClient = _tableServiceClient.GetTableClient(ProcessedEmailsTable);
        await tableClient.CreateIfNotExistsAsync();

        var entity = new ProcessedEmailEntity
        {
            RowKey = messageId,
            Subject = subject,
            SenderName = senderName,
            Summary = summary,
            ProcessedAt = DateTimeOffset.UtcNow
        };

        await tableClient.UpsertEntityAsync(entity);
        _logger.LogInformation("Marked email {MessageId} as processed: {Subject}", messageId, subject);
    }

    public async Task<List<ProcessedEmailEntity>> GetEmailsSinceAsync(DateTimeOffset since)
    {
        var tableClient = _tableServiceClient.GetTableClient(ProcessedEmailsTable);
        await tableClient.CreateIfNotExistsAsync();

        var results = new List<ProcessedEmailEntity>();
        var query = tableClient.QueryAsync<ProcessedEmailEntity>(
            e => e.PartitionKey == "emails" && e.ProcessedAt >= since);

        await foreach (var entity in query)
        {
            results.Add(entity);
        }

        return results;
    }

    public async Task<CalendarEventEntity?> GetCalendarEventMappingAsync(string subjectHash)
    {
        var tableClient = _tableServiceClient.GetTableClient(CalendarEventsTable);
        await tableClient.CreateIfNotExistsAsync();

        try
        {
            var response = await tableClient.GetEntityAsync<CalendarEventEntity>("events", subjectHash);
            return response.Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task SaveCalendarEventMappingAsync(string subjectHash, string googleEventId, string emailId, string title, DateTimeOffset eventDate)
    {
        var tableClient = _tableServiceClient.GetTableClient(CalendarEventsTable);
        await tableClient.CreateIfNotExistsAsync();

        var entity = new CalendarEventEntity
        {
            RowKey = subjectHash,
            GoogleEventId = googleEventId,
            OriginalEmailId = emailId,
            Title = title,
            EventDate = eventDate,
            LastUpdatedAt = DateTimeOffset.UtcNow
        };

        await tableClient.UpsertEntityAsync(entity);
        _logger.LogInformation("Saved calendar event mapping: {Title} -> {EventId}", title, googleEventId);
    }

    public async Task DeleteCalendarEventMappingAsync(string subjectHash)
    {
        var tableClient = _tableServiceClient.GetTableClient(CalendarEventsTable);
        await tableClient.CreateIfNotExistsAsync();

        try
        {
            await tableClient.DeleteEntityAsync("events", subjectHash);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // Already deleted, ignore
        }
    }
}
