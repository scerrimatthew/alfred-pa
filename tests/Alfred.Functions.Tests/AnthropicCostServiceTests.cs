using System.Net;
using Alfred.Functions.Services.AI;
using Alfred.Functions.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Alfred.Functions.Tests;

// Covers the Anthropic Admin API cost report: page parsing (per-day totals plus the
// per-description line-item breakdown), the Telegram summary formatting, and the HTTP
// flow (URL, headers, pagination, failures) via the HttpFactory seam over a recording
// fake handler.
public class AnthropicCostServiceTests
{
    private static readonly DateTime Day = new(2026, 8, 19); // an arbitrary fixed UTC date

    // For tests that only care about daily totals: collect descriptions from forever
    private static readonly DateTime AllDays = DateTime.MinValue;

    // ---- ParseCostReportPage: daily totals ----

    [Fact]
    public void Parse_StringCentAmounts_AreSummedPerBucketDay()
    {
        var (daily, _, nextPage) = AnthropicCostService.ParseCostReportPage("""
            {"data": [
                {"starting_at": "2026-08-01T00:00:00Z", "results": [{"amount": "100.5", "currency": "USD"}, {"amount": "49.5"}]},
                {"starting_at": "2026-08-02T00:00:00Z", "results": [{"amount": "200"}]}
            ], "has_more": false, "next_page": null}
            """, AllDays);

        Assert.Equal(2, daily.Count);
        Assert.Equal(150m, daily[new DateTime(2026, 8, 1)]);
        Assert.Equal(200m, daily[new DateTime(2026, 8, 2)]);
        Assert.Null(nextPage);
    }

    [Fact]
    public void Parse_NumericAmounts_AreToleratedAlongsideStrings()
    {
        var (daily, _, _) = AnthropicCostService.ParseCostReportPage("""
            {"data": [{"starting_at": "2026-08-01T00:00:00Z", "results": [{"amount": 75.25}, {"amount": "24.75"}]}]}
            """, AllDays);

        Assert.Equal(100m, daily[new DateTime(2026, 8, 1)]);
    }

    [Fact]
    public void Parse_MissingOrUnparseableAmounts_AreSkippedNotFatal()
    {
        var (daily, descriptions, _) = AnthropicCostService.ParseCostReportPage("""
            {"data": [{"starting_at": "2026-08-01T00:00:00Z", "results": [
                {"amount": "not-a-number"},
                {"currency": "USD"},
                {"amount": "42"}
            ]}]}
            """, AllDays);

        Assert.Equal(42m, daily[new DateTime(2026, 8, 1)]);
        // The skipped results must not leak into the breakdown either
        Assert.Equal(42m, Assert.Single(descriptions).Value);
    }

    [Fact]
    public void Parse_MalformedBuckets_AreSkipped()
    {
        var (daily, _, _) = AnthropicCostService.ParseCostReportPage("""
            {"data": [
                {"results": [{"amount": "10"}]},
                {"starting_at": "whenever", "results": [{"amount": "10"}]},
                {"starting_at": "2026-08-01T00:00:00Z"},
                {"starting_at": "2026-08-02T00:00:00Z", "results": "none"},
                {"starting_at": "2026-08-03T00:00:00Z", "results": [{"amount": "5"}]}
            ]}
            """, AllDays);

        Assert.Equal(5m, Assert.Single(daily).Value);
    }

    [Fact]
    public void Parse_TwoBucketsOnTheSameUtcDay_Accumulate()
    {
        var (daily, _, _) = AnthropicCostService.ParseCostReportPage("""
            {"data": [
                {"starting_at": "2026-08-01T00:00:00Z", "results": [{"amount": "10"}]},
                {"starting_at": "2026-08-01T12:00:00Z", "results": [{"amount": "15"}]}
            ]}
            """, AllDays);

        Assert.Equal(25m, Assert.Single(daily).Value);
    }

    [Fact]
    public void Parse_OffsetTimestamps_AreKeyedByTheirUtcDate()
    {
        // 22:00 at UTC-4 is 02:00 UTC the NEXT day — buckets must land on UTC dates
        var (daily, _, _) = AnthropicCostService.ParseCostReportPage("""
            {"data": [{"starting_at": "2026-08-01T22:00:00-04:00", "results": [{"amount": "10"}]}]}
            """, AllDays);

        Assert.Equal(10m, daily[new DateTime(2026, 8, 2)]);
    }

    [Theory]
    [InlineData("""{"data": [], "has_more": true, "next_page": "cursor123"}""", "cursor123")]
    [InlineData("""{"data": [], "has_more": true, "next_page": null}""", null)]  // has_more without a cursor
    [InlineData("""{"data": [], "has_more": true}""", null)]
    [InlineData("""{"data": [], "has_more": false, "next_page": "cursor123"}""", null)] // cursor without has_more
    [InlineData("""{"data": []}""", null)]
    public void Parse_NextPage_OnlyWhenHasMoreAndACursorArePresent(string json, string? expected)
    {
        var (daily, _, nextPage) = AnthropicCostService.ParseCostReportPage(json, AllDays);

        Assert.Empty(daily);
        Assert.Equal(expected, nextPage);
    }

    [Fact]
    public void Parse_MissingDataProperty_YieldsAnEmptyPage()
    {
        var (daily, descriptions, nextPage) = AnthropicCostService.ParseCostReportPage("{}", AllDays);

        Assert.Empty(daily);
        Assert.Empty(descriptions);
        Assert.Null(nextPage);
    }

    // ---- ParseCostReportPage: the per-description breakdown ----

    [Fact]
    public void Parse_Descriptions_AggregateAcrossDaysWhileDailyTotalsStayPerDay()
    {
        var (daily, descriptions, _) = AnthropicCostService.ParseCostReportPage("""
            {"data": [
                {"starting_at": "2026-08-01T00:00:00Z", "results": [
                    {"amount": "10", "description": "Claude Sonnet input"},
                    {"amount": "5", "description": "Claude Sonnet output"}]},
                {"starting_at": "2026-08-02T00:00:00Z", "results": [
                    {"amount": "7", "description": "Claude Sonnet input"}]}
            ]}
            """, AllDays);

        Assert.Equal(15m, daily[new DateTime(2026, 8, 1)]);
        Assert.Equal(7m, daily[new DateTime(2026, 8, 2)]);
        Assert.Equal(2, descriptions.Count);
        Assert.Equal(17m, descriptions["Claude Sonnet input"]); // same line item, summed across days
        Assert.Equal(5m, descriptions["Claude Sonnet output"]);
    }

    [Fact]
    public void Parse_Descriptions_OnlyCountForDaysOnOrAfterTheCutoff()
    {
        var (daily, descriptions, _) = AnthropicCostService.ParseCostReportPage("""
            {"data": [
                {"starting_at": "2026-08-01T00:00:00Z", "results": [{"amount": "100", "description": "Old spend"}]},
                {"starting_at": "2026-08-02T00:00:00Z", "results": [{"amount": "40", "description": "Recent spend"}]}
            ]}
            """, new DateTime(2026, 8, 2));

        // The cutoff trims the breakdown only — daily totals keep every day
        Assert.Equal(100m, daily[new DateTime(2026, 8, 1)]);
        Assert.Equal(40m, daily[new DateTime(2026, 8, 2)]);
        var item = Assert.Single(descriptions);
        Assert.Equal("Recent spend", item.Key); // the on-the-cutoff day counts, the earlier one doesn't
        Assert.Equal(40m, item.Value);
    }

    [Fact]
    public void Parse_Descriptions_MissingBlankOrPaddedFieldsNormalize()
    {
        var (_, descriptions, _) = AnthropicCostService.ParseCostReportPage("""
            {"data": [{"starting_at": "2026-08-01T00:00:00Z", "results": [
                {"amount": "10"},
                {"amount": "20", "description": "   "},
                {"amount": "30", "description": null},
                {"amount": "40", "description": "  Claude Opus 5  "}
            ]}]}
            """, AllDays);

        Assert.Equal(2, descriptions.Count);
        Assert.Equal(60m, descriptions["(other)"]);       // missing, blank and null all pool here
        Assert.Equal(40m, descriptions["Claude Opus 5"]); // padded descriptions are trimmed
    }

    // ---- FormatSummary: the daily windows ----

    [Fact]
    public void Format_SumsTheRightDaysIntoEachWindow()
    {
        var daily = new Dictionary<DateTime, decimal>
        {
            [Day] = 100m,                  // today
            [Day.AddDays(-1)] = 250m,      // yesterday
            [Day.AddDays(-6)] = 50m,       // last day inside the 7-day window
            [Day.AddDays(-7)] = 1000m,     // outside 7 days, inside 30
            [Day.AddDays(-29)] = 25m,      // last day inside the 30-day window
            [Day.AddDays(-30)] = 99999m    // outside every window — must not count
        };

        var summary = AnthropicCostService.FormatSummary(daily, [], Day);

        Assert.Contains("Today: <b>$1.00</b>", summary);
        Assert.Contains("Yesterday: $2.50", summary);
        Assert.Contains("Last 7 days: $4.00", summary);    // 100 + 250 + 50 cents
        Assert.Contains("Last 30 days: $14.25", summary);  // + 1000 + 25 cents
    }

    [Fact]
    public void Format_NoDataAtAll_ShowsZeroDollarsEverywhere()
    {
        var summary = AnthropicCostService.FormatSummary([], [], Day);

        Assert.Contains("Today: <b>$0.00</b>", summary);
        Assert.Contains("Yesterday: $0.00", summary);
        Assert.Contains("Last 7 days: $0.00", summary);
        Assert.Contains("Last 30 days: $0.00", summary);
    }

    [Fact]
    public void Format_CarriesTheHeaderAndTheCreditBalanceCaveat()
    {
        var summary = AnthropicCostService.FormatSummary([], [], Day);

        Assert.Contains("💳 <b>Anthropic API spend</b> (UTC days)", summary);
        // The number Matthew actually wants (remaining credit) isn't exposed — say so
        Assert.Contains("billed spend", summary);
        Assert.Contains("credit balance", summary);
    }

    [Fact]
    public void Format_CentsBecomeInvariantTwoDecimalDollars()
    {
        var summary = AnthropicCostService.FormatSummary(
            new Dictionary<DateTime, decimal> { [Day] = 5m, [Day.AddDays(-1)] = 1234.56m }, [], Day);

        Assert.Contains("Today: <b>$0.05</b>", summary);
        Assert.Contains("Yesterday: $12.35", summary); // 1234.56 cents, rounded to whole cents in dollars
    }

    // ---- FormatSummary: the "Where it went" breakdown ----

    [Fact]
    public void Format_Breakdown_ListsLineItemsLargestFirst()
    {
        var descriptions = new Dictionary<string, decimal>
        {
            ["Web search"] = 50m,
            ["Claude Opus 5"] = 150m
        };

        var summary = AnthropicCostService.FormatSummary([], descriptions, Day);

        Assert.Contains("<b>Where it went</b> (last 7 days)", summary);
        Assert.Contains("• $1.50 — Claude Opus 5", summary);
        Assert.Contains("• $0.50 — Web search", summary);
        Assert.True(
            summary.IndexOf("Claude Opus 5", StringComparison.Ordinal) < summary.IndexOf("Web search", StringComparison.Ordinal),
            "line items must be sorted by spend, largest first");
    }

    [Fact]
    public void Format_Breakdown_CapsAtEightNamedItemsAndRollsTheRestUp()
    {
        // Ten line items, 1000¢ down to 100¢ — the smallest two must fold into a rollup
        var descriptions = Enumerable.Range(1, 10)
            .ToDictionary(i => $"item{i}", i => (11 - i) * 100m);

        var summary = AnthropicCostService.FormatSummary([], descriptions, Day);

        Assert.Contains("• $10.00 — item1", summary);
        Assert.Contains("• $3.00 — item8", summary); // the eighth and last named line
        Assert.DoesNotContain("item9", summary);
        Assert.DoesNotContain("item10", summary);
        Assert.Contains("• $3.00 — everything else", summary); // 200¢ + 100¢
    }

    [Fact]
    public void Format_Breakdown_ExactlyEightItems_HasNoRollupLine()
    {
        var descriptions = Enumerable.Range(1, 8).ToDictionary(i => $"item{i}", i => (decimal)i);

        var summary = AnthropicCostService.FormatSummary([], descriptions, Day);

        Assert.Contains("item1", summary);
        Assert.Contains("item8", summary);
        Assert.DoesNotContain("everything else", summary);
    }

    [Fact]
    public void Format_Breakdown_NonPositiveAmountsNeverAppear()
    {
        var descriptions = new Dictionary<string, decimal>
        {
            ["real spend"] = 25m,
            ["a refund"] = -50m,
            ["free tier"] = 0m
        };

        var summary = AnthropicCostService.FormatSummary([], descriptions, Day);

        Assert.Contains("• $0.25 — real spend", summary);
        Assert.DoesNotContain("a refund", summary);
        Assert.DoesNotContain("free tier", summary);
    }

    [Fact]
    public void Format_Breakdown_AllNonPositive_OmitsTheSectionEntirely()
    {
        var summary = AnthropicCostService.FormatSummary(
            new Dictionary<DateTime, decimal> { [Day] = 100m },
            new Dictionary<string, decimal> { ["a refund"] = -50m, ["free tier"] = 0m },
            Day);

        Assert.DoesNotContain("Where it went", summary);
        Assert.DoesNotContain("•", summary);
        // ...while the daily windows still render
        Assert.Contains("Today: <b>$1.00</b>", summary);
    }

    [Fact]
    public void Format_NoBreakdownData_KeepsTheOldMessageShape()
    {
        var summary = AnthropicCostService.FormatSummary(
            new Dictionary<DateTime, decimal> { [Day] = 100m }, [], Day);

        Assert.DoesNotContain("Where it went", summary);
        Assert.DoesNotContain("•", summary);
        Assert.Contains("Last 30 days: $1.00", summary);
    }

    [Fact]
    public void Format_Breakdown_HtmlEscapesDescriptions()
    {
        // The message goes out in Telegram HTML mode — raw <, > or & would fail to send
        var descriptions = new Dictionary<string, decimal> { ["Tokens <input> & <output>"] = 100m };

        var summary = AnthropicCostService.FormatSummary([], descriptions, Day);

        Assert.Contains("• $1.00 — Tokens &lt;input&gt; &amp; &lt;output&gt;", summary);
        Assert.DoesNotContain("<input>", summary);
    }

    // ---- GetCostSummaryAsync (via the HttpFactory seam) ----

    private readonly FakeHttpHandler _http = new();
    private readonly AnthropicCostService _service;

    public AnthropicCostServiceTests()
    {
        _service = new AnthropicCostService(NullLogger<AnthropicCostService>.Instance)
        {
            // The service disposes the client per call — hand out fresh ones over the
            // shared recording handler
            HttpFactory = () => new HttpClient(_http, disposeHandler: false)
        };
    }

    private static async Task WithAdminKeyAsync(string? value, Func<Task> body)
    {
        var original = Environment.GetEnvironmentVariable("Anthropic__AdminApiKey");
        try
        {
            Environment.SetEnvironmentVariable("Anthropic__AdminApiKey", value);
            await body();
        }
        finally
        {
            Environment.SetEnvironmentVariable("Anthropic__AdminApiKey", original);
        }
    }

    private static string PageJson(DateTime day, string amountCents, string? nextPage = null) =>
        $$"""
        {"data": [{"starting_at": "{{day:yyyy-MM-dd}}T00:00:00Z", "results": [{"amount": "{{amountCents}}"}]}],
         "has_more": {{(nextPage is null ? "false" : "true")}}, "next_page": {{(nextPage is null ? "null" : $"\"{nextPage}\"")}}}
        """;

    [Fact]
    public async Task GetCostSummary_WithoutTheAdminKey_ThrowsInsteadOfCallingTheApi()
    {
        await WithAdminKeyAsync(null, async () =>
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _service.GetCostSummaryAsync());

            Assert.Contains("admin API key not configured", ex.Message);
            Assert.Empty(_http.Requests);
        });
    }

    [Fact]
    public async Task GetCostSummary_RequestsAThirtyDayDailyWindowWithTheAdminHeaders()
    {
        string? apiKey = null, version = null, userAgent = null;
        _http.EnqueueResponder(req =>
        {
            apiKey = string.Join(",", req.Headers.GetValues("x-api-key"));
            version = string.Join(",", req.Headers.GetValues("anthropic-version"));
            userAgent = string.Join(" ", req.Headers.UserAgent);
            return FakeHttpHandler.JsonResponse(PageJson(DateTime.UtcNow.Date, "12345"));
        });

        var beforeToday = DateTime.UtcNow.Date;
        string summary = "";
        await WithAdminKeyAsync("sk-ant-admin-test", async () => summary = await _service.GetCostSummaryAsync());
        var afterToday = DateTime.UtcNow.Date;

        var request = Assert.Single(_http.Requests);
        Assert.Equal("https://api.anthropic.com/v1/organizations/cost_report", request.Uri.GetLeftPart(UriPartial.Path));

        var query = System.Web.HttpUtility.ParseQueryString(request.Query);
        Assert.Equal("1d", query["bucket_width"]);
        Assert.Equal("31", query["limit"]);
        // group_by description splits each day into line items for the breakdown section
        Assert.Equal("description", query["group_by[]"]);
        Assert.Null(query["page"]); // first request carries no cursor

        // The window: 30 UTC days, ending tomorrow so today's bucket is included
        var startingAt = DateTimeOffset.Parse(query["starting_at"]!);
        var endingAt = DateTimeOffset.Parse(query["ending_at"]!);
        Assert.Equal(30, (endingAt - startingAt).TotalDays);
        Assert.Contains(endingAt.UtcDateTime.Date, new[] { beforeToday.AddDays(1), afterToday.AddDays(1) });

        Assert.Equal("sk-ant-admin-test", apiKey);
        Assert.Equal("2023-06-01", version);
        Assert.Contains("Alfred", userAgent);

        Assert.Contains("Today: <b>$123.45</b>", summary);
    }

    [Fact]
    public async Task GetCostSummary_FollowsThePaginationCursorAndMergesThePages()
    {
        var today = DateTime.UtcNow.Date;
        _http.EnqueueJson(PageJson(today, "100", nextPage: "cursor123"));
        _http.EnqueueJson(PageJson(today.AddDays(-1), "200"));

        string summary = "";
        await WithAdminKeyAsync("sk-ant-admin-test", async () => summary = await _service.GetCostSummaryAsync());

        Assert.Equal(2, _http.Requests.Count);
        var secondQuery = System.Web.HttpUtility.ParseQueryString(_http.Requests[1].Query);
        Assert.Equal("cursor123", secondQuery["page"]);

        Assert.Contains("Today: <b>$1.00</b>", summary);
        Assert.Contains("Yesterday: $2.00", summary);
        Assert.Contains("Last 7 days: $3.00", summary); // both pages merged
        // Description-less results pool under "(other)" — merged across pages too
        Assert.Contains("• $3.00 — (other)", summary);
    }

    [Fact]
    public async Task GetCostSummary_BreakdownCoversOnlyTheLastSevenDays()
    {
        var today = DateTime.UtcNow.Date;
        _http.EnqueueJson($$"""
            {"data": [
                {"starting_at": "{{today:yyyy-MM-dd}}T00:00:00Z", "results": [{"amount": "100", "description": "Claude Opus 5"}]},
                {"starting_at": "{{today.AddDays(-10):yyyy-MM-dd}}T00:00:00Z", "results": [{"amount": "500", "description": "Ancient history"}]}
            ], "has_more": false, "next_page": null}
            """);

        string summary = "";
        await WithAdminKeyAsync("sk-ant-admin-test", async () => summary = await _service.GetCostSummaryAsync());

        Assert.Contains("• $1.00 — Claude Opus 5", summary);
        Assert.DoesNotContain("Ancient history", summary); // outside the 7-day breakdown window...
        Assert.Contains("Last 30 days: $6.00", summary);   // ...but still inside the daily totals
    }

    [Fact]
    public async Task GetCostSummary_StopsAfterFivePagesEvenIfTheApiKeepsPaging()
    {
        _http.Route("GET /v1/organizations/cost_report", """{"data": [], "has_more": true, "next_page": "again"}""");

        string summary = "";
        await WithAdminKeyAsync("sk-ant-admin-test", async () => summary = await _service.GetCostSummaryAsync());

        Assert.Equal(5, _http.Requests.Count);
        Assert.Contains("Today: <b>$0.00</b>", summary); // still answers with what it has
    }

    [Fact]
    public async Task GetCostSummary_ApiRejection_ThrowsWithTheStatusAndBody()
    {
        _http.EnqueueJson("""{"error": "permission denied"}""", HttpStatusCode.Forbidden);

        await WithAdminKeyAsync("sk-ant-admin-test", async () =>
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _service.GetCostSummaryAsync());

            Assert.Contains("Cost API returned 403", ex.Message);
            Assert.Contains("permission denied", ex.Message);
        });
    }

    [Fact]
    public async Task GetCostSummary_ApiRejection_CapsALongErrorBody()
    {
        _http.EnqueueResponder(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent(new string('x', 350))
        });

        await WithAdminKeyAsync("sk-ant-admin-test", async () =>
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _service.GetCostSummaryAsync());

            Assert.Contains("Cost API returned 500", ex.Message);
            Assert.Contains(new string('x', 300) + "…", ex.Message);
            Assert.DoesNotContain(new string('x', 301), ex.Message);
        });
    }
}
