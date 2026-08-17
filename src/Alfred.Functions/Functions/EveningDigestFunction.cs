using Alfred.Functions.Configuration;
using Alfred.Functions.Services.AI;
using Alfred.Functions.Services.Calendar;
using Alfred.Functions.Services.Notifications;
using Alfred.Functions.Services.State;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Alfred.Functions.Functions;

public class EveningDigestFunction
{
    private readonly ISummarizerService _summarizer;
    private readonly ICalendarService _calendarService;
    private readonly INotificationService _notificationService;
    private readonly IStateService _stateService;
    private readonly AlfredOptions _options;
    private readonly ILogger<EveningDigestFunction> _logger;

    public EveningDigestFunction(
        ISummarizerService summarizer,
        ICalendarService calendarService,
        INotificationService notificationService,
        IStateService stateService,
        IOptions<AlfredOptions> options,
        ILogger<EveningDigestFunction> logger)
    {
        _summarizer = summarizer;
        _calendarService = calendarService;
        _notificationService = notificationService;
        _stateService = stateService;
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

            if (todaysEmails.Count == 0 && upcomingActions.Count == 0 && !_options.SendEmptyDigest)
            {
                _logger.LogInformation("No personal emails or actions to report, skipping personal digest");
                return;
            }

            var digestMessage = await _summarizer.BuildPersonalDigestAsync(todaysEmails, upcomingActions);

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
}
