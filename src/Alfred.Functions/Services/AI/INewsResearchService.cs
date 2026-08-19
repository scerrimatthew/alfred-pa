using Alfred.Functions.Models;

namespace Alfred.Functions.Services.AI;

public interface INewsResearchService
{
    // Researches the last ~24h of AI news via web search, filtered through the watchlist
    // brief, Matthew's standing feedback rules, and the already-reported list.
    Task<AiNewsDigest> ResearchDailyNewsAsync(List<NewsRuleEntity> rules, List<ReportedNewsEntity> recentlyReported);
}
