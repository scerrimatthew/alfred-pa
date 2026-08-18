using Alfred.Functions.Models;

namespace Alfred.Functions.Services.Gmail;

public interface IGmailReaderService
{
    Task<List<SchoolEmail>> GetNewEmailsAsync();
    Task<List<SchoolEmail>> GetNewPersonalEmailsAsync();
    Task<List<InboxSearchResult>> SearchInboxAsync(string query, int maxResults);
    Task<SchoolEmail?> GetEmailAsync(string messageId);
    Task MarkAsReadAndLabelAsync(string messageId, string labelPath);
    Task MarkAsUnreadAsync(string messageId);
    Task RecategorizeAsync(string messageId, string newLabelPath);
    Task<string> CreateReplyDraftAsync(string messageId, string body, bool replyAll);
    Task<bool> HasRepliedAsync(string threadId, string messageId);
}
