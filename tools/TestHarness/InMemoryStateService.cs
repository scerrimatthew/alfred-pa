using Alfred.Functions.Models;
using Alfred.Functions.Services.State;

namespace TestHarness;

public class InMemoryStateService : IStateService
{
    private readonly Dictionary<string, ProcessedEmailEntity> _emails = new();
    private readonly Dictionary<string, ProcessedEmailEntity> _personalEmails = new();
    private readonly Dictionary<string, CalendarEventEntity> _events = new();

    public Task<bool> IsEmailProcessedAsync(string messageId) =>
        Task.FromResult(_emails.ContainsKey(messageId));

    public Task MarkEmailProcessedAsync(string messageId, string subject, string senderName, string summary, string? homework = null, string? category = null, string? threadId = null)
    {
        _emails[messageId] = new ProcessedEmailEntity
        {
            RowKey = messageId,
            Subject = subject,
            SenderName = senderName,
            Summary = summary,
            Homework = homework,
            Category = category,
            ProcessedAt = DateTimeOffset.UtcNow
        };
        Console.WriteLine($"  [State] Marked as processed: {subject}");
        return Task.CompletedTask;
    }

    public Task<bool> IsPersonalEmailProcessedAsync(string messageId) =>
        Task.FromResult(_personalEmails.ContainsKey(messageId));

    public Task MarkPersonalEmailProcessedAsync(string messageId, string subject, string senderName, string summary, string? category = null, bool suppressed = false, string? threadId = null, string? senderEmail = null, bool needsReply = false, DateTimeOffset? processedAt = null)
    {
        _personalEmails[messageId] = new ProcessedEmailEntity
        {
            PartitionKey = "personal",
            RowKey = messageId,
            Subject = subject,
            SenderName = senderName,
            SenderEmail = senderEmail,
            Summary = summary,
            Category = category,
            Suppressed = suppressed,
            GmailThreadId = threadId,
            NeedsReply = needsReply,
            ProcessedAt = processedAt ?? DateTimeOffset.UtcNow
        };
        Console.WriteLine($"  [State] Marked personal email as processed: {subject}");
        return Task.CompletedTask;
    }

    private BackfillStateEntity? _backfillState;

    public Task<BackfillStateEntity?> GetBackfillStateAsync() => Task.FromResult(_backfillState);

    public Task SaveBackfillStateAsync(BackfillStateEntity entity)
    {
        _backfillState = entity;
        return Task.CompletedTask;
    }

    public Task ClearBackfillStateAsync()
    {
        _backfillState = null;
        return Task.CompletedTask;
    }

    public Task<ProcessedEmailEntity?> GetPersonalEmailAsync(string messageId) =>
        Task.FromResult(_personalEmails.GetValueOrDefault(messageId));

    public Task<List<ProcessedEmailEntity>> GetPersonalEmailsByThreadAsync(string threadId) =>
        Task.FromResult(_personalEmails.Values
            .Where(e => e.GmailThreadId == threadId)
            .OrderBy(e => e.ProcessedAt)
            .ToList());

    public Task<List<ProcessedEmailEntity>> GetPersonalEmailsNeedingReplyAsync(DateTimeOffset since) =>
        Task.FromResult(_personalEmails.Values
            .Where(e => e.NeedsReply && e.ProcessedAt >= since)
            .OrderBy(e => e.ProcessedAt)
            .ToList());

    public Task ClearNeedsReplyAsync(string messageId)
    {
        if (_personalEmails.TryGetValue(messageId, out var entity))
            entity.NeedsReply = false;
        return Task.CompletedTask;
    }

    private readonly Dictionary<string, SnoozedEmailEntity> _snoozes = new();

    public Task SaveSnoozeAsync(string messageId, string subject, string senderName, string summary, string? threadId, DateTimeOffset dueAt)
    {
        _snoozes[messageId] = new SnoozedEmailEntity
        {
            RowKey = messageId,
            Subject = subject,
            SenderName = senderName,
            Summary = summary,
            ThreadId = threadId,
            DueAt = dueAt,
            CreatedAt = DateTimeOffset.UtcNow
        };
        return Task.CompletedTask;
    }

    public Task<List<SnoozedEmailEntity>> GetDueSnoozesAsync(DateTimeOffset now) =>
        Task.FromResult(_snoozes.Values.Where(s => s.DueAt <= now).ToList());

    public Task<List<SnoozedEmailEntity>> GetSnoozesAsync() =>
        Task.FromResult(_snoozes.Values.OrderBy(s => s.DueAt).ToList());

    public Task DeleteSnoozeAsync(string messageId)
    {
        _snoozes.Remove(messageId);
        return Task.CompletedTask;
    }

    private readonly Dictionary<string, AttentionRuleEntity> _attentionRules = new();

    public Task<List<AttentionRuleEntity>> GetAttentionRulesAsync() =>
        Task.FromResult(_attentionRules.Values.ToList());

    public Task SaveAttentionRuleAsync(string ruleId, string pattern, string? exampleSender, string? exampleSubject)
    {
        _attentionRules[ruleId] = new AttentionRuleEntity
        {
            RowKey = ruleId,
            Pattern = pattern,
            ExampleSender = exampleSender,
            ExampleSubject = exampleSubject,
            CreatedAt = DateTimeOffset.UtcNow
        };
        return Task.CompletedTask;
    }

    public Task DeleteAttentionRuleAsync(string ruleId)
    {
        _attentionRules.Remove(ruleId);
        return Task.CompletedTask;
    }

    private readonly Dictionary<string, NewsRuleEntity> _newsRules = new();
    private readonly Dictionary<string, ReportedNewsEntity> _reportedNews = new();

    public Task<List<NewsRuleEntity>> GetNewsRulesAsync() =>
        Task.FromResult(_newsRules.Values.ToList());

    public Task SaveNewsRuleAsync(string ruleId, string instruction)
    {
        _newsRules[ruleId] = new NewsRuleEntity
        {
            RowKey = ruleId,
            Instruction = instruction,
            CreatedAt = DateTimeOffset.UtcNow
        };
        return Task.CompletedTask;
    }

    public Task DeleteNewsRuleAsync(string ruleId)
    {
        _newsRules.Remove(ruleId);
        return Task.CompletedTask;
    }

    public Task<List<ReportedNewsEntity>> GetReportedNewsSinceAsync(DateTimeOffset since) =>
        Task.FromResult(_reportedNews.Values.Where(e => e.ReportedAt >= since).ToList());

    public Task<ReportedNewsEntity?> GetReportedNewsAsync(string rowKey) =>
        Task.FromResult(_reportedNews.GetValueOrDefault(rowKey));

    public Task SaveReportedNewsAsync(List<AiNewsItem> items)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var item in items)
        {
            var rowKey = HashUrl(item.Url);
            _reportedNews[rowKey] = new ReportedNewsEntity
            {
                RowKey = rowKey,
                Headline = item.Headline,
                Url = item.Url,
                Category = item.Category,
                Summary = item.Summary,
                WhyItMatters = item.WhyItMatters,
                ReportedAt = now
            };
        }
        return Task.CompletedTask;
    }

    // Same keying as TableStorageStateService.HashUrl so feedback-button lookups
    // (which pass the hash) behave like production
    private static string HashUrl(string url)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(url));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    private readonly Dictionary<string, NewsCandidateEntity> _newsCandidates = new();

    public Task SaveNewsCandidatesAsync(List<NewsCandidateEntity> candidates)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var candidate in candidates)
        {
            candidate.RowKey = HashUrl(!string.IsNullOrWhiteSpace(candidate.Url)
                ? candidate.Url
                : candidate.Headline);
            candidate.SeenAt = now;
            _newsCandidates[candidate.RowKey] = candidate;
        }
        return Task.CompletedTask;
    }

    public Task<List<NewsCandidateEntity>> GetNewsCandidatesSinceAsync(DateTimeOffset since) =>
        Task.FromResult(_newsCandidates.Values.Where(c => c.SeenAt >= since).ToList());

    private NewsRequestStateEntity? _newsRequest;

    public Task<NewsRequestStateEntity?> GetNewsRequestAsync() => Task.FromResult(_newsRequest);

    public Task SaveNewsRequestAsync(NewsRequestStateEntity entity)
    {
        _newsRequest = entity;
        return Task.CompletedTask;
    }

    public Task ClearNewsRequestAsync()
    {
        _newsRequest = null;
        return Task.CompletedTask;
    }

    private readonly HashSet<long> _claimedUpdates = new();

    public Task<bool> TryClaimUpdateAsync(long updateId) =>
        Task.FromResult(_claimedUpdates.Add(updateId));

    private readonly Dictionary<string, SenderStatsEntity> _senderStats = new();

    public Task RecordSenderSeenAsync(string senderEmail, string senderName, bool wasQuiet, string? listUnsubscribe, bool oneClick)
    {
        var rowKey = SenderStatsEntity.RowKeyFor(senderEmail);
        if (!_senderStats.TryGetValue(rowKey, out var entity))
        {
            entity = new SenderStatsEntity { RowKey = rowKey, SenderEmail = senderEmail };
            _senderStats[rowKey] = entity;
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
        return Task.CompletedTask;
    }

    public Task<List<SenderStatsEntity>> GetUnsubscribeCandidatesAsync(int minEmails, int maxCandidates) =>
        Task.FromResult(_senderStats.Values
            .Where(e => !e.Unsubscribed
                && e.TotalCount >= minEmails
                && e.QuietCount == e.TotalCount
                && !string.IsNullOrWhiteSpace(e.ListUnsubscribe)
                && e.ProposedAt is null)
            .OrderByDescending(e => e.TotalCount)
            .Take(maxCandidates)
            .ToList());

    public Task<SenderStatsEntity?> GetSenderStatAsync(string rowKey) =>
        Task.FromResult(_senderStats.GetValueOrDefault(rowKey));

    public Task UpsertSenderStatAsync(SenderStatsEntity entity)
    {
        _senderStats[entity.RowKey] = entity;
        return Task.CompletedTask;
    }

    private readonly Dictionary<string, SuppressionRuleEntity> _rules = new();

    public Task<List<SuppressionRuleEntity>> GetSuppressionRulesAsync() =>
        Task.FromResult(_rules.Values.ToList());

    public Task SaveSuppressionRuleAsync(string ruleId, string pattern, string? exampleSender, string? exampleSubject)
    {
        _rules[ruleId] = new SuppressionRuleEntity
        {
            RowKey = ruleId,
            Pattern = pattern,
            ExampleSender = exampleSender,
            ExampleSubject = exampleSubject,
            CreatedAt = DateTimeOffset.UtcNow
        };
        return Task.CompletedTask;
    }

    public Task DeleteSuppressionRuleAsync(string ruleId)
    {
        _rules.Remove(ruleId);
        return Task.CompletedTask;
    }

    public Task UpdatePersonalEmailCategoryAsync(string messageId, string category)
    {
        if (_personalEmails.TryGetValue(messageId, out var entity))
            entity.Category = category;
        return Task.CompletedTask;
    }

    public Task<List<ProcessedEmailEntity>> GetEmailsSinceAsync(DateTimeOffset since) =>
        Task.FromResult(_emails.Values.Where(e => e.ProcessedAt >= since).ToList());

    public Task<List<ProcessedEmailEntity>> GetPersonalEmailsSinceAsync(DateTimeOffset since) =>
        Task.FromResult(_personalEmails.Values.Where(e => e.ProcessedAt >= since).ToList());

    private readonly Dictionary<long, List<ChatTurnEntity>> _chatTurns = new();

    public Task SaveChatTurnAsync(long chatId, string question, string answer)
    {
        var now = DateTimeOffset.UtcNow;
        if (!_chatTurns.TryGetValue(chatId, out var turns))
        {
            turns = new List<ChatTurnEntity>();
            _chatTurns[chatId] = turns;
        }

        turns.Add(new ChatTurnEntity
        {
            PartitionKey = chatId.ToString(),
            RowKey = (long.MaxValue - now.UtcTicks).ToString("D19"),
            Question = question,
            Answer = answer,
            AskedAt = now
        });

        // Prune turns older than a day, matching the table implementation
        turns.RemoveAll(t => t.AskedAt < now.AddDays(-1));
        return Task.CompletedTask;
    }

    public Task<List<ChatTurnEntity>> GetRecentChatTurnsAsync(long chatId, DateTimeOffset since, int maxCount)
    {
        var turns = _chatTurns.GetValueOrDefault(chatId) ?? new List<ChatTurnEntity>();
        var recent = turns
            .Where(t => t.AskedAt >= since)
            .OrderByDescending(t => t.AskedAt)
            .Take(maxCount)
            .Reverse() // chronological order for the prompt
            .ToList();
        return Task.FromResult(recent);
    }

    public Task ClearChatTurnsAsync(long chatId)
    {
        _chatTurns.Remove(chatId);
        return Task.CompletedTask;
    }

    public Task<CalendarEventEntity?> GetCalendarEventMappingAsync(string subjectHash) =>
        Task.FromResult(_events.GetValueOrDefault(subjectHash));

    public Task SaveCalendarEventMappingAsync(string subjectHash, string googleEventId, string emailId, string title, DateTimeOffset eventDate)
    {
        _events[subjectHash] = new CalendarEventEntity
        {
            RowKey = subjectHash,
            GoogleEventId = googleEventId,
            OriginalEmailId = emailId,
            Title = title,
            EventDate = eventDate,
            LastUpdatedAt = DateTimeOffset.UtcNow
        };
        return Task.CompletedTask;
    }

    public Task DeleteCalendarEventMappingAsync(string subjectHash)
    {
        _events.Remove(subjectHash);
        return Task.CompletedTask;
    }
}
