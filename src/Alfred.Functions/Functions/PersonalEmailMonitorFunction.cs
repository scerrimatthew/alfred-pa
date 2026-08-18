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

public class PersonalEmailMonitorFunction
{
    private readonly IGmailReaderService _gmailReader;
    private readonly ISummarizerService _summarizer;
    private readonly ICalendarService _calendarService;
    private readonly INotificationService _notificationService;
    private readonly IStateService _stateService;
    private readonly AlfredOptions _alfredOptions;
    private readonly ILogger<PersonalEmailMonitorFunction> _logger;

    public PersonalEmailMonitorFunction(
        IGmailReaderService gmailReader,
        ISummarizerService summarizer,
        ICalendarService calendarService,
        INotificationService notificationService,
        IStateService stateService,
        IOptions<AlfredOptions> alfredOptions,
        ILogger<PersonalEmailMonitorFunction> logger)
    {
        _gmailReader = gmailReader;
        _summarizer = summarizer;
        _calendarService = calendarService;
        _notificationService = notificationService;
        _stateService = stateService;
        _alfredOptions = alfredOptions.Value;
        _logger = logger;
    }

    // Offset from EmailMonitor (:00/:15/:30/:45) to spread Gmail and Claude API load
    [Function("PersonalEmailMonitor")]
    public async Task Run([TimerTrigger("0 5/15 * * * *")] TimerInfo timerInfo)
    {
        if (string.IsNullOrWhiteSpace(_alfredOptions.PersonalTelegramChatId))
        {
            _logger.LogInformation("PersonalEmailMonitor skipped — Alfred__PersonalTelegramChatId not configured");
            return;
        }

        _logger.LogInformation("PersonalEmailMonitor triggered at {Time}", DateTime.UtcNow);

        try
        {
            var newEmails = await _gmailReader.GetNewPersonalEmailsAsync();

            if (newEmails.Count == 0)
            {
                _logger.LogInformation("No new personal emails found");
                return;
            }

            var suppressionRules = await _stateService.GetSuppressionRulesAsync();
            var attentionRules = await _stateService.GetAttentionRulesAsync();

            foreach (var email in newEmails)
            {
                try
                {
                    _logger.LogInformation("Triaging personal email: {Subject}", email.Subject);

                    // A reply's thread id points at the first message; the first message's
                    // thread id equals its own id, so it can't have earlier entries
                    var threadContext = email.ThreadId != email.MessageId
                        ? await _stateService.GetPersonalEmailsByThreadAsync(email.ThreadId)
                        : [];

                    var triage = await _summarizer.TriagePersonalEmailAsync(email, suppressionRules, attentionRules, threadContext);

                    if (triage.Suppressed)
                    {
                        // Matthew asked not to hear about these — file silently, no calendar, no alert
                        _logger.LogInformation("Suppressed by rule {Rule}: {Subject}", triage.MatchedRule, email.Subject);
                    }
                    else
                    {
                        // Never create "pay this" reminders from an email that looks fraudulent
                        if (string.IsNullOrWhiteSpace(triage.FraudWarning))
                            await _calendarService.ProcessPersonalEventsAsync(triage.CalendarEvents, email.MessageId);

                        if (triage.RequiresAttention || _alfredOptions.NotifyAllPersonalEmails)
                        {
                            var message = !string.IsNullOrWhiteSpace(triage.TelegramMessage)
                                ? triage.TelegramMessage
                                : $"📬 <b>{email.Subject}</b>\nFrom: {email.SenderName}\n\n{triage.Summary}";

                            if (!string.IsNullOrWhiteSpace(triage.FraudWarning))
                                message = $"⚠️ <b>Careful:</b> {triage.FraudWarning}\n\n{message}";

                            message += $"\n\n<a href=\"{GmailLinks.ForThread(email.ThreadId)}\">Open in Gmail</a>";

                            var buttons = new List<NotificationButton>
                            {
                                new("Mark unread", $"mu:{email.MessageId}"),
                                new("Mute sender", $"sup:{email.MessageId}"),
                                new("Remind me tomorrow", $"sn1:{email.MessageId}")
                            };

                            await _notificationService.SendPersonalAlertAsync(message, buttons);
                        }
                        else
                        {
                            _logger.LogInformation("No notification needed ({Category}): {Subject}",
                                triage.Category, email.Subject);
                        }
                    }

                    await _stateService.MarkPersonalEmailProcessedAsync(
                        email.MessageId, email.Subject, email.SenderName, triage.Summary, triage.Category, triage.Suppressed, email.ThreadId, email.SenderEmail);

                    await _gmailReader.MarkAsReadAndLabelAsync(email.MessageId, LabelNames.ForPersonal(triage.Category));

                    _logger.LogInformation("Successfully processed personal email: {Subject}", email.Subject);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to process personal email: {Subject} ({MessageId})",
                        email.Subject, email.MessageId);

                    await _notificationService.SendPersonalErrorAsync(
                        $"Failed to process: {email.Subject}\n{ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PersonalEmailMonitor failed");
            await _notificationService.SendPersonalErrorAsync($"PersonalEmailMonitor failed: {ex.Message}");
        }
    }
}
