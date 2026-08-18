using Alfred.Functions.Configuration;
using Alfred.Functions.Services.Notifications;
using Alfred.Functions.Services.State;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Alfred.Functions.Functions;

// Once a month, proposes unsubscribing from senders whose every email was filed
// quietly and who publish a List-Unsubscribe mechanism. Each sender is proposed
// exactly once; the actual unsubscribe happens via the inline buttons.
public class MailingListHygieneFunction
{
    private const int MinEmailsBeforeProposing = 3;
    private const int MaxProposalsPerRun = 5;

    private readonly IStateService _stateService;
    private readonly INotificationService _notificationService;
    private readonly AlfredOptions _options;
    private readonly ILogger<MailingListHygieneFunction> _logger;

    public MailingListHygieneFunction(
        IStateService stateService,
        INotificationService notificationService,
        IOptions<AlfredOptions> options,
        ILogger<MailingListHygieneFunction> logger)
    {
        _stateService = stateService;
        _notificationService = notificationService;
        _options = options.Value;
        _logger = logger;
    }

    // 1st of the month at 14:45 UTC — shortly after the evening digest
    [Function("MailingListHygiene")]
    public async Task Run([TimerTrigger("0 45 14 1 * *")] TimerInfo timerInfo)
    {
        if (string.IsNullOrWhiteSpace(_options.PersonalTelegramChatId))
            return;

        try
        {
            var candidates = await _stateService.GetUnsubscribeCandidatesAsync(
                MinEmailsBeforeProposing, MaxProposalsPerRun);

            if (candidates.Count == 0)
            {
                _logger.LogInformation("MailingListHygiene: no unsubscribe candidates this month");
                return;
            }

            await _notificationService.SendPersonalAlertAsync(
                "🧹 Monthly inbox hygiene — these senders keep writing but never needed your attention:");

            foreach (var sender in candidates)
            {
                var message = $"<b>{sender.SenderName}</b> ({sender.SenderEmail}) — "
                    + $"{sender.TotalCount} emails, none worth interrupting you for. Unsubscribe?";

                var buttons = new List<NotificationButton>
                {
                    new("Unsubscribe", $"unsub:{sender.RowKey}"),
                    new("Keep them", $"keep:{sender.RowKey}")
                };

                await _notificationService.SendPersonalAlertAsync(message, buttons);

                sender.ProposedAt = DateTimeOffset.UtcNow;
                await _stateService.UpsertSenderStatAsync(sender);
            }

            _logger.LogInformation("MailingListHygiene proposed {Count} unsubscribes", candidates.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MailingListHygiene failed");
            await _notificationService.SendPersonalErrorAsync($"MailingListHygiene failed: {ex.Message}");
        }
    }
}
