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
    private const string ChatHistoryTable = "ChatHistory";
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

    public Task MarkEmailProcessedAsync(string messageId, string subject, string senderName, string summary, string? homework = null, string? category = null, string? threadId = null) =>
        MarkProcessedAsync(SchoolPartition, messageId, subject, senderName, summary, homework, category, suppressed: false, threadId);

    public Task MarkPersonalEmailProcessedAsync(string messageId, string subject, string senderName, string summary, string? category = null, bool suppressed = false, string? threadId = null, string? senderEmail = null) =>
        MarkProcessedAsync(PersonalPartition, messageId, subject, senderName, summary, homework: null, category, suppressed, threadId, senderEmail);

    private async Task MarkProcessedAsync(string partition, string messageId, string subject, string senderName, string summary, string? homework, string? category, bool suppressed = false, string? threadId = null, string? senderEmail = null)
    {
        var tableClient = _tableServiceClient.GetTableClient(ProcessedEmailsTable);
        await tableClient.CreateIfNotExistsAsync();

        var entity = new ProcessedEmailEntity
        {
            PartitionKey = partition,
            RowKey = messageId,
            Subject = subject,
            SenderName = senderName,
            SenderEmail = senderEmail,
            Summary = summary,
            Homework = homework,
            Category = category,
            Suppressed = suppressed,
            GmailThreadId = threadId,
            ProcessedAt = DateTimeOffset.UtcNow
        };

        await tableClient.UpsertEntityAsync(entity);
        _logger.LogInformation("Marked email {MessageId} as processed: {Subject}", messageId, subject);
    }

    public async Task<ProcessedEmailEntity?> GetPersonalEmailAsync(string messageId)
    {
        var tableClient = _tableServiceClient.GetTableClient(ProcessedEmailsTable);
        await tableClient.CreateIfNotExistsAsync();

        try
        {
            var response = await tableClient.GetEntityAsync<ProcessedEmailEntity>(PersonalPartition, messageId);
            return response.Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
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

    public async Task SaveChatTurnAsync(long chatId, string question, string answer)
    {
        var tableClient = _tableServiceClient.GetTableClient(ChatHistoryTable);
        await tableClient.CreateIfNotExistsAsync();

        var now = DateTimeOffset.UtcNow;
        var partition = chatId.ToString();
        var entity = new ChatTurnEntity
        {
            PartitionKey = partition,
            RowKey = (long.MaxValue - now.UtcTicks).ToString("D19"),
            Question = question,
            Answer = answer,
            AskedAt = now
        };

        await tableClient.UpsertEntityAsync(entity);

        // Prune turns older than a day so the table never accumulates
        var cutoff = now.AddDays(-1);
        var stale = tableClient.QueryAsync<ChatTurnEntity>(
            e => e.PartitionKey == partition && e.AskedAt < cutoff);
        await foreach (var old in stale)
        {
            await tableClient.DeleteEntityAsync(old.PartitionKey, old.RowKey);
        }
    }

    public async Task<List<ChatTurnEntity>> GetRecentChatTurnsAsync(long chatId, DateTimeOffset since, int maxCount)
    {
        var tableClient = _tableServiceClient.GetTableClient(ChatHistoryTable);
        await tableClient.CreateIfNotExistsAsync();

        var partition = chatId.ToString();
        var results = new List<ChatTurnEntity>();
        var query = tableClient.QueryAsync<ChatTurnEntity>(
            e => e.PartitionKey == partition && e.AskedAt >= since);

        // RowKey is inverted ticks, so entities arrive newest first
        await foreach (var entity in query)
        {
            results.Add(entity);
            if (results.Count >= maxCount)
                break;
        }

        results.Reverse(); // chronological order for the prompt
        return results;
    }

    public async Task ClearChatTurnsAsync(long chatId)
    {
        var tableClient = _tableServiceClient.GetTableClient(ChatHistoryTable);
        await tableClient.CreateIfNotExistsAsync();

        var partition = chatId.ToString();
        var query = tableClient.QueryAsync<ChatTurnEntity>(e => e.PartitionKey == partition);
        await foreach (var entity in query)
        {
            await tableClient.DeleteEntityAsync(entity.PartitionKey, entity.RowKey);
        }

        _logger.LogInformation("Cleared chat history for chat {ChatId}", chatId);
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
