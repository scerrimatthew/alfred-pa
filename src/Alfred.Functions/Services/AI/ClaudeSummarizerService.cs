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

        var prompt = BuildDigestPrompt(todayStr, emailSummaries, eventsList, homeworkSummary);

        var parameters = new MessageParameters
        {
            Model = Anthropic.SDK.Constants.AnthropicModels.Claude45Sonnet,
            MaxTokens = 2048,
            Messages = [new Message(RoleType.User, prompt)]
        };

        var response = await client.Messages.GetClaudeMessageAsync(parameters);

        var responseText = response.Content?.OfType<TextContent>().FirstOrDefault()?.Text ?? "No digest available.";

        _logger.LogInformation("Evening digest generated: {Length} chars", responseText.Length);
        return responseText;
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
            You are Alfred, a personal assistant helping parents of Valentina, a Year 1 student at Sacred Heart College Junior School (moving to Year 2 in September/October 2026).
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

    private static string BuildDigestPrompt(string todayStr, string emailSummaries, string eventsList, string homeworkSummary)
    {
        return $"""
            You are Alfred, a personal assistant for the parents of Valentina, a Year 1 student at Sacred Heart College Junior School (moving to Year 2 in September/October 2026). Build an evening digest.
            Today is {todayStr}.

            Format using Telegram HTML with these sections:

            🏫 <b>Good Evening!</b>
            Today's date

            ━━━━━━━━━━━━━━━

            📩 <b>TODAY'S EMAILS</b>
            Summarize each email received today

            ⏰ <b>TOMORROW</b>
            What's happening tomorrow, highlight what to prepare

            📝 <b>HOMEWORK</b>
            Current homework assignments and due dates

            📅 <b>COMING UP</b>
            Upcoming weekday events (skip weekends), up to 5 school days

            🎒 <b>TO PREPARE TONIGHT</b>
            What parents need to get ready for tomorrow

            Rules:
            - Section headers must be UPPERCASE bold with the emoji prefix, exactly as shown above
            - Add a blank line before AND after each section header for clear visual spacing
            - Add the ━━━━━━━━━━━━━━━ separator line only once, right after the date
            - Use • for bullets, — for dashes
            - Only use emojis on section headers (the emoji prefix shown above). Do NOT use emojis in bullet point content
            - If a section has no content, skip it entirely
            - Only use <b> and <a href=""> tags

            Today's emails:
            {emailSummaries}

            Upcoming calendar events (next 5 school days):
            {eventsList}

            Homework assignments from recent emails:
            {homeworkSummary}

            Respond with the formatted HTML message only.
            """;
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

            var calendarEvents = new List<CalendarEventInfo>();
            if (root.TryGetProperty("calendarEvents", out var eventsArray))
            {
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
            }

            return new EmailDigest
            {
                TelegramMessage = telegramMessage,
                CalendarEvents = calendarEvents,
                Homework = homework
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
