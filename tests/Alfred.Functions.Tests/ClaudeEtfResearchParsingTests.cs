using System.Text.Json;
using Alfred.Functions.Services.AI;
using Xunit;

namespace Alfred.Functions.Tests;

// Pins how the ETF research JSON becomes an EtfReport: fences stripped, symbol-less
// items dropped, percentages tolerated as strings, missing figures left null — and
// malformed JSON thrown, so the caller reports the failure instead of sending nothing.
public class ClaudeEtfResearchParsingTests
{
    [Fact]
    public void ValidResponse_ParsesEveryFieldAndTheTelegramMessage()
    {
        var report = ClaudeEtfResearchService.ParseEtfResponse("""
            {"items": [
                {"symbol": "VWCE", "name": "Vanguard FTSE All-World UCITS ETF", "quote": "€128.42",
                 "weekChangePercent": -1.4, "ytdChangePercent": 8.9,
                 "narrative": "Slipped with the dollar.", "sourceUrl": "https://justetf.example/vwce"},
                {"symbol": "IWDA", "name": "iShares Core MSCI World", "quote": "$102.10",
                 "weekChangePercent": 0.6, "ytdChangePercent": 11.2,
                 "narrative": "Held up on US earnings.", "sourceUrl": "https://justetf.example/iwda"}
            ], "telegramMessage": "📈 Week of 10-14 Aug"}
            """);

        Assert.Equal("📈 Week of 10-14 Aug", report.TelegramMessage);
        Assert.False(report.Incomplete);
        Assert.Equal(2, report.Items.Count);

        var vwce = report.Items[0];
        Assert.Equal("VWCE", vwce.Symbol);
        Assert.Equal("Vanguard FTSE All-World UCITS ETF", vwce.Name);
        Assert.Equal("€128.42", vwce.Quote);
        Assert.Equal(-1.4, vwce.WeekChangePercent);
        Assert.Equal(8.9, vwce.YtdChangePercent);
        Assert.Equal("Slipped with the dollar.", vwce.Narrative);
        Assert.Equal("https://justetf.example/vwce", vwce.SourceUrl);
        Assert.Equal("IWDA", report.Items[1].Symbol);
    }

    [Fact]
    public void ItemsWithoutASymbol_AreSkipped_AndSymbolsAreTrimmed()
    {
        var report = ClaudeEtfResearchService.ParseEtfResponse("""
            {"items": [
                {"name": "No symbol at all"},
                {"symbol": "", "name": "Blank"},
                {"symbol": "   ", "name": "Whitespace"},
                {"symbol": null, "name": "Null"},
                {"symbol": "  VWCE  ", "name": "Keeper"}
            ], "telegramMessage": "m"}
            """);

        var item = Assert.Single(report.Items);
        Assert.Equal("VWCE", item.Symbol);
        Assert.Equal("Keeper", item.Name);
    }

    [Theory]
    [InlineData("\"-1.4%\"", -1.4)]
    [InlineData("\"+2.1%\"", 2.1)]
    [InlineData("\"3.5\"", 3.5)]
    [InlineData("\" -0.25 %\"", -0.25)]
    [InlineData("-1.4", -1.4)]
    public void PercentagesGivenAsStrings_AreStillRead(string weekJson, double expected)
    {
        var report = ClaudeEtfResearchService.ParseEtfResponse(
            $$"""{"items": [{"symbol": "VWCE", "weekChangePercent": {{weekJson}}}], "telegramMessage": "m"}""");

        Assert.Equal(expected, Assert.Single(report.Items).WeekChangePercent);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"n/a\"")]
    [InlineData("\"not found\"")]
    [InlineData("true")]
    public void UnreadableOrMissingFigures_BecomeNullRatherThanZero(string weekJson)
    {
        // Zero would read as "flat this week" — a number Alfred never actually found
        var report = ClaudeEtfResearchService.ParseEtfResponse(
            $$"""{"items": [{"symbol": "VWCE", "weekChangePercent": {{weekJson}}}], "telegramMessage": "m"}""");

        Assert.Null(Assert.Single(report.Items).WeekChangePercent);
    }

    [Fact]
    public void FigureOmittedEntirely_IsNull()
    {
        var report = ClaudeEtfResearchService.ParseEtfResponse(
            """{"items": [{"symbol": "VWCE"}], "telegramMessage": "m"}""");

        var item = Assert.Single(report.Items);
        Assert.Null(item.WeekChangePercent);
        Assert.Null(item.YtdChangePercent);
        Assert.Null(item.Quote);
        Assert.Null(item.Name);
        Assert.Null(item.Narrative);
        Assert.Null(item.SourceUrl);
    }

    [Fact]
    public void MarkdownFencedResponse_IsUnwrapped()
    {
        var report = ClaudeEtfResearchService.ParseEtfResponse("""
            ```json
            {"items": [{"symbol": "VWCE", "quote": "€128.42"}], "telegramMessage": "📈 here you go"}
            ```
            """);

        Assert.Equal("📈 here you go", report.TelegramMessage);
        Assert.Equal("€128.42", Assert.Single(report.Items).Quote);
    }

    [Fact]
    public void NullTelegramMessageAndNoItems_YieldAnEmptyReport()
    {
        var report = ClaudeEtfResearchService.ParseEtfResponse("""{"items": [], "telegramMessage": null}""");

        Assert.Empty(report.Items);
        Assert.Null(report.TelegramMessage);
        // Nothing found is not the same as a run that was cut off
        Assert.False(report.Incomplete);
    }

    [Fact]
    public void MissingFieldsEntirely_YieldAnEmptyReport()
    {
        var report = ClaudeEtfResearchService.ParseEtfResponse("{}");

        Assert.Empty(report.Items);
        Assert.Null(report.TelegramMessage);
    }

    [Fact]
    public void ItemsNotAnArray_IsTolerated()
    {
        var report = ClaudeEtfResearchService.ParseEtfResponse("""{"items": null, "telegramMessage": "m"}""");

        Assert.Empty(report.Items);
        Assert.Equal("m", report.TelegramMessage);
    }

    [Fact]
    public void MalformedJson_Throws_SoTheFailureIsReportedNotSwallowed()
    {
        Assert.ThrowsAny<JsonException>(() =>
            ClaudeEtfResearchService.ParseEtfResponse("Sorry, I couldn't find prices for those."));
    }
}
