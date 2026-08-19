using Alfred.Functions.Configuration;
using Alfred.Functions.Services.AI;
using Alfred.Functions.Services.Notifications;
using Alfred.Functions.Services.State;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Alfred.Functions.Functions;

public class AiNewsDigestFunction
{
    // How far back the already-covered list reaches when deduplicating stories
    private const int CoveredLookbackDays = 14;

    private readonly INewsResearchService _newsResearch;
    private readonly INotificationService _notificationService;
    private readonly IStateService _stateService;
    private readonly AlfredOptions _options;
    private readonly ILogger<AiNewsDigestFunction> _logger;

    public AiNewsDigestFunction(
        INewsResearchService newsResearch,
        INotificationService notificationService,
        IStateService stateService,
        IOptions<AlfredOptions> options,
        ILogger<AiNewsDigestFunction> logger)
    {
        _newsResearch = newsResearch;
        _notificationService = notificationService;
        _stateService = stateService;
        _options = options.Value;
        _logger = logger;
    }

    [Function("AiNewsDigest")]
    public async Task Run([TimerTrigger("0 0 18 * * *")] TimerInfo timerInfo) // 6 PM UTC = 8 PM CEST
    {
        _logger.LogInformation("AiNewsDigest triggered at {Time}", DateTime.UtcNow);

        if (!_options.AiNewsEnabled || string.IsNullOrWhiteSpace(_options.PersonalTelegramChatId))
        {
            _logger.LogInformation("AI news digest disabled, skipping");
            return;
        }

        try
        {
            var rules = await _stateService.GetNewsRulesAsync();
            var recentlyReported = await _stateService.GetReportedNewsSinceAsync(
                DateTimeOffset.UtcNow.AddDays(-CoveredLookbackDays));

            var digest = await _newsResearch.ResearchDailyNewsAsync(rules, recentlyReported);

            if (digest.Items.Count == 0 || string.IsNullOrWhiteSpace(digest.TelegramMessage))
            {
                _logger.LogInformation("No AI news cleared the relevance bar today, skipping digest");
                return;
            }

            await _notificationService.SendPersonalAlertAsync(digest.TelegramMessage);

            await _stateService.SaveReportedNewsAsync(digest.Items);

            _logger.LogInformation("AI news digest sent ({ItemCount} items)", digest.Items.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AI news digest failed");
            await _notificationService.SendPersonalErrorAsync($"AI news digest failed: {ex.Message}");
        }
    }
}
