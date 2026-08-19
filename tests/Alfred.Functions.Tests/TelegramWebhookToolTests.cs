using System.Text.Json;
using System.Text.Json.Nodes;
using Alfred.Functions.Configuration;
using Alfred.Functions.Functions;
using Alfred.Functions.Models;
using Alfred.Functions.Services.AI;
using Alfred.Functions.Services.Calendar;
using Alfred.Functions.Services.Gmail;
using Alfred.Functions.Services.Notifications;
using Alfred.Functions.Services.State;
using Alfred.Functions.Tests.Support;
using Google.Apis.Calendar.v3.Data;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using static Alfred.Functions.Tests.Support.TestData;

namespace Alfred.Functions.Tests;

// Exercises the chat tool executor that TelegramWebhookFunction hands to Claude.
// The delegate is captured from the AnswerPersonalQuestionAsync call — the same way
// the summarizer receives it in production — and then driven directly.
public class TelegramWebhookToolTests : IAsyncLifetime
{
    private const string Secret = "sekrit";
    private const long PersonalChatId = 777;

    private readonly IStateService _state = Substitute.For<IStateService>();
    private readonly ICalendarService _calendar = Substitute.For<ICalendarService>();
    private readonly ISummarizerService _summarizer = Substitute.For<ISummarizerService>();
    private readonly INotificationService _notifications = Substitute.For<INotificationService>();
    private readonly IGmailReaderService _gmail = Substitute.For<IGmailReaderService>();
    private readonly INewsResearchService _newsResearch = Substitute.For<INewsResearchService>();
    private readonly IAnthropicCostService _cost = Substitute.For<IAnthropicCostService>();

    private Func<string, JsonNode?, Task<string>> _executeTool = null!;

    public async Task InitializeAsync()
    {
        _state.GetEmailsSinceAsync(Arg.Any<DateTimeOffset>()).Returns(new List<ProcessedEmailEntity>());
        _state.GetPersonalEmailsSinceAsync(Arg.Any<DateTimeOffset>()).Returns(new List<ProcessedEmailEntity>());
        _state.GetRecentChatTurnsAsync(Arg.Any<long>(), Arg.Any<DateTimeOffset>(), Arg.Any<int>())
            .Returns(new List<ChatTurnEntity>());
        _state.GetReportedNewsSinceAsync(Arg.Any<DateTimeOffset>()).Returns(new List<ReportedNewsEntity>());
        _calendar.GetUpcomingEventsAsync(Arg.Any<int>()).Returns(new List<Event>());
        _calendar.GetUpcomingPersonalEventsAsync(Arg.Any<int>()).Returns(new List<Event>());

        Func<string, JsonNode?, Task<string>>? captured = null;
        _summarizer.AnswerPersonalQuestionAsync(
                Arg.Any<string>(), Arg.Any<List<ProcessedEmailEntity>>(), Arg.Any<List<Event>>(),
                Arg.Any<List<ProcessedEmailEntity>>(), Arg.Any<List<Event>>(), Arg.Any<List<ReportedNewsEntity>>(),
                Arg.Any<List<ChatTurnEntity>>(),
                Arg.Do<Func<string, JsonNode?, Task<string>>>(f => captured = f))
            .Returns("answer");

        var options = Options(o =>
        {
            o.TelegramWebhookSecret = Secret;
            o.PersonalTelegramChatId = PersonalChatId.ToString();
        });
        var function = new TelegramWebhookFunction(
            _state, _calendar, _summarizer, _notifications, _gmail, _newsResearch, _cost, options,
            NullLogger<TelegramWebhookFunction>.Instance);

        var body = JsonSerializer.Serialize(new
        {
            message = new { chat = new { id = PersonalChatId }, from = new { id = 42 }, text = "capture the executor" }
        });
        await function.RunAsync(new FakeHttpRequestData(body), Secret);

        _executeTool = captured ?? throw new InvalidOperationException("tool executor was not captured");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static JsonNode Input(object anonymous) =>
        JsonNode.Parse(JsonSerializer.Serialize(anonymous))!;

    // ---- Gmail actions ----

    [Fact]
    public async Task MarkUnread_CallsGmailAndConfirms()
    {
        var result = await _executeTool("mark_unread", Input(new { message_id = "m1" }));

        await _gmail.Received(1).MarkAsUnreadAsync("m1");
        Assert.Equal("Email marked as unread.", result);
    }

    [Fact]
    public async Task MarkUnread_WithoutId_ReturnsAnError()
    {
        var result = await _executeTool("mark_unread", Input(new { }));

        Assert.StartsWith("Error:", result);
        await _gmail.DidNotReceiveWithAnyArgs().MarkAsUnreadAsync(default!);
    }

    [Fact]
    public async Task Recategorize_SwapsTheLabelAndUpdatesState()
    {
        var result = await _executeTool("recategorize_email", Input(new { message_id = "m1", category = "payment-request" }));

        await _gmail.Received(1).RecategorizeAsync("m1", "Payment Request");
        await _state.Received(1).UpdatePersonalEmailCategoryAsync("m1", "payment-request");
        Assert.Contains("payment-request", result);
    }

    [Fact]
    public async Task Recategorize_WithoutCategory_ReturnsAnError()
    {
        var result = await _executeTool("recategorize_email", Input(new { message_id = "m1" }));

        Assert.StartsWith("Error:", result);
        await _gmail.DidNotReceiveWithAnyArgs().RecategorizeAsync(default!, default!);
    }

    // ---- Suppression rules ----

    [Fact]
    public async Task AddSuppressionRule_SavesPatternWithExamples()
    {
        var result = await _executeTool("add_suppression_rule", Input(new
        {
            pattern = "Monthly Bolt reports",
            example_sender = "reports@bolt.eu",
            example_subject = "Your July report"
        }));

        await _state.Received(1).SaveSuppressionRuleAsync(
            Arg.Is<string>(id => id.Length == 8), "Monthly Bolt reports", "reports@bolt.eu", "Your July report");
        Assert.Contains("Monthly Bolt reports", result);
    }

    [Fact]
    public async Task AddSuppressionRule_WithoutPattern_ReturnsAnError()
    {
        var result = await _executeTool("add_suppression_rule", Input(new { }));

        Assert.StartsWith("Error:", result);
    }

    [Fact]
    public async Task ListSuppressionRules_EmptyAndPopulated()
    {
        _state.GetSuppressionRulesAsync().Returns(new List<SuppressionRuleEntity>());
        Assert.Equal("No suppression rules are active.", await _executeTool("list_suppression_rules", null));

        _state.GetSuppressionRulesAsync().Returns(new List<SuppressionRuleEntity>
        {
            new() { RowKey = "r2", Pattern = "Newer rule", CreatedAt = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero) },
            new() { RowKey = "r1", Pattern = "Older rule", CreatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) }
        });
        var listing = await _executeTool("list_suppression_rules", null);

        Assert.Contains("[r1] Older rule", listing);
        Assert.Contains("[r2] Newer rule", listing);
        Assert.True(listing.IndexOf("r1", StringComparison.Ordinal) < listing.IndexOf("r2", StringComparison.Ordinal),
            "rules must be listed oldest first");
    }

    [Fact]
    public async Task RemoveSuppressionRule_DeletesById()
    {
        var result = await _executeTool("remove_suppression_rule", Input(new { rule_id = "r1" }));

        await _state.Received(1).DeleteSuppressionRuleAsync("r1");
        Assert.Contains("r1", result);
    }

    // ---- Attention rules ----

    [Fact]
    public async Task AttentionRules_AddListRemove()
    {
        await _executeTool("add_attention_rule", Input(new { pattern = "Anything from HSBC" }));
        await _state.Received(1).SaveAttentionRuleAsync(
            Arg.Is<string>(id => id.Length == 8), "Anything from HSBC", Arg.Is((string?)null), Arg.Is((string?)null));

        _state.GetAttentionRulesAsync().Returns(new List<AttentionRuleEntity>());
        Assert.Equal("No attention rules are active.", await _executeTool("list_attention_rules", null));

        var result = await _executeTool("remove_attention_rule", Input(new { rule_id = "a1" }));
        await _state.Received(1).DeleteAttentionRuleAsync("a1");
        Assert.Contains("a1", result);
    }

    // ---- News digest preferences ----

    [Fact]
    public async Task AddNewsRule_SavesTheInstructionWithAGeneratedId()
    {
        var result = await _executeTool("add_news_rule", Input(new { instruction = "Stop covering funding rounds" }));

        await _state.Received(1).SaveNewsRuleAsync(Arg.Is<string>(id => id.Length == 8), "Stop covering funding rounds");
        Assert.Contains("Stop covering funding rounds", result);
    }

    [Fact]
    public async Task AddNewsRule_WithoutInstruction_ReturnsAnError()
    {
        var result = await _executeTool("add_news_rule", Input(new { }));

        Assert.StartsWith("Error:", result);
        await _state.DidNotReceiveWithAnyArgs().SaveNewsRuleAsync(default!, default!);
    }

    [Fact]
    public async Task ListNewsRules_EmptyAndPopulated_OldestFirst()
    {
        _state.GetNewsRulesAsync().Returns(new List<NewsRuleEntity>());
        Assert.Equal("No news digest preferences are set.", await _executeTool("list_news_rules", null));

        _state.GetNewsRulesAsync().Returns(new List<NewsRuleEntity>
        {
            new() { RowKey = "n2", Instruction = "Newer preference", CreatedAt = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero) },
            new() { RowKey = "n1", Instruction = "Older preference", CreatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) }
        });
        var listing = await _executeTool("list_news_rules", null);

        Assert.Contains("[n1] Older preference", listing);
        Assert.Contains("[n2] Newer preference", listing);
        Assert.True(listing.IndexOf("n1", StringComparison.Ordinal) < listing.IndexOf("n2", StringComparison.Ordinal),
            "news preferences must be listed oldest first");
    }

    [Fact]
    public async Task RemoveNewsRule_DeletesById()
    {
        var result = await _executeTool("remove_news_rule", Input(new { rule_id = "n1" }));

        await _state.Received(1).DeleteNewsRuleAsync("n1");
        Assert.Contains("n1", result);
    }

    [Fact]
    public async Task RemoveNewsRule_WithoutId_ReturnsAnError()
    {
        var result = await _executeTool("remove_news_rule", Input(new { }));

        Assert.StartsWith("Error:", result);
        await _state.DidNotReceiveWithAnyArgs().DeleteNewsRuleAsync(default!);
    }

    // ---- Snoozes ----

    [Fact]
    public async Task SnoozeEmail_BareDate_MeansEightInTheMorningMaltaTime()
    {
        _state.GetPersonalEmailAsync("m1").Returns(ProcessedEmail(
            messageId: "m1", subject: "GO bill", senderName: "GO", summary: "pay", threadId: "t1"));

        DateTimeOffset dueAt = default;
        _state.When(s => s.SaveSnoozeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<DateTimeOffset>()))
            .Do(ci => dueAt = ci.ArgAt<DateTimeOffset>(5));

        var result = await _executeTool("snooze_email", Input(new { message_id = "m1", remind_at = "2100-01-05" }));

        await _state.Received(1).SaveSnoozeAsync("m1", "GO bill", "GO", "pay", "t1", Arg.Any<DateTimeOffset>());
        var dueMalta = TimeZoneInfo.ConvertTime(dueAt, MaltaTz);
        Assert.Equal(new DateTime(2100, 1, 5, 8, 0, 0), dueMalta.DateTime);
        Assert.Contains("Snoozed \"GO bill\"", result);
    }

    [Fact]
    public async Task SnoozeEmail_ExplicitTime_IsInterpretedAsMaltaLocalTime()
    {
        _state.GetPersonalEmailAsync("m1").Returns(ProcessedEmail(messageId: "m1"));

        DateTimeOffset dueAt = default;
        _state.When(s => s.SaveSnoozeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<DateTimeOffset>()))
            .Do(ci => dueAt = ci.ArgAt<DateTimeOffset>(5));

        await _executeTool("snooze_email", Input(new { message_id = "m1", remind_at = "2100-07-10 17:30" }));

        var dueMalta = TimeZoneInfo.ConvertTime(dueAt, MaltaTz);
        Assert.Equal(new DateTime(2100, 7, 10, 17, 30, 0), dueMalta.DateTime);
        // July is CEST: 17:30 Malta = 15:30 UTC
        Assert.Equal(new DateTime(2100, 7, 10, 15, 30, 0), dueAt.UtcDateTime);
    }

    [Fact]
    public async Task SnoozeEmail_PastTime_IsRejected()
    {
        _state.GetPersonalEmailAsync("m1").Returns(ProcessedEmail(messageId: "m1"));

        var result = await _executeTool("snooze_email", Input(new { message_id = "m1", remind_at = "2020-01-01 08:00" }));

        Assert.Equal("Error: that reminder time is already in the past.", result);
        await _state.DidNotReceiveWithAnyArgs().SaveSnoozeAsync(default!, default!, default!, default!, default, default);
    }

    [Fact]
    public async Task SnoozeEmail_GarbageTime_IsRejected()
    {
        var result = await _executeTool("snooze_email", Input(new { message_id = "m1", remind_at = "whenever" }));

        Assert.StartsWith("Error: remind_at must be", result);
    }

    [Fact]
    public async Task SnoozeEmail_UnknownEverywhere_ReturnsNotFound()
    {
        _state.GetPersonalEmailAsync("m1").Returns((ProcessedEmailEntity?)null);
        _gmail.GetEmailAsync("m1").Returns((SchoolEmail?)null);

        var result = await _executeTool("snooze_email", Input(new { message_id = "m1", remind_at = "2100-01-05 09:00" }));

        Assert.Equal("Error: email not found.", result);
    }

    [Fact]
    public async Task ListSnoozes_ShowsMaltaDueTimes()
    {
        _state.GetSnoozesAsync().Returns(new List<SnoozedEmailEntity>());
        Assert.Equal("No reminders are pending.", await _executeTool("list_snoozes", null));

        _state.GetSnoozesAsync().Returns(new List<SnoozedEmailEntity>
        {
            new()
            {
                RowKey = "m1",
                Subject = "GO bill",
                SenderName = "GO",
                // 06:00 UTC in July = 08:00 Malta (CEST)
                DueAt = new DateTimeOffset(2100, 7, 10, 6, 0, 0, TimeSpan.Zero)
            }
        });
        var listing = await _executeTool("list_snoozes", null);

        Assert.Contains("id=m1", listing);
        Assert.Contains("08:00", listing);
        Assert.Contains("GO — GO bill", listing);
    }

    [Fact]
    public async Task CancelSnooze_Deletes()
    {
        var result = await _executeTool("cancel_snooze", Input(new { message_id = "m1" }));

        await _state.Received(1).DeleteSnoozeAsync("m1");
        Assert.Equal("Reminder cancelled.", result);
    }

    // ---- Drafts ----

    [Fact]
    public async Task DraftReply_DelegatesToGmailAndNeverSends()
    {
        _gmail.CreateReplyDraftAsync("m1", "Sounds good, Thursday works.", false)
            .Returns("Draft reply to X saved in Gmail Drafts");

        var result = await _executeTool("draft_reply", Input(new { message_id = "m1", body = "Sounds good, Thursday works." }));

        Assert.Equal("Draft reply to X saved in Gmail Drafts", result);
    }

    [Fact]
    public async Task DraftReply_ReplyAllFlag_IsPassedThrough()
    {
        _gmail.CreateReplyDraftAsync("m1", "b", true).Returns("ok");

        await _executeTool("draft_reply", Input(new { message_id = "m1", body = "b", reply_all = true }));

        await _gmail.Received(1).CreateReplyDraftAsync("m1", "b", true);
    }

    [Fact]
    public async Task DraftReply_WithoutBody_ReturnsAnError()
    {
        var result = await _executeTool("draft_reply", Input(new { message_id = "m1" }));

        Assert.StartsWith("Error:", result);
        await _gmail.DidNotReceiveWithAnyArgs().CreateReplyDraftAsync(default!, default!, default);
    }

    // ---- Calendar tools ----

    [Fact]
    public async Task CreateCalendarEvent_AllDayByDefault()
    {
        var result = await _executeTool("create_calendar_event", Input(new
        {
            title = "Pay Aeris invoice",
            date = "2026-09-30",
            description = "€120, ref 0005713"
        }));

        await _calendar.Received(1).CreatePersonalEventAsync(
            "Pay Aeris invoice", new DateTime(2026, 9, 30), null, null, "€120, ref 0005713");
        Assert.Contains("Pay Aeris invoice", result);
    }

    [Fact]
    public async Task CreateCalendarEvent_WithTimes_PassesThemAlong()
    {
        await _executeTool("create_calendar_event", Input(new
        {
            title = "Dentist",
            date = "2026-09-30",
            start_time = "09:00",
            end_time = "09:45"
        }));

        await _calendar.Received(1).CreatePersonalEventAsync(
            "Dentist", new DateTime(2026, 9, 30), new TimeSpan(9, 0, 0), new TimeSpan(9, 45, 0), null);
    }

    [Fact]
    public async Task CreateCalendarEvent_MissingTitleOrDate_ReturnsErrors()
    {
        Assert.StartsWith("Error: no title", await _executeTool("create_calendar_event", Input(new { date = "2026-09-30" })));
        Assert.StartsWith("Error: a valid date", await _executeTool("create_calendar_event", Input(new { title = "X" })));
        await _calendar.DidNotReceiveWithAnyArgs().CreatePersonalEventAsync(default!, default, default, default, default);
    }

    [Fact]
    public async Task UpdateCalendarEvent_PassesOnlyTheChangedFields()
    {
        _calendar.UpdatePersonalEventAsync("ev1", null, new DateTime(2026, 10, 2), null, null, null)
            .Returns("Dentist");

        var result = await _executeTool("update_calendar_event", Input(new { event_id = "ev1", date = "2026-10-02" }));

        Assert.Equal("Updated calendar event: Dentist.", result);
    }

    [Fact]
    public async Task UpdateCalendarEvent_WithoutId_ReturnsAnError()
    {
        Assert.StartsWith("Error:", await _executeTool("update_calendar_event", Input(new { title = "X" })));
    }

    [Fact]
    public async Task DeleteCalendarEvent_ReturnsTheDeletedTitle()
    {
        _calendar.DeletePersonalEventAsync("ev1").Returns("GO bill reminder");

        var result = await _executeTool("delete_calendar_event", Input(new { event_id = "ev1" }));

        Assert.Equal("Deleted calendar event: GO bill reminder.", result);
    }

    // ---- Inbox search and read ----

    [Fact]
    public async Task SearchInbox_FormatsHitsWithIdsAndLinks()
    {
        _gmail.SearchInboxAsync("from:go.com.mt", 10).Returns(new List<InboxSearchResult>
        {
            new()
            {
                MessageId = "m1",
                ThreadId = "t1",
                Subject = "August bill",
                SenderName = "GO",
                SenderEmail = "billing@go.com.mt",
                ReceivedDate = new DateTimeOffset(2026, 8, 3, 10, 0, 0, TimeSpan.Zero),
                Snippet = "Your bill is ready"
            }
        });

        var result = await _executeTool("search_inbox", Input(new { query = "from:go.com.mt" }));

        Assert.Contains("id=m1", result);
        Assert.Contains("GO — August bill", result);
        Assert.Contains("Your bill is ready", result);
        Assert.Contains(GmailLinks.ForThread("t1"), result);
    }

    [Fact]
    public async Task SearchInbox_NoHits_SaysSo()
    {
        _gmail.SearchInboxAsync("nothing", 10).Returns(new List<InboxSearchResult>());

        Assert.Equal("No emails matched that query.", await _executeTool("search_inbox", Input(new { query = "nothing" })));
    }

    [Fact]
    public async Task SearchInbox_HonorsMaxResults()
    {
        _gmail.SearchInboxAsync("q", 5).Returns(new List<InboxSearchResult>());

        await _executeTool("search_inbox", Input(new { query = "q", max_results = 5 }));

        await _gmail.Received(1).SearchInboxAsync("q", 5);
    }

    [Fact]
    public async Task ReadEmail_TruncatesLongBodiesAndAppendsAttachmentText()
    {
        var email = Email(messageId: "m1", threadId: "t1", subject: "Contract", body: new string('b', 4100));
        email.Documents.Add(new LinkedDocument
        {
            Title = "contract.pdf",
            Url = "attachment:contract.pdf",
            Source = LinkedDocumentSource.EmailAttachment,
            ExtractedText = new string('p', 3100)
        });
        email.Documents.Add(new LinkedDocument
        {
            Title = "empty.pdf",
            Url = "attachment:empty.pdf",
            Source = LinkedDocumentSource.EmailAttachment,
            ExtractedText = ""
        });
        _gmail.GetEmailAsync("m1").Returns(email);

        var result = await _executeTool("read_email", Input(new { message_id = "m1" }));

        Assert.Contains("Subject: Contract", result);
        Assert.Contains(new string('b', 4000) + "…", result);
        Assert.DoesNotContain(new string('b', 4001), result);
        Assert.Contains("--- ATTACHMENT: contract.pdf ---", result);
        Assert.Contains(new string('p', 3000) + "…", result);
        Assert.DoesNotContain("empty.pdf", result); // attachments without text are omitted
        Assert.Contains(GmailLinks.ForThread("t1"), result);
    }

    [Fact]
    public async Task ReadEmail_NotFound_ReturnsAnError()
    {
        _gmail.GetEmailAsync("gone").Returns((SchoolEmail?)null);

        Assert.Equal("Error: email not found.", await _executeTool("read_email", Input(new { message_id = "gone" })));
    }

    [Fact]
    public async Task UnknownTool_ReturnsAnError()
    {
        Assert.Equal("Error: unknown tool make_coffee.", await _executeTool("make_coffee", null));
    }
}
