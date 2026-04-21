using System.Text.Json;
using Alfred.Functions.Configuration;
using Alfred.Functions.Services.AI;
using Alfred.Functions.Services.Calendar;
using Alfred.Functions.Services.Notifications;
using Alfred.Functions.Services.State;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Alfred.Functions.Functions;

public class TelegramWebhookFunction
{
    private readonly IStateService _stateService;
    private readonly ICalendarService _calendarService;
    private readonly ISummarizerService _summarizerService;
    private readonly INotificationService _notificationService;
    private readonly AlfredOptions _options;
    private readonly ILogger<TelegramWebhookFunction> _logger;

    public TelegramWebhookFunction(
        IStateService stateService,
        ICalendarService calendarService,
        ISummarizerService summarizerService,
        INotificationService notificationService,
        IOptions<AlfredOptions> options,
        ILogger<TelegramWebhookFunction> logger)
    {
        _stateService = stateService;
        _calendarService = calendarService;
        _summarizerService = summarizerService;
        _notificationService = notificationService;
        _options = options.Value;
        _logger = logger;
    }

    [Function("TelegramWebhook")]
    public async Task<HttpResponseData> RunAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "telegram/{secret}")] HttpRequestData req,
        string secret)
    {
        if (secret != _options.TelegramWebhookSecret)
        {
            _logger.LogWarning("Telegram webhook called with invalid secret");
            return req.CreateResponse(System.Net.HttpStatusCode.Unauthorized);
        }

        var body = await req.ReadAsStringAsync();
        if (string.IsNullOrEmpty(body))
            return req.CreateResponse(System.Net.HttpStatusCode.OK);

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (!root.TryGetProperty("message", out var message))
                return req.CreateResponse(System.Net.HttpStatusCode.OK);

            if (!message.TryGetProperty("text", out var textElement))
                return req.CreateResponse(System.Net.HttpStatusCode.OK);

            var chatId = message.GetProperty("chat").GetProperty("id").GetInt64();
            var userId = message.TryGetProperty("from", out var from)
                ? from.GetProperty("id").GetInt64()
                : 0;
            var question = textElement.GetString();

            if (string.IsNullOrWhiteSpace(question))
                return req.CreateResponse(System.Net.HttpStatusCode.OK);

            if (!IsUserAllowed(userId))
            {
                _logger.LogWarning("Unauthorized user {UserId} in chat {ChatId}", userId, chatId);
                await _notificationService.SendMessageAsync(chatId, "Sorry, you're not authorized to use this bot.");
                return req.CreateResponse(System.Net.HttpStatusCode.OK);
            }

            _logger.LogInformation("Received question from user {UserId} in chat {ChatId}: {Question}", userId, chatId, question);

            // Gather context in parallel
            var lookbackSince = DateTimeOffset.UtcNow.AddDays(-_options.ChatLookbackDays);
            var emailsTask = _stateService.GetEmailsSinceAsync(lookbackSince);
            var eventsTask = _calendarService.GetUpcomingEventsAsync(_options.ChatLookbackDays);

            await Task.WhenAll(emailsTask, eventsTask);

            var recentEmails = emailsTask.Result;
            var upcomingEvents = eventsTask.Result;

            _logger.LogInformation("Context loaded: {EmailCount} emails, {EventCount} events",
                recentEmails.Count, upcomingEvents.Count);

            var answer = await _summarizerService.AnswerQuestionAsync(question, recentEmails, upcomingEvents);

            await _notificationService.SendMessageAsync(chatId, answer);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing Telegram webhook");
        }

        // Always return 200 to Telegram to prevent retries
        return req.CreateResponse(System.Net.HttpStatusCode.OK);
    }

    private bool IsUserAllowed(long userId)
    {
        if (string.IsNullOrWhiteSpace(_options.AllowedTelegramUserIds))
            return true;

        return _options.AllowedTelegramUserIds
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(id => long.TryParse(id, out var allowed) && allowed == userId);
    }
}
