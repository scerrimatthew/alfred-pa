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
    private const string SnoozedEmailsTable = "SnoozedEmails";
    private const string AttentionRulesTable = "AttentionRules";
    private const string SenderStatsTable = "SenderStats";
    private const string BackfillStateTable = "BackfillState";
    private const string ChatHistoryTable = "ChatHistory";
    private const string NewsRulesTable = "NewsRules";
    private const string ReportedNewsTable = "ReportedNews";
    private const string SchoolPartition = "emails";
    private const string PersonalPartition = "personal";
    private const string RulesPartition = "rules";
    private const string NewsPartition = "news";

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

    public Task MarkPersonalEmailProcessedAsync(string messageId, string subject, string senderName, string summary, string? category = null, bool suppressed = false, string? threadId = null, string? senderEmail = null, bool needsReply = false, DateTimeOffset? processedAt = null) =>
        MarkProcessedAsync(PersonalPartition, messageId, subject, senderName, summary, homework: null, category, suppressed, threadId, senderEmail, needsReply, processedAt);

    private async Task MarkProcessedAsync(string partition, string messageId, string subject, string senderName, string summary, string? homework, string? category, bool suppressed = false, string? threadId = null, string? senderEmail = null, bool needsReply = false, DateTimeOffset? processedAt = null)
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
            NeedsReply = needsReply,
            // Backfills backdate ProcessedAt to the email's receive date so historical
            // mail never shows up in "today's" digest or the needs-reply window
            ProcessedAt = processedAt ?? DateTimeOffset.UtcNow
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

    public async Task<List<ProcessedEmailEntity>> GetPersonalEmailsNeedingReplyAsync(DateTimeOffset since)
    {
        var tableClient = _tableServiceClient.GetTableClient(ProcessedEmailsTable);
        await tableClient.CreateIfNotExistsAsync();

        var results = new List<ProcessedEmailEntity>();
        // Explicit == true: rows written before the NeedsReply column existed lack the
        // property entirely and must not match
        var query = tableClient.QueryAsync<ProcessedEmailEntity>(
            e => e.PartitionKey == PersonalPartition && e.NeedsReply == true && e.ProcessedAt >= since);

        await foreach (var entity in query)
        {
            results.Add(entity);
        }

        return results.OrderBy(e => e.ProcessedAt).ToList();
    }

    public async Task ClearNeedsReplyAsync(string messageId)
    {
        var tableClient = _tableServiceClient.GetTableClient(ProcessedEmailsTable);
        await tableClient.CreateIfNotExistsAsync();

        try
        {
            var response = await tableClient.GetEntityAsync<ProcessedEmailEntity>(PersonalPartition, messageId);
            var entity = response.Value;
            entity.NeedsReply = false;
            await tableClient.UpsertEntityAsync(entity);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // Gone from state — nothing to clear
        }
    }

    public async Task<List<ProcessedEmailEntity>> GetPersonalEmailsByThreadAsync(string threadId)
    {
        var tableClient = _tableServiceClient.GetTableClient(ProcessedEmailsTable);
        await tableClient.CreateIfNotExistsAsync();

        var results = new List<ProcessedEmailEntity>();
        var query = tableClient.QueryAsync<ProcessedEmailEntity>(
            e => e.PartitionKey == PersonalPartition && e.GmailThreadId == threadId);

        await foreach (var entity in query)
        {
            results.Add(entity);
        }

        return results.OrderBy(e => e.ProcessedAt).ToList();
    }

    public async Task SaveSnoozeAsync(string messageId, string subject, string senderName, string summary, string? threadId, DateTimeOffset dueAt)
    {
        var tableClient = _tableServiceClient.GetTableClient(SnoozedEmailsTable);
        await tableClient.CreateIfNotExistsAsync();

        var entity = new SnoozedEmailEntity
        {
            PartitionKey = PersonalPartition,
            RowKey = messageId,
            Subject = subject,
            SenderName = senderName,
            Summary = summary,
            ThreadId = threadId,
            DueAt = dueAt,
            CreatedAt = DateTimeOffset.UtcNow
        };

        await tableClient.UpsertEntityAsync(entity);
        _logger.LogInformation("Snoozed email {MessageId} until {DueAt}: {Subject}", messageId, dueAt, subject);
    }

    public async Task<List<SnoozedEmailEntity>> GetDueSnoozesAsync(DateTimeOffset now)
    {
        var tableClient = _tableServiceClient.GetTableClient(SnoozedEmailsTable);
        await tableClient.CreateIfNotExistsAsync();

        var results = new List<SnoozedEmailEntity>();
        var query = tableClient.QueryAsync<SnoozedEmailEntity>(
            e => e.PartitionKey == PersonalPartition && e.DueAt <= now);

        await foreach (var entity in query)
        {
            results.Add(entity);
        }

        return results;
    }

    public async Task<List<SnoozedEmailEntity>> GetSnoozesAsync()
    {
        var tableClient = _tableServiceClient.GetTableClient(SnoozedEmailsTable);
        await tableClient.CreateIfNotExistsAsync();

        var results = new List<SnoozedEmailEntity>();
        var query = tableClient.QueryAsync<SnoozedEmailEntity>(e => e.PartitionKey == PersonalPartition);

        await foreach (var entity in query)
        {
            results.Add(entity);
        }

        return results.OrderBy(s => s.DueAt).ToList();
    }

    public async Task DeleteSnoozeAsync(string messageId)
    {
        var tableClient = _tableServiceClient.GetTableClient(SnoozedEmailsTable);
        await tableClient.CreateIfNotExistsAsync();

        try
        {
            await tableClient.DeleteEntityAsync(PersonalPartition, messageId);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // Already gone, ignore
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

    public async Task<BackfillStateEntity?> GetBackfillStateAsync()
    {
        var tableClient = _tableServiceClient.GetTableClient(BackfillStateTable);
        await tableClient.CreateIfNotExistsAsync();

        try
        {
            return (await tableClient.GetEntityAsync<BackfillStateEntity>(PersonalPartition, "backfill")).Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task SaveBackfillStateAsync(BackfillStateEntity entity)
    {
        var tableClient = _tableServiceClient.GetTableClient(BackfillStateTable);
        await tableClient.CreateIfNotExistsAsync();
        await tableClient.UpsertEntityAsync(entity);
    }

    public async Task ClearBackfillStateAsync()
    {
        var tableClient = _tableServiceClient.GetTableClient(BackfillStateTable);
        await tableClient.CreateIfNotExistsAsync();

        try
        {
            await tableClient.DeleteEntityAsync(PersonalPartition, "backfill");
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // Already gone, ignore
        }
    }

    public async Task RecordSenderSeenAsync(string senderEmail, string senderName, bool wasQuiet, string? listUnsubscribe, bool oneClick)
    {
        var tableClient = _tableServiceClient.GetTableClient(SenderStatsTable);
        await tableClient.CreateIfNotExistsAsync();

        var rowKey = SenderStatsEntity.RowKeyFor(senderEmail);
        SenderStatsEntity entity;
        try
        {
            entity = (await tableClient.GetEntityAsync<SenderStatsEntity>(PersonalPartition, rowKey)).Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            entity = new SenderStatsEntity { RowKey = rowKey, SenderEmail = senderEmail };
        }

        entity.SenderName = senderName;
        entity.TotalCount++;
        if (wasQuiet) entity.QuietCount++;
        entity.LastSeen = DateTimeOffset.UtcNow;
        if (!string.IsNullOrWhiteSpace(listUnsubscribe))
        {
            entity.ListUnsubscribe = listUnsubscribe;
            entity.ListUnsubscribeOneClick = oneClick;
        }

        await tableClient.UpsertEntityAsync(entity);
    }

    public async Task<List<SenderStatsEntity>> GetUnsubscribeCandidatesAsync(int minEmails, int maxCandidates)
    {
        var tableClient = _tableServiceClient.GetTableClient(SenderStatsTable);
        await tableClient.CreateIfNotExistsAsync();

        var results = new List<SenderStatsEntity>();
        var query = tableClient.QueryAsync<SenderStatsEntity>(
            e => e.PartitionKey == PersonalPartition && e.Unsubscribed == false);

        await foreach (var entity in query)
        {
            // A candidate: enough volume, never once attention-worthy, offers an
            // unsubscribe mechanism, and not already proposed to Matthew
            if (entity.TotalCount >= minEmails
                && entity.QuietCount == entity.TotalCount
                && !string.IsNullOrWhiteSpace(entity.ListUnsubscribe)
                && entity.ProposedAt is null)
            {
                results.Add(entity);
            }
        }

        return results.OrderByDescending(e => e.TotalCount).Take(maxCandidates).ToList();
    }

    public async Task<SenderStatsEntity?> GetSenderStatAsync(string rowKey)
    {
        var tableClient = _tableServiceClient.GetTableClient(SenderStatsTable);
        await tableClient.CreateIfNotExistsAsync();

        try
        {
            return (await tableClient.GetEntityAsync<SenderStatsEntity>(PersonalPartition, rowKey)).Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task UpsertSenderStatAsync(SenderStatsEntity entity)
    {
        var tableClient = _tableServiceClient.GetTableClient(SenderStatsTable);
        await tableClient.CreateIfNotExistsAsync();
        await tableClient.UpsertEntityAsync(entity);
    }

    public async Task<List<AttentionRuleEntity>> GetAttentionRulesAsync()
    {
        var tableClient = _tableServiceClient.GetTableClient(AttentionRulesTable);
        await tableClient.CreateIfNotExistsAsync();

        var results = new List<AttentionRuleEntity>();
        var query = tableClient.QueryAsync<AttentionRuleEntity>(e => e.PartitionKey == RulesPartition);

        await foreach (var entity in query)
        {
            results.Add(entity);
        }

        return results;
    }

    public async Task SaveAttentionRuleAsync(string ruleId, string pattern, string? exampleSender, string? exampleSubject)
    {
        var tableClient = _tableServiceClient.GetTableClient(AttentionRulesTable);
        await tableClient.CreateIfNotExistsAsync();

        var entity = new AttentionRuleEntity
        {
            RowKey = ruleId,
            Pattern = pattern,
            ExampleSender = exampleSender,
            ExampleSubject = exampleSubject,
            CreatedAt = DateTimeOffset.UtcNow
        };

        await tableClient.UpsertEntityAsync(entity);
        _logger.LogInformation("Saved attention rule {RuleId}: {Pattern}", ruleId, pattern);
    }

    public async Task DeleteAttentionRuleAsync(string ruleId)
    {
        var tableClient = _tableServiceClient.GetTableClient(AttentionRulesTable);
        await tableClient.CreateIfNotExistsAsync();

        try
        {
            await tableClient.DeleteEntityAsync(RulesPartition, ruleId);
            _logger.LogInformation("Deleted attention rule {RuleId}", ruleId);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // Already gone, ignore
        }
    }

    public async Task<List<NewsRuleEntity>> GetNewsRulesAsync()
    {
        var tableClient = _tableServiceClient.GetTableClient(NewsRulesTable);
        await tableClient.CreateIfNotExistsAsync();

        var results = new List<NewsRuleEntity>();
        var query = tableClient.QueryAsync<NewsRuleEntity>(e => e.PartitionKey == RulesPartition);

        await foreach (var entity in query)
        {
            results.Add(entity);
        }

        return results;
    }

    public async Task SaveNewsRuleAsync(string ruleId, string instruction)
    {
        var tableClient = _tableServiceClient.GetTableClient(NewsRulesTable);
        await tableClient.CreateIfNotExistsAsync();

        var entity = new NewsRuleEntity
        {
            RowKey = ruleId,
            Instruction = instruction,
            CreatedAt = DateTimeOffset.UtcNow
        };

        await tableClient.UpsertEntityAsync(entity);
        _logger.LogInformation("Saved news rule {RuleId}: {Instruction}", ruleId, instruction);
    }

    public async Task DeleteNewsRuleAsync(string ruleId)
    {
        var tableClient = _tableServiceClient.GetTableClient(NewsRulesTable);
        await tableClient.CreateIfNotExistsAsync();

        try
        {
            await tableClient.DeleteEntityAsync(RulesPartition, ruleId);
            _logger.LogInformation("Deleted news rule {RuleId}", ruleId);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // Already gone, ignore
        }
    }

    public async Task<List<ReportedNewsEntity>> GetReportedNewsSinceAsync(DateTimeOffset since)
    {
        var tableClient = _tableServiceClient.GetTableClient(ReportedNewsTable);
        await tableClient.CreateIfNotExistsAsync();

        var results = new List<ReportedNewsEntity>();
        var query = tableClient.QueryAsync<ReportedNewsEntity>(
            e => e.PartitionKey == NewsPartition && e.ReportedAt >= since);

        await foreach (var entity in query)
        {
            results.Add(entity);
        }

        return results;
    }

    public async Task SaveReportedNewsAsync(List<AiNewsItem> items)
    {
        var tableClient = _tableServiceClient.GetTableClient(ReportedNewsTable);
        await tableClient.CreateIfNotExistsAsync();

        var now = DateTimeOffset.UtcNow;
        foreach (var item in items)
        {
            var entity = new ReportedNewsEntity
            {
                PartitionKey = NewsPartition,
                // Keyed by URL hash so re-reporting the same story overwrites, not duplicates
                RowKey = HashUrl(item.Url),
                Headline = item.Headline,
                Url = item.Url,
                Category = item.Category,
                ReportedAt = now
            };
            await tableClient.UpsertEntityAsync(entity);
        }

        // Prune entries past any dedup window so the table never accumulates
        var cutoff = now.AddDays(-60);
        var stale = tableClient.QueryAsync<ReportedNewsEntity>(
            e => e.PartitionKey == NewsPartition && e.ReportedAt < cutoff);
        await foreach (var old in stale)
        {
            await tableClient.DeleteEntityAsync(old.PartitionKey, old.RowKey);
        }
    }

    internal static string HashUrl(string url)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(url));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
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
