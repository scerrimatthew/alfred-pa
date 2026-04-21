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
    public async Task Run([TimerTrigger("0 0 17 * * *")] TimerInfo timerInfo) // 5 PM UTC = 7 PM CEST
    {
        _logger.LogInformation("EveningDigest triggered at {Time}", DateTime.UtcNow);

        try
        {
            var since = DateTimeOffset.UtcNow.AddHours(-_options.LookbackHours);
            var recentEmails = await _stateService.GetEmailsSinceAsync(since);

            var upcomingEvents = await _calendarService.GetUpcomingEventsAsync(_options.SchoolDaysAhead);

            if (recentEmails.Count == 0 && upcomingEvents.Count == 0 && !_options.SendEmptyDigest)
            {
                _logger.LogInformation("No emails or events to report, skipping digest");
                return;
            }

            var digestMessage = await _summarizer.BuildEveningDigestAsync(recentEmails, upcomingEvents);

            await _notificationService.SendAlertAsync(digestMessage);

            _logger.LogInformation("Evening digest sent successfully ({EmailCount} emails, {EventCount} events)",
                recentEmails.Count, upcomingEvents.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "EveningDigest failed");
            await _notificationService.SendErrorAsync($"EveningDigest failed: {ex.Message}");
        }
    }
}
