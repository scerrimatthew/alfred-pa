using Alfred.Functions.Configuration;
using Alfred.Functions.Services.AI;
using Alfred.Functions.Services.Notifications;
using Alfred.Functions.Services.State;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Alfred.Functions.Functions;

// Midday spot check for flag-level AI news that shouldn't wait for the evening digest:
// a competitor launching into the A-SDLC niche, an Anthropic partner-program change with
// a deadline, thesis-level disconfirming evidence, or regulation with a compliance clock.
// Runs once a day (daily-capped by design); almost every run finds nothing and stays silent.
public class AiNewsFlashFunction
{
    private readonly INewsResearchService _newsResearch;
    private readonly INotificationService _notificationService;
    private readonly IStateService _stateService;
    private readonly AlfredOptions _options;
    private readonly ILogger<AiNewsFlashFunction> _logger;

    public AiNewsFlashFunction(
        INewsResearchService newsResearch,
        INotificationService notificationService,
        IStateService stateService,
        IOptions<AlfredOptions> options,
        ILogger<AiNewsFlashFunction> logger)
    {
        _newsResearch = newsResearch;
        _notificationService = notificationService;
        _stateService = stateService;
        _options = options.Value;
        _logger = logger;
    }

    [Function("AiNewsFlash")]
    public async Task Run([TimerTrigger("0 0 10 * * *")] TimerInfo timerInfo) // 10 AM UTC = noon CEST
    {
        _logger.LogInformation("AiNewsFlash triggered at {Time}", DateTime.UtcNow);

        if (!_options.AiNewsEnabled || !_options.AiNewsFlashEnabled
            || string.IsNullOrWhiteSpace(_options.PersonalTelegramChatId))
        {
            _logger.LogInformation("AI news flash check disabled, skipping");
            return;
        }

        try
        {
            var rules = await _stateService.GetNewsRulesAsync();
            var recentlyReported = await _stateService.GetReportedNewsSinceAsync(
                DateTimeOffset.UtcNow.AddDays(-AiNewsDigestFunction.CoveredLookbackDays));

            var flash = await _newsResearch.CheckUrgentNewsAsync(rules, recentlyReported);

            if (flash.Items.Count == 0 || string.IsNullOrWhiteSpace(flash.TelegramMessage))
            {
                _logger.LogInformation("Midday flash check found nothing urgent (the normal outcome)");
                return;
            }

            await _notificationService.SendPersonalAlertAsync(
                flash.TelegramMessage, AiNewsDigestFunction.BuildFeedbackButtons(flash.Items));

            // Recorded as covered so the evening digest doesn't re-report the same story
            await _stateService.SaveReportedNewsAsync(flash.Items);

            _logger.LogInformation("AI news flash alert sent ({ItemCount} items)", flash.Items.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AI news flash check failed");
            await _notificationService.SendPersonalErrorAsync($"AI news flash check failed: {ex.Message}");
        }
    }
}
