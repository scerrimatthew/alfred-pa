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

    public async Task<PersonalEmailTriage> TriagePersonalEmailAsync(SchoolEmail email, List<SuppressionRuleEntity> suppressionRules)
    {
        var client = CreateClient();

        var today = DateTime.Now.ToString("dddd, d MMMM yyyy");

        var docsWithContent = email.Documents.Where(d => !string.IsNullOrEmpty(d.ExtractedText)).ToList();
        var documentContent = docsWithContent.Count > 0
            ? "\n\nAttachment Contents:\n" + string.Join("\n---\n",
                docsWithContent.Select(d => $"[{d.Title}]\n{d.ExtractedText}"))
            : "";

        var prompt = BuildTriagePrompt(email, today, documentContent, suppressionRules);

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
        List<Google.Apis.Calendar.v3.Data.Event> upcomingEvents)
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

            ## QUESTION
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

    public async Task<string> BuildPersonalDigestAsync(
        List<ProcessedEmailEntity> todaysEmails,
        List<Google.Apis.Calendar.v3.Data.Event> upcomingActions)
    {
        var client = CreateClient();

        var todayStr = DateTime.Now.ToString("dddd, d MMMM yyyy");

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

            Shape it roughly as: a one-line opener, then what's coming up (every action and
            deadline from the data with its date — flag anything due tomorrow or overdue first,
            and never skip an event), then anything from today's emails worth knowing in a
            sentence or two. When listing more than two upcoming items, compact • bullets are
            fine; otherwise keep it in prose.

            Tone example:
            "Evening! Two things on the radar: the <b>GO bill (€45.20)</b> is due <b>Wednesday</b>,
            and you've got the dentist <b>Friday at 14:00</b>. Today was quiet otherwise — just a
            delivery notice from Wolt I filed away."

            Rules:
            - Bold only the facts that matter (amounts, dates, names)
            - Only use <b> and <a href=""> tags; do not escape characters
            - Keep it glanceable — a few lines, not a report
            """;

        var userPrompt = $"""
            ## TODAY'S PERSONAL EMAILS
            {emailsList}

            ## UPCOMING ACTIONS (from the personal calendar)
            {actionsList}

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
                var link = !string.IsNullOrEmpty(e.GmailThreadId)
                    ? $" link={Gmail.GmailLinks.ForThread(e.GmailThreadId)}"
                    : "";
                return $"- id={e.RowKey} [{e.ProcessedAt:ddd d MMM yyyy}] [{e.Category ?? "other"}]{muted} {e.SenderName} — {e.Subject}: {e.Summary}{link}";
            }))
            : "No recent personal emails.";

        var personalActionsList = personalActions.Count > 0
            ? string.Join("\n", personalActions.Select(e =>
            {
                var date = e.Start.DateTimeDateTimeOffset?.ToString("ddd d MMM yyyy HH:mm") ?? e.Start.Date ?? "TBD";
                return $"- {date}: {e.Summary} — {e.Description}";
            }))
            : "No upcoming personal actions.";

        var systemPrompt = $"""
            You are Alfred, Matthew's personal assistant, chatting with him directly on Telegram.
            Today is {today}.

            You have two kinds of context:
            - SCHOOL: emails and calendar events for Valentina, a Year 1 Bluebells student at
              Sacred Heart College Junior School (moving to Year 2 in September/October 2026)
            - PERSONAL: Matthew's own inbox (invoices, appointments, deadlines) and the personal
              calendar actions Alfred created for him

            Answer ONLY what was asked, but completely — include every relevant item.
            Reply the way a human PA would text: conversational, direct, and brief. Use prose
            for simple answers; switch to compact • bullets only when listing several items.
            Do not mention "the data" or where information comes from. Just answer.
            If you genuinely don't have the information, say you're not sure.

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
            Personal emails are listed with id=... — pass that id to tools. NEVER show raw ids in replies.
            Personal emails also carry link=... — when discussing a specific email, offer it as
            <a href="link">Open in Gmail</a> so Matthew can jump straight to it.
            Emails marked [muted] were suppressed by an existing rule — mention them only if asked.
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

            ## QUESTION
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
                """))
        };

        var messages = new List<Message> { new(RoleType.User, userPrompt) };

        var parameters = new MessageParameters
        {
            Model = Anthropic.SDK.Constants.AnthropicModels.Claude46Opus,
            MaxTokens = 2048,
            System = [new SystemMessage(systemPrompt)],
            Messages = messages,
            Tools = tools
        };

        for (var iteration = 0; iteration < 5; iteration++)
        {
            var response = await client.Messages.GetClaudeMessageAsync(parameters);

            var toolUses = response.Content?.OfType<ToolUseContent>().ToList() ?? [];
            if (toolUses.Count == 0)
            {
                return response.Content?.OfType<TextContent>().FirstOrDefault()?.Text
                    ?? "Sorry, I couldn't generate an answer. Please try again.";
            }

            messages.Add(response.Message);

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

    private static AnthropicClient CreateClient()
    {
        var apiKey = Environment.GetEnvironmentVariable("Anthropic__ApiKey")
            ?? throw new InvalidOperationException("Anthropic API key not configured");

        return new AnthropicClient(apiKey);
    }

    private static string BuildSummarizePrompt(SchoolEmail email, string today, string documentContent, string linksContent)
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

    private static string BuildTriagePrompt(SchoolEmail email, string today, string documentContent, List<SuppressionRuleEntity> suppressionRules)
    {
        // Cap the body — personal inbox emails (marketing, long threads) can be huge after HTML stripping
        var body = email.Body.Length > 8000
            ? email.Body[..8000] + "\n[... truncated]"
            : email.Body;

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

            Triage the email below. Decide whether it warrants Matthew's attention.{rulesSection}

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

    private static PersonalEmailTriage ParseTriageResponse(string json, SchoolEmail email)
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

            return new PersonalEmailTriage
            {
                RequiresAttention = requiresAttention,
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
                Suppressed = root.TryGetProperty("suppressed", out var supProp)
                    && supProp.ValueKind == JsonValueKind.True,
                MatchedRule = root.TryGetProperty("matchedRule", out var mrProp) && mrProp.ValueKind == JsonValueKind.String
                    ? mrProp.GetString()
                    : null
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

    private static (string System, string User) BuildDigestPrompt(string todayStr, string emailSummaries, int emailCount, string eventsList, string homeworkSummary)
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

    private static EmailDigest ParseDigestResponse(string json)
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
