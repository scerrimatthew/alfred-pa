using Alfred.Functions.Configuration;
using Alfred.Functions.Services.Gmail;
using Alfred.Functions.Services.Notifications;
using Alfred.Functions.Services.State;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Alfred.Functions.Functions;

// Resurfaces snoozed personal emails when their reminder time arrives
public class SnoozeCheckFunction
{
    private readonly IStateService _stateService;
    private readonly INotificationService _notificationService;
    private readonly AlfredOptions _options;
    private readonly ILogger<SnoozeCheckFunction> _logger;

    public SnoozeCheckFunction(
        IStateService stateService,
        INotificationService notificationService,
        IOptions<AlfredOptions> options,
        ILogger<SnoozeCheckFunction> logger)
    {
        _stateService = stateService;
        _notificationService = notificationService;
        _options = options.Value;
        _logger = logger;
    }

    // Offset from the two email monitors (:00 and :05) to spread the load
    [Function("SnoozeCheck")]
    public async Task Run([TimerTrigger("0 10/15 * * * *")] TimerInfo timerInfo)
    {
        if (string.IsNullOrWhiteSpace(_options.PersonalTelegramChatId))
            return;

        try
        {
            var due = await _stateService.GetDueSnoozesAsync(DateTimeOffset.UtcNow);
            if (due.Count == 0)
                return;

            foreach (var snooze in due.OrderBy(s => s.DueAt))
            {
                var message = $"""
                    ⏰ You asked me to remind you about this one:

                    <b>{snooze.Subject}</b> — {snooze.SenderName}
                    {snooze.Summary}
                    """;

                if (!string.IsNullOrEmpty(snooze.ThreadId))
                    message += $"\n\n<a href=\"{GmailLinks.ForThread(snooze.ThreadId)}\">Open in Gmail</a>";

                var buttons = new List<NotificationButton>
                {
                    new("Mark unread", $"mu:{snooze.RowKey}"),
                    new("Snooze again tomorrow", $"sn1:{snooze.RowKey}")
                };

                await _notificationService.SendPersonalAlertAsync(message, buttons);
                await _stateService.DeleteSnoozeAsync(snooze.RowKey);

                _logger.LogInformation("Snooze reminder sent for {MessageId}: {Subject}", snooze.RowKey, snooze.Subject);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SnoozeCheck failed");
            await _notificationService.SendPersonalErrorAsync($"SnoozeCheck failed: {ex.Message}");
        }
    }
}
