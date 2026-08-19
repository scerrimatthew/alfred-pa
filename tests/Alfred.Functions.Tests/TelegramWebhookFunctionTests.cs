using System.Net;
using System.Text.Json;
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
using NSubstitute.ExceptionExtensions;
using Xunit;
using static Alfred.Functions.Tests.Support.TestData;

namespace Alfred.Functions.Tests;

public class TelegramWebhookFunctionTests
{
    private const string Secret = "sekrit";
    private const long PersonalChatId = 777;
    private const long SchoolChatId = 555;
    private const long UserId = 42;

    private readonly IStateService _state = Substitute.For<IStateService>();
    private readonly ICalendarService _calendar = Substitute.For<ICalendarService>();
    private readonly ISummarizerService _summarizer = Substitute.For<ISummarizerService>();
    private readonly INotificationService _notifications = Substitute.For<INotificationService>();
    private readonly IGmailReaderService _gmail = Substitute.For<IGmailReaderService>();

    public TelegramWebhookFunctionTests()
    {
        _state.GetEmailsSinceAsync(Arg.Any<DateTimeOffset>()).Returns(new List<ProcessedEmailEntity>());
        _state.GetPersonalEmailsSinceAsync(Arg.Any<DateTimeOffset>()).Returns(new List<ProcessedEmailEntity>());
        _state.GetRecentChatTurnsAsync(Arg.Any<long>(), Arg.Any<DateTimeOffset>(), Arg.Any<int>())
            .Returns(new List<ChatTurnEntity>());
        _state.GetBackfillStateAsync().Returns((BackfillStateEntity?)null);
        _calendar.GetUpcomingEventsAsync(Arg.Any<int>()).Returns(new List<Event>());
        _calendar.GetUpcomingPersonalEventsAsync(Arg.Any<int>()).Returns(new List<Event>());
        _summarizer.AnswerQuestionAsync(Arg.Any<string>(), Arg.Any<List<ProcessedEmailEntity>>(), Arg.Any<List<Event>>(), Arg.Any<List<ChatTurnEntity>>())
            .Returns("school answer");
        _summarizer.AnswerPersonalQuestionAsync(
                Arg.Any<string>(), Arg.Any<List<ProcessedEmailEntity>>(), Arg.Any<List<Event>>(),
                Arg.Any<List<ProcessedEmailEntity>>(), Arg.Any<List<Event>>(), Arg.Any<List<ChatTurnEntity>>(),
                Arg.Any<Func<string, System.Text.Json.Nodes.JsonNode?, Task<string>>>())
            .Returns("personal answer");
    }

    private TelegramWebhookFunction CreateFunction(Action<AlfredOptions>? mutate = null) =>
        new(_state, _calendar, _summarizer, _notifications, _gmail,
            Options(o =>
            {
                o.TelegramWebhookSecret = Secret;
                o.PersonalTelegramChatId = PersonalChatId.ToString();
                mutate?.Invoke(o);
            }),
            NullLogger<TelegramWebhookFunction>.Instance);

    private static string MessageUpdate(long chatId, long userId, string text) =>
        JsonSerializer.Serialize(new { message = new { chat = new { id = chatId }, from = new { id = userId }, text } });

    private static string CallbackUpdate(string id, long userId, string? data) =>
        JsonSerializer.Serialize(new { callback_query = new { id, from = new { id = userId }, data } });

    private Task<HttpStatusCode> RunAsync(string body, string secret = Secret, Action<AlfredOptions>? mutate = null) =>
        RunAsync(CreateFunction(mutate), body, secret);

    private static async Task<HttpStatusCode> RunAsync(TelegramWebhookFunction function, string body, string secret = Secret)
    {
        var response = await function.RunAsync(new FakeHttpRequestData(body), secret);
        return response.StatusCode;
    }

    // ---- Request plumbing ----

    [Fact]
    public async Task WrongSecret_IsRejectedWith401()
    {
        var status = await RunAsync(MessageUpdate(SchoolChatId, UserId, "hi"), secret: "wrong");

        Assert.Equal(HttpStatusCode.Unauthorized, status);
        await _notifications.DidNotReceiveWithAnyArgs().SendMessageAsync(default, default!);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("""{"message":{"chat":{"id":1}}}""")] // no text
    [InlineData("""{"message":{"chat":{"id":1},"from":{"id":2},"text":"   "}}""")] // blank text
    public async Task UnusableUpdates_AreAcknowledgedWith200(string body)
    {
        var status = await RunAsync(body);

        Assert.Equal(HttpStatusCode.OK, status);
        await _summarizer.DidNotReceiveWithAnyArgs().AnswerQuestionAsync(default!, default!, default!, default!);
        await _notifications.DidNotReceiveWithAnyArgs().SendMessageAsync(default, default!);
    }

    // ---- Authorization ----

    [Fact]
    public async Task UnknownUser_GetsRefusedWhenAllowlistIsSet()
    {
        var status = await RunAsync(
            MessageUpdate(SchoolChatId, 999, "hi"),
            mutate: o => o.AllowedTelegramUserIds = "42, 43");

        Assert.Equal(HttpStatusCode.OK, status);
        await _notifications.Received(1).SendMessageAsync(SchoolChatId, "Sorry, you're not authorized to use this bot.");
        await _summarizer.DidNotReceiveWithAnyArgs().AnswerQuestionAsync(default!, default!, default!, default!);
    }

    [Fact]
    public async Task ListedUser_IsAllowedThrough()
    {
        await RunAsync(
            MessageUpdate(SchoolChatId, 43, "hi"),
            mutate: o => o.AllowedTelegramUserIds = "42, 43");

        await _notifications.Received(1).SendMessageAsync(SchoolChatId, "school answer");
    }

    [Fact]
    public async Task EmptyAllowlist_AllowsEveryone()
    {
        await RunAsync(MessageUpdate(SchoolChatId, 12345, "hi"));

        await _notifications.Received(1).SendMessageAsync(SchoolChatId, "school answer");
    }

    // ---- Q&A routing ----

    [Fact]
    public async Task SchoolChatQuestion_UsesTheSchoolOnlyPath()
    {
        await RunAsync(MessageUpdate(SchoolChatId, UserId, "what's on tomorrow?"));

        await _summarizer.Received(1).AnswerQuestionAsync(
            "what's on tomorrow?", Arg.Any<List<ProcessedEmailEntity>>(), Arg.Any<List<Event>>(), Arg.Any<List<ChatTurnEntity>>());
        await _summarizer.DidNotReceiveWithAnyArgs().AnswerPersonalQuestionAsync(
            default!, default!, default!, default!, default!, default!, default!);
        await _notifications.Received(1).SendMessageAsync(SchoolChatId, "school answer");
        await _state.Received(1).SaveChatTurnAsync(SchoolChatId, "what's on tomorrow?", "school answer");
    }

    [Fact]
    public async Task PersonalChatQuestion_UsesThePersonalPathWithTools()
    {
        await RunAsync(MessageUpdate(PersonalChatId, UserId, "any bills due?"));

        await _summarizer.Received(1).AnswerPersonalQuestionAsync(
            "any bills due?",
            Arg.Any<List<ProcessedEmailEntity>>(), Arg.Any<List<Event>>(),
            Arg.Any<List<ProcessedEmailEntity>>(), Arg.Any<List<Event>>(),
            Arg.Any<List<ChatTurnEntity>>(),
            Arg.Any<Func<string, System.Text.Json.Nodes.JsonNode?, Task<string>>>());
        await _notifications.Received(1).SendMessageAsync(PersonalChatId, "personal answer");
    }

    [Fact]
    public async Task SavedChatTurn_IsStrippedOfHtmlAndCapped()
    {
        var longTail = new string('x', 800);
        _summarizer.AnswerQuestionAsync(Arg.Any<string>(), Arg.Any<List<ProcessedEmailEntity>>(), Arg.Any<List<Event>>(), Arg.Any<List<ChatTurnEntity>>())
            .Returns($"<b>Bold</b> and <a href=\"u\">link</a> {longTail}");

        string? saved = null;
        _state.When(s => s.SaveChatTurnAsync(Arg.Any<long>(), Arg.Any<string>(), Arg.Any<string>()))
            .Do(ci => saved = ci.ArgAt<string>(2));

        await RunAsync(MessageUpdate(SchoolChatId, UserId, "q"));

        Assert.NotNull(saved);
        Assert.StartsWith("Bold and link x", saved);
        Assert.DoesNotContain("<b>", saved);
        Assert.Equal(701, saved.Length); // 700 chars + ellipsis
        Assert.EndsWith("…", saved);
    }

    [Fact]
    public async Task HistorySaveFailure_DoesNotBreakTheReply()
    {
        _state.SaveChatTurnAsync(Arg.Any<long>(), Arg.Any<string>(), Arg.Any<string>())
            .ThrowsAsync(new TimeoutException("tables down"));

        var status = await RunAsync(MessageUpdate(SchoolChatId, UserId, "q"));

        Assert.Equal(HttpStatusCode.OK, status);
        await _notifications.Received(1).SendMessageAsync(SchoolChatId, "school answer");
    }

    [Theory]
    [InlineData("/new")]
    [InlineData("/reset")]
    [InlineData("/NEW")]
    public async Task NewCommand_ClearsConversationMemory(string command)
    {
        await RunAsync(MessageUpdate(SchoolChatId, UserId, command));

        await _state.Received(1).ClearChatTurnsAsync(SchoolChatId);
        await _notifications.Received(1).SendMessageAsync(SchoolChatId, Arg.Is<string>(m => m.Contains("Fresh start")));
        await _summarizer.DidNotReceiveWithAnyArgs().AnswerQuestionAsync(default!, default!, default!, default!);
    }

    // ---- /backfill command ----

    [Fact]
    public async Task BackfillStatus_WithoutARunningSweep_SaysSo()
    {
        await RunAsync(MessageUpdate(PersonalChatId, UserId, "/backfill status"));

        await _notifications.Received(1).SendMessageAsync(PersonalChatId, "No backfill is running.");
    }

    [Fact]
    public async Task BackfillStatus_WithARunningSweep_ReportsProgress()
    {
        _state.GetBackfillStateAsync().Returns(new BackfillStateEntity
        {
            OldestDate = new DateTimeOffset(2026, 6, 20, 0, 0, 0, TimeSpan.Zero),
            ProcessedCount = 123
        });

        await RunAsync(MessageUpdate(PersonalChatId, UserId, "/backfill status"));

        // Date formatting is culture-sensitive, so pin the stable parts only
        await _notifications.Received(1).SendMessageAsync(
            PersonalChatId, Arg.Is<string>(m => m.Contains("Backfill running") && m.Contains("123 emails filed") && m.Contains("2026")));
    }

    [Fact]
    public async Task BackfillCancel_ClearsTheMarker()
    {
        await RunAsync(MessageUpdate(PersonalChatId, UserId, "/backfill cancel"));

        await _state.Received(1).ClearBackfillStateAsync();
        await _notifications.Received(1).SendMessageAsync(PersonalChatId, Arg.Is<string>(m => m.Contains("cancelled")));
    }

    [Theory]
    [InlineData("/backfill abc")]
    [InlineData("/backfill 0")]
    [InlineData("/backfill 366")]
    [InlineData("/backfill -5")]
    public async Task BackfillWithBadArgument_ShowsUsage(string command)
    {
        await RunAsync(MessageUpdate(PersonalChatId, UserId, command));

        await _state.DidNotReceiveWithAnyArgs().SaveBackfillStateAsync(default!);
        await _notifications.Received(1).SendMessageAsync(PersonalChatId, Arg.Is<string>(m => m.StartsWith("Usage:")));
    }

    [Fact]
    public async Task BackfillWithDays_StartsAWindowThatFarBack()
    {
        BackfillStateEntity? saved = null;
        _state.When(s => s.SaveBackfillStateAsync(Arg.Any<BackfillStateEntity>()))
            .Do(ci => saved = ci.Arg<BackfillStateEntity>());

        await RunAsync(MessageUpdate(PersonalChatId, UserId, "/backfill 30"));

        Assert.NotNull(saved);
        var expectedOldest = DateTimeOffset.UtcNow.AddDays(-30);
        Assert.True(Math.Abs((saved.OldestDate - expectedOldest).TotalMinutes) < 5,
            $"OldestDate {saved.OldestDate} should be ~30 days ago");
        Assert.Equal(0, saved.ProcessedCount);
        await _notifications.Received(1).SendMessageAsync(PersonalChatId, Arg.Is<string>(m => m.Contains("<b>30 days</b>")));
    }

    [Fact]
    public async Task BackfillReRequest_KeepsCreditForWorkAlreadyDone()
    {
        _state.GetBackfillStateAsync().Returns(new BackfillStateEntity { ProcessedCount = 55 });

        BackfillStateEntity? saved = null;
        _state.When(s => s.SaveBackfillStateAsync(Arg.Any<BackfillStateEntity>()))
            .Do(ci => saved = ci.Arg<BackfillStateEntity>());

        await RunAsync(MessageUpdate(PersonalChatId, UserId, "/backfill"));

        Assert.NotNull(saved);
        Assert.Equal(55, saved.ProcessedCount);
        // Default window is 60 days
        var expectedOldest = DateTimeOffset.UtcNow.AddDays(-60);
        Assert.True(Math.Abs((saved.OldestDate - expectedOldest).TotalMinutes) < 5);
    }

    [Fact]
    public async Task BackfillFromTheSchoolChat_IsTreatedAsAnOrdinaryQuestion()
    {
        await RunAsync(MessageUpdate(SchoolChatId, UserId, "/backfill 30"));

        await _state.DidNotReceiveWithAnyArgs().SaveBackfillStateAsync(default!);
        await _summarizer.Received(1).AnswerQuestionAsync(
            "/backfill 30", Arg.Any<List<ProcessedEmailEntity>>(), Arg.Any<List<Event>>(), Arg.Any<List<ChatTurnEntity>>());
    }

    // ---- /evolve command ----

    [Fact]
    public async Task EvolveWithoutInstruction_AsksForOne()
    {
        await RunAsync(MessageUpdate(PersonalChatId, UserId, "/evolve"));

        await _notifications.Received(1).SendMessageAsync(PersonalChatId, Arg.Is<string>(m => m.Contains("Tell me what to change")));
    }

    [Fact]
    public async Task EvolveWithoutGitHubToken_ExplainsTheMissingConfig()
    {
        var original = Environment.GetEnvironmentVariable("GitHub__Token");
        try
        {
            Environment.SetEnvironmentVariable("GitHub__Token", null);

            await RunAsync(MessageUpdate(PersonalChatId, UserId, "/evolve add a pony"));

            await _notifications.Received(1).SendMessageAsync(PersonalChatId, Arg.Is<string>(m => m.Contains("GitHub__Token")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GitHub__Token", original);
        }
    }

    // ---- Callback queries (inline buttons) ----

    [Fact]
    public async Task Callback_MarkUnread_RestoresTheEmailAndConfirms()
    {
        await RunAsync(CallbackUpdate("cb1", UserId, "mu:m1"));

        await _gmail.Received(1).MarkAsUnreadAsync("m1");
        await _notifications.Received(1).AnswerCallbackAsync("cb1", "Marked unread — it's back in your inbox.");
    }

    [Fact]
    public async Task Callback_FromUnauthorizedUser_IsRefused()
    {
        var status = await RunAsync(
            CallbackUpdate("cb1", 999, "mu:m1"),
            mutate: o => o.AllowedTelegramUserIds = "42");

        Assert.Equal(HttpStatusCode.OK, status);
        await _gmail.DidNotReceiveWithAnyArgs().MarkAsUnreadAsync(default!);
        await _notifications.Received(1).AnswerCallbackAsync("cb1", "Not authorized.");
    }

    [Fact]
    public async Task Callback_WithoutData_IsAcknowledgedQuietly()
    {
        await RunAsync(CallbackUpdate("cb1", UserId, null));

        await _notifications.Received(1).AnswerCallbackAsync("cb1", null);
    }

    [Fact]
    public async Task Callback_ActionFailure_AnswersWithAFriendlyError()
    {
        _gmail.MarkAsUnreadAsync("m1").ThrowsAsync(new HttpRequestException("gmail down"));

        await RunAsync(CallbackUpdate("cb1", UserId, "mu:m1"));

        await _notifications.Received(1).AnswerCallbackAsync("cb1", "Sorry, that didn't work — try asking me in chat.");
    }

    [Fact]
    public async Task Callback_UnknownAction_SaysTheButtonExpired()
    {
        await RunAsync(CallbackUpdate("cb1", UserId, "zzz:1"));

        await _notifications.Received(1).AnswerCallbackAsync("cb1", "I don't recognize that button anymore.");
    }

    [Fact]
    public async Task Callback_MuteSender_CreatesASuppressionRuleForTheSender()
    {
        _state.GetPersonalEmailAsync("m1").Returns(ProcessedEmail(
            messageId: "m1", subject: "Report July", senderName: "Bolt", senderEmail: "reports@bolt.eu"));

        await RunAsync(CallbackUpdate("cb1", UserId, "sup:m1"));

        await _state.Received(1).SaveSuppressionRuleAsync(
            Arg.Is<string>(id => id.Length == 8),
            "All emails from reports@bolt.eu",
            "reports@bolt.eu",
            "Report July");
        await _notifications.Received(1).AnswerCallbackAsync("cb1", Arg.Is<string>(m => m.Contains("Muted") && m.Contains("Bolt")));
    }

    [Fact]
    public async Task Callback_MuteSender_UnknownEmail_Apologizes()
    {
        _state.GetPersonalEmailAsync("m1").Returns((ProcessedEmailEntity?)null);

        await RunAsync(CallbackUpdate("cb1", UserId, "sup:m1"));

        await _state.DidNotReceiveWithAnyArgs().SaveSuppressionRuleAsync(default!, default!, default, default);
        await _notifications.Received(1).AnswerCallbackAsync("cb1", Arg.Is<string>(m => m.Contains("can't find")));
    }

    [Fact]
    public async Task Callback_SnoozeTomorrow_SchedulesForEightInTheMorningMaltaTime()
    {
        _state.GetPersonalEmailAsync("m1").Returns(ProcessedEmail(
            messageId: "m1", subject: "GO bill", senderName: "GO", summary: "pay it", threadId: "t1"));

        DateTimeOffset dueAt = default;
        _state.When(s => s.SaveSnoozeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<DateTimeOffset>()))
            .Do(ci => dueAt = ci.ArgAt<DateTimeOffset>(5));

        var beforeMaltaDate = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, MaltaTz).Date;
        await RunAsync(CallbackUpdate("cb1", UserId, "sn1:m1"));
        var afterMaltaDate = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, MaltaTz).Date;

        await _state.Received(1).SaveSnoozeAsync("m1", "GO bill", "GO", "pay it", "t1", Arg.Any<DateTimeOffset>());
        var dueMalta = TimeZoneInfo.ConvertTime(dueAt, MaltaTz);
        Assert.Equal(new TimeSpan(8, 0, 0), dueMalta.TimeOfDay);
        Assert.True(dueMalta.Date == beforeMaltaDate.AddDays(1) || dueMalta.Date == afterMaltaDate.AddDays(1),
            "snooze must land on tomorrow morning, Malta time");
        await _notifications.Received(1).AnswerCallbackAsync("cb1", Arg.Is<string>(m => m.Contains("Snoozed")));
    }

    [Fact]
    public async Task Callback_SnoozeUntriagedEmail_FallsBackToGmail()
    {
        _state.GetPersonalEmailAsync("m9").Returns((ProcessedEmailEntity?)null);
        _gmail.GetEmailAsync("m9").Returns(Email(messageId: "m9", threadId: "t9", subject: "Old one", senderName: "Old Sender"));

        await RunAsync(CallbackUpdate("cb1", UserId, "sn1:m9"));

        await _state.Received(1).SaveSnoozeAsync("m9", "Old one", "Old Sender", "", "t9", Arg.Any<DateTimeOffset>());
    }

    [Fact]
    public async Task Callback_SnoozeVanishedEmail_Apologizes()
    {
        _state.GetPersonalEmailAsync("gone").Returns((ProcessedEmailEntity?)null);
        _gmail.GetEmailAsync("gone").Returns((SchoolEmail?)null);

        await RunAsync(CallbackUpdate("cb1", UserId, "sn1:gone"));

        await _state.DidNotReceiveWithAnyArgs().SaveSnoozeAsync(default!, default!, default!, default!, default, default);
        await _notifications.Received(1).AnswerCallbackAsync("cb1", "I can't find that email anymore.");
    }

    // ---- Unsubscribe callbacks (List-Unsubscribe parsing) ----

    private SenderStatsEntity SetUpSenderStat(string? listUnsubscribe, bool unsubscribed = false)
    {
        var stats = new SenderStatsEntity
        {
            RowKey = "s1",
            SenderName = "Shop News",
            SenderEmail = "news@shop.com",
            ListUnsubscribe = listUnsubscribe,
            ListUnsubscribeOneClick = false,
            Unsubscribed = unsubscribed
        };
        _state.GetSenderStatAsync("s1").Returns(stats);
        return stats;
    }

    [Fact]
    public async Task Unsubscribe_MailtoTarget_SendsTheUnsubscribeEmail()
    {
        var stats = SetUpSenderStat("<mailto:unsub@shop.com?subject=stop-mailing>");

        await RunAsync(CallbackUpdate("cb1", UserId, "unsub:s1"));

        await _gmail.Received(1).SendUnsubscribeEmailAsync("unsub@shop.com", "stop-mailing");
        Assert.True(stats.Unsubscribed);
        await _state.Received(1).UpsertSenderStatAsync(stats);
        await _notifications.Received(1).AnswerCallbackAsync("cb1", Arg.Is<string>(m => m.Contains("sent the unsubscribe email")));
    }

    [Fact]
    public async Task Unsubscribe_MailtoWinsOverPlainHttpLink()
    {
        SetUpSenderStat("<https://shop.com/unsub>, <mailto:unsub@shop.com>");

        await RunAsync(CallbackUpdate("cb1", UserId, "unsub:s1"));

        await _gmail.Received(1).SendUnsubscribeEmailAsync("unsub@shop.com", null);
        await _notifications.DidNotReceiveWithAnyArgs().SendPersonalAlertAsync(default!);
    }

    [Fact]
    public async Task Unsubscribe_PlainHttpLink_IsHandedToMatthew()
    {
        var stats = SetUpSenderStat("<https://shop.com/unsub?u=1>");

        await RunAsync(CallbackUpdate("cb1", UserId, "unsub:s1"));

        await _gmail.DidNotReceiveWithAnyArgs().SendUnsubscribeEmailAsync(default!, default);
        await _notifications.Received(1).SendPersonalAlertAsync(
            Arg.Is<string>(m => m.Contains("https://shop.com/unsub?u=1") && m.Contains("Shop News")));
        Assert.True(stats.Unsubscribed);
        await _notifications.Received(1).AnswerCallbackAsync("cb1", Arg.Is<string>(m => m.Contains("sent you the link")));
    }

    [Fact]
    public async Task Unsubscribe_OneClick_PostsTheRfc8058FormAndConfirms()
    {
        // RFC 8058: a single POST with body "List-Unsubscribe=One-Click", no interaction.
        // The webhook news up its own HttpClient, so a loopback server plays the list host.
        using var server = new LoopbackHttpServer();
        var stats = SetUpSenderStat($"<{server.Url}>");
        stats.ListUnsubscribeOneClick = true;

        await RunAsync(CallbackUpdate("cb1", UserId, "unsub:s1"));

        Assert.NotNull(server.RequestText);
        Assert.StartsWith("POST /unsubscribe HTTP/1.1", server.RequestText);
        Assert.Contains("application/x-www-form-urlencoded", server.RequestText);
        Assert.EndsWith("List-Unsubscribe=One-Click", server.RequestText);

        Assert.True(stats.Unsubscribed);
        await _state.Received(1).UpsertSenderStatAsync(stats);
        await _gmail.DidNotReceiveWithAnyArgs().SendUnsubscribeEmailAsync(default!, default);
        await _notifications.Received(1).AnswerCallbackAsync("cb1", "Done — unsubscribed from Shop News.");
    }

    [Fact]
    public async Task Unsubscribe_OneClickRejected_FallsBackToTheMailtoAddress()
    {
        using var server = new LoopbackHttpServer { ResponseStatusCode = 500 };
        var stats = SetUpSenderStat($"<{server.Url}>, <mailto:unsub@shop.com>");
        stats.ListUnsubscribeOneClick = true;

        await RunAsync(CallbackUpdate("cb1", UserId, "unsub:s1"));

        await _gmail.Received(1).SendUnsubscribeEmailAsync("unsub@shop.com", null);
        Assert.True(stats.Unsubscribed);
        await _notifications.Received(1).AnswerCallbackAsync("cb1", Arg.Is<string>(m => m.Contains("sent the unsubscribe email")));
    }

    [Fact]
    public async Task Unsubscribe_NoUsableMechanism_SaysSo()
    {
        SetUpSenderStat(null);

        await RunAsync(CallbackUpdate("cb1", UserId, "unsub:s1"));

        await _notifications.Received(1).AnswerCallbackAsync(
            "cb1", "That sender doesn't offer a usable unsubscribe mechanism.");
    }

    [Fact]
    public async Task Unsubscribe_AlreadyDone_IsIdempotent()
    {
        SetUpSenderStat("<mailto:unsub@shop.com>", unsubscribed: true);

        await RunAsync(CallbackUpdate("cb1", UserId, "unsub:s1"));

        await _gmail.DidNotReceiveWithAnyArgs().SendUnsubscribeEmailAsync(default!, default);
        await _notifications.Received(1).AnswerCallbackAsync("cb1", "Already unsubscribed from Shop News.");
    }

    [Fact]
    public async Task Unsubscribe_UnknownSender_Apologizes()
    {
        _state.GetSenderStatAsync("s1").Returns((SenderStatsEntity?)null);

        await RunAsync(CallbackUpdate("cb1", UserId, "unsub:s1"));

        await _notifications.Received(1).AnswerCallbackAsync("cb1", "I can't find that sender anymore.");
    }

    [Fact]
    public async Task KeepThem_AcknowledgesWithoutChangingAnything()
    {
        SetUpSenderStat("<mailto:unsub@shop.com>");

        await RunAsync(CallbackUpdate("cb1", UserId, "keep:s1"));

        await _state.DidNotReceiveWithAnyArgs().UpsertSenderStatAsync(default!);
        await _notifications.Received(1).AnswerCallbackAsync(
            "cb1", Arg.Is<string>(m => m.Contains("keep Shop News coming")));
    }
}
