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
                // Fresh mail is done either way; a pending backfill still gets its batch below
                _logger.LogInformation("No new personal emails found");
            }

            var suppressionRules = await _stateService.GetSuppressionRulesAsync();
            var attentionRules = await _stateService.GetAttentionRulesAsync();
            var userFacts = await _stateService.GetUserFactsAsync();

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

                    var triage = await _summarizer.TriagePersonalEmailAsync(email, suppressionRules, attentionRules, userFacts, threadContext);

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

                        // Emails Matthew already read himself are processed silently — except
                        // fraud warnings, which he may not have spotted while reading
                        var shouldNotify = (triage.RequiresAttention || _alfredOptions.NotifyAllPersonalEmails)
                            && (email.WasUnread || !string.IsNullOrWhiteSpace(triage.FraudWarning));

                        if (shouldNotify)
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
                        email.MessageId, email.Subject, email.SenderName, triage.Summary, triage.Category, triage.Suppressed, email.ThreadId, email.SenderEmail,
                        needsReply: triage.NeedsReply && !triage.Suppressed);

                    await _gmailReader.MarkAsReadAndLabelAsync(email.MessageId, LabelNames.ForPersonal(triage.Category));

                    // Newsletter-mined story leads feed the evening AI-news digest — best-effort
                    if (triage.NewsLeads.Count > 0)
                    {
                        try
                        {
                            await _stateService.SaveNewsCandidatesAsync(triage.NewsLeads
                                .Select(l => new NewsCandidateEntity
                                {
                                    Headline = l.Headline,
                                    Url = l.Url,
                                    Note = l.Note,
                                    Source = email.SenderName
                                })
                                .ToList());
                            _logger.LogInformation("Saved {Count} news leads from {Sender}",
                                triage.NewsLeads.Count, email.SenderName);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to save news leads from {Sender}", email.SenderName);
                        }
                    }

                    // Sender tally feeds the monthly unsubscribe proposals — best-effort
                    if (!string.IsNullOrWhiteSpace(email.SenderEmail))
                    {
                        try
                        {
                            await _stateService.RecordSenderSeenAsync(
                                email.SenderEmail, email.SenderName,
                                wasQuiet: triage.Suppressed || !triage.RequiresAttention,
                                email.ListUnsubscribe, email.ListUnsubscribeOneClick);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to record sender stats for {Sender}", email.SenderEmail);
                        }
                    }

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

        await ProcessBackfillBatchAsync();
    }

    // One batch of a /backfill sweep, run after (and never instead of) fresh mail.
    // Quiet by design: no notifications, no needs-reply nagging, labels without
    // changing read state, and ProcessedAt backdated to the receive date. Calendar
    // events are still created — the triage prompt drops dates that already passed.
    // The ProcessedEmails table dedups everything, so overlapping backfills or an
    // overlap with normal runs never processes an email twice.
    private const int BackfillBatchSize = 20;

    private async Task ProcessBackfillBatchAsync()
    {
        BackfillStateEntity? backfill;
        try
        {
            backfill = await _stateService.GetBackfillStateAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Backfill state check failed");
            return;
        }

        if (backfill is null)
            return;

        try
        {
            var batch = await _gmailReader.GetBackfillBatchAsync(backfill.OldestDate, BackfillBatchSize);

            if (batch.Count > 0)
            {
                var suppressionRules = await _stateService.GetSuppressionRulesAsync();
                var attentionRules = await _stateService.GetAttentionRulesAsync();
                var userFacts = await _stateService.GetUserFactsAsync();

                foreach (var email in batch)
                {
                    try
                    {
                        _logger.LogInformation("Backfill triage: {Subject} ({Date})", email.Subject, email.ReceivedDate);

                        var threadContext = email.ThreadId != email.MessageId
                            ? await _stateService.GetPersonalEmailsByThreadAsync(email.ThreadId)
                            : [];

                        var triage = await _summarizer.TriagePersonalEmailAsync(email, suppressionRules, attentionRules, userFacts, threadContext);

                        if (!triage.Suppressed && string.IsNullOrWhiteSpace(triage.FraudWarning))
                            await _calendarService.ProcessPersonalEventsAsync(triage.CalendarEvents, email.MessageId);

                        await _stateService.MarkPersonalEmailProcessedAsync(
                            email.MessageId, email.Subject, email.SenderName, triage.Summary, triage.Category,
                            triage.Suppressed, email.ThreadId, email.SenderEmail,
                            needsReply: false, processedAt: email.ReceivedDate);

                        await _gmailReader.LabelWithoutMarkingReadAsync(email.MessageId, LabelNames.ForPersonal(triage.Category));

                        if (!string.IsNullOrWhiteSpace(email.SenderEmail))
                        {
                            await _stateService.RecordSenderSeenAsync(
                                email.SenderEmail, email.SenderName,
                                wasQuiet: triage.Suppressed || !triage.RequiresAttention,
                                email.ListUnsubscribe, email.ListUnsubscribeOneClick);
                        }

                        backfill.ProcessedCount++;
                    }
                    catch (Exception ex)
                    {
                        // Quiet mode: log and move on — the email stays unprocessed and the
                        // next batch retries it
                        _logger.LogError(ex, "Backfill failed on {Subject} ({MessageId})", email.Subject, email.MessageId);
                    }
                }

                await _stateService.SaveBackfillStateAsync(backfill);
            }

            if (batch.Count < BackfillBatchSize)
            {
                await _stateService.ClearBackfillStateAsync();
                var days = (int)Math.Ceiling((DateTimeOffset.UtcNow - backfill.OldestDate).TotalDays);
                await _notificationService.SendPersonalAlertAsync(
                    $"🗂️ Backfill finished — I've quietly filed <b>{backfill.ProcessedCount}</b> emails from the last {days} days: "
                    + "categorized and labeled in Gmail, future deadlines on your calendar, and all of it available to ask about.");
                _logger.LogInformation("Backfill complete: {Count} emails processed", backfill.ProcessedCount);
            }
            else
            {
                _logger.LogInformation("Backfill progress: {Count} processed so far, more remaining", backfill.ProcessedCount);
            }
        }
        catch (Exception ex)
        {
            // Quiet mode: never notify about backfill hiccups; the marker stays and the
            // next run picks up where this one stopped
            _logger.LogError(ex, "Backfill batch failed; will retry next run");
        }
    }
}
