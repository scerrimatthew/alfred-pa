using Alfred.Functions.Configuration;
using Alfred.Functions.Services.AI;
using Alfred.Functions.Services.Notifications;
using Alfred.Functions.Services.State;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Alfred.Functions.Functions;

// Friday-evening synthesis connecting the week's reported AI-news stories per vision
// strand — "the week in AI vs the thesis" — sent shortly before the daily digest so the
// week's wrap-up lands first. Pure synthesis over what was already reported; no web search.
public class AiNewsWeeklyFunction
{
    internal const int WeekLookbackDays = 7;

    private readonly INewsResearchService _newsResearch;
    private readonly INotificationService _notificationService;
    private readonly IStateService _stateService;
    private readonly AlfredOptions _options;
    private readonly ILogger<AiNewsWeeklyFunction> _logger;

    public AiNewsWeeklyFunction(
        INewsResearchService newsResearch,
        INotificationService notificationService,
        IStateService stateService,
        IOptions<AlfredOptions> options,
        ILogger<AiNewsWeeklyFunction> logger)
    {
        _newsResearch = newsResearch;
        _notificationService = notificationService;
        _stateService = stateService;
        _options = options.Value;
        _logger = logger;
    }

    [Function("AiNewsWeekly")]
    public async Task Run([TimerTrigger("0 0 16 * * 5")] TimerInfo timerInfo) // Friday 4 PM UTC = 6 PM CEST
    {
        _logger.LogInformation("AiNewsWeekly triggered at {Time}", DateTime.UtcNow);

        if (!_options.AiNewsEnabled || !_options.AiNewsWeeklyEnabled
            || string.IsNullOrWhiteSpace(_options.PersonalTelegramChatId))
        {
            _logger.LogInformation("AI news weekly synthesis disabled, skipping");
            return;
        }

        try
        {
            var weekItems = await _stateService.GetReportedNewsSinceAsync(
                DateTimeOffset.UtcNow.AddDays(-WeekLookbackDays));

            if (weekItems.Count == 0)
            {
                _logger.LogInformation("Nothing was reported this week — skipping the weekly synthesis");
                return;
            }

            var rules = await _stateService.GetNewsRulesAsync();
            var synthesis = await _newsResearch.BuildWeeklySynthesisAsync(weekItems, rules);

            if (string.IsNullOrWhiteSpace(synthesis))
            {
                _logger.LogWarning("Weekly synthesis came back empty, skipping");
                return;
            }

            await _notificationService.SendPersonalAlertAsync(synthesis);

            _logger.LogInformation("Weekly AI-news synthesis sent ({ItemCount} stories covered)", weekItems.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Weekly AI-news synthesis failed");
            await _notificationService.SendPersonalErrorAsync($"Weekly AI-news synthesis failed: {ex.Message}");
        }
    }
}
