using Alfred.Functions.Models;
using Alfred.Functions.Services.AI;
using Xunit;
using static Alfred.Functions.Tests.Support.TestData;

namespace Alfred.Functions.Tests;

// Pins the ETF research prompt contract: the watchlist (with names, notes and last
// week's snapshot) reaches Claude in the user turn, the "no advice" and "don't invent
// numbers" instructions stay in the system prompt, and the window framing switches
// between the Saturday timer and an on-demand /etf.
public class ClaudeEtfResearchPromptTests
{
    private const string Today = "Saturday, 15 August 2026";

    private static (string System, string User) Prompt(List<EtfHoldingEntity> holdings, bool onDemand = false) =>
        ClaudeEtfResearchService.BuildEtfPrompt(Today, holdings, onDemand);

    // ---- Search budget ----

    [Theory]
    [InlineData(0, 4)]   // base only
    [InlineData(1, 5)]
    [InlineData(3, 7)]
    [InlineData(8, 12)]  // exactly at the ceiling — a full watchlist matches the news budget
    [InlineData(9, 12)]  // capped: a cut-off run costs a whole week here
    [InlineData(50, 12)]
    public void SearchBudgetFor_ScalesWithTheWatchlistButIsCapped(int holdingCount, int expected)
    {
        Assert.Equal(expected, ClaudeEtfResearchService.SearchBudgetFor(holdingCount));
    }

    // ---- Watchlist rendering ----

    [Fact]
    public void Holdings_RenderWithNameNotesAndLastWeeksSnapshot()
    {
        var reportedAt = new DateTimeOffset(2026, 8, 8, 9, 0, 0, TimeSpan.Zero);
        var holdings = new List<EtfHoldingEntity>
        {
            EtfHolding("VWCE", name: "Vanguard FTSE All-World UCITS ETF", notes: "core holding, monthly DCA",
                lastQuote: "€128.42", lastWeekChangePercent: -1.4, lastReportedAt: reportedAt)
        };

        var line = ClaudeEtfResearchService.FormatHoldingsSection(holdings);

        Assert.Contains("- VWCE — Vanguard FTSE All-World UCITS ETF", line);
        Assert.Contains("why he holds it: core holding, monthly DCA", line);
        Assert.Contains($"last reported {reportedAt:d MMM yyyy} at €128.42", line);
        // The sign is explicit so "1.4% that week" can't be read as a gain
        Assert.Contains("(-1.4% that week)", line);
    }

    [Fact]
    public void Holdings_PositiveLastMove_CarriesAPlusSign()
    {
        var line = ClaudeEtfResearchService.FormatHoldingsSection([
            EtfHolding("IWDA", lastQuote: "$102.10", lastWeekChangePercent: 2.05,
                lastReportedAt: new DateTimeOffset(2026, 8, 8, 9, 0, 0, TimeSpan.Zero))
        ]);

        Assert.Contains("(+2.1% that week)", line);
    }

    [Fact]
    public void Holdings_NeverReportedBefore_CarryNoSnapshotClause()
    {
        var line = ClaudeEtfResearchService.FormatHoldingsSection([EtfHolding("VWCE")]);

        Assert.Equal("- VWCE", line);
    }

    [Fact]
    public void Holdings_ReportedWithoutAQuote_SayNotAvailableInsteadOfBlank()
    {
        var reportedAt = new DateTimeOffset(2026, 8, 8, 9, 0, 0, TimeSpan.Zero);

        var line = ClaudeEtfResearchService.FormatHoldingsSection([
            EtfHolding("VWCE", lastReportedAt: reportedAt)
        ]);

        Assert.Contains($"last reported {reportedAt:d MMM yyyy} at n/a", line);
        // No percentage was recorded either, so no "(… that week)" clause is invented
        Assert.DoesNotContain("that week", line);
    }

    [Fact]
    public void Holdings_RenderOneLinePerFundInTheGivenOrder()
    {
        var line = ClaudeEtfResearchService.FormatHoldingsSection([
            EtfHolding("VWCE"), EtfHolding("IWDA"), EtfHolding("SXR8.DE")
        ]);

        Assert.Equal(["- VWCE", "- IWDA", "- SXR8.DE"], line.Split('\n'));
    }

    // ---- Prompt assembly ----

    [Fact]
    public void UserPrompt_CarriesTheWatchlist_SystemPromptCarriesTheDate()
    {
        var (system, user) = Prompt([EtfHolding("VWCE", name: "Vanguard FTSE All-World UCITS ETF")]);

        Assert.Contains($"Today is {Today}.", system);
        Assert.Contains("WATCHLIST", user);
        Assert.Contains("- VWCE — Vanguard FTSE All-World UCITS ETF", user);
    }

    [Fact]
    public void SystemPrompt_ForbidsAdviceAndPredictions()
    {
        // The whole feature is informational; an "advice" slip is the one output that
        // would actually be harmful, so the instruction must never quietly disappear
        var (system, _) = Prompt([EtfHolding("VWCE")]);

        Assert.Contains("Never give buy, sell, or hold advice", system);
        Assert.Contains("never predict prices", system);
        Assert.Contains("informing him, not advising him", system);
    }

    [Fact]
    public void SystemPrompt_ForbidsEstimatingAFigureItCouldNotFind()
    {
        var (system, _) = Prompt([EtfHolding("VWCE")]);

        Assert.Contains("leave the number null", system);
        Assert.Contains("estimating one", system);
        Assert.Contains("Numbers must come from a source you actually read", system);
    }

    [Fact]
    public void SystemPrompt_SpellsOutEveryJsonFieldTheParserReads()
    {
        var (system, _) = Prompt([EtfHolding("VWCE")]);

        foreach (var field in new[]
                 {
                     "\"items\"", "\"symbol\"", "\"name\"", "\"quote\"", "\"weekChangePercent\"",
                     "\"ytdChangePercent\"", "\"narrative\"", "\"sourceUrl\"", "\"telegramMessage\""
                 })
        {
            Assert.Contains(field, system);
        }
    }

    [Fact]
    public void SystemPrompt_RestrictsTheHtmlToWhatTelegramAccepts()
    {
        var (system, _) = Prompt([EtfHolding("VWCE")]);

        Assert.Contains("Only use <b> and <a href=\"\"> tags", system);
        Assert.Contains("&lt; &gt; &amp;", system);
    }

    [Fact]
    public void WeeklyRun_CoversTheTradingWeekThatJustClosed()
    {
        var (system, _) = Prompt([EtfHolding("VWCE")], onDemand: false);

        Assert.Contains("Cover the trading week that has just closed (Monday to Friday close)", system);
        Assert.DoesNotContain("Matthew asked for this now", system);
    }

    [Fact]
    public void OnDemandRun_CoversTheLastFiveSessionsInstead()
    {
        var (system, _) = Prompt([EtfHolding("VWCE")], onDemand: true);

        Assert.Contains("Matthew asked for this now", system);
        Assert.Contains("last five trading sessions", system);
        Assert.DoesNotContain("Monday to Friday close", system);
    }

    [Fact]
    public void PreviousSnapshot_AsksForContinuationOrReversalFraming()
    {
        var (system, _) = Prompt([EtfHolding("VWCE")]);

        Assert.Contains("continuation or reversal", system);
    }
}
