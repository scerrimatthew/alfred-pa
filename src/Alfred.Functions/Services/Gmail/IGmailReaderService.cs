using Alfred.Functions.Models;

namespace Alfred.Functions.Services.Gmail;

public interface IGmailReaderService
{
    Task<List<SchoolEmail>> GetNewEmailsAsync();
    Task<List<SchoolEmail>> GetNewPersonalEmailsAsync();
    Task MarkAsReadAndLabelAsync(string messageId, string labelPath);
    Task MarkAsUnreadAsync(string messageId);
    Task RecategorizeAsync(string messageId, string newLabelPath);
}
