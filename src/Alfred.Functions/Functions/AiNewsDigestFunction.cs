using Alfred.Functions.Configuration;
using Alfred.Functions.Models;
using Alfred.Functions.Services.AI;
using Alfred.Functions.Services.Notifications;
using Alfred.Functions.Services.State;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Alfred.Functions.Functions;

public class AiNewsDigestFunction
{
    // Every-other-day cadence anchor: any date's day count since this epoch, mod 2, decides
    // whether it's a digest day. Set to the date this change was written so the cadence lands
    // on a digest day as soon as it ships, rather than skipping a day first — but that's only
    // guaranteed if deploy happens the same day; a day's slip just means one extra day of quiet.
    private static readonly DateTime DigestCadenceEpoch = new(2026, 8, 28);

    // How far back the already-covered list reaches when deduplicating stories
    internal const int CoveredLookbackDays = 14;

    // How far back newsletter-mined candidate stories are pulled into the run — a little
    // over two days, so nothing mined on the off day between two evening digests slips through
    internal const int CandidateLookbackHours = 50;

    private readonly INewsResearchService _newsResearch;
    private readonly INotificationService _notificationService;
    private readonly IStateService _stateService;
    private readonly AlfredOptions _options;
    private readonly ILogger<AiNewsDigestFunction> _logger;

    // Test seam: when set, overrides "today" (Malta time) used by the every-other-day gate,
    // so tests aren't at the mercy of which real-world day they happen to run on. Never set
    // in production.
    internal Func<DateTime>? TodayMaltaOverride { get; set; }

    public AiNewsDigestFunction(
        INewsResearchService newsResearch,
        INotificationService notificationService,
        IStateService stateService,
        IOptions<AlfredOptions> options,
        ILogger<AiNewsDigestFunction> logger)
    {
        _newsResearch = newsResearch;
        _notificationService = notificationService;
        _stateService = stateService;
        _options = options.Value;
        _logger = logger;
    }

    [Function("AiNewsDigest")]
    public async Task Run([TimerTrigger("0 0 18 * * *")] TimerInfo timerInfo) // 6 PM UTC = 8 PM CEST, every other day
    {
        _logger.LogInformation("AiNewsDigest triggered at {Time}", DateTime.UtcNow);

        if (!_options.AiNewsEnabled || string.IsNullOrWhiteSpace(_options.PersonalTelegramChatId))
        {
            _logger.LogInformation("AI news digest disabled, skipping");
            return;
        }

        var todayMalta = TodayMaltaOverride?.Invoke()
            ?? TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Europe/Malta")).Date;
        if (!IsDigestDay(todayMalta))
        {
            _logger.LogInformation("Off day in the every-other-day cadence, skipping AI news digest");
            return;
        }

        try
        {
            var rules = await _stateService.GetNewsRulesAsync();
            var recentlyReported = await _stateService.GetReportedNewsSinceAsync(
                DateTimeOffset.UtcNow.AddDays(-CoveredLookbackDays));
            var candidates = await _stateService.GetNewsCandidatesSinceAsync(
                DateTimeOffset.UtcNow.AddHours(-CandidateLookbackHours));

            var digest = await _newsResearch.ResearchDailyNewsAsync(rules, recentlyReported, candidates);

            if (digest.Incomplete)
            {
                // Not a quiet day — the run was cut off. Say so instead of silently skipping.
                _logger.LogWarning("AI news research was cut off before finishing — no digest today");
                await _notificationService.SendPersonalErrorAsync(
                    "AI news digest ran out of time before finishing — skipping today.");
                return;
            }

            if (digest.Items.Count == 0 || string.IsNullOrWhiteSpace(digest.TelegramMessage))
            {
                _logger.LogInformation("No AI news cleared the relevance bar today, skipping digest");
                return;
            }

            await _notificationService.SendPersonalAlertAsync(digest.TelegramMessage, BuildFeedbackButtons(digest.Items));

            await _stateService.SaveReportedNewsAsync(digest.Items);

            _logger.LogInformation("AI news digest sent ({ItemCount} items)", digest.Items.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AI news digest failed");
            await _notificationService.SendPersonalErrorAsync($"AI news digest failed: {ex.Message}");
        }
    }

    // Anchored to a fixed epoch rather than day-of-year — day-of-year parity flips twice in a
    // row across the turn of a non-leap year, which would send the digest on consecutive days
    internal static bool IsDigestDay(DateTime dateMalta) =>
        (dateMalta.Date - DigestCadenceEpoch).Days % 2 == 0;

    // One 👍/👎 pair per story (the notification service lays buttons out two per row, so
    // each pair forms one row). Callback data carries the story's URL hash — the same key
    // the ReportedNews table uses — well inside Telegram's 64-byte limit.
    internal static List<NotificationButton> BuildFeedbackButtons(List<AiNewsItem> items)
    {
        var buttons = new List<NotificationButton>();
        foreach (var item in items)
        {
            var key = TableStorageStateService.HashUrl(item.Url);
            var label = ShortLabel(item.Headline);
            buttons.Add(new NotificationButton($"👍 {label}", $"nf:+:{key}"));
            buttons.Add(new NotificationButton($"👎 {label}", $"nf:-:{key}"));
        }
        return buttons;
    }

    internal static string ShortLabel(string headline)
    {
        if (headline.Length <= 22)
            return headline;

        // Don't cut through a surrogate pair (emoji etc.) — a lone surrogate in a button
        // label makes Telegram reject the whole message
        var cut = char.IsHighSurrogate(headline[20]) ? 20 : 21;
        return headline[..cut].TrimEnd() + "…";
    }
}
