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

    public Task MarkPersonalEmailProcessedAsync(string messageId, string subject, string senderName, string summary, string? category = null, bool suppressed = false, string? threadId = null)
    {
        _personalEmails[messageId] = new ProcessedEmailEntity
        {
            PartitionKey = "personal",
            RowKey = messageId,
            Subject = subject,
            SenderName = senderName,
            Summary = summary,
            Category = category,
            Suppressed = suppressed,
            ProcessedAt = DateTimeOffset.UtcNow
        };
        Console.WriteLine($"  [State] Marked personal email as processed: {subject}");
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
