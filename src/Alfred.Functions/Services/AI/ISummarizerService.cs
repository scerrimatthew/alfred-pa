using Alfred.Functions.Models;

namespace Alfred.Functions.Services.AI;

public interface ISummarizerService
{
    Task<EmailDigest> SummarizeEmailAsync(SchoolEmail email);
    Task<string> BuildEveningDigestAsync(List<ProcessedEmailEntity> recentEmails, List<Google.Apis.Calendar.v3.Data.Event> upcomingEvents);
}
