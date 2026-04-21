using Alfred.Functions.Models;

namespace Alfred.Functions.Services.State;

public interface IStateService
{
    Task<bool> IsEmailProcessedAsync(string messageId);
    Task MarkEmailProcessedAsync(string messageId, string subject, string senderName, string summary, string? homework = null);
    Task<List<ProcessedEmailEntity>> GetEmailsSinceAsync(DateTimeOffset since);
    Task<CalendarEventEntity?> GetCalendarEventMappingAsync(string subjectHash);
    Task SaveCalendarEventMappingAsync(string subjectHash, string googleEventId, string emailId, string title, DateTimeOffset eventDate);
    Task DeleteCalendarEventMappingAsync(string subjectHash);
}
