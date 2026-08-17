using System.Text.Json;
using System.Text.Json.Nodes;
using Alfred.Functions.Configuration;
using Alfred.Functions.Services.AI;
using Alfred.Functions.Services.Calendar;
using Alfred.Functions.Services.Gmail;
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
    private readonly IGmailReaderService _gmailReader;
    private readonly AlfredOptions _options;
    private readonly ILogger<TelegramWebhookFunction> _logger;

    public TelegramWebhookFunction(
        IStateService stateService,
        ICalendarService calendarService,
        ISummarizerService summarizerService,
        INotificationService notificationService,
        IGmailReaderService gmailReader,
        IOptions<AlfredOptions> options,
        ILogger<TelegramWebhookFunction> logger)
    {
        _stateService = stateService;
        _calendarService = calendarService;
        _summarizerService = summarizerService;
        _notificationService = notificationService;
        _gmailReader = gmailReader;
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

            // The personal DM gets personal context and email tools on top of the school context;
            // the shared school chat stays school-only
            var isPersonalChat = chatId.ToString() == _options.PersonalTelegramChatId.Trim();

            string answer;
            if (isPersonalChat)
            {
                var personalEmailsTask = _stateService.GetPersonalEmailsSinceAsync(lookbackSince);
                var personalActionsTask = _calendarService.GetUpcomingPersonalEventsAsync(_options.ChatLookbackDays);

                await Task.WhenAll(emailsTask, eventsTask, personalEmailsTask, personalActionsTask);

                _logger.LogInformation("Personal context loaded: {School} school + {Personal} personal emails, {Actions} actions",
                    emailsTask.Result.Count, personalEmailsTask.Result.Count, personalActionsTask.Result.Count);

                answer = await _summarizerService.AnswerPersonalQuestionAsync(
                    question,
                    emailsTask.Result,
                    eventsTask.Result,
                    personalEmailsTask.Result,
                    personalActionsTask.Result,
                    ExecuteEmailToolAsync);
            }
            else
            {
                await Task.WhenAll(emailsTask, eventsTask);

                _logger.LogInformation("Context loaded: {EmailCount} emails, {EventCount} events",
                    emailsTask.Result.Count, eventsTask.Result.Count);

                answer = await _summarizerService.AnswerQuestionAsync(question, emailsTask.Result, eventsTask.Result);
            }

            await _notificationService.SendMessageAsync(chatId, answer);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing Telegram webhook");
        }

        // Always return 200 to Telegram to prevent retries
        return req.CreateResponse(System.Net.HttpStatusCode.OK);
    }

    private async Task<string> ExecuteEmailToolAsync(string toolName, JsonNode? input)
    {
        switch (toolName)
        {
            case "mark_unread":
            {
                var messageId = input?["message_id"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(messageId))
                    return "Error: no message_id provided.";

                await _gmailReader.MarkAsUnreadAsync(messageId);
                return "Email marked as unread.";
            }

            case "recategorize_email":
            {
                var messageId = input?["message_id"]?.GetValue<string>();
                var category = input?["category"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(messageId) || string.IsNullOrWhiteSpace(category))
                    return "Error: message_id and category are required.";

                await _gmailReader.RecategorizeAsync(messageId, LabelNames.ForPersonal(category));
                await _stateService.UpdatePersonalEmailCategoryAsync(messageId, category);
                return $"Email recategorized to {category}.";
            }

            case "add_suppression_rule":
            {
                var pattern = input?["pattern"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(pattern))
                    return "Error: no pattern provided.";

                var ruleId = Guid.NewGuid().ToString("N")[..8];
                await _stateService.SaveSuppressionRuleAsync(
                    ruleId,
                    pattern,
                    input?["example_sender"]?.GetValue<string>(),
                    input?["example_subject"]?.GetValue<string>());
                return $"Suppression rule {ruleId} saved: {pattern}";
            }

            case "list_suppression_rules":
            {
                var rules = await _stateService.GetSuppressionRulesAsync();
                if (rules.Count == 0)
                    return "No suppression rules are active.";

                return string.Join("\n", rules.OrderBy(r => r.CreatedAt).Select(r =>
                    $"[{r.RowKey}] {r.Pattern} (added {r.CreatedAt:d MMM yyyy})"));
            }

            case "remove_suppression_rule":
            {
                var ruleId = input?["rule_id"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(ruleId))
                    return "Error: no rule_id provided.";

                await _stateService.DeleteSuppressionRuleAsync(ruleId);
                return $"Suppression rule {ruleId} removed.";
            }

            case "update_calendar_event":
            {
                var eventId = input?["event_id"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(eventId))
                    return "Error: no event_id provided.";

                DateTime? date = DateTime.TryParse(input?["date"]?.GetValue<string>(), out var d) ? d : null;
                TimeSpan? startTime = TimeSpan.TryParse(input?["start_time"]?.GetValue<string>(), out var st) ? st : null;
                TimeSpan? endTime = TimeSpan.TryParse(input?["end_time"]?.GetValue<string>(), out var et) ? et : null;

                var title = await _calendarService.UpdatePersonalEventAsync(
                    eventId,
                    input?["title"]?.GetValue<string>(),
                    date, startTime, endTime,
                    input?["description"]?.GetValue<string>());
                return $"Updated calendar event: {title}.";
            }

            case "delete_calendar_event":
            {
                var eventId = input?["event_id"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(eventId))
                    return "Error: no event_id provided.";

                var title = await _calendarService.DeletePersonalEventAsync(eventId);
                return $"Deleted calendar event: {title}.";
            }

            default:
                return $"Error: unknown tool {toolName}.";
        }
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
