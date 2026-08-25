using System.Globalization;
using System.Text.Json;
using Alfred.Functions.Models;
using Anthropic.SDK;
using Microsoft.Extensions.Logging;

namespace Alfred.Functions.Services.AI;

public class ClaudeEtfResearchService : IEtfResearchService
{
    // Search budget scales with the watchlist — one lookup per fund plus a few for the
    // macro backdrop — but never past what one run can finish inside its wall-clock
    // budget. A weekly report that gets cut off costs the whole week, not one evening,
    // so this ceiling sits at the news digest's rather than above it.
    internal const int BaseSearches = 4;
    internal const int SearchesPerHolding = 1;
    internal const int MaxSearches = 12;

    private readonly ILogger<ClaudeEtfResearchService> _logger;

    public ClaudeEtfResearchService(ILogger<ClaudeEtfResearchService> logger)
    {
        _logger = logger;
    }

    public async Task<EtfReport> ResearchWeeklyPerformanceAsync(List<EtfHoldingEntity> holdings, bool onDemand = false)
    {
        if (holdings.Count == 0)
            return new EtfReport();

        var today = DateTime.Now.ToString("dddd, d MMMM yyyy");
        var (systemPrompt, userPrompt) = BuildEtfPrompt(today, holdings, onDemand);

        // Sonnet: a factual price read with light narrative — same tier as the news
        // flash check under the cost policy, not an Opus-grade judgment task
        var responseText = await WebResearchRunner.RunAsync(
            CreateClient(), "claude-sonnet-5", systemPrompt, userPrompt,
            SearchBudgetFor(holdings.Count),
            onDemand ? "etf report (on demand)" : "weekly etf report",
            _logger);

        return responseText is null ? new EtfReport { Incomplete = true } : ParseEtfResponse(responseText);
    }

    internal static int SearchBudgetFor(int holdingCount) =>
        Math.Min(MaxSearches, BaseSearches + SearchesPerHolding * holdingCount);

    // Test seam: when set, replaces the live client (tests back it with a fake
    // HttpClient). Never set in production.
    internal Func<AnthropicClient>? ClientFactory { get; set; }

    private AnthropicClient CreateClient()
    {
        if (ClientFactory is not null) return ClientFactory();

        var apiKey = Environment.GetEnvironmentVariable("Anthropic__ApiKey")
            ?? throw new InvalidOperationException("Anthropic API key not configured");

        return new AnthropicClient(apiKey, WebResearchRunner.LongRunHttpClient);
    }

    internal static string FormatHoldingsSection(List<EtfHoldingEntity> holdings) =>
        string.Join("\n", holdings.Select(h =>
        {
            var name = !string.IsNullOrWhiteSpace(h.Name) ? $" — {h.Name}" : "";
            var notes = !string.IsNullOrWhiteSpace(h.Notes) ? $" | why he holds it: {h.Notes}" : "";
            var previous = h.LastReportedAt is not null
                ? $" | last reported {h.LastReportedAt:d MMM yyyy} at {h.LastQuote ?? "n/a"}"
                  + (h.LastWeekChangePercent is not null
                      ? $" ({h.LastWeekChangePercent.Value.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture)}% that week)"
                      : "")
                : "";
            return $"- {h.Symbol}{name}{notes}{previous}";
        }));

    internal static (string System, string User) BuildEtfPrompt(
        string today,
        List<EtfHoldingEntity> holdings,
        bool onDemand)
    {
        var windowLine = onDemand
            ? "Matthew asked for this now, so cover the last five trading sessions up to the most "
              + "recent close — say clearly which dates the numbers cover."
            : "Cover the trading week that has just closed (Monday to Friday close) — say clearly "
              + "which dates the numbers cover.";

        var systemPrompt = $"""
            You are Alfred, Matthew's personal assistant. This job is his weekly read on the
            ETFs he follows: for each fund, how it did over the week and — the part he actually
            wants — a short narrative explaining what moved it.

            Today is {today}.

            How to research:
            - {windowLine}
            - Use web search to find, for each ETF on the WATCHLIST: the latest close price
              (with its currency), the change over the week in percent, and the year-to-date
              change in percent. Search by ticker and by full fund name; ETF tickers differ per
              exchange, so prefer a source that clearly identifies the same fund.
            - Then work out WHY it moved: the index or sector behind it, and the week's drivers
              (central-bank decisions, inflation prints, big earnings, oil, the dollar/euro,
              a single dominant holding). A few macro searches cover several funds at once.
            - Numbers must come from a source you actually read. If you cannot find a reliable
              figure for a fund, leave the number null and say so in its narrative rather than
              estimating one.
            - Where the WATCHLIST shows what was reported last week, frame the move as
              continuation or reversal instead of describing the week in isolation.
            - Never give buy, sell, or hold advice, and never predict prices. Describe what
              happened, why, and what to watch. You are informing him, not advising him.

            Respond with valid JSON only, no markdown code fences, with exactly these fields:

            1. "items": one entry per ETF on the watchlist, in the same order. Each item:
               - "symbol": the ticker exactly as it appears on the watchlist
               - "name": the full fund name
               - "quote": latest close with currency symbol, e.g. "€128.42" (null if not found)
               - "weekChangePercent": number, e.g. -1.4 (null if not found)
               - "ytdChangePercent": number (null if not found)
               - "narrative": two or three sentences — the move and what drove it, in plain
                 language. This is the point of the report; make it worth reading.
               - "sourceUrl": link to the page the numbers came from

            2. "telegramMessage": the report as Matthew reads it, or null if you found nothing
               at all. Write it the way a sharp human PA would text a weekend market round-up:
               - Open with one line starting "📈 " naming the dates covered and the week's mood
                 in a phrase.
               - Then one short block per fund: <b>TICKER</b> followed by the price, the week's
                 move with ▲ or ▼ (and YTD in passing), then the narrative in conversational
                 prose. Bold the numbers that matter (<b>+2.1%</b>).
               - Close with one line on what to watch next week.
               - Keep it glanceable — no headers, no separator lines, no advice.
               - Only use <b> and <a href=""> tags. Outside those tags, text pulled from the web
                 must not contain raw <, > or & characters — rephrase or escape them
                 (&lt; &gt; &amp;) or the message will fail to send.
            """;

        var userPrompt = $"""
            ## WATCHLIST (the ETFs Matthew follows)
            {FormatHoldingsSection(holdings)}

            Research the week for each of these and respond with the JSON described in your instructions.
            """;

        return (systemPrompt, userPrompt);
    }

    internal static EtfReport ParseEtfResponse(string json)
    {
        // Let a malformed response throw — the caller reports the failure to Matthew
        // instead of silently skipping the week
        using var doc = JsonDocument.Parse(WebResearchRunner.StripCodeFence(json));
        var root = doc.RootElement;

        var report = new EtfReport
        {
            TelegramMessage = root.TryGetProperty("telegramMessage", out var tmProp) && tmProp.ValueKind == JsonValueKind.String
                ? tmProp.GetString()
                : null
        };

        if (root.TryGetProperty("items", out var itemsProp) && itemsProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in itemsProp.EnumerateArray())
            {
                var symbol = item.TryGetProperty("symbol", out var sProp) ? sProp.GetString() : null;
                if (string.IsNullOrWhiteSpace(symbol))
                    continue;

                report.Items.Add(new EtfPerformance
                {
                    Symbol = symbol.Trim(),
                    Name = ReadString(item, "name"),
                    Quote = ReadString(item, "quote"),
                    WeekChangePercent = ReadNumber(item, "weekChangePercent"),
                    YtdChangePercent = ReadNumber(item, "ytdChangePercent"),
                    Narrative = ReadString(item, "narrative"),
                    SourceUrl = ReadString(item, "sourceUrl")
                });
            }
        }

        return report;
    }

    private static string? ReadString(JsonElement item, string name) =>
        item.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    // Percentages come back as numbers normally, but a model that formats one as "-1.4%"
    // shouldn't cost the whole figure
    private static double? ReadNumber(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var prop))
            return null;

        if (prop.ValueKind == JsonValueKind.Number)
            return prop.GetDouble();

        if (prop.ValueKind == JsonValueKind.String)
        {
            var text = (prop.GetString() ?? "").Trim().TrimEnd('%').Replace("+", "");
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                return parsed;
        }

        return null;
    }
}
