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

            // Inline-button presses arrive as callback_query updates, not messages
            if (root.TryGetProperty("callback_query", out var callbackQuery))
            {
                await HandleCallbackQueryAsync(callbackQuery);
                return req.CreateResponse(System.Net.HttpStatusCode.OK);
            }

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

            // The personal DM gets personal context and email tools on top of the school context;
            // the shared school chat stays school-only
            var isPersonalChat = chatId.ToString() == _options.PersonalTelegramChatId.Trim();

            if (isPersonalChat && question.TrimStart().StartsWith("/evolve", StringComparison.OrdinalIgnoreCase))
            {
                await HandleEvolveCommandAsync(chatId, question.TrimStart()["/evolve".Length..].Trim());
                return req.CreateResponse(System.Net.HttpStatusCode.OK);
            }

            var command = question.Trim();
            if (command.Equals("/new", StringComparison.OrdinalIgnoreCase) ||
                command.Equals("/reset", StringComparison.OrdinalIgnoreCase))
            {
                await _stateService.ClearChatTurnsAsync(chatId);
                await _notificationService.SendMessageAsync(chatId, "Fresh start — I've cleared our recent conversation.");
                return req.CreateResponse(System.Net.HttpStatusCode.OK);
            }

            // Gather context in parallel
            var lookbackSince = DateTimeOffset.UtcNow.AddDays(-_options.ChatLookbackDays);
            var emailsTask = _stateService.GetEmailsSinceAsync(lookbackSince);
            var eventsTask = _calendarService.GetUpcomingEventsAsync(_options.ChatLookbackDays);
            var historySince = DateTimeOffset.UtcNow.AddMinutes(-_options.ChatHistoryMaxAgeMinutes);
            var historyTask = _stateService.GetRecentChatTurnsAsync(chatId, historySince, _options.ChatHistoryMaxTurns);

            string answer;
            if (isPersonalChat)
            {
                var personalEmailsTask = _stateService.GetPersonalEmailsSinceAsync(lookbackSince);
                var personalActionsTask = _calendarService.GetUpcomingPersonalEventsAsync(_options.ChatLookbackDays);

                await Task.WhenAll(emailsTask, eventsTask, historyTask, personalEmailsTask, personalActionsTask);

                _logger.LogInformation("Personal context loaded: {School} school + {Personal} personal emails, {Actions} actions, {Turns} chat turns",
                    emailsTask.Result.Count, personalEmailsTask.Result.Count, personalActionsTask.Result.Count, historyTask.Result.Count);

                answer = await _summarizerService.AnswerPersonalQuestionAsync(
                    question,
                    emailsTask.Result,
                    eventsTask.Result,
                    personalEmailsTask.Result,
                    personalActionsTask.Result,
                    historyTask.Result,
                    ExecuteEmailToolAsync);
            }
            else
            {
                await Task.WhenAll(emailsTask, eventsTask, historyTask);

                _logger.LogInformation("Context loaded: {EmailCount} emails, {EventCount} events, {Turns} chat turns",
                    emailsTask.Result.Count, eventsTask.Result.Count, historyTask.Result.Count);

                answer = await _summarizerService.AnswerQuestionAsync(question, emailsTask.Result, eventsTask.Result, historyTask.Result);
            }

            await _notificationService.SendMessageAsync(chatId, answer);

            try
            {
                await _stateService.SaveChatTurnAsync(chatId, question, TrimAnswerForHistory(answer));
            }
            catch (Exception ex)
            {
                // The answer already went out — a history write failure shouldn't surface as a webhook error
                _logger.LogWarning(ex, "Failed to save chat turn for chat {ChatId}", chatId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing Telegram webhook");
        }

        // Always return 200 to Telegram to prevent retries
        return req.CreateResponse(System.Net.HttpStatusCode.OK);
    }

    // One-tap actions from the inline buttons under personal alerts
    private async Task HandleCallbackQueryAsync(JsonElement callbackQuery)
    {
        var callbackId = callbackQuery.GetProperty("id").GetString()!;
        var userId = callbackQuery.TryGetProperty("from", out var from)
            ? from.GetProperty("id").GetInt64()
            : 0;
        var data = callbackQuery.TryGetProperty("data", out var dataElement)
            ? dataElement.GetString()
            : null;

        if (!IsUserAllowed(userId))
        {
            _logger.LogWarning("Unauthorized callback from user {UserId}", userId);
            await _notificationService.AnswerCallbackAsync(callbackId, "Not authorized.");
            return;
        }

        if (string.IsNullOrWhiteSpace(data))
        {
            await _notificationService.AnswerCallbackAsync(callbackId);
            return;
        }

        _logger.LogInformation("Callback action from user {UserId}: {Data}", userId, data);

        try
        {
            var result = await ExecuteCallbackActionAsync(data);
            await _notificationService.AnswerCallbackAsync(callbackId, result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Callback action failed: {Data}", data);
            await _notificationService.AnswerCallbackAsync(callbackId, "Sorry, that didn't work — try asking me in chat.");
        }
    }

    private async Task<string> ExecuteCallbackActionAsync(string data)
    {
        var separator = data.IndexOf(':');
        var (action, arg) = separator > 0
            ? (data[..separator], data[(separator + 1)..])
            : (data, "");

        switch (action)
        {
            case "mu":
                await _gmailReader.MarkAsUnreadAsync(arg);
                return "Marked unread — it's back in your inbox.";

            case "sup":
            {
                var email = await _stateService.GetPersonalEmailAsync(arg);
                if (email is null)
                    return "I can't find that email in my records anymore.";

                var sender = !string.IsNullOrWhiteSpace(email.SenderEmail) ? email.SenderEmail : email.SenderName;
                var ruleId = Guid.NewGuid().ToString("N")[..8];
                await _stateService.SaveSuppressionRuleAsync(
                    ruleId,
                    $"All emails from {sender}",
                    email.SenderEmail ?? email.SenderName,
                    email.Subject);
                return $"Muted — no more alerts about emails from {email.SenderName}.";
            }

            case "sn1":
            {
                var email = await _stateService.GetPersonalEmailAsync(arg);
                string subject, senderName, summary;
                string? threadId;
                if (email is not null)
                {
                    (subject, senderName, summary, threadId) = (email.Subject, email.SenderName, email.Summary, email.GmailThreadId);
                }
                else
                {
                    // Snoozes can be re-armed on emails Alfred never triaged (found via search)
                    var raw = await _gmailReader.GetEmailAsync(arg);
                    if (raw is null)
                        return "I can't find that email anymore.";
                    (subject, senderName, summary, threadId) = (raw.Subject, raw.SenderName, "", raw.ThreadId);
                }

                await _stateService.SaveSnoozeAsync(arg, subject, senderName, summary, threadId, NextMorningMalta());
                return "Snoozed — I'll bring it back tomorrow morning.";
            }

            case "unsub":
            {
                var stats = await _stateService.GetSenderStatAsync(arg);
                if (stats is null)
                    return "I can't find that sender anymore.";
                if (stats.Unsubscribed)
                    return $"Already unsubscribed from {stats.SenderName}.";

                var (mailto, mailtoSubject, httpUrl) = ParseListUnsubscribe(stats.ListUnsubscribe);

                // RFC 8058 one-click: a single POST, no interaction needed
                if (httpUrl is not null && stats.ListUnsubscribeOneClick)
                {
                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                    var response = await http.PostAsync(httpUrl, new StringContent(
                        "List-Unsubscribe=One-Click", System.Text.Encoding.UTF8, "application/x-www-form-urlencoded"));
                    if (response.IsSuccessStatusCode)
                    {
                        stats.Unsubscribed = true;
                        await _stateService.UpsertSenderStatAsync(stats);
                        return $"Done — unsubscribed from {stats.SenderName}.";
                    }
                    _logger.LogWarning("One-click unsubscribe returned {Status} for {Sender}",
                        response.StatusCode, stats.SenderEmail);
                }

                if (mailto is not null)
                {
                    await _gmailReader.SendUnsubscribeEmailAsync(mailto, mailtoSubject);
                    stats.Unsubscribed = true;
                    await _stateService.UpsertSenderStatAsync(stats);
                    return $"Done — sent the unsubscribe email for {stats.SenderName}.";
                }

                if (httpUrl is not null)
                {
                    // Plain link — needs a human tap; hand it over instead of guessing
                    await _notificationService.SendPersonalAlertAsync(
                        $"This one needs a tap from you: <a href=\"{httpUrl}\">unsubscribe from {stats.SenderName}</a>");
                    stats.Unsubscribed = true;
                    await _stateService.UpsertSenderStatAsync(stats);
                    return "They want you to confirm it yourself — I've sent you the link.";
                }

                return "That sender doesn't offer a usable unsubscribe mechanism.";
            }

            case "keep":
            {
                var stats = await _stateService.GetSenderStatAsync(arg);
                return stats is null
                    ? "Noted."
                    : $"Noted — I'll keep {stats.SenderName} coming and won't suggest this again.";
            }

            default:
                return "I don't recognize that button anymore.";
        }
    }

    // A List-Unsubscribe header holds one or two <targets>: mailto: and/or https:
    private static (string? Mailto, string? MailtoSubject, string? HttpUrl) ParseListUnsubscribe(string? header)
    {
        if (string.IsNullOrWhiteSpace(header))
            return (null, null, null);

        string? mailto = null, mailtoSubject = null, httpUrl = null;
        foreach (System.Text.RegularExpressions.Match match in
                 System.Text.RegularExpressions.Regex.Matches(header, "<([^>]+)>"))
        {
            var target = match.Groups[1].Value.Trim();
            if (target.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) && mailto is null)
            {
                var rest = target["mailto:".Length..];
                var queryIndex = rest.IndexOf('?');
                mailto = queryIndex >= 0 ? rest[..queryIndex] : rest;
                if (queryIndex >= 0)
                {
                    var query = System.Web.HttpUtility.ParseQueryString(rest[(queryIndex + 1)..]);
                    mailtoSubject = query["subject"];
                }
            }
            else if (target.StartsWith("http", StringComparison.OrdinalIgnoreCase) && httpUrl is null)
            {
                httpUrl = target;
            }
        }

        return (mailto, mailtoSubject, httpUrl);
    }

    // Tomorrow at 08:00 Malta time, as a UTC instant
    private static DateTimeOffset NextMorningMalta()
    {
        var maltaTz = TimeZoneInfo.FindSystemTimeZoneById("Europe/Malta");
        var nowMalta = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, maltaTz);
        var tomorrowMorning = nowMalta.Date.AddDays(1).AddHours(8);
        return new DateTimeOffset(tomorrowMorning, maltaTz.GetUtcOffset(tomorrowMorning));
    }

    // "/evolve <instruction>" hands the instruction to a GitHub Actions workflow that runs a
    // headless Claude Code session against this repo, builds, commits, and redeploys
    private async Task HandleEvolveCommandAsync(long chatId, string instruction)
    {
        if (string.IsNullOrWhiteSpace(instruction))
        {
            await _notificationService.SendMessageAsync(chatId,
                "Tell me what to change, e.g. /evolve make the personal digest shorter");
            return;
        }

        var token = Environment.GetEnvironmentVariable("GitHub__Token");
        var repo = Environment.GetEnvironmentVariable("GitHub__Repo") ?? "scerrimatthew/alfred-pa";
        if (string.IsNullOrWhiteSpace(token))
        {
            await _notificationService.SendMessageAsync(chatId,
                "The GitHub token (GitHub__Token) isn't configured, so I can't start a coding session.");
            return;
        }

        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Alfred");
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

        var payload = JsonSerializer.Serialize(new { @ref = "main", inputs = new { instruction } });
        var response = await http.PostAsync(
            $"https://api.github.com/repos/{repo}/actions/workflows/evolve.yml/dispatches",
            new StringContent(payload, System.Text.Encoding.UTF8, "application/json"));

        if (response.IsSuccessStatusCode)
        {
            _logger.LogInformation("Evolve dispatched: {Instruction}", instruction);
            await _notificationService.SendMessageAsync(chatId,
                "On it — I've started a coding session for that change. I'll message you once it's built and deployed (usually 5-10 minutes).");
        }
        else
        {
            var body = await response.Content.ReadAsStringAsync();
            _logger.LogError("Evolve dispatch failed: {Status} {Body}", response.StatusCode, body);
            await _notificationService.SendMessageAsync(chatId,
                $"Couldn't start the coding session — GitHub returned {(int)response.StatusCode}.");
        }
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

            case "search_inbox":
            {
                var query = input?["query"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(query))
                    return "Error: no query provided.";

                var maxResults = input?["max_results"]?.GetValue<int>() ?? 10;
                var results = await _gmailReader.SearchInboxAsync(query, maxResults);
                if (results.Count == 0)
                    return "No emails matched that query.";

                return string.Join("\n", results.Select(r =>
                    $"- id={r.MessageId} [{r.ReceivedDate:ddd d MMM yyyy}] {r.SenderName} — {r.Subject}: {r.Snippet} link={GmailLinks.ForThread(r.ThreadId)}"));
            }

            case "read_email":
            {
                var messageId = input?["message_id"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(messageId))
                    return "Error: no message_id provided.";

                var email = await _gmailReader.GetEmailAsync(messageId);
                if (email is null)
                    return "Error: email not found.";

                var body = email.Body.Length > 4000 ? email.Body[..4000] + "…" : email.Body;
                var attachments = string.Concat(email.Documents
                    .Where(d => !string.IsNullOrWhiteSpace(d.ExtractedText))
                    .Select(d =>
                    {
                        var text = d.ExtractedText!.Length > 3000 ? d.ExtractedText[..3000] + "…" : d.ExtractedText;
                        return $"\n\n--- ATTACHMENT: {d.Title} ---\n{text}";
                    }));

                return $"""
                    From: {email.SenderName} <{email.SenderEmail}>
                    Date: {email.ReceivedDate:ddd d MMM yyyy HH:mm}
                    Subject: {email.Subject}
                    Link: {GmailLinks.ForThread(email.ThreadId)}

                    {body}{attachments}
                    """;
            }

            case "add_attention_rule":
            {
                var pattern = input?["pattern"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(pattern))
                    return "Error: no pattern provided.";

                var ruleId = Guid.NewGuid().ToString("N")[..8];
                await _stateService.SaveAttentionRuleAsync(
                    ruleId,
                    pattern,
                    input?["example_sender"]?.GetValue<string>(),
                    input?["example_subject"]?.GetValue<string>());
                return $"Attention rule {ruleId} saved: {pattern}";
            }

            case "list_attention_rules":
            {
                var rules = await _stateService.GetAttentionRulesAsync();
                if (rules.Count == 0)
                    return "No attention rules are active.";

                return string.Join("\n", rules.OrderBy(r => r.CreatedAt).Select(r =>
                    $"[{r.RowKey}] {r.Pattern} (added {r.CreatedAt:d MMM yyyy})"));
            }

            case "remove_attention_rule":
            {
                var ruleId = input?["rule_id"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(ruleId))
                    return "Error: no rule_id provided.";

                await _stateService.DeleteAttentionRuleAsync(ruleId);
                return $"Attention rule {ruleId} removed.";
            }

            case "snooze_email":
            {
                var messageId = input?["message_id"]?.GetValue<string>();
                var remindAt = input?["remind_at"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(messageId) || string.IsNullOrWhiteSpace(remindAt))
                    return "Error: message_id and remind_at are required.";

                if (!DateTime.TryParse(remindAt, out var localTime))
                    return "Error: remind_at must be \"yyyy-MM-dd HH:mm\" (Malta time).";
                if (localTime.TimeOfDay == TimeSpan.Zero)
                    localTime = localTime.AddHours(8); // bare date -> that morning

                var maltaTz = TimeZoneInfo.FindSystemTimeZoneById("Europe/Malta");
                var dueAt = new DateTimeOffset(localTime, maltaTz.GetUtcOffset(localTime));
                if (dueAt <= DateTimeOffset.UtcNow)
                    return "Error: that reminder time is already in the past.";

                var email = await _stateService.GetPersonalEmailAsync(messageId);
                string subject, senderName, summary;
                string? threadId;
                if (email is not null)
                {
                    (subject, senderName, summary, threadId) = (email.Subject, email.SenderName, email.Summary, email.GmailThreadId);
                }
                else
                {
                    // Not in Alfred's records (older email found via search) — read it from Gmail
                    var raw = await _gmailReader.GetEmailAsync(messageId);
                    if (raw is null)
                        return "Error: email not found.";
                    (subject, senderName, summary, threadId) = (raw.Subject, raw.SenderName, "", raw.ThreadId);
                }

                await _stateService.SaveSnoozeAsync(messageId, subject, senderName, summary, threadId, dueAt);
                return $"Snoozed \"{subject}\" — reminder set for {localTime:ddd d MMM HH:mm} (Malta time).";
            }

            case "list_snoozes":
            {
                var snoozes = await _stateService.GetSnoozesAsync();
                if (snoozes.Count == 0)
                    return "No reminders are pending.";

                var maltaTz = TimeZoneInfo.FindSystemTimeZoneById("Europe/Malta");
                return string.Join("\n", snoozes.Select(s =>
                    $"- id={s.RowKey} due {TimeZoneInfo.ConvertTime(s.DueAt, maltaTz):ddd d MMM HH:mm}: {s.SenderName} — {s.Subject}"));
            }

            case "cancel_snooze":
            {
                var messageId = input?["message_id"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(messageId))
                    return "Error: no message_id provided.";

                await _stateService.DeleteSnoozeAsync(messageId);
                return "Reminder cancelled.";
            }

            case "draft_reply":
            {
                var messageId = input?["message_id"]?.GetValue<string>();
                var draftBody = input?["body"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(messageId) || string.IsNullOrWhiteSpace(draftBody))
                    return "Error: message_id and body are required.";

                var replyAll = input?["reply_all"]?.GetValue<bool>() ?? false;
                return await _gmailReader.CreateReplyDraftAsync(messageId, draftBody, replyAll);
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

    // History keeps a de-formatted, capped copy of each answer: enough for follow-ups,
    // small enough that stale detail can't crowd out the live email/calendar context
    private static string TrimAnswerForHistory(string answer)
    {
        var plain = System.Text.RegularExpressions.Regex.Replace(answer, "<[^>]+>", "").Trim();
        return plain.Length <= 700 ? plain : plain[..700] + "…";
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
