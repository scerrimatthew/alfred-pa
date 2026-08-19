using System.Text.Json;
using Alfred.Functions.Models;
using Anthropic.SDK;
using Anthropic.SDK.Messaging;
using Microsoft.Extensions.Logging;

namespace Alfred.Functions.Services.AI;

public class ClaudeSummarizerService : ISummarizerService
{
    private readonly ILogger<ClaudeSummarizerService> _logger;

    public ClaudeSummarizerService(ILogger<ClaudeSummarizerService> logger)
    {
        _logger = logger;
    }

    public async Task<EmailDigest> SummarizeEmailAsync(SchoolEmail email)
    {
        var client = CreateClient();

        var today = DateTime.Now.ToString("dddd, d MMMM yyyy");

        var docsWithContent = email.Documents.Where(d => !string.IsNullOrEmpty(d.ExtractedText)).ToList();
        var documentContent = docsWithContent.Count > 0
            ? "\n\nDocument Contents (read from attachments and linked files):\n" + string.Join("\n---\n",
                docsWithContent.Select(d => $"[{d.Title}]\n{d.ExtractedText}"))
            : "";

        var linksList = email.Documents.Where(d => d.Source == LinkedDocumentSource.BodyLink).ToList();
        var linksContent = linksList.Count > 0
            ? "\n\nLinks found in email:\n" + string.Join("\n",
                linksList.Select(d => $"- {d.Title}: {d.Url}"))
            : "";

        var prompt = BuildSummarizePrompt(email, today, documentContent, linksContent);

        var parameters = new MessageParameters
        {
            Model = Anthropic.SDK.Constants.AnthropicModels.Claude45Sonnet,
            MaxTokens = 8192,
            Messages = [new Message(RoleType.User, prompt)]
        };

        var response = await client.Messages.GetClaudeMessageAsync(parameters);

        var responseText = response.Content?.OfType<TextContent>().FirstOrDefault()?.Text ?? "{}";

        _logger.LogInformation("Claude response for {Subject}: {Length} chars", email.Subject, responseText.Length);

        return ParseDigestResponse(responseText);
    }

    public async Task<PersonalEmailTriage> TriagePersonalEmailAsync(SchoolEmail email, List<SuppressionRuleEntity> suppressionRules, List<AttentionRuleEntity> attentionRules, List<ProcessedEmailEntity> threadContext)
    {
        var client = CreateClient();

        var today = DateTime.Now.ToString("dddd, d MMMM yyyy");

        var docsWithContent = email.Documents.Where(d => !string.IsNullOrEmpty(d.ExtractedText)).ToList();
        var documentContent = docsWithContent.Count > 0
            ? "\n\nAttachment Contents:\n" + string.Join("\n---\n",
                docsWithContent.Select(d => $"[{d.Title}]\n{d.ExtractedText}"))
            : "";

        var prompt = BuildTriagePrompt(email, today, documentContent, suppressionRules, attentionRules, threadContext);

        var parameters = new MessageParameters
        {
            Model = Anthropic.SDK.Constants.AnthropicModels.Claude45Sonnet,
            MaxTokens = 2048,
            Messages = [new Message(RoleType.User, prompt)]
        };

        var response = await client.Messages.GetClaudeMessageAsync(parameters);

        var responseText = response.Content?.OfType<TextContent>().FirstOrDefault()?.Text ?? "{}";

        _logger.LogInformation("Claude triage for {Subject}: {Length} chars", email.Subject, responseText.Length);

        return ParseTriageResponse(responseText, email);
    }

    public async Task<string> BuildEveningDigestAsync(
        List<ProcessedEmailEntity> recentEmails,
        List<Google.Apis.Calendar.v3.Data.Event> upcomingEvents)
    {
        var client = CreateClient();

        var today = DateTime.Now;
        var todayStr = today.ToString("dddd, d MMMM yyyy");

        var emailSummaries = recentEmails.Count > 0
            ? string.Join("\n", recentEmails.Select(e =>
                $"- [{e.SenderName}] {e.Subject}: {e.Summary}"))
            : "No new school emails today.";

        var eventsList = upcomingEvents.Count > 0
            ? string.Join("\n", upcomingEvents.Select(e =>
            {
                var date = e.Start.DateTimeDateTimeOffset?.ToString("ddd d MMM") ?? e.Start.Date ?? "TBD";
                return $"- {date}: {e.Summary} — {e.Description}";
            }))
            : "No upcoming events.";

        var homeworkItems = recentEmails
            .Where(e => !string.IsNullOrWhiteSpace(e.Homework))
            .Select(e => $"- {e.Homework}")
            .ToList();
        var homeworkSummary = homeworkItems.Count > 0
            ? string.Join("\n", homeworkItems)
            : "No homework assignments.";

        var (systemPrompt, userPrompt) = BuildDigestPrompt(todayStr, emailSummaries, recentEmails.Count, eventsList, homeworkSummary);

        var parameters = new MessageParameters
        {
            Model = Anthropic.SDK.Constants.AnthropicModels.Claude46Opus,
            MaxTokens = 2048,
            System = [new SystemMessage(systemPrompt)],
            Messages = [new Message(RoleType.User, userPrompt)]
        };

        var response = await client.Messages.GetClaudeMessageAsync(parameters);

        var responseText = response.Content?.OfType<TextContent>().FirstOrDefault()?.Text ?? "No digest available.";

        _logger.LogInformation("Evening digest generated: {Length} chars", responseText.Length);
        return responseText;
    }

    public async Task<string> AnswerQuestionAsync(
        string question,
        List<ProcessedEmailEntity> recentEmails,
        List<Google.Apis.Calendar.v3.Data.Event> upcomingEvents,
        List<ChatTurnEntity> recentTurns)
    {
        var client = CreateClient();

        var today = DateTime.Now.ToString("dddd, d MMMM yyyy");

        var emailSummaries = recentEmails.Count > 0
            ? string.Join("\n", recentEmails.OrderByDescending(e => e.ProcessedAt).Select(e =>
            {
                var date = e.ProcessedAt.ToString("ddd d MMM yyyy");
                var homework = !string.IsNullOrWhiteSpace(e.Homework) ? $" | Homework: {e.Homework}" : "";
                return $"- [{date}] {e.Subject}: {e.Summary}{homework}";
            }))
            : "No recent emails.";

        var eventsList = upcomingEvents.Count > 0
            ? string.Join("\n", upcomingEvents.Select(e =>
            {
                var date = e.Start.DateTimeDateTimeOffset?.ToString("ddd d MMM yyyy HH:mm") ?? e.Start.Date ?? "TBD";
                return $"- {date}: {e.Summary} — {e.Description}";
            }))
            : "No upcoming events.";

        var systemPrompt = $"""
            You are Alfred, a helpful personal assistant for the parents of Valentina, a Year 1 Bluebells student at Sacred Heart College Junior School (moving to Year 2 in September/October 2026).
            Today is {today}. Only include information relevant to Year 1 or whole-school events.

            Answer ONLY what was asked — do not add reminders, previews of other days, or proactive suggestions beyond the scope of the question. But within that scope, be COMPLETE — include every relevant event, deadline, homework item, and thing to prepare. Do not skip any items that directly answer the question.

            Keep your answer short and to the point — use bullet points, not paragraphs.

            DEFAULT SCHEDULE — PE kit is needed on Monday, Tuesday, and Friday. On all other days (Wednesday, Thursday) it is regular school uniform. When answering questions about what to wear or bring, state which one applies (e.g. "PE kit" or "regular school uniform"). If any email explicitly states a different PE schedule for a specific day or week, that email takes priority over the default.

            Do not mention where the information comes from. Do not reference "the data", emails, or calendar sources. Just answer as if you know it.
            If you genuinely don't have the information, say you're not sure.

            You may be shown the recent back-and-forth of this chat. Use it only to resolve
            follow-ups ("and what about Tuesday?"); if the new question stands on its own,
            answer it fresh and do not force a connection to earlier messages.

            When relevant, include links to documents using <a href="url">title</a> format.

            Format your reply using Telegram HTML:
            - Use <b>bold</b> for emphasis
            - Use • for bullet points, with a blank line between each bullet for readability
            - Only use <b> and <a href=""> tags
            """;

        var userPrompt = $"""
            ## RECENT EMAILS
            {emailSummaries}

            ## CALENDAR EVENTS
            {eventsList}

            {FormatConversationSection(recentTurns)}## QUESTION
            {question}
            """;

        var parameters = new MessageParameters
        {
            Model = Anthropic.SDK.Constants.AnthropicModels.Claude46Opus,
            MaxTokens = 2048,
            System = [new SystemMessage(systemPrompt)],
            Messages = [new Message(RoleType.User, userPrompt)]
        };

        var response = await client.Messages.GetClaudeMessageAsync(parameters);

        var responseText = response.Content?.OfType<TextContent>().FirstOrDefault()?.Text
            ?? "Sorry, I couldn't generate an answer. Please try again.";

        _logger.LogInformation("Answered question: {Length} chars", responseText.Length);
        return responseText;
    }

    public async Task<string> TellJokeAsync(string topic, List<string> recentJokes)
    {
        var client = CreateClient();

        var today = DateTime.Now.ToString("dddd, d MMMM yyyy");

        var topicLine = string.IsNullOrWhiteSpace(topic)
            ? "No topic was given — pick something yourself."
            : $"Topic requested: {topic}";

        var avoidSection = recentJokes.Count > 0
            ? "## JOKES YOU ALREADY TOLD IN THIS CHAT (do not repeat these or anything close to them)\n"
              + string.Join("\n", recentJokes.Select(j => $"- {j}")) + "\n\n"
            : "";

        var systemPrompt = $"""
            You are Alfred, a personal assistant with the dry, understated wit of a good butler.
            Today is {today}. You have been asked for a joke.

            Tell exactly ONE joke. Keep it short — two or three lines at most.
            Keep it clean and family-friendly: the same joke may land in a family chat with children around.
            No politics, no religion, no jokes about real people.

            If a topic is given, make the joke about that topic. If not, pick something yourself —
            wordplay, dad jokes, and gentle observational humour all work.

            Reply with the joke only — no preamble, no "here's a joke", no explanation afterwards.

            Format your reply using Telegram HTML:
            - Use <b>bold</b> sparingly, for the punchline at most
            - Only use <b> tags
            """;

        var userPrompt = $"""
            {avoidSection}## REQUEST
            {topicLine}
            """;

        var parameters = new MessageParameters
        {
            Model = Anthropic.SDK.Constants.AnthropicModels.Claude46Opus,
            MaxTokens = 512,
            System = [new SystemMessage(systemPrompt)],
            Messages = [new Message(RoleType.User, userPrompt)]
        };

        var response = await client.Messages.GetClaudeMessageAsync(parameters);

        var responseText = response.Content?.OfType<TextContent>().FirstOrDefault()?.Text
            ?? "I'm afraid my sense of humour has failed me. Ask me again in a moment.";

        _logger.LogInformation("Told a joke ({Length} chars), topic: {Topic}, avoided {Count} recent",
            responseText.Length, string.IsNullOrWhiteSpace(topic) ? "(none)" : topic, recentJokes.Count);
        return responseText.Trim();
    }

    public async Task<string> BuildPersonalDigestAsync(
        List<ProcessedEmailEntity> todaysEmails,
        List<Google.Apis.Calendar.v3.Data.Event> upcomingActions,
        List<ProcessedEmailEntity> awaitingReply)
    {
        var client = CreateClient();

        var todayStr = DateTime.Now.ToString("dddd, d MMMM yyyy");

        var awaitingList = awaitingReply.Count > 0
            ? string.Join("\n", awaitingReply.Select(e =>
                $"- {e.SenderName} — {e.Subject} (arrived {e.ProcessedAt:ddd d MMM}): {e.Summary}"))
            : "Nothing is waiting on a reply.";

        var emailsList = todaysEmails.Count > 0
            ? string.Join("\n", todaysEmails.Select(e =>
                $"- [{e.Category ?? "other"}] {e.SenderName} — {e.Subject}: {e.Summary}"))
            : "No personal emails today.";

        var actionsList = upcomingActions.Count > 0
            ? string.Join("\n", upcomingActions.Select(e =>
            {
                var date = e.Start.DateTimeDateTimeOffset?.ToString("ddd d MMM HH:mm") ?? e.Start.Date ?? "TBD";
                return $"- {date}: {e.Summary} — {e.Description}";
            }))
            : "No upcoming actions.";

        var systemPrompt = $"""
            You are Alfred, Matthew's personal assistant for his Gmail inbox.
            Today is {todayStr}.

            Write a short evening check-in the way a human PA would text a wrap-up — conversational,
            no section headers, no separator lines, no report structure.

            Shape it roughly as: a one-line opener starting with the 🤖 emoji, then what's coming up (every action and
            deadline from the data with its date — flag anything due tomorrow or overdue first,
            and never skip an event), then anything from today's emails worth knowing in a
            sentence or two, then — only if the AWAITING YOUR REPLY section has entries — a
            gentle nudge naming who is still waiting on him and since when ("Sarah's been
            waiting on an answer since Tuesday"). When listing more than two upcoming items,
            compact • bullets are fine; otherwise keep it in prose.

            Tone example:
            "🤖 Evening! Two things on the radar: the <b>GO bill (€45.20)</b> is due <b>Wednesday</b>,
            and you've got the dentist <b>Friday at 14:00</b>. Today was quiet otherwise — just a
            delivery notice from Wolt I filed away."

            Rules:
            - Begin the greeting line with the 🤖 emoji, followed by a space
            - Bold only the facts that matter (amounts, dates, names)
            - Only use <b> and <a href=""> tags; do not escape characters
            - Keep it glanceable — a few lines, not a report
            """;

        var userPrompt = $"""
            ## TODAY'S PERSONAL EMAILS
            {emailsList}

            ## UPCOMING ACTIONS (from the personal calendar)
            {actionsList}

            ## AWAITING YOUR REPLY (emails from real people Matthew hasn't answered)
            {awaitingList}

            Respond with the formatted HTML digest only.
            """;

        var parameters = new MessageParameters
        {
            Model = Anthropic.SDK.Constants.AnthropicModels.Claude46Opus,
            MaxTokens = 2048,
            System = [new SystemMessage(systemPrompt)],
            Messages = [new Message(RoleType.User, userPrompt)]
        };

        var response = await client.Messages.GetClaudeMessageAsync(parameters);

        var responseText = response.Content?.OfType<TextContent>().FirstOrDefault()?.Text ?? "No digest available.";

        _logger.LogInformation("Personal digest generated: {Length} chars", responseText.Length);
        return responseText;
    }

    public async Task<string> AnswerPersonalQuestionAsync(
        string question,
        List<ProcessedEmailEntity> schoolEmails,
        List<Google.Apis.Calendar.v3.Data.Event> schoolEvents,
        List<ProcessedEmailEntity> personalEmails,
        List<Google.Apis.Calendar.v3.Data.Event> personalActions,
        List<ReportedNewsEntity> recentNews,
        List<ChatTurnEntity> recentTurns,
        Func<string, System.Text.Json.Nodes.JsonNode?, Task<string>> executeTool)
    {
        var client = CreateClient();

        var today = DateTime.Now.ToString("dddd, d MMMM yyyy");

        var schoolEmailsList = schoolEmails.Count > 0
            ? string.Join("\n", schoolEmails.OrderByDescending(e => e.ProcessedAt).Select(e =>
                $"- [{e.ProcessedAt:ddd d MMM yyyy}] {e.Subject}: {e.Summary}"))
            : "No recent school emails.";

        var schoolEventsList = schoolEvents.Count > 0
            ? string.Join("\n", schoolEvents.Select(e =>
            {
                var date = e.Start.DateTimeDateTimeOffset?.ToString("ddd d MMM yyyy HH:mm") ?? e.Start.Date ?? "TBD";
                return $"- {date}: {e.Summary} — {e.Description}";
            }))
            : "No upcoming school events.";

        var personalEmailsList = personalEmails.Count > 0
            ? string.Join("\n", personalEmails.OrderByDescending(e => e.ProcessedAt).Select(e =>
            {
                var muted = e.Suppressed ? " [muted]" : "";
                var needsReply = e.NeedsReply ? " [needs reply]" : "";
                var link = !string.IsNullOrEmpty(e.GmailThreadId)
                    ? $" link={Gmail.GmailLinks.ForThread(e.GmailThreadId)}"
                    : "";
                return $"- id={e.RowKey} [{e.ProcessedAt:ddd d MMM yyyy}] [{e.Category ?? "other"}]{muted}{needsReply} {e.SenderName} — {e.Subject}: {e.Summary}{link}";
            }))
            : "No recent personal emails.";

        var personalActionsList = personalActions.Count > 0
            ? string.Join("\n", personalActions.Select(e =>
            {
                var date = e.Start.DateTimeDateTimeOffset?.ToString("ddd d MMM yyyy HH:mm") ?? e.Start.Date ?? "TBD";
                return $"- eventId={e.Id} {date}: {e.Summary} — {e.Description}";
            }))
            : "No upcoming personal actions.";

        var recentNewsList = recentNews.Count > 0
            ? string.Join("\n", recentNews.OrderByDescending(n => n.ReportedAt).Select(n =>
            {
                var summary = !string.IsNullOrWhiteSpace(n.Summary) ? $": {n.Summary}" : "";
                var why = !string.IsNullOrWhiteSpace(n.WhyItMatters) ? $" | why it mattered: {n.WhyItMatters}" : "";
                return $"- [{n.ReportedAt:ddd d MMM}] [{n.Category ?? "uncategorized"}] {n.Headline} ({n.Url}){summary}{why}";
            }))
            : "No AI news reported recently.";

        var systemPrompt = $"""
            You are Alfred, Matthew's personal assistant, chatting with him directly on Telegram.
            Today is {today}.

            You have three kinds of context:
            - SCHOOL: emails and calendar events for Valentina, a Year 1 Bluebells student at
              Sacred Heart College Junior School (moving to Year 2 in September/October 2026)
            - PERSONAL: Matthew's own inbox (invoices, appointments, deadlines) and the personal
              calendar actions Alfred created for him
            - RECENT AI NEWS: the stories Alfred's evening AI-news briefing reported to Matthew
              lately. When he follows up on one ("tell me more about that DORA story", "what was
              that consultancy launch about?"), match it by headline/topic, then use web_search
              to pull the PRIMARY source (and related coverage if useful) and give him a proper
              read-out: what actually happened, the key numbers, and what it means for Cleverbit's
              bet. Link the sources you used. Only use web_search for news follow-ups or when he
              explicitly asks you to look something up online — never for questions his emails
              and calendar can answer.

            Answer ONLY what was asked, but completely — include every relevant item.
            Reply the way a human PA would text: conversational, direct, and brief. Use prose
            for simple answers; switch to compact • bullets only when listing several items.
            Do not mention "the data" or where information comes from. Just answer.
            If you genuinely don't have the information, say you're not sure.

            You may be shown the recent back-and-forth of this chat. Use it only to resolve
            follow-ups ("and what about Tuesday?", "delete that one"); if the new question
            stands on its own, answer it fresh and do not force a connection to earlier messages.

            The email lists below only cover what Alfred has processed recently. If Matthew asks
            about an email you don't see there (older than the window, read before Alfred saw it,
            or missing detail like an amount), SEARCH the inbox directly:
            - search_inbox: query Gmail with standard search syntax (from:, subject:,
              after:2026/08/01, before:2026/08/02, has:attachment, "quoted phrases"). Start
              specific; broaden if nothing matches.
            - read_email: fetch one email's full body plus the text of its PDF attachments —
              use it on the best match to pull out specifics (invoice amounts, due dates,
              reference numbers).
            Answer from the provided context first and only search when it can't answer the
            question. Never invent an email you didn't find either way.

            You can also ACT on Matthew's personal emails when he asks:
            - mark_unread: mark an email as unread in Gmail (e.g. "mark that invoice as unread")
            - recategorize_email: change an email's category label (e.g. "that's not an invoice, it's a delivery")
            - add_suppression_rule: when Matthew asks to stop being notified about a kind of email
              ("ignore these Bolt reports in future"). Write the pattern as a GENERALIZED description
              of the recurring email — strip specific months, dates, and numbers so future editions
              match (e.g. "Monthly 'work profile report' emails from Bolt Business" rather than
              "Your work profile report for July 2026"). Include the example sender/subject when you
              can identify the email he means. Suppressed emails are still filed and labeled, just
              never notified about.
            - list_suppression_rules / remove_suppression_rule: review or undo suppression rules
              ("what am I ignoring?", "start showing me Bolt reports again" — list first to find
              the rule id if you don't have it).
            - add_attention_rule: the OPPOSITE of suppression — when Matthew asks to always be
              alerted about a kind of email ("always tell me when the bank writes", "never miss
              anything about the car insurance"). Write the pattern as a generalized description,
              same as suppression rules. Matching emails always notify, even if a suppression
              rule also matches.
            - list_attention_rules / remove_attention_rule: review or undo attention rules.
            - snooze_email: when Matthew wants to deal with an email later ("remind me about
              this tomorrow", "snooze the GO bill till Friday"), schedule a reminder. Compute
              remind_at ("yyyy-MM-dd HH:mm", Malta time) from his words — a bare day means
              08:00 that morning. Alfred re-sends the alert at that time.
            - list_snoozes / cancel_snooze: review or cancel pending reminders ("what have I
              snoozed?", "forget that reminder" — list first to find the id if needed).
            - add_news_rule: when Matthew gives feedback on the evening AI-news digest
              ("stop covering funding rounds", "more on EU AI Act enforcement", "that
              consultancy story was spot on — more like it"). Write the instruction as a
              GENERALIZED standing preference for future digests, keeping his direction
              (more of / less of / never) explicit.
            - list_news_rules / remove_news_rule: review or undo news digest preferences
              ("what news feedback have I given you?", "start covering funding rounds again"
              — list first to find the rule id if you don't have it).
            - draft_reply: write a reply to an email and save it in his Gmail Drafts for him to
              review and send — NOTHING is ever sent automatically. Use when Matthew asks to
              reply to an email ("reply saying I'll pay Friday", "tell Antonio Thursday works").
              Write the body as plain text in Matthew's voice — brief and natural, no HTML,
              signed off "Matthew". If he dictated exact wording, keep it near-verbatim;
              otherwise phrase his intent naturally. Set reply_all only when he asks to reply
              to everyone. Afterwards confirm the draft is waiting in his Drafts to review.
            - create_calendar_event: add a new reminder to Matthew's personal calendar whenever he
              asks you to remember a date ("remind me to pay this by the 31st", "put the dentist
              appointment in for Tuesday at 9"). Never say you can't create one, and never repurpose
              an unrelated existing reminder instead. Use a short, specific title, put the useful
              detail (amount, reference number, IBAN, who to pay) in the description, and leave it
              all-day unless a time was given. Check the personal actions listed below first so you
              don't add one that's already there.
            - update_calendar_event / delete_calendar_event: fix or remove a reminder Alfred created
              when Matthew says it's wrong or irrelevant ("move the dentist to Friday", "the GO bill
              is already paid, drop the reminder"). Personal actions carry eventId=... — pass it to
              these tools. Only Alfred-created events can be changed; his own calendar entries are
              off-limits.
            Personal emails are listed with id=... — pass that id to tools. NEVER show raw ids in replies.
            Personal emails also carry link=... — when discussing a specific email, offer it as
            <a href="link">Open in Gmail</a> so Matthew can jump straight to it.
            Emails marked [muted] were suppressed by an existing rule — mention them only if asked.
            Emails marked [needs reply] are ones Matthew hasn't answered yet ("what am I still
            owing replies on?") — though he may have replied since the flag was set.
            When Matthew references an email loosely ("that HSBC one"), match it by sender/subject.
            If the reference is ambiguous, ask which one he means instead of guessing.
            After acting, confirm briefly what you did.

            Format replies using Telegram HTML:
            - <b>bold</b> for emphasis, • for bullets with a blank line between each
            - Only <b> and <a href=""> tags
            """;

        var userPrompt = $"""
            ## SCHOOL EMAILS
            {schoolEmailsList}

            ## SCHOOL CALENDAR
            {schoolEventsList}

            ## PERSONAL EMAILS
            {personalEmailsList}

            ## PERSONAL ACTIONS (Alfred-created calendar entries)
            {personalActionsList}

            ## RECENT AI NEWS (stories the evening briefing already reported)
            {recentNewsList}

            {FormatConversationSection(recentTurns)}## QUESTION
            {question}
            """;

        var tools = new List<Anthropic.SDK.Common.Tool>
        {
            new Anthropic.SDK.Common.Function(
                "mark_unread",
                "Mark a personal email as unread in Gmail.",
                System.Text.Json.Nodes.JsonNode.Parse("""
                {
                    "type": "object",
                    "properties": {
                        "message_id": { "type": "string", "description": "The Gmail message id of the email (from id=...)" }
                    },
                    "required": ["message_id"]
                }
                """)),
            new Anthropic.SDK.Common.Function(
                "recategorize_email",
                "Change the category (Gmail label) of a personal email.",
                System.Text.Json.Nodes.JsonNode.Parse("""
                {
                    "type": "object",
                    "properties": {
                        "message_id": { "type": "string", "description": "The Gmail message id of the email (from id=...)" },
                        "category": {
                            "type": "string",
                            "enum": ["invoice", "payment-request", "personal-reply", "appointment", "financial", "official", "security", "delivery", "notification", "other"]
                        }
                    },
                    "required": ["message_id", "category"]
                }
                """)),
            new Anthropic.SDK.Common.Function(
                "add_suppression_rule",
                "Create a rule so Matthew is no longer notified about emails matching a recurring pattern. The pattern must be a generalized natural-language description of the recurring email, not an exact subject.",
                System.Text.Json.Nodes.JsonNode.Parse("""
                {
                    "type": "object",
                    "properties": {
                        "pattern": { "type": "string", "description": "Generalized description of what to suppress, e.g. \"Monthly 'work profile report' emails from Bolt Business\"" },
                        "example_sender": { "type": "string", "description": "Sender of the example email, if known" },
                        "example_subject": { "type": "string", "description": "Subject of the example email, if known" }
                    },
                    "required": ["pattern"]
                }
                """)),
            new Anthropic.SDK.Common.Function(
                "list_suppression_rules",
                "List the active suppression rules with their ids.",
                System.Text.Json.Nodes.JsonNode.Parse("""
                { "type": "object", "properties": {} }
                """)),
            new Anthropic.SDK.Common.Function(
                "remove_suppression_rule",
                "Delete a suppression rule so those emails notify again.",
                System.Text.Json.Nodes.JsonNode.Parse("""
                {
                    "type": "object",
                    "properties": {
                        "rule_id": { "type": "string", "description": "The id of the rule to remove (from list_suppression_rules)" }
                    },
                    "required": ["rule_id"]
                }
                """)),
            new Anthropic.SDK.Common.Function(
                "create_calendar_event",
                "Add a new reminder/event to Matthew's personal calendar. Use whenever he asks to be reminded of a date or deadline.",
                System.Text.Json.Nodes.JsonNode.Parse("""
                {
                    "type": "object",
                    "properties": {
                        "title": { "type": "string", "description": "Short, specific title, e.g. \"Pay Aeris invoice #0005713\"" },
                        "date": { "type": "string", "description": "Date of the event or deadline, yyyy-MM-dd" },
                        "start_time": { "type": "string", "description": "Start time, HH:mm (omit for an all-day reminder)" },
                        "end_time": { "type": "string", "description": "End time, HH:mm (defaults to an hour after the start)" },
                        "description": { "type": "string", "description": "The useful detail: amount, reference number, payee, IBAN, link" }
                    },
                    "required": ["title", "date"]
                }
                """)),
            new Anthropic.SDK.Common.Function(
                "update_calendar_event",
                "Change an Alfred-created reminder/event on Matthew's personal calendar. Only pass the fields being changed.",
                System.Text.Json.Nodes.JsonNode.Parse("""
                {
                    "type": "object",
                    "properties": {
                        "event_id": { "type": "string", "description": "The calendar event id (from eventId=...)" },
                        "title": { "type": "string" },
                        "date": { "type": "string", "description": "New date, yyyy-MM-dd" },
                        "start_time": { "type": "string", "description": "New start time, HH:mm (omit to keep all-day or existing time)" },
                        "end_time": { "type": "string", "description": "New end time, HH:mm" },
                        "description": { "type": "string" }
                    },
                    "required": ["event_id"]
                }
                """)),
            new Anthropic.SDK.Common.Function(
                "add_attention_rule",
                "Create a rule so Matthew is ALWAYS notified about emails matching a recurring pattern, overriding the normal triage bar and any suppression rule. The pattern must be a generalized natural-language description.",
                System.Text.Json.Nodes.JsonNode.Parse("""
                {
                    "type": "object",
                    "properties": {
                        "pattern": { "type": "string", "description": "Generalized description of what always warrants attention, e.g. \"Any email from HSBC about the mortgage\"" },
                        "example_sender": { "type": "string", "description": "Sender of the example email, if known" },
                        "example_subject": { "type": "string", "description": "Subject of the example email, if known" }
                    },
                    "required": ["pattern"]
                }
                """)),
            new Anthropic.SDK.Common.Function(
                "list_attention_rules",
                "List the active attention (always-notify) rules with their ids.",
                System.Text.Json.Nodes.JsonNode.Parse("""
                { "type": "object", "properties": {} }
                """)),
            new Anthropic.SDK.Common.Function(
                "remove_attention_rule",
                "Delete an attention rule so those emails go back to normal triage.",
                System.Text.Json.Nodes.JsonNode.Parse("""
                {
                    "type": "object",
                    "properties": {
                        "rule_id": { "type": "string", "description": "The id of the rule to remove (from list_attention_rules)" }
                    },
                    "required": ["rule_id"]
                }
                """)),
            new Anthropic.SDK.Common.Function(
                "snooze_email",
                "Schedule a reminder about an email — Alfred re-sends the alert at the given time.",
                System.Text.Json.Nodes.JsonNode.Parse("""
                {
                    "type": "object",
                    "properties": {
                        "message_id": { "type": "string", "description": "The Gmail message id of the email (from id=...)" },
                        "remind_at": { "type": "string", "description": "When to remind, \"yyyy-MM-dd HH:mm\" in Malta time. A bare date (00:00) means 08:00 that morning." }
                    },
                    "required": ["message_id", "remind_at"]
                }
                """)),
            new Anthropic.SDK.Common.Function(
                "list_snoozes",
                "List pending email reminders with their ids and due times.",
                System.Text.Json.Nodes.JsonNode.Parse("""
                { "type": "object", "properties": {} }
                """)),
            new Anthropic.SDK.Common.Function(
                "cancel_snooze",
                "Cancel a pending email reminder.",
                System.Text.Json.Nodes.JsonNode.Parse("""
                {
                    "type": "object",
                    "properties": {
                        "message_id": { "type": "string", "description": "The Gmail message id of the snoozed email (from list_snoozes or id=...)" }
                    },
                    "required": ["message_id"]
                }
                """)),
            new Anthropic.SDK.Common.Function(
                "add_news_rule",
                "Save a standing preference for the evening AI-news digest — what to cover more, less, or never. Use when Matthew gives feedback on the news briefing.",
                System.Text.Json.Nodes.JsonNode.Parse("""
                {
                    "type": "object",
                    "properties": {
                        "instruction": { "type": "string", "description": "Generalized standing preference, e.g. \"Skip funding-round stories entirely\" or \"Go deeper on EU AI Act enforcement actions\"" }
                    },
                    "required": ["instruction"]
                }
                """)),
            new Anthropic.SDK.Common.Function(
                "list_news_rules",
                "List the active AI-news digest preferences with their ids.",
                System.Text.Json.Nodes.JsonNode.Parse("""
                { "type": "object", "properties": {} }
                """)),
            new Anthropic.SDK.Common.Function(
                "remove_news_rule",
                "Delete an AI-news digest preference.",
                System.Text.Json.Nodes.JsonNode.Parse("""
                {
                    "type": "object",
                    "properties": {
                        "rule_id": { "type": "string", "description": "The id of the rule to remove (from list_news_rules)" }
                    },
                    "required": ["rule_id"]
                }
                """)),
            new Anthropic.SDK.Common.Function(
                "draft_reply",
                "Write a reply to an email and save it as a Gmail draft in the same thread. The draft is NEVER sent — Matthew reviews and sends it himself from Gmail.",
                System.Text.Json.Nodes.JsonNode.Parse("""
                {
                    "type": "object",
                    "properties": {
                        "message_id": { "type": "string", "description": "The Gmail message id of the email being replied to (from id=...)" },
                        "body": { "type": "string", "description": "Plain-text reply body, written in Matthew's voice" },
                        "reply_all": { "type": "boolean", "description": "Reply to all original recipients instead of just the sender (default false)" }
                    },
                    "required": ["message_id", "body"]
                }
                """)),
            new Anthropic.SDK.Common.Function(
                "search_inbox",
                "Search Matthew's Gmail inbox directly, for emails the provided context doesn't cover. Uses standard Gmail query syntax. Returns matches with id, date, sender, subject, and a short snippet.",
                System.Text.Json.Nodes.JsonNode.Parse("""
                {
                    "type": "object",
                    "properties": {
                        "query": { "type": "string", "description": "Gmail search query, e.g. \"from:go.com.mt after:2026/08/01 before:2026/08/02\" or \"invoice has:attachment\"" },
                        "max_results": { "type": "integer", "description": "Max matches to return (default 10, cap 20)" }
                    },
                    "required": ["query"]
                }
                """)),
            new Anthropic.SDK.Common.Function(
                "read_email",
                "Fetch one email's full body plus the extracted text of its PDF attachments. Use on a search_inbox match (or a known id) to read the details.",
                System.Text.Json.Nodes.JsonNode.Parse("""
                {
                    "type": "object",
                    "properties": {
                        "message_id": { "type": "string", "description": "The Gmail message id (from id=...)" }
                    },
                    "required": ["message_id"]
                }
                """)),
            new Anthropic.SDK.Common.Function(
                "delete_calendar_event",
                "Delete an Alfred-created reminder/event from Matthew's personal calendar.",
                System.Text.Json.Nodes.JsonNode.Parse("""
                {
                    "type": "object",
                    "properties": {
                        "event_id": { "type": "string", "description": "The calendar event id (from eventId=...)" }
                    },
                    "required": ["event_id"]
                }
                """))
        };

        // Server-side web search for AI-news follow-ups ("tell me more about that DORA
        // story") and explicit look-this-up requests
        tools.Add(ServerTools.GetWebSearchTool(maxUses: 5));

        var messages = new List<Message> { new(RoleType.User, userPrompt) };

        var parameters = new MessageParameters
        {
            Model = Anthropic.SDK.Constants.AnthropicModels.Claude46Opus,
            // Web-search turns carry search narration on top of the answer
            MaxTokens = 4096,
            System = [new SystemMessage(systemPrompt)],
            Messages = messages,
            Tools = tools
        };

        // Enough room for a search -> refine -> read -> act -> answer chain
        for (var iteration = 0; iteration < 10; iteration++)
        {
            var response = await client.Messages.GetClaudeMessageAsync(parameters);

            if (response.StopReason == "pause_turn")
            {
                // Server-side web search paused mid-turn — append the FULL partial content
                // (server_tool_use / result blocks included; response.Message would strip
                // them and restart the search) and re-send so the server resumes
                messages.Add(new Message { Role = RoleType.Assistant, Content = response.Content });
                continue;
            }

            var toolUses = response.Content?.OfType<ToolUseContent>().ToList() ?? [];
            if (toolUses.Count == 0)
            {
                // With server tools in play the answer is the LAST text block — earlier
                // ones are search narration interleaved with result blocks
                return response.Content?.OfType<TextContent>().LastOrDefault()?.Text
                    ?? "Sorry, I couldn't generate an answer. Please try again.";
            }

            // Full content, not response.Message — a turn can mix web-search blocks with
            // client tool calls, and the stripped copy would corrupt the conversation
            messages.Add(new Message { Role = RoleType.Assistant, Content = response.Content });

            foreach (var toolUse in toolUses)
            {
                string result;
                try
                {
                    result = await executeTool(toolUse.Name, toolUse.Input);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Tool {Tool} failed", toolUse.Name);
                    result = $"Error: {ex.Message}";
                }

                messages.Add(new Message
                {
                    Role = RoleType.User,
                    Content = [new ToolResultContent
                    {
                        ToolUseId = toolUse.Id,
                        Content = [new TextContent { Text = result }]
                    }]
                });
            }
        }

        return "I tried to help but got stuck in a loop of actions — please check Gmail directly.";
    }

    internal static string FormatConversationSection(List<ChatTurnEntity> recentTurns)
    {
        if (recentTurns.Count == 0)
            return string.Empty;

        var maltaTz = TimeZoneInfo.FindSystemTimeZoneById("Europe/Malta");
        var lines = recentTurns.Select(t =>
        {
            var time = TimeZoneInfo.ConvertTime(t.AskedAt, maltaTz).ToString("ddd HH:mm");
            return $"[{time}] Q: {t.Question}\n[{time}] A: {t.Answer}";
        });

        return "## RECENT CONVERSATION (context only — the question below may be unrelated; "
            + "ignore this section if it isn't relevant. The email and calendar data above is "
            + "current and takes priority over anything said here)\n"
            + string.Join("\n", lines) + "\n\n";
    }

    // Test seam: when set, replaces the live client (tests back it with a fake
    // HttpClient). Never set in production.
    internal Func<AnthropicClient>? ClientFactory { get; set; }

    private AnthropicClient CreateClient()
    {
        if (ClientFactory is not null) return ClientFactory();

        var apiKey = Environment.GetEnvironmentVariable("Anthropic__ApiKey")
            ?? throw new InvalidOperationException("Anthropic API key not configured");

        return new AnthropicClient(apiKey);
    }

    internal static string BuildSummarizePrompt(SchoolEmail email, string today, string documentContent, string linksContent)
    {
        return $"""
            You are Alfred, a personal assistant helping parents of Valentina, a Year 1 Bluebells student at Sacred Heart College Junior School (moving to Year 2 in September/October 2026).
            Today is {today}.
            This email was sent on {email.ReceivedDate:dddd, d MMMM yyyy}.

            CRITICAL: When the email says "tomorrow", "next week", "this Wednesday", etc., resolve those dates
            relative to the EMAIL SEND DATE ({email.ReceivedDate:yyyy-MM-dd}), NOT relative to today.
            For example, if the email was sent on Monday 20 April and says "tomorrow", that means Tuesday 21 April.

            CRITICAL: For weekly plans that say "week starting DD/MM", use that date as Monday and calculate
            all other days from it. For example, "week starting 20/4/2026" means Monday=20 April, Tuesday=21 April,
            Wednesday=22 April, Thursday=23 April, Friday=24 April. Always verify day-date alignment before outputting.

            IMPORTANT CONTEXT:
            - Valentina is in Year 1. Only include information relevant to Year 1 (or whole-school events). Ignore Year 2+ specific content unless it will apply when she moves up.
            - Do NOT create calendar events for optional programmes that require signing up first (e.g. summer programmes, extracurricular clubs). Mention them in the summary but not in calendarEvents.
            - Do NOT create calendar events for events that require registration/RSVP before attending. Instead, create a deadline reminder for the registration/RSVP date if applicable.
            - Do NOT create calendar events for homework. List homework details in the Telegram message only.
            - DO create calendar events for confirmed school activities, outings, book changes, meetings, holidays, and field days.

            Analyze this school email AND all attached/linked document contents below. Extract everything
            a parent needs to know for Valentina, especially:
            - Things to bring or prepare (PE kit, lunch, toys, books, etc.)
            - Changes from the regular schedule
            - Forms to submit or deadlines to meet
            - Homework assignments
            - Upcoming events, outings, or meetings

            Produce a JSON response with three fields:

            1. "telegramMessage": Format using Telegram HTML. Follow this template exactly:

               📩 <b>SUBJECT LINE HERE</b>

               2-3 sentence summary paragraph.

               ━━━━━━━━━━━━━━━

               ✅ <b>WHAT TO PREPARE</b>

               • Item one
               • Item two

               📝 <b>HOMEWORK</b>

               • Subject — Description and due date

               📅 <b>CALENDAR</b>

               • Event name — Date

               🔗 <b>LINKS</b>

               • <a href="url">Title</a>

               Rules:
               - Subject line in UPPERCASE bold with 📩 emoji prefix
               - Section headers must be UPPERCASE bold with the emoji prefix shown above
               - Add a blank line before AND after each section header for clear visual spacing
               - Add the ━━━━━━━━━━━━━━━ separator line only once, right after the summary paragraph
               - Summary: plain text paragraph, no bullets
               - "WHAT TO PREPARE" only if there are actionable items for parents
               - "HOMEWORK" only if homework is mentioned. Include subject, description, and due date
               - "CALENDAR" only if calendar events were created
               - "LINKS" only if there are links. Give each a short descriptive title. Use <a href="url">Title</a> format.
               - Use • (bullet character) for list items, — (em dash) to separate event names from dates
               - Only use emojis on section headers. Do NOT use emojis in bullet point content
               - Omit sections with no content
               - Only use these HTML tags: <b>, <a href="">. No other tags.

            2. "calendarEvents": An array of events to add to the calendar, each with:
               - "title": Use a consistent naming convention with these prefixes:
                   • "Outing: " for trips and visits (e.g. "Outing: Bristow Potteries")
                   • "Activity: " for in-school activities (e.g. "Activity: Transport Malta")
                   • "Meeting: " for parent meetings (e.g. "Meeting: Online Safety")
                   • "Deadline: " for deadlines and due dates (e.g. "Deadline: Community Day RSVP")
                   • "Holiday: " for school holidays (e.g. "Holiday: Workers' Day")
                   • No prefix for general school events (e.g. "Field Day", "Community Day")
                 Always include "Year 1" in the title when the event is year-specific.
               - "description": brief description including what to bring/prepare
               - "date": ISO date string (yyyy-MM-dd)
               - "startTime": HH:mm or null for all-day events
               - "endTime": HH:mm or null for all-day events
               - "action": "create", "update", or "delete"

            3. "homework": A plain text string summarizing any homework assignments mentioned in the email.
               Include the subject, what needs to be done, and the due date if given.
               Set to null if no homework is mentioned.

            4. "requiresImmediateAlert": A boolean indicating whether this email is urgent enough to
               send an immediate Telegram notification. Most emails do NOT need this — they will be
               included in the evening digest instead. Set to true ONLY for:
               - Last-minute schedule changes (e.g. cancelled outing, early dismissal tomorrow)
               - Urgent warnings or emergencies (e.g. health alerts, school closure)
               - Deadlines that expire today or tomorrow
               - Time-sensitive requests that cannot wait until the evening digest
               Set to false for everything else, including: weekly plans, homework, routine reminders,
               upcoming events more than 2 days away, newsletters, photo galleries, general updates,
               and anything that can safely wait for the evening digest.

            5. "category": A single word classifying the email, one of: "weekly-plan", "homework",
               "event", "outing", "meeting", "newsletter", "admin", "other". Pick the dominant
               theme when several apply (e.g. a weekly plan mentioning homework is "weekly-plan").

            Important formatting rules:
            - Use Telegram HTML format, NOT MarkdownV2
            - Only allowed tags: <b>bold</b> and <a href="url">link</a>
            - Do NOT escape any characters — HTML mode handles special chars natively
            - Use literal • for bullets and — for dashes

            Email Subject: {email.Subject}
            From: {email.SenderName}
            Date: {email.ReceivedDate:ddd, d MMM yyyy HH:mm}

            Email Body:
            {email.Body}
            {documentContent}
            {linksContent}

            Respond with valid JSON only, no markdown code fences.
            """;
    }

    internal static string BuildTriagePrompt(SchoolEmail email, string today, string documentContent, List<SuppressionRuleEntity> suppressionRules, List<AttentionRuleEntity> attentionRules, List<ProcessedEmailEntity> threadContext)
    {
        // Cap the body — personal inbox emails (marketing, long threads) can be huge after HTML stripping
        var body = email.Body.Length > 8000
            ? email.Body[..8000] + "\n[... truncated]"
            : email.Body;

        var threadSection = threadContext.Count > 0
            ? "\n\nTHREAD CONTEXT — this email is a new message in a conversation Alfred already processed. Earlier messages in this thread:\n"
              + string.Join("\n", threadContext.Select(t =>
                  $"- [{t.ProcessedAt:ddd d MMM}] {t.SenderName} — {t.Subject}: {t.Summary}"))
              + """


              Treat this email as a FOLLOW-UP, not a fresh item:
              - Focus the summary and telegramMessage on what is NEW in this message (an answer,
                a changed date, a new ask), wording it as a follow-up ("Sarah got back about...").
              - Judge attention by the new content alone: a thread Matthew was already alerted
                about does not need re-alerting for pleasantries, confirmations of what he
                already knows, or automated "thanks, we received it" replies.
              - A genuinely new development (someone answered his question, a deadline moved,
                money is now due) DOES warrant attention as usual.
              - Do not re-create calendar events the earlier messages already produced; only
                include calendarEvents for genuinely new or changed dates.
              """
            : "";

        var attentionSection = attentionRules.Count > 0
            ? "\n\nATTENTION RULES — Matthew has explicitly asked to ALWAYS be notified about emails matching these patterns:\n"
              + string.Join("\n", attentionRules.Select(r =>
              {
                  var example = r.ExampleSender is not null || r.ExampleSubject is not null
                      ? $" (example: from \"{r.ExampleSender}\", subject \"{r.ExampleSubject}\")"
                      : "";
                  return $"- [{r.RowKey}] {r.Pattern}{example}";
              }))
              + """


              Apply these rules with REASONING, not literal matching. When an email matches one,
              set "requiresAttention" to true regardless of the usual bar, and put the rule id in
              "matchedAttentionRule". Attention rules WIN over suppression rules: if both match,
              notify anyway (suppressed = false). Do not stretch a rule to cover genuinely
              different emails.
              """
            : "";

        var rulesSection = suppressionRules.Count > 0
            ? "\n\nSUPPRESSION RULES — Matthew has explicitly asked NOT to be notified about emails matching these patterns:\n"
              + string.Join("\n", suppressionRules.Select(r =>
              {
                  var example = r.ExampleSender is not null || r.ExampleSubject is not null
                      ? $" (example: from \"{r.ExampleSender}\", subject \"{r.ExampleSubject}\")"
                      : "";
                  return $"- [{r.RowKey}] {r.Pattern}{example}";
              }))
              + """


              Apply these rules with REASONING, not literal matching: match the recurring essence of
              the rule. A rule about "monthly work profile report for July 2026" also matches the
              August 2026 edition, a renamed variant, or the same report from a slightly different
              sender address. When an email matches a rule, set "suppressed" to true and put the
              rule id in "matchedRule". Do not stretch rules to cover genuinely different emails —
              a payment reminder from the same sender is NOT covered by a rule about its newsletters.
              """
            : "";

        return $"""
            You are Alfred, a personal assistant helping Matthew stay on top of his personal Gmail inbox.
            Today is {today}.
            This email was sent on {email.ReceivedDate:dddd, d MMMM yyyy}.

            CRITICAL date handling — the email may be hours or days old by the time you read it:
            - Resolve relative dates in the email ("tomorrow", "this Friday") against the EMAIL
              SEND DATE ({email.ReceivedDate:yyyy-MM-dd}), NOT against today.
            - In your telegramMessage, express timing relative to TODAY ({today}). An appointment
              the email calls "tomorrow" that resolves to today is "today", never "tomorrow".
              Prefer explicit dates and times ("Mon 17 Aug at 08:00") over relative words.
            - If the resolved date has already passed, say so plainly ("this was for this morning
              at 08:00") and do NOT create calendar events for it.

            Triage the email below. Decide whether it warrants Matthew's attention.{rulesSection}{attentionSection}{threadSection}

            The bar for attention: would a sharp human PA actually interrupt Matthew's day for this?
            Most emails don't clear that bar. Interrupt him for:
            - Invoices, bills, and requests for payment
            - Emails written by a real person addressed to him, especially replies to his own emails
            - Bank, card, insurance, or government correspondence that needs something from him
            - Appointment and booking confirmations, changes, and cancellations
            - Concrete deadlines and expiring renewals
            - Security alerts suggesting something is actually wrong (suspicious sign-in, fraud
              warning) — NOT routine "new login from your device" notices
            - Delivery problems needing action (failed delivery, customs charge) — not tracking updates

            Everything else gets filed quietly (requiresAttention = false), including:
            - Marketing, promotions, sales outreach, newsletters, and product announcements
            - Saved-search, job-alert, price-drop, and wishlist notifications
            - Social media activity
            - Routine receipts, payment confirmations, and order/booking confirmations he'd expect
            - Routine delivery tracking updates
            - Surveys, terms-of-service updates, and generic account housekeeping

            Produce a JSON response with these fields:

            0. "suppressed": boolean — true if a suppression rule matches this email (see above;
               false when there are no rules). Also provide "matchedRule" with the matching rule id,
               or null.

            1. "requiresAttention": boolean, per the bar above. When unsure, prefer FALSE — the
               email stays in Gmail and the digest either way; a wrongly-silenced email costs
               little, but constant interruptions make Matthew ignore the ones that matter.
               Exception: when an attention rule matches, this is always true. Also provide
               "matchedAttentionRule" with the matching attention rule id, or null.

            2. "category": one of "invoice", "payment-request", "personal-reply", "appointment",
               "financial", "official", "security", "delivery", "notification", "other".

            3. "summary": 1-2 plain-text sentences capturing what the email is and any action needed.

            4. "calendarEvents": An array of concrete, dated actions Matthew must take, each with:
               - "title": "Deadline: ..." for payments/renewals/submissions due (e.g. "Deadline: Pay GO invoice €45"),
                 "Appointment: ..." for confirmed appointments/bookings (e.g. "Appointment: Dentist")
               - "description": brief description including amount, reference, or location if present
               - "date": ISO date string (yyyy-MM-dd) — the due date or appointment date
               - "startTime": HH:mm or null for all-day (deadlines are usually all-day)
               - "endTime": HH:mm or null
               - "action": "create", "update", or "delete"
               ONLY include events with a concrete date and a real personal action or commitment.
               Do NOT create events for: marketing offer expiries, newsletter content, generic
               notifications, past dates, or anything without a clear action for Matthew.
               Use an empty array when there is nothing actionable.

            FRAUD CHECK — for any email asking for money or payment details (invoices, payment
            requests, bank-detail changes), sanity-check it before summarizing:
            - Does the actual sender address ({email.SenderEmail}) plausibly belong to the
              organization the email claims to be from? Watch for lookalike domains
              (g0.com.mt, hsbc-secure-alerts.com), freemail addresses (gmail/outlook/yahoo)
              claiming to be companies, and reply-to mismatches mentioned in the body.
            - Classic invoice-fraud tells: "our bank details have changed", unusual urgency or
              secrecy, payment methods like gift cards or crypto, a claimed organization Matthew
              has no visible relationship with.
            If it looks suspicious, set "fraudWarning" to ONE plain sentence naming the specific
            mismatch (e.g. "Claims to be BOV but was sent from bov-alerts.net, which is not the
            bank's domain."). Otherwise set it to null — an expected invoice from a matching
            domain is NOT suspicious, and false alarms teach Matthew to ignore real ones.

            5. "telegramMessage": ALWAYS provide this, even when requiresAttention is false.
               Write it the way a sharp human PA would text Matthew — NOT a form or report:
               - Conversational and direct, usually 1-3 short sentences. No section headers,
                 no separator lines, no template structure, no emoji prefixes.
               - Lead with what it is and what it means for him. Keep amounts and dates inline,
                 bolding only the one or two facts that matter (<b>€45.20</b>, <b>25 Aug</b>).
               - If there's something to do, say what and by when. If you created calendar
                 events, mention it in passing ("added it to your calendar").
               - Tone examples:
                 "Your GO bill for August is in — <b>€45.20</b>, due <b>25 Aug</b>. Added a reminder to your calendar."
                 "Antonio's confirmed your haircut for tomorrow at <b>08:00</b>."
                 "Sarah replied about the weekend plans — she can do Saturday."
               - Only use these HTML tags: <b>, <a href="">. No other tags.
               - Do NOT escape any characters — HTML mode handles special chars natively

            6. "fraudWarning": string or null, per the FRAUD CHECK above.

            7. "needsReply": boolean — true ONLY when a real person wrote to Matthew and clearly
               expects a response from him (a question, an invitation, a request he must answer).
               False for automated mail, newsletters, receipts, notifications, and FYI-only
               notes. Alfred nudges Matthew about unanswered needsReply emails, so be
               conservative — a wrong true nags him about nothing.

            8. "newsLeads": ONLY when this email is a newsletter or briefing substantially about
               AI / the software industry (an AI newsletter, a dev-tools digest, an industry
               round-up), extract the concrete news stories it mentions — Alfred feeds them to a
               separate evening AI-news briefing as candidate leads. Each lead:
               - "headline": the story in a short phrase
               - "url": the story's link if one is in the email (the article, not the
                 newsletter's own tracking/subscribe links), else null
               - "note": one short sentence of what the newsletter says about it, or null
               List the genuine news stories only — skip the newsletter's own promotions, jobs,
               sponsor slots, and tutorials. For every other kind of email, use an empty array.
               This field never affects requiresAttention: a newsletter full of leads is still
               filed quietly.

            Email Subject: {email.Subject}
            From: {email.SenderName} <{email.SenderEmail}>
            To: Matthew (scerri.matthew@gmail.com)
            Date: {email.ReceivedDate:ddd, d MMM yyyy HH:mm}

            Email Body:
            {body}
            {documentContent}

            Respond with valid JSON only, no markdown code fences.
            """;
    }

    internal static PersonalEmailTriage ParseTriageResponse(string json, SchoolEmail email)
    {
        try
        {
            json = json.Trim();
            if (json.StartsWith("```"))
            {
                var firstNewline = json.IndexOf('\n');
                if (firstNewline > 0)
                    json = json[(firstNewline + 1)..];
                if (json.EndsWith("```"))
                    json = json[..^3];
                json = json.Trim();
            }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var requiresAttention = root.TryGetProperty("requiresAttention", out var raProp)
                && raProp.ValueKind == JsonValueKind.True;

            var matchedAttentionRule = root.TryGetProperty("matchedAttentionRule", out var marProp) && marProp.ValueKind == JsonValueKind.String
                ? marProp.GetString()
                : null;

            var fraudWarning = root.TryGetProperty("fraudWarning", out var fwProp) && fwProp.ValueKind == JsonValueKind.String
                ? fwProp.GetString()
                : null;

            return new PersonalEmailTriage
            {
                // An attention rule match or a fraud warning always notifies and beats suppression
                RequiresAttention = requiresAttention || matchedAttentionRule is not null || fraudWarning is not null,
                Category = root.TryGetProperty("category", out var catProp)
                    ? catProp.GetString() ?? "other"
                    : "other",
                Summary = root.TryGetProperty("summary", out var sumProp)
                    ? sumProp.GetString() ?? email.Subject
                    : email.Subject,
                TelegramMessage = root.TryGetProperty("telegramMessage", out var tmProp)
                    ? tmProp.GetString() ?? ""
                    : "",
                CalendarEvents = ParseCalendarEvents(root),
                Suppressed = matchedAttentionRule is null && fraudWarning is null
                    && root.TryGetProperty("suppressed", out var supProp)
                    && supProp.ValueKind == JsonValueKind.True,
                MatchedRule = root.TryGetProperty("matchedRule", out var mrProp) && mrProp.ValueKind == JsonValueKind.String
                    ? mrProp.GetString()
                    : null,
                MatchedAttentionRule = matchedAttentionRule,
                FraudWarning = fraudWarning,
                NeedsReply = root.TryGetProperty("needsReply", out var nrProp)
                    && nrProp.ValueKind == JsonValueKind.True,
                NewsLeads = ParseNewsLeads(root)
            };
        }
        catch (JsonException)
        {
            // Fail open — better a redundant notification than a silently dropped invoice
            return new PersonalEmailTriage
            {
                RequiresAttention = true,
                Category = "other",
                Summary = email.Subject,
                TelegramMessage = $"📬 <b>{email.Subject}</b>\nFrom: {email.SenderName}\n\nAlfred could not summarize this email — please check Gmail directly."
            };
        }
    }

    private static List<NewsLead> ParseNewsLeads(JsonElement root)
    {
        var leads = new List<NewsLead>();
        if (!root.TryGetProperty("newsLeads", out var leadsProp) || leadsProp.ValueKind != JsonValueKind.Array)
            return leads;

        foreach (var lead in leadsProp.EnumerateArray())
        {
            var headline = lead.TryGetProperty("headline", out var hProp) && hProp.ValueKind == JsonValueKind.String
                ? hProp.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(headline))
                continue;

            leads.Add(new NewsLead
            {
                Headline = headline,
                Url = lead.TryGetProperty("url", out var uProp) && uProp.ValueKind == JsonValueKind.String
                    ? uProp.GetString()
                    : null,
                Note = lead.TryGetProperty("note", out var nProp) && nProp.ValueKind == JsonValueKind.String
                    ? nProp.GetString()
                    : null
            });
        }

        return leads;
    }

    internal static (string System, string User) BuildDigestPrompt(string todayStr, string emailSummaries, int emailCount, string eventsList, string homeworkSummary)
    {
        var systemPrompt = $"""
            You are Alfred, a personal assistant for the parents of Valentina, a Year 1 Bluebells student at Sacred Heart College Junior School (moving to Year 2 in September/October 2026).
            Today is {todayStr}.

            CRITICAL: Your digest must be COMPLETE. You must include every single calendar event, deadline, and homework item from the data provided. Do not skip or omit any items. Cross-check your output against the provided data before responding to ensure nothing is missing.

            Keep each bullet point short — but never leave out an item.


            Build an evening digest formatted using Telegram HTML with exactly 3 sections:

            🏫 <b>Good Evening!</b>
            Today's date and how many school emails were received today (e.g. "2 school emails received today" or "No school emails today").

            ━━━━━━━━━━━━━━━

            ⏰ <b>TOMORROW</b>
            Everything relevant for tomorrow in one combined section:
            - Every calendar event and deadline happening tomorrow
            - What to prepare tonight (things to bring, forms to submit, items to pack, etc.)
            - Any homework due tomorrow
            - Keep it actionable — parents should know exactly what to do tonight and what to expect tomorrow
            - Only include information from the provided data, not routine assumptions

            📅 <b>LATER</b>
            What's happening over the next 3 school days after tomorrow. For each event or deadline, include what parents need to prepare (things to bring, forms to submit, homework due, etc.).

            Rules:
            - Section headers must be UPPERCASE bold with the emoji prefix, exactly as shown above
            - Add a blank line before AND after each section header for clear visual spacing
            - Add the ━━━━━━━━━━━━━━━ separator line only once, right after the greeting
            - Use • for bullets, — for dashes
            - Only use emojis on section headers. Do NOT use emojis in bullet point content
            - If a section has no content, skip it entirely
            - Only use <b> and <a href=""> tags
            """;

        var userPrompt = $"""
            ## TODAY'S EMAILS ({emailCount} received)
            {emailSummaries}

            ## CALENDAR EVENTS
            {eventsList}

            ## HOMEWORK ASSIGNMENTS
            {homeworkSummary}

            Respond with the formatted HTML digest only.
            """;

        return (systemPrompt, userPrompt);
    }

    internal static EmailDigest ParseDigestResponse(string json)
    {
        try
        {
            // Strip markdown code fences if Claude wrapped the response
            json = json.Trim();
            if (json.StartsWith("```"))
            {
                var firstNewline = json.IndexOf('\n');
                if (firstNewline > 0)
                    json = json[(firstNewline + 1)..];
                if (json.EndsWith("```"))
                    json = json[..^3];
                json = json.Trim();
            }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var telegramMessage = root.GetProperty("telegramMessage").GetString() ?? "";

            var homework = root.TryGetProperty("homework", out var hwProp) && hwProp.ValueKind != JsonValueKind.Null
                ? hwProp.GetString()
                : null;

            var requiresImmediateAlert = root.TryGetProperty("requiresImmediateAlert", out var riaProp)
                && riaProp.ValueKind == JsonValueKind.True;

            var calendarEvents = ParseCalendarEvents(root);

            var category = root.TryGetProperty("category", out var catProp)
                ? catProp.GetString() ?? "other"
                : "other";

            return new EmailDigest
            {
                TelegramMessage = telegramMessage,
                CalendarEvents = calendarEvents,
                Homework = homework,
                RequiresImmediateAlert = requiresImmediateAlert,
                Category = category
            };
        }
        catch (JsonException)
        {
            // JSON may be truncated — try to extract telegramMessage via string search
            var message = ExtractTelegramMessageFromBrokenJson(json);
            return new EmailDigest
            {
                TelegramMessage = message,
                CalendarEvents = []
            };
        }
    }

    private static List<CalendarEventInfo> ParseCalendarEvents(JsonElement root)
    {
        var calendarEvents = new List<CalendarEventInfo>();
        if (!root.TryGetProperty("calendarEvents", out var eventsArray))
            return calendarEvents;

        foreach (var eventEl in eventsArray.EnumerateArray())
        {
            var action = eventEl.TryGetProperty("action", out var actionProp)
                ? Enum.TryParse<CalendarEventAction>(actionProp.GetString(), true, out var parsed)
                    ? parsed
                    : CalendarEventAction.Create
                : CalendarEventAction.Create;

            calendarEvents.Add(new CalendarEventInfo
            {
                Title = eventEl.GetProperty("title").GetString() ?? "",
                Description = eventEl.GetProperty("description").GetString() ?? "",
                Date = DateTime.Parse(eventEl.GetProperty("date").GetString() ?? DateTime.Today.ToString("O")),
                StartTime = eventEl.TryGetProperty("startTime", out var st) && st.ValueKind != JsonValueKind.Null
                    ? TimeSpan.Parse(st.GetString()!)
                    : null,
                EndTime = eventEl.TryGetProperty("endTime", out var et) && et.ValueKind != JsonValueKind.Null
                    ? TimeSpan.Parse(et.GetString()!)
                    : null,
                Action = action
            });
        }

        return calendarEvents;
    }

    private static string ExtractTelegramMessageFromBrokenJson(string json)
    {
        // Try to find the telegramMessage value even in truncated JSON
        const string key = "\"telegramMessage\":";
        var startIndex = json.IndexOf(key, StringComparison.Ordinal);
        if (startIndex < 0)
            return "Alfred could not parse this email summary. Please check Gmail directly.";

        startIndex += key.Length;

        // Skip whitespace and opening quote
        while (startIndex < json.Length && json[startIndex] is ' ' or '\n' or '\r' or '"')
            startIndex++;

        // Find the closing of the telegramMessage value
        // Look for the pattern  ",\n  "calendarEvents" which marks the end
        const string endMarker = "\"calendarEvents\"";
        var endIndex = json.IndexOf(endMarker, startIndex, StringComparison.Ordinal);

        string rawMessage;
        if (endIndex > 0)
        {
            // Back up past the comma and whitespace
            rawMessage = json[startIndex..endIndex].TrimEnd(' ', '\n', '\r', ',', '"');
        }
        else
        {
            // No end marker — take everything after the key, strip trailing junk
            rawMessage = json[startIndex..].TrimEnd(' ', '\n', '\r', '"', ',', '}');
        }

        // Unescape JSON string escapes
        rawMessage = rawMessage.Replace("\\n", "\n").Replace("\\\"", "\"").Replace("\\\\", "\\");

        return rawMessage;
    }
}
