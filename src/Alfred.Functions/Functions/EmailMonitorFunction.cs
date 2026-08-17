using Alfred.Functions.Configuration;
using Alfred.Functions.Services.AI;
using Alfred.Functions.Services.Calendar;
using Alfred.Functions.Services.Gmail;
using Alfred.Functions.Services.Notifications;
using Alfred.Functions.Services.State;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Alfred.Functions.Functions;

public class EmailMonitorFunction
{
    private readonly IGmailReaderService _gmailReader;
    private readonly ISummarizerService _summarizer;
    private readonly ICalendarService _calendarService;
    private readonly INotificationService _notificationService;
    private readonly IStateService _stateService;
    private readonly AlfredOptions _options;
    private readonly ILogger<EmailMonitorFunction> _logger;

    public EmailMonitorFunction(
        IGmailReaderService gmailReader,
        ISummarizerService summarizer,
        ICalendarService calendarService,
        INotificationService notificationService,
        IStateService stateService,
        IOptions<AlfredOptions> options,
        ILogger<EmailMonitorFunction> logger)
    {
        _gmailReader = gmailReader;
        _summarizer = summarizer;
        _calendarService = calendarService;
        _notificationService = notificationService;
        _stateService = stateService;
        _options = options.Value;
        _logger = logger;
    }

    [Function("EmailMonitor")]
    public async Task Run([TimerTrigger("0 */15 * * * *")] TimerInfo timerInfo)
    {
        _logger.LogInformation("EmailMonitor triggered at {Time}", DateTime.UtcNow);

        try
        {
            var newEmails = await _gmailReader.GetNewEmailsAsync();

            if (newEmails.Count == 0)
            {
                _logger.LogInformation("No new school emails found");
                return;
            }

            // During summer break the evening digest is paused, so every school email
            // must alert immediately or it would never be seen
            var maltaTz = TimeZoneInfo.FindSystemTimeZoneById("Europe/Malta");
            var todayMalta = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, maltaTz).Date;
            var inSummerBreak = _options.IsInSummerBreak(todayMalta);

            foreach (var email in newEmails)
            {
                try
                {
                    _logger.LogInformation("Processing email: {Subject}", email.Subject);

                    var digest = await _summarizer.SummarizeEmailAsync(email);

                    await _calendarService.ProcessEventsAsync(digest.CalendarEvents, email.MessageId);

                    if (digest.RequiresImmediateAlert || inSummerBreak)
                    {
                        await _notificationService.SendAlertAsync(digest.TelegramMessage);
                    }
                    else
                    {
                        _logger.LogInformation("Skipping immediate notification, will appear in evening digest: {Subject}", email.Subject);
                    }

                    await _stateService.MarkEmailProcessedAsync(
                        email.MessageId, email.Subject, email.SenderName, digest.TelegramMessage, digest.Homework, digest.Category);

                    await _gmailReader.MarkAsReadAndLabelAsync(email.MessageId, LabelNames.ForSchool(digest.Category));

                    _logger.LogInformation("Successfully processed email: {Subject}", email.Subject);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to process email: {Subject} ({MessageId})",
                        email.Subject, email.MessageId);

                    await _notificationService.SendErrorAsync(
                        $"Failed to process: {email.Subject}\n{ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "EmailMonitor failed");
            await _notificationService.SendErrorAsync($"EmailMonitor failed: {ex.Message}");
        }
    }
}
