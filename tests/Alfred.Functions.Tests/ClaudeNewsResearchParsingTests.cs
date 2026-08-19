using System.Text.Json;
using Alfred.Functions.Services.AI;
using Xunit;

namespace Alfred.Functions.Tests;

// Pins how the news-research JSON reply becomes an AiNewsDigest: fences stripped,
// incomplete items dropped, optional fields tolerated — and malformed JSON thrown
// (deliberately: the digest function reports the failure to Matthew).
public class ClaudeNewsResearchParsingTests
{
    [Fact]
    public void ValidResponse_ParsesItemsAndTelegramMessage()
    {
        var digest = ClaudeNewsResearchService.ParseNewsResponse("""
            {"items": [
                {"headline": "DORA 2026 shows review times up", "url": "https://dora.dev/2026",
                 "category": "thesis-evidence", "summary": "s", "whyItMatters": "w"},
                {"headline": "Accenture launches agentic SDLC unit", "url": "https://acc.example/x",
                 "category": "competitor", "summary": "s", "whyItMatters": "w"}
            ], "telegramMessage": "🗞 Evening! Two stories tonight."}
            """);

        Assert.Equal("🗞 Evening! Two stories tonight.", digest.TelegramMessage);
        Assert.Equal(2, digest.Items.Count);
        Assert.Equal("DORA 2026 shows review times up", digest.Items[0].Headline);
        Assert.Equal("https://dora.dev/2026", digest.Items[0].Url);
        Assert.Equal("thesis-evidence", digest.Items[0].Category);
        Assert.Equal("competitor", digest.Items[1].Category);
    }

    [Fact]
    public void MarkdownFencedResponse_IsUnwrapped()
    {
        var digest = ClaudeNewsResearchService.ParseNewsResponse("""
            ```json
            {"items": [{"headline": "H", "url": "https://u"}], "telegramMessage": "m"}
            ```
            """);

        Assert.Equal("m", digest.TelegramMessage);
        Assert.Equal("H", Assert.Single(digest.Items).Headline);
    }

    [Fact]
    public void QuietDay_EmptyItemsAndNullMessage()
    {
        var digest = ClaudeNewsResearchService.ParseNewsResponse(
            """{"items": [], "telegramMessage": null}""");

        Assert.Empty(digest.Items);
        Assert.Null(digest.TelegramMessage);
    }

    [Fact]
    public void ItemsMissingHeadlineOrUrl_AreSkipped()
    {
        var digest = ClaudeNewsResearchService.ParseNewsResponse("""
            {"items": [
                {"headline": "No url"},
                {"url": "https://no-headline.example"},
                {"headline": "   ", "url": "https://blank-headline.example"},
                {"headline": "Keeper", "url": "https://keeper.example"}
            ], "telegramMessage": "m"}
            """);

        var item = Assert.Single(digest.Items);
        Assert.Equal("Keeper", item.Headline);
        Assert.Equal("https://keeper.example", item.Url);
    }

    [Fact]
    public void MissingCategory_IsToleratedAsNull()
    {
        var digest = ClaudeNewsResearchService.ParseNewsResponse(
            """{"items": [{"headline": "H", "url": "https://u"}], "telegramMessage": "m"}""");

        Assert.Null(Assert.Single(digest.Items).Category);
    }

    [Fact]
    public void MissingFieldsEntirely_YieldEmptyDigest()
    {
        var digest = ClaudeNewsResearchService.ParseNewsResponse("{}");

        Assert.Empty(digest.Items);
        Assert.Null(digest.TelegramMessage);
    }

    [Fact]
    public void MalformedJson_Throws_SoTheFailureIsReportedNotSwallowed()
    {
        Assert.ThrowsAny<JsonException>(() =>
            ClaudeNewsResearchService.ParseNewsResponse("Sorry, I could not find any news today."));
    }
}
