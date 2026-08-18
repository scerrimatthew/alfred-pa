using Alfred.Functions.Models;

namespace Alfred.Functions.Services.State;

public interface IStateService
{
    Task<bool> IsEmailProcessedAsync(string messageId);
    Task MarkEmailProcessedAsync(string messageId, string subject, string senderName, string summary, string? homework = null, string? category = null, string? threadId = null);
    Task<bool> IsPersonalEmailProcessedAsync(string messageId);
    Task MarkPersonalEmailProcessedAsync(string messageId, string subject, string senderName, string summary, string? category = null, bool suppressed = false, string? threadId = null, string? senderEmail = null);
    Task<ProcessedEmailEntity?> GetPersonalEmailAsync(string messageId);
    Task UpdatePersonalEmailCategoryAsync(string messageId, string category);
    Task<List<SuppressionRuleEntity>> GetSuppressionRulesAsync();
    Task SaveSuppressionRuleAsync(string ruleId, string pattern, string? exampleSender, string? exampleSubject);
    Task DeleteSuppressionRuleAsync(string ruleId);
    Task<List<ProcessedEmailEntity>> GetEmailsSinceAsync(DateTimeOffset since);
    Task<List<ProcessedEmailEntity>> GetPersonalEmailsSinceAsync(DateTimeOffset since);
    Task SaveChatTurnAsync(long chatId, string question, string answer);
    Task<List<ChatTurnEntity>> GetRecentChatTurnsAsync(long chatId, DateTimeOffset since, int maxCount);
    Task ClearChatTurnsAsync(long chatId);
    Task<CalendarEventEntity?> GetCalendarEventMappingAsync(string subjectHash);
    Task SaveCalendarEventMappingAsync(string subjectHash, string googleEventId, string emailId, string title, DateTimeOffset eventDate);
    Task DeleteCalendarEventMappingAsync(string subjectHash);
}
