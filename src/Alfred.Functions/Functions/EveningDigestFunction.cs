using Alfred.Functions.Configuration;
using Alfred.Functions.Models;
using Alfred.Functions.Services.AI;
using Alfred.Functions.Services.Calendar;
using Alfred.Functions.Services.Gmail;
using Alfred.Functions.Services.Notifications;
using Alfred.Functions.Services.State;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Alfred.Functions.Functions;

public class EveningDigestFunction
{
    // How far back the digest looks for unanswered needs-reply emails, and how many
    // Gmail thread checks one digest will spend on them
    private const int NeedsReplyLookbackDays = 7;
    private const int NeedsReplyMaxChecks = 10;

    private readonly ISummarizerService _summarizer;
    private readonly ICalendarService _calendarService;
    private readonly INotificationService _notificationService;
    private readonly IStateService _stateService;
    private readonly IGmailReaderService _gmailReader;
    private readonly AlfredOptions _options;
    private readonly ILogger<EveningDigestFunction> _logger;

    public EveningDigestFunction(
        ISummarizerService summarizer,
        ICalendarService calendarService,
        INotificationService notificationService,
        IStateService stateService,
        IGmailReaderService gmailReader,
        IOptions<AlfredOptions> options,
        ILogger<EveningDigestFunction> logger)
    {
        _summarizer = summarizer;
        _calendarService = calendarService;
        _notificationService = notificationService;
        _stateService = stateService;
        _gmailReader = gmailReader;
        _options = options.Value;
        _logger = logger;
    }

    [Function("EveningDigest")]
    public async Task Run([TimerTrigger("0 0 14 * * *")] TimerInfo timerInfo) // 2 PM UTC = 4 PM CEST
    {
        _logger.LogInformation("EveningDigest triggered at {Time}", DateTime.UtcNow);

        await SendSchoolDigestAsync();
        await SendPersonalDigestAsync();
    }

    private async Task SendSchoolDigestAsync()
    {
        try
        {
            var maltaTz = TimeZoneInfo.FindSystemTimeZoneById("Europe/Malta");
            var todayMalta = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, maltaTz).Date;

            if (_options.IsInSummerBreak(todayMalta))
            {
                _logger.LogInformation(
                    "School digest skipped — summer break ({Start} to {End})",
                    _options.SummerBreakStart, _options.SummerBreakEnd);
                return;
            }

            var since = DateTimeOffset.UtcNow.AddHours(-_options.LookbackHours);
            var recentEmails = await _stateService.GetEmailsSinceAsync(since);

            var upcomingEvents = await _calendarService.GetUpcomingEventsAsync(_options.SchoolDaysAhead);

            if (recentEmails.Count == 0 && upcomingEvents.Count == 0 && !_options.SendEmptyDigest)
            {
                _logger.LogInformation("No school emails or events to report, skipping school digest");
                return;
            }

            var digestMessage = await _summarizer.BuildEveningDigestAsync(recentEmails, upcomingEvents);

            await _notificationService.SendAlertAsync(digestMessage);

            _logger.LogInformation("School digest sent successfully ({EmailCount} emails, {EventCount} events)",
                recentEmails.Count, upcomingEvents.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "School digest failed");
            await _notificationService.SendErrorAsync($"EveningDigest failed: {ex.Message}");
        }
    }

    // The personal digest runs year-round — deadlines and appointments don't take summers off
    private async Task SendPersonalDigestAsync()
    {
        if (string.IsNullOrWhiteSpace(_options.PersonalTelegramChatId))
            return;

        try
        {
            var since = DateTimeOffset.UtcNow.AddHours(-_options.LookbackHours);
            var todaysEmails = (await _stateService.GetPersonalEmailsSinceAsync(since))
                .Where(e => !e.Suppressed)
                .ToList();

            var upcomingActions = await _calendarService.GetUpcomingPersonalEventsAsync(_options.PersonalDigestDaysAhead);

            var awaitingReply = await GetUnansweredEmailsAsync();

            if (todaysEmails.Count == 0 && upcomingActions.Count == 0 && awaitingReply.Count == 0 && !_options.SendEmptyDigest)
            {
                _logger.LogInformation("No personal emails or actions to report, skipping personal digest");
                return;
            }

            var digestMessage = await _summarizer.BuildPersonalDigestAsync(todaysEmails, upcomingActions, awaitingReply);

            await _notificationService.SendPersonalAlertAsync(digestMessage);

            _logger.LogInformation("Personal digest sent successfully ({EmailCount} emails, {ActionCount} actions)",
                todaysEmails.Count, upcomingActions.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Personal digest failed");
            await _notificationService.SendPersonalErrorAsync($"Personal digest failed: {ex.Message}");
        }
    }

    // Needs-reply emails from the past week that Matthew still hasn't answered.
    // Checks Gmail for a sent reply in each thread and clears the flag once one appears,
    // so an email is only ever nudged about while it's genuinely unanswered.
    private async Task<List<ProcessedEmailEntity>> GetUnansweredEmailsAsync()
    {
        var flagged = await _stateService.GetPersonalEmailsNeedingReplyAsync(
            DateTimeOffset.UtcNow.AddDays(-NeedsReplyLookbackDays));

        var awaitingReply = new List<ProcessedEmailEntity>();
        foreach (var email in flagged.Take(NeedsReplyMaxChecks))
        {
            try
            {
                if (!string.IsNullOrEmpty(email.GmailThreadId)
                    && await _gmailReader.HasRepliedAsync(email.GmailThreadId, email.RowKey))
                {
                    await _stateService.ClearNeedsReplyAsync(email.RowKey);
                }
                else
                {
                    awaitingReply.Add(email);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Reply check failed for {MessageId}, including it in the digest anyway", email.RowKey);
                awaitingReply.Add(email);
            }
        }

        return awaitingReply;
    }
}
