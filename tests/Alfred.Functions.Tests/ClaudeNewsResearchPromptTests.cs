using Alfred.Functions.Models;
using Alfred.Functions.Services.AI;
using Xunit;

namespace Alfred.Functions.Tests;

// Pins the news-research prompt contracts: the watchlist brief, Matthew's standing
// feedback rules, the already-covered list, newsletter-mined candidates, and the
// on-demand topic must all reach Claude in the right section (system vs user), and
// the item budgets must be spelled out — for the daily digest, the midday flash
// check, and the Friday weekly synthesis alike.
public class ClaudeNewsResearchPromptTests
{
    private static NewsRuleEntity Rule(string id, string instruction, DateTimeOffset createdAt) =>
        new() { RowKey = id, Instruction = instruction, CreatedAt = createdAt };

    private static ReportedNewsEntity Reported(string headline, string url, DateTimeOffset reportedAt) =>
        new() { Headline = headline, Url = url, ReportedAt = reportedAt };

    // ---- Daily digest prompt ----

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

        var (system, _) = ClaudeNewsResearchService.BuildNewsPrompt("Monday, 1 June 2026", rules, [], [], 5);

        Assert.Contains($"- [n1] Skip funding rounds (added {older:d MMM yyyy})", system);
        Assert.Contains($"- [n2] More on EU AI Act enforcement (added {newer:d MMM yyyy})", system);
        Assert.True(system.IndexOf("[n1]", StringComparison.Ordinal) < system.IndexOf("[n2]", StringComparison.Ordinal),
            "feedback rules must render oldest first");
    }

    [Fact]
    public void NoFeedbackRules_SaysNoneYet()
    {
        var (system, _) = ClaudeNewsResearchService.BuildNewsPrompt("Monday, 1 June 2026", [], [], [], 5);

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

        var (system, user) = ClaudeNewsResearchService.BuildNewsPrompt("Monday, 1 June 2026", [], covered, [], 5);

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
        var (_, user) = ClaudeNewsResearchService.BuildNewsPrompt("Monday, 1 June 2026", [], [], [], 5);

        Assert.Contains("Nothing reported yet.", user);
        Assert.Contains("ALREADY COVERED", user);
    }

    [Fact]
    public void MaxItemsBudget_IsSpelledOutInTheSystemPrompt()
    {
        var (system, _) = ClaudeNewsResearchService.BuildNewsPrompt("Monday, 1 June 2026", [], [], [], 7);

        Assert.Contains("at most 7 stories", system);
    }

    [Fact]
    public void WatchlistBriefAndTodaysDate_AreEmbeddedInTheSystemPrompt()
    {
        var (system, _) = ClaudeNewsResearchService.BuildNewsPrompt("Wednesday, 19 August 2026", [], [], [], 5);

        Assert.Contains("Today is Wednesday, 19 August 2026.", system);
        // The full standing brief travels verbatim
        Assert.Contains(AiNewsBriefing.Watchlist, system);
    }

    [Fact]
    public void NewsletterCandidates_RenderInTheUserPromptWithSourceUrlAndNote_NewestFirst()
    {
        var older = new DateTimeOffset(2026, 8, 18, 8, 0, 0, TimeSpan.Zero);
        var newer = new DateTimeOffset(2026, 8, 19, 8, 0, 0, TimeSpan.Zero);
        var candidates = new List<NewsCandidateEntity>
        {
            new() { Headline = "Old lead", Url = "https://lead.example/old", Note = "per TLDR, a big deal", Source = "TLDR AI", SeenAt = older },
            new() { Headline = "Fresh lead", Source = "Import AI", SeenAt = newer } // no url, no note
        };

        var (system, user) = ClaudeNewsResearchService.BuildNewsPrompt("Monday, 1 June 2026", [], [], candidates, 5);

        Assert.Contains("CANDIDATE STORIES", user);
        Assert.Contains("- [TLDR AI] Old lead (https://lead.example/old) — per TLDR, a big deal", user);
        // A lead without url/note renders bare — no dangling parentheses or dashes
        Assert.Contains("- [Import AI] Fresh lead\n", user + "\n");
        Assert.True(user.IndexOf("Fresh lead", StringComparison.Ordinal) < user.IndexOf("Old lead", StringComparison.Ordinal),
            "candidates must render newest first");
        // Candidates are run-specific leads, not part of the standing system brief
        Assert.DoesNotContain("CANDIDATE STORIES", system);
    }

    [Fact]
    public void NoCandidates_OmitsTheCandidateSectionEntirely()
    {
        var (system, user) = ClaudeNewsResearchService.BuildNewsPrompt("Monday, 1 June 2026", [], [], [], 5);

        Assert.DoesNotContain("CANDIDATE STORIES", user);
        Assert.DoesNotContain("CANDIDATE STORIES", system);
    }

    [Fact]
    public void Topic_TurnsTheRunIntoATargetedSweepInTheSystemPrompt()
    {
        var (system, _) = ClaudeNewsResearchService.BuildNewsPrompt(
            "Monday, 1 June 2026", [], [], [], 5, topic: "EU AI Act enforcement");

        Assert.Contains("TARGETED SWEEP", system);
        Assert.Contains("\"EU AI Act enforcement\"", system);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoTopic_OmitsTheTargetedSweepSection(string? topic)
    {
        var (system, _) = ClaudeNewsResearchService.BuildNewsPrompt("Monday, 1 June 2026", [], [], [], 5, topic);

        Assert.DoesNotContain("TARGETED SWEEP", system);
    }

    // ---- Formatting helpers shared across the prompts ----

    [Fact]
    public void FormatFeedbackSection_EmptyAndPopulated()
    {
        Assert.Equal("None yet.", ClaudeNewsResearchService.FormatFeedbackSection([]));

        var createdAt = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);
        var section = ClaudeNewsResearchService.FormatFeedbackSection([Rule("n1", "Skip funding rounds", createdAt)]);
        Assert.Equal($"- [n1] Skip funding rounds (added {createdAt:d MMM yyyy})", section);
    }

    [Fact]
    public void FormatCoveredSection_EmptyAndPopulated()
    {
        Assert.Equal("Nothing reported yet.", ClaudeNewsResearchService.FormatCoveredSection([]));

        var reportedAt = new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero);
        var section = ClaudeNewsResearchService.FormatCoveredSection([Reported("DORA lands", "https://d.example", reportedAt)]);
        Assert.Equal($"- [{reportedAt:d MMM}] DORA lands (https://d.example)", section);
    }

    // ---- Midday flash prompt ----

    [Fact]
    public void FlashPrompt_CarriesTheWatchlistDateFeedbackAndCoveredList()
    {
        var createdAt = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);
        var reportedAt = new DateTimeOffset(2026, 8, 15, 0, 0, 0, TimeSpan.Zero);

        var (system, user) = ClaudeNewsResearchService.BuildFlashPrompt(
            "Wednesday, 19 August 2026",
            [Rule("n1", "Skip funding rounds", createdAt)],
            [Reported("Old story", "https://old.example/1", reportedAt)]);

        Assert.Contains("Today is Wednesday, 19 August 2026.", system);
        Assert.Contains(AiNewsBriefing.Watchlist, system);
        Assert.Contains("[n1] Skip funding rounds", system);
        // The covered list rides in the user turn, framed as do-not-re-flag
        Assert.Contains($"- [{reportedAt:d MMM}] Old story (https://old.example/1)", user);
        Assert.Contains("do not re-flag", user);
    }

    [Fact]
    public void FlashPrompt_IsFlagLevelOnly_CappedAtThreeItems_WithTheSirenMessageSpec()
    {
        var (system, _) = ClaudeNewsResearchService.BuildFlashPrompt("Monday, 1 June 2026", [], []);

        // Not the evening digest: a hard bar, a three-item cap, and the 🚨 opener
        Assert.Contains("NOT the evening news digest", system);
        Assert.Contains("at most 3 stories", system);
        Assert.Contains("🚨", system);
        Assert.Contains("expected answer on a normal day", system);
    }

    // ---- Weekly synthesis prompt ----

    [Fact]
    public void WeeklyPrompt_RendersEachStoryWithDateCategorySummaryAndWhy_OldestFirst()
    {
        var monday = new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);
        var thursday = new DateTimeOffset(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);
        var weekItems = new List<ReportedNewsEntity>
        {
            new()
            {
                Headline = "Late story", Url = "https://late.example", Category = "competitor",
                Summary = "A firm launched.", WhyItMatters = "Direct niche overlap.", ReportedAt = thursday
            },
            new()
            {
                // No category, summary, or why — the bare fallback rendering
                Headline = "Early story", Url = "https://early.example", ReportedAt = monday
            }
        };

        var (_, user) = ClaudeNewsResearchService.BuildWeeklyPrompt("Friday, 14 August 2026", weekItems, []);

        Assert.Contains(
            $"- [{thursday:ddd d MMM}] [competitor] Late story (https://late.example): A firm launched. | why it mattered: Direct niche overlap.",
            user);
        Assert.Contains($"- [{monday:ddd d MMM}] [uncategorized] Early story (https://early.example)", user);
        Assert.DoesNotContain("Early story (https://early.example):", user); // no dangling summary separator
        Assert.True(user.IndexOf("Early story", StringComparison.Ordinal) < user.IndexOf("Late story", StringComparison.Ordinal),
            "the week's stories must render oldest first");
    }

    [Fact]
    public void WeeklyPrompt_SystemCarriesTheWatchlistDateAndFeedback_AndAsksForHtmlNotJson()
    {
        var createdAt = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);

        var (system, user) = ClaudeNewsResearchService.BuildWeeklyPrompt(
            "Friday, 14 August 2026", [], [Rule("n1", "Skip funding rounds", createdAt)]);

        Assert.Contains("Today is Friday, 14 August 2026.", system);
        Assert.Contains(AiNewsBriefing.Watchlist, system);
        Assert.Contains("[n1] Skip funding rounds", system);
        // Unlike the research runs, the synthesis is a plain formatted message
        Assert.Contains("no JSON", system);
        Assert.Contains("STORIES REPORTED THIS WEEK", user);
    }
}
