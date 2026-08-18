using Alfred.Functions.Configuration;
using Alfred.Functions.Functions;
using Alfred.Functions.Models;
using Alfred.Functions.Services.AI;
using Alfred.Functions.Services.Calendar;
using Alfred.Functions.Services.Gmail;
using Alfred.Functions.Services.Notifications;
using Alfred.Functions.Services.State;
using Google.Apis.Calendar.v3.Data;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;
using static Alfred.Functions.Tests.Support.TestData;

namespace Alfred.Functions.Tests;

public class EveningDigestFunctionTests
{
    private readonly ISummarizerService _summarizer = Substitute.For<ISummarizerService>();
    private readonly ICalendarService _calendar = Substitute.For<ICalendarService>();
    private readonly INotificationService _notifications = Substitute.For<INotificationService>();
    private readonly IStateService _state = Substitute.For<IStateService>();
    private readonly IGmailReaderService _gmail = Substitute.For<IGmailReaderService>();

    public EveningDigestFunctionTests()
    {
        _state.GetEmailsSinceAsync(Arg.Any<DateTimeOffset>()).Returns(new List<ProcessedEmailEntity>());
        _state.GetPersonalEmailsSinceAsync(Arg.Any<DateTimeOffset>()).Returns(new List<ProcessedEmailEntity>());
        _state.GetPersonalEmailsNeedingReplyAsync(Arg.Any<DateTimeOffset>()).Returns(new List<ProcessedEmailEntity>());
        _calendar.GetUpcomingEventsAsync(Arg.Any<int>()).Returns(new List<Event>());
        _calendar.GetUpcomingPersonalEventsAsync(Arg.Any<int>()).Returns(new List<Event>());
    }

    private EveningDigestFunction CreateFunction(Action<AlfredOptions>? mutate = null) =>
        new(_summarizer, _calendar, _notifications, _state, _gmail,
            Options(mutate), NullLogger<EveningDigestFunction>.Instance);

    private static TimerInfo Timer => new();

    [Fact]
    public async Task SchoolDigest_WithRecentEmails_SendsTheGeneratedDigest()
    {
        var emails = new List<ProcessedEmailEntity> { ProcessedEmail(partition: "emails") };
        _state.GetEmailsSinceAsync(Arg.Any<DateTimeOffset>()).Returns(emails);
        _summarizer.BuildEveningDigestAsync(emails, Arg.Any<List<Event>>()).Returns("SCHOOL DIGEST");

        await CreateFunction().Run(Timer);

        await _notifications.Received(1).SendAlertAsync("SCHOOL DIGEST");
    }

    [Fact]
    public async Task SchoolDigest_NothingToReport_SkipsSilently()
    {
        await CreateFunction().Run(Timer);

        await _summarizer.DidNotReceiveWithAnyArgs().BuildEveningDigestAsync(default!, default!);
        await _notifications.DidNotReceiveWithAnyArgs().SendAlertAsync(default!);
    }

    [Fact]
    public async Task SchoolDigest_SendEmptyDigest_ForcesASendEvenWithNoData()
    {
        _summarizer.BuildEveningDigestAsync(Arg.Any<List<ProcessedEmailEntity>>(), Arg.Any<List<Event>>())
            .Returns("EMPTY DIGEST");

        await CreateFunction(o => o.SendEmptyDigest = true).Run(Timer);

        await _notifications.Received(1).SendAlertAsync("EMPTY DIGEST");
    }

    [Fact]
    public async Task SchoolDigest_DuringSummerBreak_IsSkippedEntirely()
    {
        var emails = new List<ProcessedEmailEntity> { ProcessedEmail(partition: "emails") };
        _state.GetEmailsSinceAsync(Arg.Any<DateTimeOffset>()).Returns(emails);

        await CreateFunction(o =>
        {
            o.SummerBreakStart = "01-01";
            o.SummerBreakEnd = "12-31";
        }).Run(Timer);

        await _state.DidNotReceiveWithAnyArgs().GetEmailsSinceAsync(default);
        await _notifications.DidNotReceiveWithAnyArgs().SendAlertAsync(default!);
    }

    [Fact]
    public async Task SchoolDigestFailure_ReportsError_ButPersonalDigestStillRuns()
    {
        _state.GetEmailsSinceAsync(Arg.Any<DateTimeOffset>()).ThrowsAsync(new TimeoutException("tables down"));
        var personalEmails = new List<ProcessedEmailEntity> { ProcessedEmail() };
        _state.GetPersonalEmailsSinceAsync(Arg.Any<DateTimeOffset>()).Returns(personalEmails);
        _summarizer.BuildPersonalDigestAsync(Arg.Any<List<ProcessedEmailEntity>>(), Arg.Any<List<Event>>(), Arg.Any<List<ProcessedEmailEntity>>())
            .Returns("PERSONAL DIGEST");

        await CreateFunction(o => o.PersonalTelegramChatId = "777").Run(Timer);

        await _notifications.Received(1).SendErrorAsync(Arg.Is<string>(m => m.Contains("tables down")));
        await _notifications.Received(1).SendPersonalAlertAsync("PERSONAL DIGEST");
    }

    [Fact]
    public async Task PersonalDigest_WithoutChatId_NeverRuns()
    {
        _state.GetPersonalEmailsSinceAsync(Arg.Any<DateTimeOffset>())
            .Returns(new List<ProcessedEmailEntity> { ProcessedEmail() });

        await CreateFunction().Run(Timer);

        await _summarizer.DidNotReceiveWithAnyArgs().BuildPersonalDigestAsync(default!, default!, default!);
        await _notifications.DidNotReceiveWithAnyArgs().SendPersonalAlertAsync(default!);
    }

    [Fact]
    public async Task PersonalDigest_ExcludesSuppressedEmails()
    {
        var visible = ProcessedEmail(messageId: "m1");
        var muted = ProcessedEmail(messageId: "m2", suppressed: true);
        _state.GetPersonalEmailsSinceAsync(Arg.Any<DateTimeOffset>())
            .Returns(new List<ProcessedEmailEntity> { visible, muted });

        List<ProcessedEmailEntity>? passed = null;
        _summarizer.BuildPersonalDigestAsync(
                Arg.Do<List<ProcessedEmailEntity>>(e => passed = e),
                Arg.Any<List<Event>>(),
                Arg.Any<List<ProcessedEmailEntity>>())
            .Returns("PERSONAL DIGEST");

        await CreateFunction(o => o.PersonalTelegramChatId = "777").Run(Timer);

        Assert.NotNull(passed);
        Assert.Single(passed);
        Assert.Equal("m1", passed[0].RowKey);
    }

    [Fact]
    public async Task NeedsReply_AnsweredThreadsAreClearedAndNotNagged()
    {
        var answered = ProcessedEmail(messageId: "m1", threadId: "t1", needsReply: true);
        var unanswered = ProcessedEmail(messageId: "m2", threadId: "t2", needsReply: true);
        _state.GetPersonalEmailsNeedingReplyAsync(Arg.Any<DateTimeOffset>())
            .Returns(new List<ProcessedEmailEntity> { answered, unanswered });
        _gmail.HasRepliedAsync("t1", "m1").Returns(true);
        _gmail.HasRepliedAsync("t2", "m2").Returns(false);

        List<ProcessedEmailEntity>? awaiting = null;
        _summarizer.BuildPersonalDigestAsync(
                Arg.Any<List<ProcessedEmailEntity>>(),
                Arg.Any<List<Event>>(),
                Arg.Do<List<ProcessedEmailEntity>>(a => awaiting = a))
            .Returns("PERSONAL DIGEST");

        await CreateFunction(o => o.PersonalTelegramChatId = "777").Run(Timer);

        await _state.Received(1).ClearNeedsReplyAsync("m1");
        await _state.DidNotReceive().ClearNeedsReplyAsync("m2");
        Assert.NotNull(awaiting);
        Assert.Single(awaiting);
        Assert.Equal("m2", awaiting[0].RowKey);
    }

    [Fact]
    public async Task NeedsReply_GmailCheckFailure_KeepsTheNudgeJustInCase()
    {
        var flagged = ProcessedEmail(messageId: "m1", threadId: "t1", needsReply: true);
        _state.GetPersonalEmailsNeedingReplyAsync(Arg.Any<DateTimeOffset>())
            .Returns(new List<ProcessedEmailEntity> { flagged });
        _gmail.HasRepliedAsync("t1", "m1").ThrowsAsync(new HttpRequestException("gmail down"));

        List<ProcessedEmailEntity>? awaiting = null;
        _summarizer.BuildPersonalDigestAsync(
                Arg.Any<List<ProcessedEmailEntity>>(),
                Arg.Any<List<Event>>(),
                Arg.Do<List<ProcessedEmailEntity>>(a => awaiting = a))
            .Returns("PERSONAL DIGEST");

        await CreateFunction(o => o.PersonalTelegramChatId = "777").Run(Timer);

        Assert.NotNull(awaiting);
        Assert.Single(awaiting);
        Assert.Equal("m1", awaiting[0].RowKey);
        await _state.DidNotReceiveWithAnyArgs().ClearNeedsReplyAsync(default!);
    }

    [Fact]
    public async Task PersonalDigest_NothingAtAll_SkipsSilently()
    {
        await CreateFunction(o => o.PersonalTelegramChatId = "777").Run(Timer);

        await _summarizer.DidNotReceiveWithAnyArgs().BuildPersonalDigestAsync(default!, default!, default!);
        await _notifications.DidNotReceiveWithAnyArgs().SendPersonalAlertAsync(default!);
    }
}
