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
    // How far back reported AI-news stories are loaded into the personal chat context
    // so "tell me more about that story" follow-ups can find them
    private const int NewsChatLookbackDays = 7;

    // An in-flight /news marker older than this is treated as crashed and ignored
    private static readonly TimeSpan NewsRequestTimeout = TimeSpan.FromMinutes(10);

    private readonly IStateService _stateService;
    private readonly ICalendarService _calendarService;
    private readonly ISummarizerService _summarizerService;
    private readonly INotificationService _notificationService;
    private readonly IGmailReaderService _gmailReader;
    private readonly INewsResearchService _newsResearch;
    private readonly IAnthropicCostService _costService;
    private readonly AlfredOptions _options;
    private readonly ILogger<TelegramWebhookFunction> _logger;

    public TelegramWebhookFunction(
        IStateService stateService,
        ICalendarService calendarService,
        ISummarizerService summarizerService,
        INotificationService notificationService,
        IGmailReaderService gmailReader,
        INewsResearchService newsResearch,
        IAnthropicCostService costService,
        IOptions<AlfredOptions> options,
        ILogger<TelegramWebhookFunction> logger)
    {
        _stateService = stateService;
        _calendarService = calendarService;
        _summarizerService = summarizerService;
        _notificationService = notificationService;
        _gmailReader = gmailReader;
        _newsResearch = newsResearch;
        _costService = costService;
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

        // Set once an authorized message sender is known, so the outer catch can apologize
        long replyChatId = 0;

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            // Telegram re-delivers updates it thinks failed (slow web-search answers, long
            // /news runs) — claim each update_id once and drop duplicates. Fail open on a
            // storage hiccup: a rare double answer beats a dropped message.
            var updateId = root.TryGetProperty("update_id", out var updProp) ? updProp.GetInt64() : 0;
            if (updateId != 0)
            {
                bool claimed;
                try
                {
                    claimed = await _stateService.TryClaimUpdateAsync(updateId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Update-dedup claim failed for {UpdateId} — processing anyway", updateId);
                    claimed = true;
                }

                if (!claimed)
                {
                    _logger.LogInformation("Dropped duplicate delivery of update {UpdateId}", updateId);
                    return req.CreateResponse(System.Net.HttpStatusCode.OK);
                }
            }

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

            replyChatId = chatId;
            _logger.LogInformation("Received question from user {UserId} in chat {ChatId}: {Question}", userId, chatId, question);

            // The personal DM gets personal context and email tools on top of the school context;
            // the shared school chat stays school-only
            var isPersonalChat = chatId.ToString() == _options.PersonalTelegramChatId.Trim();

            if (isPersonalChat && question.TrimStart().StartsWith("/evolve", StringComparison.OrdinalIgnoreCase))
            {
                await HandleEvolveCommandAsync(chatId, question.TrimStart()["/evolve".Length..].Trim());
                return req.CreateResponse(System.Net.HttpStatusCode.OK);
            }

            if (isPersonalChat && question.TrimStart().StartsWith("/backfill", StringComparison.OrdinalIgnoreCase))
            {
                await HandleBackfillCommandAsync(chatId, question.TrimStart()["/backfill".Length..].Trim());
                return req.CreateResponse(System.Net.HttpStatusCode.OK);
            }

            // "/ai-news", "/ai-news <topic>", the underscore form "/ai_news" (the only
            // shape Telegram's command menu can register — hyphens aren't allowed there),
            // and "@BotName" mentions — but not prefixed words like "/ai-newsy"
            var newsMatch = System.Text.RegularExpressions.Regex.Match(
                question.TrimStart(),
                @"^/ai[-_]news(?:@\S+)?(?:\s+(?<topic>.*))?$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase
                    | System.Text.RegularExpressions.RegexOptions.Singleline);
            if (isPersonalChat && newsMatch.Success)
            {
                await HandleNewsCommandAsync(chatId, newsMatch.Groups["topic"].Value.Trim());
                return req.CreateResponse(System.Net.HttpStatusCode.OK);
            }

            // The command's old name — redirect instead of dropping into Q&A
            if (isPersonalChat && System.Text.RegularExpressions.Regex.IsMatch(
                    question.TrimStart(), @"^/news(?:@\S+)?(?:\s|$)",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase
                        | System.Text.RegularExpressions.RegexOptions.Singleline))
            {
                await _notificationService.SendMessageAsync(chatId, "That command moved — it's /ai-news now.");
                return req.CreateResponse(System.Net.HttpStatusCode.OK);
            }

            if (isPersonalChat && System.Text.RegularExpressions.Regex.IsMatch(
                    question.TrimStart(), @"^/deploy(?:@\S+)?(?:\s|$)",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase
                        | System.Text.RegularExpressions.RegexOptions.Singleline))
            {
                await HandleDeployCommandAsync(chatId);
                return req.CreateResponse(System.Net.HttpStatusCode.OK);
            }

            if (isPersonalChat && System.Text.RegularExpressions.Regex.IsMatch(
                    question.TrimStart(), @"^/cost(?:@\S+)?(?:\s|$)",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase
                        | System.Text.RegularExpressions.RegexOptions.Singleline))
            {
                await HandleCostCommandAsync(chatId);
                return req.CreateResponse(System.Net.HttpStatusCode.OK);
            }

            var command = question.Trim();
            if (TryParseJokeCommand(command, out var jokeTopic))
            {
                await HandleJokeCommandAsync(chatId, jokeTopic);
                return req.CreateResponse(System.Net.HttpStatusCode.OK);
            }

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
                var recentNewsTask = _stateService.GetReportedNewsSinceAsync(
                    DateTimeOffset.UtcNow.AddDays(-NewsChatLookbackDays));

                await Task.WhenAll(emailsTask, eventsTask, historyTask, personalEmailsTask, personalActionsTask, recentNewsTask);

                _logger.LogInformation("Personal context loaded: {School} school + {Personal} personal emails, {Actions} actions, {News} news items, {Turns} chat turns",
                    emailsTask.Result.Count, personalEmailsTask.Result.Count, personalActionsTask.Result.Count, recentNewsTask.Result.Count, historyTask.Result.Count);

                answer = await _summarizerService.AnswerPersonalQuestionAsync(
                    question,
                    emailsTask.Result,
                    eventsTask.Result,
                    personalEmailsTask.Result,
                    personalActionsTask.Result,
                    recentNewsTask.Result,
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

            // Silence is the worst failure mode — best-effort apology when the chat is
            // known, carrying a short error signature so failures diagnose themselves in
            // chat (Application Insights isn't reachable from everywhere Matthew debugs)
            if (replyChatId != 0)
            {
                try
                {
                    await _notificationService.SendMessageAsync(replyChatId,
                        $"Something went wrong on my end while handling that — try again in a moment. ({ErrorSignature(ex)})");
                }
                catch (Exception sendEx)
                {
                    _logger.LogWarning(sendEx, "Failed to send error apology to chat {ChatId}", replyChatId);
                }
            }
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

            // 👍/👎 under a news digest item — arg is "+:<urlhash>" or "-:<urlhash>"
            case "nf":
            {
                var thumbsUp = arg.StartsWith('+');
                var story = await _stateService.GetReportedNewsAsync(arg.Length > 2 ? arg[2..] : "");
                if (story is null)
                    return "That story has aged out of my records — tell me in chat instead.";

                var topic = !string.IsNullOrWhiteSpace(story.Category) ? $" (topic: {story.Category})" : "";
                var instruction = thumbsUp
                    ? $"More stories like \"{story.Headline}\"{topic} — Matthew flagged this one as exactly what he wants."
                    : $"Fewer stories like \"{story.Headline}\"{topic} — Matthew flagged this one as not worth his time; drop this topic unless something major changes.";

                // Keyed by the story hash so a second press (or a change of heart from 👍
                // to 👎) replaces the rule instead of stacking contradictory ones
                await _stateService.SaveNewsRuleAsync($"fb-{story.RowKey}", instruction);
                return thumbsUp
                    ? "Noted — more like that one."
                    : "Noted — I'll steer away from that topic.";
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

    // "/backfill <days>" starts a quiet historical sweep of the personal inbox; each
    // PersonalEmailMonitor run then works through a batch until the window is covered.
    // Intersections with earlier backfills or normal runs are deduped by ProcessedEmails.
    private async Task HandleBackfillCommandAsync(long chatId, string argument)
    {
        if (argument.Equals("cancel", StringComparison.OrdinalIgnoreCase)
            || argument.Equals("stop", StringComparison.OrdinalIgnoreCase))
        {
            await _stateService.ClearBackfillStateAsync();
            await _notificationService.SendMessageAsync(chatId, "Backfill cancelled — anything already filed stays filed.");
            return;
        }

        if (argument.Equals("status", StringComparison.OrdinalIgnoreCase))
        {
            var current = await _stateService.GetBackfillStateAsync();
            await _notificationService.SendMessageAsync(chatId, current is null
                ? "No backfill is running."
                : $"Backfill running — {current.ProcessedCount} emails filed so far, window back to {current.OldestDate:d MMM yyyy}.");
            return;
        }

        var days = 60;
        if (argument.Length > 0 && (!int.TryParse(argument, out days) || days < 1 || days > 365))
        {
            await _notificationService.SendMessageAsync(chatId,
                "Usage: /backfill [days 1-365], /backfill status, or /backfill cancel. Default is 60 days.");
            return;
        }

        var existing = await _stateService.GetBackfillStateAsync();
        var state = new Models.BackfillStateEntity
        {
            OldestDate = DateTimeOffset.UtcNow.AddDays(-days),
            RequestedAt = DateTimeOffset.UtcNow,
            // A wider re-request keeps credit for work already done; dedup prevents redoing it
            ProcessedCount = existing?.ProcessedCount ?? 0
        };
        await _stateService.SaveBackfillStateAsync(state);

        await _notificationService.SendMessageAsync(chatId,
            $"Backfill started — I'll quietly work through the last <b>{days} days</b> of inbox email in batches "
            + "(roughly every 15 minutes), categorizing, labeling, and picking up future deadlines. "
            + "No notifications along the way; I'll message you once when it's done. "
            + "Anything already processed is skipped automatically.");
    }

    // "/news" runs the AI-news research on demand; "/news <topic>" makes it a targeted
    // sweep. Research takes minutes, and Telegram re-delivers updates it thinks failed, so
    // a single-row marker drops duplicate triggers while a run is in flight.
    private async Task HandleNewsCommandAsync(long chatId, string topic)
    {
        var existing = await _stateService.GetNewsRequestAsync();
        if (existing is not null && existing.RequestedAt > DateTimeOffset.UtcNow - NewsRequestTimeout)
        {
            // Redeliveries of the same update were already dropped by the update-id claim,
            // so this is Matthew asking again mid-run — tell him, don't leave him hanging
            _logger.LogInformation("A /news research run started at {Time} is still in flight", existing.RequestedAt);
            var minutes = Math.Max(1, (int)(DateTimeOffset.UtcNow - existing.RequestedAt).TotalMinutes);
            await _notificationService.SendMessageAsync(chatId,
                $"Still working on the previous sweep (started about {minutes} min ago) — I'll send it as soon as it lands.");
            return;
        }

        var requestedAt = DateTimeOffset.UtcNow;
        await _stateService.SaveNewsRequestAsync(new Models.NewsRequestStateEntity
        {
            RequestedAt = requestedAt,
            Topic = string.IsNullOrWhiteSpace(topic) ? null : topic
        });

        // The topic is raw user text going into HTML-mode messages — escape it
        var safeTopic = System.Net.WebUtility.HtmlEncode(topic);

        try
        {
            await _notificationService.SendMessageAsync(chatId, string.IsNullOrWhiteSpace(topic)
                ? "🗞 On it — sweeping the AI news now. Give me a couple of minutes."
                : $"🗞 On it — running a targeted sweep on <b>{safeTopic}</b>. Give me a couple of minutes.");

            var rules = await _stateService.GetNewsRulesAsync();
            var recentlyReported = await _stateService.GetReportedNewsSinceAsync(
                DateTimeOffset.UtcNow.AddDays(-AiNewsDigestFunction.CoveredLookbackDays));
            var candidates = await _stateService.GetNewsCandidatesSinceAsync(
                DateTimeOffset.UtcNow.AddHours(-AiNewsDigestFunction.CandidateLookbackHours));

            var digest = await _newsResearch.ResearchDailyNewsAsync(
                rules, recentlyReported, candidates,
                string.IsNullOrWhiteSpace(topic) ? null : topic);

            if (digest.Incomplete)
            {
                await _notificationService.SendMessageAsync(chatId,
                    "The sweep ran long and I had to cut it off before it finished — try again in a few minutes.");
                return;
            }

            if (digest.Items.Count == 0 || string.IsNullOrWhiteSpace(digest.TelegramMessage))
            {
                // Unlike the evening timer, an on-demand run always answers
                await _notificationService.SendMessageAsync(chatId, string.IsNullOrWhiteSpace(topic)
                    ? "Nothing new worth your time since the last briefing — quiet out there."
                    : $"Nothing substantial on <b>{safeTopic}</b> right now — I'll keep it on the radar.");
                return;
            }

            await _notificationService.SendPersonalAlertAsync(
                digest.TelegramMessage, AiNewsDigestFunction.BuildFeedbackButtons(digest.Items));

            await _stateService.SaveReportedNewsAsync(digest.Items);

            _logger.LogInformation("On-demand news digest sent ({ItemCount} items, topic: {Topic})",
                digest.Items.Count, string.IsNullOrWhiteSpace(topic) ? "none" : topic);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "On-demand news research failed");
            await _notificationService.SendMessageAsync(chatId,
                "The news sweep hit a snag — try again in a few minutes.");
        }
        finally
        {
            // Only clear the marker this run wrote — a run that outlived the 10-minute
            // timeout must not delete the marker of a successor that took over. A failure
            // here must not mask the original exception; a leaked marker expires anyway.
            try
            {
                var current = await _stateService.GetNewsRequestAsync();
                if (current is not null && current.RequestedAt == requestedAt)
                    await _stateService.ClearNewsRequestAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to clear /news marker — it will expire on its own");
            }
        }
    }

    // "/joke [topic]" — available in both chats. In group chats Telegram appends the bot
    // name to commands ("/joke@alfred_bot"), so the mention is stripped before the topic.
    private static bool TryParseJokeCommand(string text, out string topic)
    {
        topic = "";
        if (!text.StartsWith("/joke", StringComparison.OrdinalIgnoreCase))
            return false;

        var rest = text["/joke".Length..];
        if (rest.StartsWith('@'))
        {
            var space = rest.IndexOf(' ');
            rest = space < 0 ? "" : rest[space..];
        }
        else if (rest.Length > 0 && !char.IsWhiteSpace(rest[0]))
        {
            // Something like "/jokes" — not this command
            return false;
        }

        topic = rest.Trim();
        return true;
    }

    private async Task HandleJokeCommandAsync(long chatId, string topic)
    {
        // The chat history (kept for a day) doubles as the "already told that one" list
        var recentTurns = await _stateService.GetRecentChatTurnsAsync(chatId, DateTimeOffset.UtcNow.AddDays(-1), 20);
        var recentJokes = recentTurns
            .Where(t => t.Question.StartsWith("/joke", StringComparison.OrdinalIgnoreCase))
            .Select(t => t.Answer)
            .ToList();

        var joke = await _summarizerService.TellJokeAsync(topic, recentJokes);
        await _notificationService.SendMessageAsync(chatId, joke);

        try
        {
            await _stateService.SaveChatTurnAsync(chatId, topic.Length > 0 ? $"/joke {topic}" : "/joke", TrimAnswerForHistory(joke));
        }
        catch (Exception ex)
        {
            // The joke already went out — a history write failure shouldn't surface as a webhook error
            _logger.LogWarning(ex, "Failed to save joke turn for chat {ChatId}", chatId);
        }
    }

    // Test seam: when set, supplies the HttpClient for GitHub workflow-dispatch calls
    // (tests back it with a fake handler). Never set in production.
    internal Func<HttpClient>? GitHubHttpFactory { get; set; }

    private HttpClient CreateGitHubHttpClient(string token)
    {
        var http = GitHubHttpFactory is not null ? GitHubHttpFactory() : new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Alfred");
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    // "/deploy" dispatches the plain deploy workflow — build, coverage gate, then a release
    // of current main, no coding session. The workflow reports back to Telegram itself.
    private async Task HandleDeployCommandAsync(long chatId)
    {
        var token = Environment.GetEnvironmentVariable("GitHub__Token");
        var repo = Environment.GetEnvironmentVariable("GitHub__Repo") ?? "scerrimatthew/alfred-pa";
        if (string.IsNullOrWhiteSpace(token))
        {
            await _notificationService.SendMessageAsync(chatId,
                "The GitHub token (GitHub__Token) isn't configured, so I can't start a deploy.");
            return;
        }

        using var http = CreateGitHubHttpClient(token);

        var payload = JsonSerializer.Serialize(new { @ref = "main" });
        var response = await http.PostAsync(
            $"https://api.github.com/repos/{repo}/actions/workflows/deploy.yml/dispatches",
            new StringContent(payload, System.Text.Encoding.UTF8, "application/json"));

        if (response.IsSuccessStatusCode)
        {
            _logger.LogInformation("Deploy workflow dispatched");
            await _notificationService.SendMessageAsync(chatId,
                "Deploying current main — build and tests first, then the release. "
                + "I'll report back here when it's done (usually 3-5 minutes).");
        }
        else
        {
            var body = await response.Content.ReadAsStringAsync();
            _logger.LogError("Deploy dispatch failed: {Status} {Body}", response.StatusCode, body);
            await _notificationService.SendMessageAsync(chatId,
                $"Couldn't start the deploy — GitHub returned {(int)response.StatusCode}.");
        }
    }

    // "/cost" — the organization's Anthropic API spend, from the Admin API cost report.
    // Needs Anthropic__AdminApiKey (an sk-ant-admin key — separate from the normal API key).
    private async Task HandleCostCommandAsync(long chatId)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("Anthropic__AdminApiKey")))
        {
            await _notificationService.SendMessageAsync(chatId,
                "The Anthropic admin key (Anthropic__AdminApiKey) isn't configured. It's a separate key from the "
                + "normal API key — create one under Console → Settings → Organization → Admin keys "
                + "(requires an organization account, not an individual one) and add it to the function app settings.");
            return;
        }

        try
        {
            var summary = await _costService.GetCostSummaryAsync();
            await _notificationService.SendMessageAsync(chatId, summary);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cost report failed");
            await _notificationService.SendMessageAsync(chatId,
                $"Couldn't fetch the cost report. ({ErrorSignature(ex)})");
        }
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

        using var http = CreateGitHubHttpClient(token);

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

            case "add_news_rule":
            {
                var instruction = input?["instruction"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(instruction))
                    return "Error: no instruction provided.";

                var ruleId = Guid.NewGuid().ToString("N")[..8];
                await _stateService.SaveNewsRuleAsync(ruleId, instruction);
                return $"News preference {ruleId} saved: {instruction}";
            }

            case "list_news_rules":
            {
                var rules = await _stateService.GetNewsRulesAsync();
                if (rules.Count == 0)
                    return "No news digest preferences are set.";

                return string.Join("\n", rules.OrderBy(r => r.CreatedAt).Select(r =>
                    $"[{r.RowKey}] {r.Instruction} (added {r.CreatedAt:d MMM yyyy})"));
            }

            case "remove_news_rule":
            {
                var ruleId = input?["rule_id"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(ruleId))
                    return "Error: no rule_id provided.";

                await _stateService.DeleteNewsRuleAsync(ruleId);
                return $"News preference {ruleId} removed.";
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

            case "create_calendar_event":
            {
                var title = input?["title"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(title))
                    return "Error: no title provided.";

                if (!DateTime.TryParse(input?["date"]?.GetValue<string>(), out var eventDate))
                    return "Error: a valid date (yyyy-MM-dd) is required.";

                TimeSpan? startTime = TimeSpan.TryParse(input?["start_time"]?.GetValue<string>(), out var st) ? st : null;
                TimeSpan? endTime = TimeSpan.TryParse(input?["end_time"]?.GetValue<string>(), out var et) ? et : null;

                await _calendarService.CreatePersonalEventAsync(
                    title, eventDate, startTime, endTime,
                    input?["description"]?.GetValue<string>());
                return $"Created calendar event: {title} on {eventDate:ddd d MMM yyyy}.";
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

    // Exception type + terse message, HTML-escaped and capped, for the in-chat apology
    internal static string ErrorSignature(Exception ex)
    {
        var message = ex.Message;
        if (message.Length > 200)
        {
            // Don't cut through a surrogate pair — a lone surrogate would make Telegram
            // reject the apology itself, degrading back to silence
            var cut = char.IsHighSurrogate(message[199]) ? 199 : 200;
            message = message[..cut] + "…";
        }
        return System.Net.WebUtility.HtmlEncode($"{ex.GetType().Name}: {message}");
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
