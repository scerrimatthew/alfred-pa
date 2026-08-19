using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Alfred.Functions.Services.AI;

public interface IAnthropicCostService
{
    // Formatted Telegram HTML summary of the organization's Anthropic API spend,
    // fetched from the Admin API cost report (requires Anthropic__AdminApiKey)
    Task<string> GetCostSummaryAsync();
}

public class AnthropicCostService : IAnthropicCostService
{
    // The cost report only supports daily buckets, capped at 31 per request —
    // 30 days covers today/yesterday/week/month views in a single window
    internal const int LookbackDays = 30;

    private const string CostReportUrl = "https://api.anthropic.com/v1/organizations/cost_report";

    private readonly ILogger<AnthropicCostService> _logger;

    public AnthropicCostService(ILogger<AnthropicCostService> logger)
    {
        _logger = logger;
    }

    // Test seam: when set, supplies the HttpClient (tests back it with a fake handler).
    // Never set in production.
    internal Func<HttpClient>? HttpFactory { get; set; }

    public async Task<string> GetCostSummaryAsync()
    {
        var adminKey = Environment.GetEnvironmentVariable("Anthropic__AdminApiKey")
            ?? throw new InvalidOperationException("Anthropic admin API key not configured");

        using var http = HttpFactory is not null ? HttpFactory() : new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.Add("x-api-key", adminKey);
        http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Alfred");

        // Cost buckets are UTC days (matching what the Console shows)
        var todayUtc = DateTime.UtcNow.Date;
        var startingAt = todayUtc.AddDays(-(LookbackDays - 1));
        var endingAt = todayUtc.AddDays(1);

        var dailyCents = new Dictionary<DateTime, decimal>();
        string? page = null;
        for (var i = 0; i < 5; i++) // pagination guard — 31 daily buckets rarely need even 2 pages
        {
            var url = CostReportUrl
                + $"?starting_at={startingAt:yyyy-MM-dd'T'HH:mm:ss'Z'}"
                + $"&ending_at={endingAt:yyyy-MM-dd'T'HH:mm:ss'Z'}"
                + "&bucket_width=1d&limit=31"
                + (page is null ? "" : $"&page={Uri.EscapeDataString(page)}");

            var response = await http.GetAsync(url);
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                var detail = body.Length > 300 ? body[..300] + "…" : body;
                throw new InvalidOperationException($"Cost API returned {(int)response.StatusCode}: {detail}");
            }

            var (pageCents, nextPage) = ParseCostReportPage(body);
            foreach (var (day, cents) in pageCents)
                dailyCents[day] = dailyCents.GetValueOrDefault(day) + cents;

            page = nextPage;
            if (page is null) break;
        }

        _logger.LogInformation("Cost report fetched: {Days} days with spend", dailyCents.Count);
        return FormatSummary(dailyCents, todayUtc);
    }

    // One page of the cost report: per-UTC-day total in cents, plus the pagination cursor.
    // Amounts arrive as decimal strings in cents ("123.45"); numbers are tolerated too.
    internal static (Dictionary<DateTime, decimal> DailyCents, string? NextPage) ParseCostReportPage(string json)
    {
        var dailyCents = new Dictionary<DateTime, decimal>();

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var bucket in data.EnumerateArray())
            {
                if (!bucket.TryGetProperty("starting_at", out var saProp)
                    || !DateTimeOffset.TryParse(saProp.GetString(), CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal, out var bucketStart))
                    continue;

                if (!bucket.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
                    continue;

                decimal bucketCents = 0;
                foreach (var result in results.EnumerateArray())
                {
                    if (!result.TryGetProperty("amount", out var amountProp))
                        continue;

                    if (amountProp.ValueKind == JsonValueKind.String
                        && decimal.TryParse(amountProp.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var fromString))
                    {
                        bucketCents += fromString;
                    }
                    else if (amountProp.ValueKind == JsonValueKind.Number && amountProp.TryGetDecimal(out var fromNumber))
                    {
                        bucketCents += fromNumber;
                    }
                }

                var day = bucketStart.UtcDateTime.Date;
                dailyCents[day] = dailyCents.GetValueOrDefault(day) + bucketCents;
            }
        }

        var hasMore = root.TryGetProperty("has_more", out var hmProp) && hmProp.ValueKind == JsonValueKind.True;
        var nextPage = hasMore && root.TryGetProperty("next_page", out var npProp) && npProp.ValueKind == JsonValueKind.String
            ? npProp.GetString()
            : null;

        return (dailyCents, nextPage);
    }

    internal static string FormatSummary(Dictionary<DateTime, decimal> dailyCents, DateTime todayUtc)
    {
        decimal SumDays(DateTime fromInclusive, DateTime toInclusive)
        {
            decimal total = 0;
            for (var day = fromInclusive; day <= toInclusive; day = day.AddDays(1))
                total += dailyCents.GetValueOrDefault(day);
            return total;
        }

        string Usd(decimal cents) => "$" + (cents / 100m).ToString("0.00", CultureInfo.InvariantCulture);

        var today = SumDays(todayUtc, todayUtc);
        var yesterday = SumDays(todayUtc.AddDays(-1), todayUtc.AddDays(-1));
        var last7 = SumDays(todayUtc.AddDays(-6), todayUtc);
        var last30 = SumDays(todayUtc.AddDays(-(LookbackDays - 1)), todayUtc);

        return $"""
            💳 <b>Anthropic API spend</b> (UTC days)

            Today: <b>{Usd(today)}</b>
            Yesterday: {Usd(yesterday)}
            Last 7 days: {Usd(last7)}
            Last 30 days: {Usd(last30)}

            That's billed spend — the API doesn't expose the remaining credit balance, so set a billing alert in the Console to catch low credits before they run out.
            """;
    }
}
