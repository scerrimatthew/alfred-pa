using Alfred.Functions.Models;

namespace Alfred.Functions.Services.State;

public interface IStateService
{
    Task<bool> IsEmailProcessedAsync(string messageId);
    Task MarkEmailProcessedAsync(string messageId, string subject, string senderName, string summary, string? homework = null, string? category = null, string? threadId = null);
    Task<bool> IsPersonalEmailProcessedAsync(string messageId);
    Task MarkPersonalEmailProcessedAsync(string messageId, string subject, string senderName, string summary, string? category = null, bool suppressed = false, string? threadId = null, string? senderEmail = null, bool needsReply = false, DateTimeOffset? processedAt = null);
    Task<BackfillStateEntity?> GetBackfillStateAsync();
    Task SaveBackfillStateAsync(BackfillStateEntity entity);
    Task ClearBackfillStateAsync();
    Task<List<ProcessedEmailEntity>> GetPersonalEmailsNeedingReplyAsync(DateTimeOffset since);
    Task ClearNeedsReplyAsync(string messageId);
    Task<ProcessedEmailEntity?> GetPersonalEmailAsync(string messageId);
    Task<List<ProcessedEmailEntity>> GetPersonalEmailsByThreadAsync(string threadId);
    Task UpdatePersonalEmailCategoryAsync(string messageId, string category);
    Task SaveSnoozeAsync(string messageId, string subject, string senderName, string summary, string? threadId, DateTimeOffset dueAt);
    Task<List<SnoozedEmailEntity>> GetDueSnoozesAsync(DateTimeOffset now);
    Task<List<SnoozedEmailEntity>> GetSnoozesAsync();
    Task DeleteSnoozeAsync(string messageId);
    Task<List<SuppressionRuleEntity>> GetSuppressionRulesAsync();
    Task SaveSuppressionRuleAsync(string ruleId, string pattern, string? exampleSender, string? exampleSubject);
    Task DeleteSuppressionRuleAsync(string ruleId);
    Task RecordSenderSeenAsync(string senderEmail, string senderName, bool wasQuiet, string? listUnsubscribe, bool oneClick);
    Task<List<SenderStatsEntity>> GetUnsubscribeCandidatesAsync(int minEmails, int maxCandidates);
    Task<SenderStatsEntity?> GetSenderStatAsync(string rowKey);
    Task UpsertSenderStatAsync(SenderStatsEntity entity);
    Task<List<AttentionRuleEntity>> GetAttentionRulesAsync();
    Task SaveAttentionRuleAsync(string ruleId, string pattern, string? exampleSender, string? exampleSubject);
    Task DeleteAttentionRuleAsync(string ruleId);
    Task<List<ProcessedEmailEntity>> GetEmailsSinceAsync(DateTimeOffset since);
    Task<List<ProcessedEmailEntity>> GetPersonalEmailsSinceAsync(DateTimeOffset since);
    Task SaveChatTurnAsync(long chatId, string question, string answer);
    Task<List<ChatTurnEntity>> GetRecentChatTurnsAsync(long chatId, DateTimeOffset since, int maxCount);
    Task ClearChatTurnsAsync(long chatId);
    Task<CalendarEventEntity?> GetCalendarEventMappingAsync(string subjectHash);
    Task SaveCalendarEventMappingAsync(string subjectHash, string googleEventId, string emailId, string title, DateTimeOffset eventDate);
    Task DeleteCalendarEventMappingAsync(string subjectHash);
}
