using System.Net;
using Alfred.Functions.Services.AI;
using Alfred.Functions.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Alfred.Functions.Tests;

// Covers the Anthropic Admin API cost report: page parsing, the Telegram summary
// formatting, and the HTTP flow (URL, headers, pagination, failures) via the
// HttpFactory seam over a recording fake handler.
public class AnthropicCostServiceTests
{
    private static readonly DateTime Day = new(2026, 8, 19); // an arbitrary fixed UTC date

    // ---- ParseCostReportPage ----

    [Fact]
    public void Parse_StringCentAmounts_AreSummedPerBucketDay()
    {
        var (daily, nextPage) = AnthropicCostService.ParseCostReportPage("""
            {"data": [
                {"starting_at": "2026-08-01T00:00:00Z", "results": [{"amount": "100.5", "currency": "USD"}, {"amount": "49.5"}]},
                {"starting_at": "2026-08-02T00:00:00Z", "results": [{"amount": "200"}]}
            ], "has_more": false, "next_page": null}
            """);

        Assert.Equal(2, daily.Count);
        Assert.Equal(150m, daily[new DateTime(2026, 8, 1)]);
        Assert.Equal(200m, daily[new DateTime(2026, 8, 2)]);
        Assert.Null(nextPage);
    }

    [Fact]
    public void Parse_NumericAmounts_AreToleratedAlongsideStrings()
    {
        var (daily, _) = AnthropicCostService.ParseCostReportPage("""
            {"data": [{"starting_at": "2026-08-01T00:00:00Z", "results": [{"amount": 75.25}, {"amount": "24.75"}]}]}
            """);

        Assert.Equal(100m, daily[new DateTime(2026, 8, 1)]);
    }

    [Fact]
    public void Parse_MissingOrUnparseableAmounts_AreSkippedNotFatal()
    {
        var (daily, _) = AnthropicCostService.ParseCostReportPage("""
            {"data": [{"starting_at": "2026-08-01T00:00:00Z", "results": [
                {"amount": "not-a-number"},
                {"currency": "USD"},
                {"amount": "42"}
            ]}]}
            """);

        Assert.Equal(42m, daily[new DateTime(2026, 8, 1)]);
    }

    [Fact]
    public void Parse_MalformedBuckets_AreSkipped()
    {
        var (daily, _) = AnthropicCostService.ParseCostReportPage("""
            {"data": [
                {"results": [{"amount": "10"}]},
                {"starting_at": "whenever", "results": [{"amount": "10"}]},
                {"starting_at": "2026-08-01T00:00:00Z"},
                {"starting_at": "2026-08-02T00:00:00Z", "results": "none"},
                {"starting_at": "2026-08-03T00:00:00Z", "results": [{"amount": "5"}]}
            ]}
            """);

        Assert.Equal(5m, Assert.Single(daily).Value);
    }

    [Fact]
    public void Parse_TwoBucketsOnTheSameUtcDay_Accumulate()
    {
        var (daily, _) = AnthropicCostService.ParseCostReportPage("""
            {"data": [
                {"starting_at": "2026-08-01T00:00:00Z", "results": [{"amount": "10"}]},
                {"starting_at": "2026-08-01T12:00:00Z", "results": [{"amount": "15"}]}
            ]}
            """);

        Assert.Equal(25m, Assert.Single(daily).Value);
    }

    [Fact]
    public void Parse_OffsetTimestamps_AreKeyedByTheirUtcDate()
    {
        // 22:00 at UTC-4 is 02:00 UTC the NEXT day — buckets must land on UTC dates
        var (daily, _) = AnthropicCostService.ParseCostReportPage("""
            {"data": [{"starting_at": "2026-08-01T22:00:00-04:00", "results": [{"amount": "10"}]}]}
            """);

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
        var (daily, nextPage) = AnthropicCostService.ParseCostReportPage(json);

        Assert.Empty(daily);
        Assert.Equal(expected, nextPage);
    }

    [Fact]
    public void Parse_MissingDataProperty_YieldsAnEmptyPage()
    {
        var (daily, nextPage) = AnthropicCostService.ParseCostReportPage("{}");

        Assert.Empty(daily);
        Assert.Null(nextPage);
    }

    // ---- FormatSummary ----

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

        var summary = AnthropicCostService.FormatSummary(daily, Day);

        Assert.Contains("Today: <b>$1.00</b>", summary);
        Assert.Contains("Yesterday: $2.50", summary);
        Assert.Contains("Last 7 days: $4.00", summary);    // 100 + 250 + 50 cents
        Assert.Contains("Last 30 days: $14.25", summary);  // + 1000 + 25 cents
    }

    [Fact]
    public void Format_NoDataAtAll_ShowsZeroDollarsEverywhere()
    {
        var summary = AnthropicCostService.FormatSummary([], Day);

        Assert.Contains("Today: <b>$0.00</b>", summary);
        Assert.Contains("Yesterday: $0.00", summary);
        Assert.Contains("Last 7 days: $0.00", summary);
        Assert.Contains("Last 30 days: $0.00", summary);
    }

    [Fact]
    public void Format_CarriesTheHeaderAndTheCreditBalanceCaveat()
    {
        var summary = AnthropicCostService.FormatSummary([], Day);

        Assert.Contains("💳 <b>Anthropic API spend</b> (UTC days)", summary);
        // The number Matthew actually wants (remaining credit) isn't exposed — say so
        Assert.Contains("billed spend", summary);
        Assert.Contains("credit balance", summary);
    }

    [Fact]
    public void Format_CentsBecomeInvariantTwoDecimalDollars()
    {
        var summary = AnthropicCostService.FormatSummary(
            new Dictionary<DateTime, decimal> { [Day] = 5m, [Day.AddDays(-1)] = 1234.56m }, Day);

        Assert.Contains("Today: <b>$0.05</b>", summary);
        Assert.Contains("Yesterday: $12.35", summary); // 1234.56 cents, rounded to whole cents in dollars
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
