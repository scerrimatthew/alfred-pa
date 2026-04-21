using Alfred.Functions.Models;
using Alfred.Functions.Services.State;

namespace TestHarness;

public class InMemoryStateService : IStateService
{
    private readonly Dictionary<string, ProcessedEmailEntity> _emails = new();
    private readonly Dictionary<string, CalendarEventEntity> _events = new();

    public Task<bool> IsEmailProcessedAsync(string messageId) =>
        Task.FromResult(_emails.ContainsKey(messageId));

    public Task MarkEmailProcessedAsync(string messageId, string subject, string senderName, string summary, string? homework = null)
    {
        _emails[messageId] = new ProcessedEmailEntity
        {
            RowKey = messageId,
            Subject = subject,
            SenderName = senderName,
            Summary = summary,
            Homework = homework,
            ProcessedAt = DateTimeOffset.UtcNow
        };
        Console.WriteLine($"  [State] Marked as processed: {subject}");
        return Task.CompletedTask;
    }

    public Task<List<ProcessedEmailEntity>> GetEmailsSinceAsync(DateTimeOffset since) =>
        Task.FromResult(_emails.Values.Where(e => e.ProcessedAt >= since).ToList());

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
