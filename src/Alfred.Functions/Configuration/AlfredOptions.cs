using System.Globalization;

namespace Alfred.Functions.Configuration;

public class AlfredOptions
{
    public const string SectionName = "Alfred";

    public string SchoolEmailSender { get; set; } = "noreply@myschoolmanagement.com";
    public string SharedCalendarId { get; set; } = string.Empty;
    public string TelegramChatId { get; set; } = string.Empty;
    public bool SendEmptyDigest { get; set; } = false;
    public int LookbackHours { get; set; } = 25;
    public int SchoolDaysAhead { get; set; } = 3;
    public string TelegramWebhookSecret { get; set; } = string.Empty;
    public int ChatLookbackDays { get; set; } = 30;
    public int ChatHistoryMaxTurns { get; set; } = 5;
    public int ChatHistoryMaxAgeMinutes { get; set; } = 60;
    public string AllowedTelegramUserIds { get; set; } = string.Empty;
    public string PersonalTelegramChatId { get; set; } = string.Empty;
    public bool NotifyAllPersonalEmails { get; set; } = false;
    public string PersonalCalendarId { get; set; } = "primary";
    public int PersonalLookbackHours { get; set; } = 0; // 0 = use LookbackHours

    // When true (default) the monitors query by date window instead of is:unread, so emails
    // Matthew reads before the next poll still get processed (silently — no alert) and appear
    // in digests and chat context. False restores the old unread-only behavior.
    public bool IncludeReadEmails { get; set; } = true;
    public int PersonalDigestDaysAhead { get; set; } = 7;

    // Summer break window (MM-dd, inclusive, Malta time). Evening digests pause during
    // this window; school emails alert immediately instead. Empty string disables the pause.
    public string SummerBreakStart { get; set; } = "07-01";
    public string SummerBreakEnd { get; set; } = "09-20";

    // Daily AI-news digest (evening timer, personal chat). Also requires
    // PersonalTelegramChatId to be set.
    public bool AiNewsEnabled { get; set; } = true;
    public int AiNewsMaxItems { get; set; } = 5;

    // Midday flash check for flag-level news that can't wait for the evening digest,
    // and the Friday weekly synthesis. Both also require AiNewsEnabled.
    public bool AiNewsFlashEnabled { get; set; } = true;
    public bool AiNewsWeeklyEnabled { get; set; } = true;

    public bool IsInSummerBreak(DateTime maltaDate)
    {
        if (!TryParseMonthDay(SummerBreakStart, out var startMonth, out var startDay) ||
            !TryParseMonthDay(SummerBreakEnd, out var endMonth, out var endDay))
        {
            return false;
        }

        var start = new DateTime(maltaDate.Year, startMonth, startDay);
        var end = new DateTime(maltaDate.Year, endMonth, endDay);
        return maltaDate.Date >= start && maltaDate.Date <= end;
    }

    private static bool TryParseMonthDay(string value, out int month, out int day)
    {
        if (DateTime.TryParseExact(value, "MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            month = parsed.Month;
            day = parsed.Day;
            return true;
        }

        month = 0;
        day = 0;
        return false;
    }
}

public class GoogleOptions
{
    public const string SectionName = "Google";

    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
}
