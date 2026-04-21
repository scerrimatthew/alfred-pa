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

        var attachmentContent = email.PdfAttachments.Count > 0
            ? "\n\nPDF Attachment Content:\n" + string.Join("\n---\n",
                email.PdfAttachments.Select(a => $"[{a.FileName}]\n{a.ExtractedText}"))
            : "";

        var today = DateTime.Now.ToString("dddd, d MMMM yyyy");

        var prompt = BuildSummarizePrompt(email, today, attachmentContent);

        var parameters = new MessageParameters
        {
            Model = Anthropic.SDK.Constants.AnthropicModels.Claude45Sonnet,
            MaxTokens = 1024,
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

        var prompt = BuildDigestPrompt(todayStr, emailSummaries, eventsList);

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

    private static string BuildSummarizePrompt(SchoolEmail email, string today, string attachmentContent)
    {
        return $"""
            You are Alfred, a personal assistant helping parents stay on top of their children's school communications.
            Today is {today}.

            Analyze this school email and produce a JSON response with two fields:

            1. "telegramMessage": A concise Telegram message (MarkdownV2 format) that includes:
               - School name header with the school emoji
               - Email subject with the envelope emoji
               - Sender name
               - 2-3 sentence summary
               - Action items (things parents need to do/bring/sign) with the lightning emoji header
               - Any calendar events created with the calendar emoji

            2. "calendarEvents": An array of events to add to the calendar, each with:
               - "title": event name
               - "description": brief description
               - "date": ISO date string (yyyy-MM-dd)
               - "startTime": HH:mm or null for all-day events
               - "endTime": HH:mm or null for all-day events
               - "action": "create", "update", or "delete"

            Important Telegram MarkdownV2 rules:
            - Escape special characters per MarkdownV2 rules
            - Use *bold* for headers
            - Use bullet points with bullet character

            Email Subject: {email.Subject}
            From: {email.SenderName}
            Date: {email.ReceivedDate:ddd, d MMM yyyy HH:mm}

            Email Body:
            {email.Body}
            {attachmentContent}

            Respond with valid JSON only, no markdown code fences.
            """;
    }

    private static string BuildDigestPrompt(string todayStr, string emailSummaries, string eventsList)
    {
        return $"""
            You are Alfred, a personal assistant. Build an evening digest for a parent.
            Today is {todayStr}.

            Format a Telegram message (MarkdownV2) with these sections:

            1. Header: *Alfred — Evening Digest* and today's date
            2. *Today's emails* — summarize each email received today
            3. *Tomorrow* — what's happening tomorrow (highlight action items)
            4. *Coming up (next 5 school days)* — upcoming weekday events, skip weekends
            5. *Action items for tomorrow* — what parents need to prepare tonight

            Use section separators.
            If a section has no content, say "Nothing scheduled" or similar.

            Today's emails:
            {emailSummaries}

            Upcoming calendar events (next 5 school days):
            {eventsList}

            Important: Escape MarkdownV2 special characters. Respond with the formatted message only.
            """;
    }

    private static EmailDigest ParseDigestResponse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var telegramMessage = root.GetProperty("telegramMessage").GetString() ?? "";

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
                CalendarEvents = calendarEvents
            };
        }
        catch (JsonException)
        {
            return new EmailDigest
            {
                TelegramMessage = json,
                CalendarEvents = []
            };
        }
    }
}
