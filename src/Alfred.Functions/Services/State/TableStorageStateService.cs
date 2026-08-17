using Alfred.Functions.Models;
using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging;

namespace Alfred.Functions.Services.State;

public class TableStorageStateService : IStateService
{
    private const string ProcessedEmailsTable = "ProcessedEmails";
    private const string CalendarEventsTable = "CalendarEvents";
    private const string SuppressionRulesTable = "SuppressionRules";
    private const string SchoolPartition = "emails";
    private const string PersonalPartition = "personal";
    private const string RulesPartition = "rules";

    private readonly TableServiceClient _tableServiceClient;
    private readonly ILogger<TableStorageStateService> _logger;

    public TableStorageStateService(TableServiceClient tableServiceClient, ILogger<TableStorageStateService> logger)
    {
        _tableServiceClient = tableServiceClient;
        _logger = logger;
    }

    public Task<bool> IsEmailProcessedAsync(string messageId) =>
        IsProcessedAsync(SchoolPartition, messageId);

    public Task<bool> IsPersonalEmailProcessedAsync(string messageId) =>
        IsProcessedAsync(PersonalPartition, messageId);

    private async Task<bool> IsProcessedAsync(string partition, string messageId)
    {
        var tableClient = _tableServiceClient.GetTableClient(ProcessedEmailsTable);
        await tableClient.CreateIfNotExistsAsync();

        try
        {
            await tableClient.GetEntityAsync<ProcessedEmailEntity>(partition, messageId);
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return false;
        }
    }

    public Task MarkEmailProcessedAsync(string messageId, string subject, string senderName, string summary, string? homework = null, string? category = null) =>
        MarkProcessedAsync(SchoolPartition, messageId, subject, senderName, summary, homework, category);

    public Task MarkPersonalEmailProcessedAsync(string messageId, string subject, string senderName, string summary, string? category = null, bool suppressed = false) =>
        MarkProcessedAsync(PersonalPartition, messageId, subject, senderName, summary, homework: null, category, suppressed);

    private async Task MarkProcessedAsync(string partition, string messageId, string subject, string senderName, string summary, string? homework, string? category, bool suppressed = false)
    {
        var tableClient = _tableServiceClient.GetTableClient(ProcessedEmailsTable);
        await tableClient.CreateIfNotExistsAsync();

        var entity = new ProcessedEmailEntity
        {
            PartitionKey = partition,
            RowKey = messageId,
            Subject = subject,
            SenderName = senderName,
            Summary = summary,
            Homework = homework,
            Category = category,
            Suppressed = suppressed,
            ProcessedAt = DateTimeOffset.UtcNow
        };

        await tableClient.UpsertEntityAsync(entity);
        _logger.LogInformation("Marked email {MessageId} as processed: {Subject}", messageId, subject);
    }

    public Task<List<ProcessedEmailEntity>> GetEmailsSinceAsync(DateTimeOffset since) =>
        GetEmailsSinceCoreAsync(SchoolPartition, since);

    public Task<List<ProcessedEmailEntity>> GetPersonalEmailsSinceAsync(DateTimeOffset since) =>
        GetEmailsSinceCoreAsync(PersonalPartition, since);

    private async Task<List<ProcessedEmailEntity>> GetEmailsSinceCoreAsync(string partition, DateTimeOffset since)
    {
        var tableClient = _tableServiceClient.GetTableClient(ProcessedEmailsTable);
        await tableClient.CreateIfNotExistsAsync();

        var results = new List<ProcessedEmailEntity>();
        var query = tableClient.QueryAsync<ProcessedEmailEntity>(
            e => e.PartitionKey == partition && e.ProcessedAt >= since);

        await foreach (var entity in query)
        {
            results.Add(entity);
        }

        return results;
    }

    public async Task UpdatePersonalEmailCategoryAsync(string messageId, string category)
    {
        var tableClient = _tableServiceClient.GetTableClient(ProcessedEmailsTable);
        await tableClient.CreateIfNotExistsAsync();

        try
        {
            var response = await tableClient.GetEntityAsync<ProcessedEmailEntity>(PersonalPartition, messageId);
            var entity = response.Value;
            entity.Category = category;
            await tableClient.UpsertEntityAsync(entity);
            _logger.LogInformation("Updated category of {MessageId} to {Category}", messageId, category);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            _logger.LogWarning("Cannot update category — personal email {MessageId} not found in state", messageId);
        }
    }

    public async Task<List<SuppressionRuleEntity>> GetSuppressionRulesAsync()
    {
        var tableClient = _tableServiceClient.GetTableClient(SuppressionRulesTable);
        await tableClient.CreateIfNotExistsAsync();

        var results = new List<SuppressionRuleEntity>();
        var query = tableClient.QueryAsync<SuppressionRuleEntity>(e => e.PartitionKey == RulesPartition);

        await foreach (var entity in query)
        {
            results.Add(entity);
        }

        return results;
    }

    public async Task SaveSuppressionRuleAsync(string ruleId, string pattern, string? exampleSender, string? exampleSubject)
    {
        var tableClient = _tableServiceClient.GetTableClient(SuppressionRulesTable);
        await tableClient.CreateIfNotExistsAsync();

        var entity = new SuppressionRuleEntity
        {
            RowKey = ruleId,
            Pattern = pattern,
            ExampleSender = exampleSender,
            ExampleSubject = exampleSubject,
            CreatedAt = DateTimeOffset.UtcNow
        };

        await tableClient.UpsertEntityAsync(entity);
        _logger.LogInformation("Saved suppression rule {RuleId}: {Pattern}", ruleId, pattern);
    }

    public async Task DeleteSuppressionRuleAsync(string ruleId)
    {
        var tableClient = _tableServiceClient.GetTableClient(SuppressionRulesTable);
        await tableClient.CreateIfNotExistsAsync();

        try
        {
            await tableClient.DeleteEntityAsync(RulesPartition, ruleId);
            _logger.LogInformation("Deleted suppression rule {RuleId}", ruleId);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // Already gone, ignore
        }
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
