using Alfred.Functions.Models;

namespace Alfred.Functions.Services.AI;

public interface INewsResearchService
{
    // Researches the last ~24h of AI news via web search, filtered through the watchlist
    // brief, Matthew's standing feedback rules, and the already-reported list. Newsletter
    // candidates are leads mined from inbox newsletters for the run to verify. An optional
    // topic turns the run into an on-demand targeted sweep (/news <topic>).
    Task<AiNewsDigest> ResearchDailyNewsAsync(
        List<NewsRuleEntity> rules,
        List<ReportedNewsEntity> recentlyReported,
        List<NewsCandidateEntity> newsletterCandidates,
        string? topic = null);

    // Lightweight midday sweep for flag-level stories only (competitor launch in the
    // A-SDLC niche, Anthropic partner-program change with a deadline, thesis-level
    // disconfirming evidence, regulation with a compliance clock). Empty on most days.
    Task<AiNewsDigest> CheckUrgentNewsAsync(
        List<NewsRuleEntity> rules,
        List<ReportedNewsEntity> recentlyReported);

    // Friday wrap-up connecting the week's reported stories per vision strand.
    // No web search — synthesis over what was already reported.
    Task<string?> BuildWeeklySynthesisAsync(
        List<ReportedNewsEntity> weekItems,
        List<NewsRuleEntity> rules);
}
