using Alfred.Functions.Models;

namespace Alfred.Functions.Services.Gmail;

public interface IGmailReaderService
{
    Task<List<SchoolEmail>> GetNewEmailsAsync();
}
