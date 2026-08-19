using Alfred.Functions.Models;
using Alfred.Functions.Services.AI;
using Xunit;

namespace Alfred.Functions.Tests;

// Pins the news-research prompt contract: the watchlist brief, Matthew's standing
// feedback rules, and the already-covered list must all reach Claude in the right
// section (system vs user), and the maxItems budget must be spelled out.
public class ClaudeNewsResearchPromptTests
{
    private static NewsRuleEntity Rule(string id, string instruction, DateTimeOffset createdAt) =>
        new() { RowKey = id, Instruction = instruction, CreatedAt = createdAt };

    private static ReportedNewsEntity Reported(string headline, string url, DateTimeOffset reportedAt) =>
        new() { Headline = headline, Url = url, ReportedAt = reportedAt };

    [Fact]
    public void FeedbackRules_RenderWithIdInstructionAndDate_OldestFirst()
    {
        var older = new DateTimeOffset(2026, 3, 5, 0, 0, 0, TimeSpan.Zero);
        var newer = new DateTimeOffset(2026, 7, 20, 0, 0, 0, TimeSpan.Zero);
        var rules = new List<NewsRuleEntity>
        {
            Rule("n2", "More on EU AI Act enforcement", newer),
            Rule("n1", "Skip funding rounds", older)
        };

        var (system, _) = ClaudeNewsResearchService.BuildNewsPrompt("Monday, 1 June 2026", rules, [], 5);

        Assert.Contains($"- [n1] Skip funding rounds (added {older:d MMM yyyy})", system);
        Assert.Contains($"- [n2] More on EU AI Act enforcement (added {newer:d MMM yyyy})", system);
        Assert.True(system.IndexOf("[n1]", StringComparison.Ordinal) < system.IndexOf("[n2]", StringComparison.Ordinal),
            "feedback rules must render oldest first");
    }

    [Fact]
    public void NoFeedbackRules_SaysNoneYet()
    {
        var (system, _) = ClaudeNewsResearchService.BuildNewsPrompt("Monday, 1 June 2026", [], [], 5);

        Assert.Contains("None yet.", system);
    }

    [Fact]
    public void CoveredStories_RenderInTheUserPrompt_NewestFirst()
    {
        var older = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        var newer = new DateTimeOffset(2026, 8, 15, 0, 0, 0, TimeSpan.Zero);
        var covered = new List<ReportedNewsEntity>
        {
            Reported("Old story", "https://old.example/1", older),
            Reported("Fresh story", "https://fresh.example/2", newer)
        };

        var (system, user) = ClaudeNewsResearchService.BuildNewsPrompt("Monday, 1 June 2026", [], covered, 5);

        Assert.Contains($"- [{newer:d MMM}] Fresh story (https://fresh.example/2)", user);
        Assert.Contains($"- [{older:d MMM}] Old story (https://old.example/1)", user);
        Assert.True(user.IndexOf("Fresh story", StringComparison.Ordinal) < user.IndexOf("Old story", StringComparison.Ordinal),
            "covered stories must render newest first");
        // Dedup context belongs to the user turn, not the standing system brief
        Assert.DoesNotContain("Fresh story", system);
    }

    [Fact]
    public void NothingCoveredYet_SaysSoInTheUserPrompt()
    {
        var (_, user) = ClaudeNewsResearchService.BuildNewsPrompt("Monday, 1 June 2026", [], [], 5);

        Assert.Contains("Nothing reported yet.", user);
        Assert.Contains("ALREADY COVERED", user);
    }

    [Fact]
    public void MaxItemsBudget_IsSpelledOutInTheSystemPrompt()
    {
        var (system, _) = ClaudeNewsResearchService.BuildNewsPrompt("Monday, 1 June 2026", [], [], 7);

        Assert.Contains("at most 7 stories", system);
    }

    [Fact]
    public void WatchlistBriefAndTodaysDate_AreEmbeddedInTheSystemPrompt()
    {
        var (system, _) = ClaudeNewsResearchService.BuildNewsPrompt("Wednesday, 19 August 2026", [], [], 5);

        Assert.Contains("Today is Wednesday, 19 August 2026.", system);
        // The full standing brief travels verbatim
        Assert.Contains(AiNewsBriefing.Watchlist, system);
    }
}
