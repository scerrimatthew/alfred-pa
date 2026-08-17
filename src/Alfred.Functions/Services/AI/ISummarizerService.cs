using Alfred.Functions.Models;

namespace Alfred.Functions.Services.AI;

public interface ISummarizerService
{
    Task<EmailDigest> SummarizeEmailAsync(SchoolEmail email);
    Task<PersonalEmailTriage> TriagePersonalEmailAsync(SchoolEmail email, List<SuppressionRuleEntity> suppressionRules);
    Task<string> BuildEveningDigestAsync(List<ProcessedEmailEntity> recentEmails, List<Google.Apis.Calendar.v3.Data.Event> upcomingEvents);
    Task<string> BuildPersonalDigestAsync(List<ProcessedEmailEntity> todaysEmails, List<Google.Apis.Calendar.v3.Data.Event> upcomingActions);
    Task<string> AnswerQuestionAsync(string question, List<ProcessedEmailEntity> recentEmails, List<Google.Apis.Calendar.v3.Data.Event> upcomingEvents);
    Task<string> AnswerPersonalQuestionAsync(
        string question,
        List<ProcessedEmailEntity> schoolEmails,
        List<Google.Apis.Calendar.v3.Data.Event> schoolEvents,
        List<ProcessedEmailEntity> personalEmails,
        List<Google.Apis.Calendar.v3.Data.Event> personalActions,
        Func<string, System.Text.Json.Nodes.JsonNode?, Task<string>> executeTool);
}
