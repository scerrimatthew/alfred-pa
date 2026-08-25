using Alfred.Functions.Configuration;
using Alfred.Functions.Models;
using Microsoft.Extensions.Options;

namespace Alfred.Functions.Tests.Support;

internal static class TestData
{
    // Default options for function tests: summer break disabled so results don't
    // depend on the calendar day the suite happens to run on. Tests that exercise
    // break behavior opt back in explicitly.
    public static IOptions<AlfredOptions> Options(Action<AlfredOptions>? mutate = null)
    {
        var options = new AlfredOptions
        {
            SummerBreakStart = "",
            SummerBreakEnd = ""
        };
        mutate?.Invoke(options);
        return Microsoft.Extensions.Options.Options.Create(options);
    }

    public static SchoolEmail Email(
        string messageId = "msg-1",
        string? threadId = null,
        string subject = "Test subject",
        string senderName = "Some Sender",
        string senderEmail = "sender@example.com",
        string body = "Hello",
        bool wasUnread = true,
        DateTimeOffset? receivedDate = null,
        string? listUnsubscribe = null,
        bool listUnsubscribeOneClick = false)
    {
        return new SchoolEmail
        {
            MessageId = messageId,
            ThreadId = threadId ?? messageId,
            Subject = subject,
            SenderName = senderName,
            SenderEmail = senderEmail,
            ReceivedDate = receivedDate ?? new DateTimeOffset(2026, 8, 10, 8, 0, 0, TimeSpan.Zero),
            Body = body,
            WasUnread = wasUnread,
            ListUnsubscribe = listUnsubscribe,
            ListUnsubscribeOneClick = listUnsubscribeOneClick
        };
    }

    public static EmailDigest Digest(
        string telegramMessage = "summary message",
        bool requiresImmediateAlert = false,
        string category = "event",
        string? homework = null,
        List<CalendarEventInfo>? calendarEvents = null)
    {
        return new EmailDigest
        {
            TelegramMessage = telegramMessage,
            RequiresImmediateAlert = requiresImmediateAlert,
            Category = category,
            Homework = homework,
            CalendarEvents = calendarEvents ?? []
        };
    }

    public static PersonalEmailTriage Triage(
        bool requiresAttention = false,
        string category = "other",
        string summary = "triage summary",
        string telegramMessage = "",
        bool suppressed = false,
        string? matchedRule = null,
        string? fraudWarning = null,
        bool needsReply = false,
        List<CalendarEventInfo>? calendarEvents = null,
        List<NewsLead>? newsLeads = null)
    {
        return new PersonalEmailTriage
        {
            RequiresAttention = requiresAttention,
            Category = category,
            Summary = summary,
            TelegramMessage = telegramMessage,
            Suppressed = suppressed,
            MatchedRule = matchedRule,
            FraudWarning = fraudWarning,
            NeedsReply = needsReply,
            CalendarEvents = calendarEvents ?? [],
            NewsLeads = newsLeads ?? []
        };
    }

    public static ProcessedEmailEntity ProcessedEmail(
        string messageId = "msg-1",
        string subject = "Test subject",
        string senderName = "Some Sender",
        string? senderEmail = "sender@example.com",
        string summary = "summary",
        string? threadId = "thread-1",
        bool needsReply = false,
        bool suppressed = false,
        DateTimeOffset? processedAt = null,
        string partition = "personal")
    {
        return new ProcessedEmailEntity
        {
            PartitionKey = partition,
            RowKey = messageId,
            Subject = subject,
            SenderName = senderName,
            SenderEmail = senderEmail,
            Summary = summary,
            GmailThreadId = threadId,
            NeedsReply = needsReply,
            Suppressed = suppressed,
            ProcessedAt = processedAt ?? DateTimeOffset.UtcNow.AddHours(-1)
        };
    }

    public static EtfHoldingEntity EtfHolding(
        string symbol = "VWCE",
        string? name = null,
        string? notes = null,
        DateTimeOffset? createdAt = null,
        string? lastQuote = null,
        double? lastWeekChangePercent = null,
        DateTimeOffset? lastReportedAt = null)
    {
        return new EtfHoldingEntity
        {
            // Mirrors the table: the row key is always the normalized ticker
            RowKey = Alfred.Functions.Services.State.TableStorageStateService.EtfKey(symbol),
            Symbol = symbol,
            Name = name,
            Notes = notes,
            CreatedAt = createdAt ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            LastQuote = lastQuote,
            LastWeekChangePercent = lastWeekChangePercent,
            LastReportedAt = lastReportedAt
        };
    }

    public static TimeZoneInfo MaltaTz => TimeZoneInfo.FindSystemTimeZoneById("Europe/Malta");
}
