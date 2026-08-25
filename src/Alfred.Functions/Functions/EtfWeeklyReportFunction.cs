using Alfred.Functions.Configuration;
using Alfred.Functions.Models;
using Alfred.Functions.Services.AI;
using Alfred.Functions.Services.Notifications;
using Alfred.Functions.Services.State;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Alfred.Functions.Functions;

// Saturday-morning read on the ETFs Matthew follows: last week's close and move for each
// fund plus a short narrative on what drove it. It runs after Friday's US close so the
// whole trading week is in the numbers.
public class EtfWeeklyReportFunction
{
    // A research marker younger than this means a run (timer or /etf) is still going;
    // matches the webhook's window so the two paths agree on what "in flight" means
    internal static readonly TimeSpan ResearchInFlightWindow = TimeSpan.FromMinutes(10);

    private readonly IEtfResearchService _etfResearch;
    private readonly INotificationService _notificationService;
    private readonly IStateService _stateService;
    private readonly AlfredOptions _options;
    private readonly ILogger<EtfWeeklyReportFunction> _logger;

    public EtfWeeklyReportFunction(
        IEtfResearchService etfResearch,
        INotificationService notificationService,
        IStateService stateService,
        IOptions<AlfredOptions> options,
        ILogger<EtfWeeklyReportFunction> logger)
    {
        _etfResearch = etfResearch;
        _notificationService = notificationService;
        _stateService = stateService;
        _options = options.Value;
        _logger = logger;
    }

    [Function("EtfWeeklyReport")]
    public async Task Run([TimerTrigger("0 30 8 * * 6")] TimerInfo timerInfo) // Saturday 8:30 AM UTC = 10:30 CEST
    {
        _logger.LogInformation("EtfWeeklyReport triggered at {Time}", DateTime.UtcNow);

        if (!_options.EtfReportEnabled || string.IsNullOrWhiteSpace(_options.PersonalTelegramChatId))
        {
            _logger.LogInformation("Weekly ETF report disabled, skipping");
            return;
        }

        try
        {
            var saved = await _stateService.GetEtfHoldingsAsync();
            var (holdings, dropped) = BuildWatchlist(saved, _options.EtfTickers, _options.EtfMaxHoldings);

            if (holdings.Count == 0)
            {
                // Nothing tracked yet. Ask once — otherwise the feature sits silent every
                // Saturday waiting for Matthew to guess that he has to name his funds —
                // and stay quiet on every run after that.
                if (await _stateService.TryClaimEtfNudgeAsync())
                {
                    try
                    {
                        await _notificationService.SendPersonalAlertAsync(
                            "📈 I can send you a weekly read on your ETFs every Saturday morning — the price, "
                            + "the week's move, and what actually drove it. Which ones should I follow? Just tell me "
                            + "(\"track VWCE and IWDA\") and I'll take it from there.");
                        _logger.LogInformation("Sent the one-time ETF onboarding nudge");
                    }
                    catch
                    {
                        // The claim is only spent if the nudge actually went out — otherwise
                        // one bad Saturday would mean Matthew is never asked again. A failure
                        // to hand it back must not replace the send failure being reported.
                        try
                        {
                            await _stateService.ReleaseEtfNudgeAsync();
                        }
                        catch (Exception releaseEx)
                        {
                            _logger.LogWarning(releaseEx, "Failed to hand back the ETF onboarding nudge claim");
                        }
                        throw;
                    }
                }
                else
                {
                    _logger.LogInformation("No ETFs are being tracked — skipping the weekly report");
                }
                return;
            }

            if (dropped > 0)
            {
                _logger.LogWarning("ETF watchlist has {Dropped} funds beyond the {Max} covered this week",
                    dropped, Math.Max(1, _options.EtfMaxHoldings));
            }

            // An on-demand /etf can still be running when the timer fires (or vice versa) —
            // both would bill a full web-search run and message the same chat, so they share
            // the marker the webhook uses
            var inFlight = await _stateService.GetEtfRequestAsync();
            if (inFlight is not null && inFlight.RequestedAt > DateTimeOffset.UtcNow - ResearchInFlightWindow)
            {
                _logger.LogInformation("An ETF research run started at {Time} is still in flight — skipping this run",
                    inFlight.RequestedAt);
                return;
            }

            var requestedAt = DateTimeOffset.UtcNow;
            await _stateService.SaveEtfRequestAsync(new NewsRequestStateEntity { RequestedAt = requestedAt });

            try
            {
                var report = await _etfResearch.ResearchWeeklyPerformanceAsync(holdings);

                if (report.Incomplete)
                {
                    _logger.LogWarning("Weekly ETF research was cut off before finishing — no report today");
                    await _notificationService.SendPersonalErrorAsync(
                        "The weekly ETF report ran out of time before finishing — skipping it this week.");
                    return;
                }

                if (report.Items.Count == 0 || string.IsNullOrWhiteSpace(report.TelegramMessage))
                {
                    // Unlike the news digest, an empty result here isn't a quiet week — the
                    // numbers exist and weren't found, which is worth knowing about
                    _logger.LogWarning("Weekly ETF research came back empty for {Count} tracked funds", holdings.Count);
                    await _notificationService.SendPersonalErrorAsync(
                        "I couldn't pull this week's ETF numbers — I'll try again next Saturday.");
                    return;
                }

                await _notificationService.SendPersonalAlertAsync(AppendDroppedNote(report.TelegramMessage, dropped));

                await _stateService.SaveEtfSnapshotsAsync(report.Items);

                _logger.LogInformation("Weekly ETF report sent ({Count} funds)", report.Items.Count);
            }
            finally
            {
                // Only clear the marker this run wrote — a run that outlived the window must
                // not delete a successor's marker. A leaked marker expires on its own, and a
                // failure here must not mask the original exception.
                try
                {
                    var current = await _stateService.GetEtfRequestAsync();
                    if (current is not null && IsSameRun(current.RequestedAt, requestedAt))
                        await _stateService.ClearEtfRequestAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to clear the ETF research marker — it will expire on its own");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Weekly ETF report failed");
            await _notificationService.SendPersonalErrorAsync($"Weekly ETF report failed: {ex.Message}");
        }
    }

    // Marker ownership: the stored timestamp round-trips through Table Storage, which need
    // not preserve sub-millisecond ticks, so "my own run" is a second-wide window rather
    // than an exact match — otherwise a run would never clear the marker it just wrote
    internal static bool IsSameRun(DateTimeOffset stored, DateTimeOffset mine) =>
        (stored - mine).Duration() < TimeSpan.FromSeconds(1);

    // The watchlist Matthew manages from chat, plus any tickers seeded in configuration
    // that he hasn't saved (configured ones never override a saved holding's name/notes).
    // Capped so one run stays inside its research budget; the overflow count is returned
    // rather than silently dropped.
    internal static (List<EtfHoldingEntity> Holdings, int Dropped) BuildWatchlist(
        List<EtfHoldingEntity> saved,
        string configuredTickers,
        int maxHoldings)
    {
        var holdings = saved.OrderBy(h => h.CreatedAt).ThenBy(h => h.RowKey, StringComparer.Ordinal).ToList();
        var known = holdings.Select(h => h.RowKey).ToHashSet(StringComparer.Ordinal);

        foreach (var ticker in ParseSymbols(configuredTickers))
        {
            var key = TableStorageStateService.EtfKey(ticker);
            if (!known.Add(key))
                continue;

            holdings.Add(new EtfHoldingEntity { RowKey = key, Symbol = ticker });
        }

        return ApplyCap(holdings, maxHoldings);
    }

    // The tickers Matthew just named, in the order he named them, carrying the saved name,
    // notes and last snapshot for any he already tracks so they aren't researched cold.
    // Same cap as the watchlist, applied to the tail of his list rather than reordering it.
    internal static (List<EtfHoldingEntity> Holdings, int Dropped) BuildRequestedWatchlist(
        List<string> symbols,
        List<EtfHoldingEntity> saved,
        int maxHoldings)
    {
        var savedByKey = saved
            .GroupBy(h => h.RowKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var holdings = new List<EtfHoldingEntity>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var symbol in symbols)
        {
            var key = TableStorageStateService.EtfKey(symbol);
            if (!seen.Add(key))
                continue;

            holdings.Add(savedByKey.TryGetValue(key, out var tracked)
                ? tracked
                : new EtfHoldingEntity { RowKey = key, Symbol = symbol });
        }

        return ApplyCap(holdings, maxHoldings);
    }

    private static (List<EtfHoldingEntity> Holdings, int Dropped) ApplyCap(
        List<EtfHoldingEntity> holdings, int maxHoldings)
    {
        var cap = Math.Max(1, maxHoldings);
        return holdings.Count <= cap
            ? (holdings, 0)
            : (holdings.Take(cap).ToList(), holdings.Count - cap);
    }

    // "VWCE, SXR8.DE IWDA" — commas, semicolons, or whitespace all separate tickers
    internal static List<string> ParseSymbols(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split([',', ';', ' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();

    // A capped watchlist says so in the message — a silent cap would read as full coverage
    internal static string AppendDroppedNote(string message, int dropped) =>
        dropped <= 0
            ? message
            : message + $"\n\n(That's the first part of your list — {dropped} more "
                + $"{(dropped == 1 ? "fund is" : "funds are")} on it than I can cover in one go.)";
}
