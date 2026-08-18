using Alfred.Functions.Configuration;
using Alfred.Functions.Functions;
using Alfred.Functions.Models;
using Alfred.Functions.Services.AI;
using Alfred.Functions.Services.Calendar;
using Alfred.Functions.Services.Gmail;
using Alfred.Functions.Services.Notifications;
using Alfred.Functions.Services.State;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;
using static Alfred.Functions.Tests.Support.TestData;

namespace Alfred.Functions.Tests;

public class EmailMonitorFunctionTests
{
    private readonly IGmailReaderService _gmail = Substitute.For<IGmailReaderService>();
    private readonly ISummarizerService _summarizer = Substitute.For<ISummarizerService>();
    private readonly ICalendarService _calendar = Substitute.For<ICalendarService>();
    private readonly INotificationService _notifications = Substitute.For<INotificationService>();
    private readonly IStateService _state = Substitute.For<IStateService>();

    private EmailMonitorFunction CreateFunction(IOptions<AlfredOptions>? options = null) =>
        new(_gmail, _summarizer, _calendar, _notifications, _state,
            options ?? Options(), NullLogger<EmailMonitorFunction>.Instance);

    private static TimerInfo Timer => new();

    [Fact]
    public async Task NoNewEmails_DoesNothing()
    {
        _gmail.GetNewEmailsAsync().Returns([]);

        await CreateFunction().Run(Timer);

        await _summarizer.DidNotReceiveWithAnyArgs().SummarizeEmailAsync(default!);
        await _notifications.DidNotReceiveWithAnyArgs().SendAlertAsync(default!);
        await _state.DidNotReceiveWithAnyArgs().MarkEmailProcessedAsync(default!, default!, default!, default!);
    }

    [Fact]
    public async Task UrgentUnreadEmail_SendsAlertWithGmailLink()
    {
        var email = Email(messageId: "m1", threadId: "t1", subject: "Early dismissal");
        _gmail.GetNewEmailsAsync().Returns([email]);
        _summarizer.SummarizeEmailAsync(email).Returns(Digest("URGENT SUMMARY", requiresImmediateAlert: true));

        string? sent = null;
        _notifications.When(n => n.SendAlertAsync(Arg.Any<string>())).Do(ci => sent = ci.Arg<string>());

        await CreateFunction().Run(Timer);

        Assert.NotNull(sent);
        Assert.StartsWith("URGENT SUMMARY", sent);
        Assert.Contains(GmailLinks.ForThread("t1"), sent);
    }

    [Fact]
    public async Task NonUrgentEmail_IsProcessedSilently()
    {
        var email = Email(messageId: "m1", subject: "Weekly plan");
        _gmail.GetNewEmailsAsync().Returns([email]);
        _summarizer.SummarizeEmailAsync(email).Returns(
            Digest("plan summary", requiresImmediateAlert: false, category: "weekly-plan", homework: "Read pages 1-3"));

        await CreateFunction().Run(Timer);

        await _notifications.DidNotReceiveWithAnyArgs().SendAlertAsync(default!);
        await _state.Received(1).MarkEmailProcessedAsync(
            "m1", "Weekly plan", email.SenderName, "plan summary", "Read pages 1-3", "weekly-plan", email.ThreadId);
        await _gmail.Received(1).MarkAsReadAndLabelAsync("m1", "Alfred/School/Weekly Plan");
    }

    [Fact]
    public async Task AlreadyReadEmail_NeverAlertsEvenWhenUrgent()
    {
        var email = Email(wasUnread: false);
        _gmail.GetNewEmailsAsync().Returns([email]);
        _summarizer.SummarizeEmailAsync(email).Returns(Digest(requiresImmediateAlert: true));

        await CreateFunction().Run(Timer);

        await _notifications.DidNotReceiveWithAnyArgs().SendAlertAsync(default!);
        // Still fed into state and labeled for the digest / chat context
        await _state.ReceivedWithAnyArgs(1).MarkEmailProcessedAsync(default!, default!, default!, default!);
        await _gmail.ReceivedWithAnyArgs(1).MarkAsReadAndLabelAsync(default!, default!);
    }

    [Fact]
    public async Task DuringSummerBreak_EveryUnreadEmailAlerts()
    {
        var email = Email(threadId: "t9");
        _gmail.GetNewEmailsAsync().Returns([email]);
        _summarizer.SummarizeEmailAsync(email).Returns(Digest("routine news", requiresImmediateAlert: false));

        // Break window covering the whole year makes "today" always a break day
        var options = Options(o =>
        {
            o.SummerBreakStart = "01-01";
            o.SummerBreakEnd = "12-31";
        });

        await CreateFunction(options).Run(Timer);

        await _notifications.Received(1).SendAlertAsync(Arg.Is<string>(m => m.StartsWith("routine news")));
    }

    [Fact]
    public async Task CalendarEventsAreProcessedBeforeNotifying()
    {
        var events = new List<CalendarEventInfo>
        {
            new()
            {
                Title = "Outing: Zoo",
                Description = "Bring a hat",
                Date = new DateTime(2026, 9, 10),
                Action = CalendarEventAction.Create
            }
        };
        var email = Email(messageId: "m1");
        _gmail.GetNewEmailsAsync().Returns([email]);
        _summarizer.SummarizeEmailAsync(email).Returns(Digest(calendarEvents: events));

        await CreateFunction().Run(Timer);

        await _calendar.Received(1).ProcessEventsAsync(events, "m1");
    }

    [Fact]
    public async Task FailureOnOneEmail_NotifiesErrorAndContinuesWithTheNext()
    {
        var bad = Email(messageId: "bad", subject: "Broken");
        var good = Email(messageId: "good", subject: "Fine");
        _gmail.GetNewEmailsAsync().Returns([bad, good]);
        _summarizer.SummarizeEmailAsync(bad).ThrowsAsync(new InvalidOperationException("claude down"));
        _summarizer.SummarizeEmailAsync(good).Returns(Digest("ok"));

        await CreateFunction().Run(Timer);

        await _notifications.Received(1).SendErrorAsync(Arg.Is<string>(m => m.Contains("Broken") && m.Contains("claude down")));
        await _state.Received(1).MarkEmailProcessedAsync(
            "good", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>());
        await _state.DidNotReceive().MarkEmailProcessedAsync(
            "bad", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task GmailOutage_ReportsErrorInsteadOfThrowing()
    {
        _gmail.GetNewEmailsAsync().ThrowsAsync(new HttpRequestException("gmail 500"));

        await CreateFunction().Run(Timer);

        await _notifications.Received(1).SendErrorAsync(Arg.Is<string>(m => m.Contains("gmail 500")));
    }
}
