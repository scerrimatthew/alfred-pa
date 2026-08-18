using Alfred.Functions.Functions;
using Alfred.Functions.Models;
using Alfred.Functions.Services.Gmail;
using Alfred.Functions.Services.Notifications;
using Alfred.Functions.Services.State;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;
using static Alfred.Functions.Tests.Support.TestData;

namespace Alfred.Functions.Tests;

public class SnoozeCheckFunctionTests
{
    private readonly IStateService _state = Substitute.For<IStateService>();
    private readonly INotificationService _notifications = Substitute.For<INotificationService>();

    private SnoozeCheckFunction CreateFunction(string personalChatId = "777") =>
        new(_state, _notifications,
            Options(o => o.PersonalTelegramChatId = personalChatId),
            NullLogger<SnoozeCheckFunction>.Instance);

    private static SnoozedEmailEntity Snooze(string id, string subject, DateTimeOffset dueAt, string? threadId = "t1") =>
        new()
        {
            RowKey = id,
            Subject = subject,
            SenderName = "Sender",
            Summary = "the summary",
            ThreadId = threadId,
            DueAt = dueAt
        };

    [Fact]
    public async Task WithoutPersonalChatId_DoesNothing()
    {
        await CreateFunction(personalChatId: "").Run(new TimerInfo());

        await _state.DidNotReceiveWithAnyArgs().GetDueSnoozesAsync(default);
    }

    [Fact]
    public async Task NoDueSnoozes_StaysSilent()
    {
        _state.GetDueSnoozesAsync(Arg.Any<DateTimeOffset>()).Returns(new List<SnoozedEmailEntity>());

        await CreateFunction().Run(new TimerInfo());

        await _notifications.DidNotReceiveWithAnyArgs().SendPersonalAlertAsync(default!);
        await _state.DidNotReceiveWithAnyArgs().DeleteSnoozeAsync(default!);
    }

    [Fact]
    public async Task DueSnoozes_AreResentOldestFirstAndDeleted()
    {
        var older = Snooze("m-old", "Older reminder", DateTimeOffset.UtcNow.AddHours(-2));
        var newer = Snooze("m-new", "Newer reminder", DateTimeOffset.UtcNow.AddHours(-1));
        // Deliberately out of order — the function must sort by DueAt
        _state.GetDueSnoozesAsync(Arg.Any<DateTimeOffset>())
            .Returns(new List<SnoozedEmailEntity> { newer, older });

        var messages = new List<string>();
        _notifications.When(n => n.SendPersonalAlertAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<NotificationButton>?>()))
            .Do(ci => messages.Add(ci.ArgAt<string>(0)));

        await CreateFunction().Run(new TimerInfo());

        Assert.Equal(2, messages.Count);
        Assert.Contains("Older reminder", messages[0]);
        Assert.Contains("Newer reminder", messages[1]);
        await _state.Received(1).DeleteSnoozeAsync("m-old");
        await _state.Received(1).DeleteSnoozeAsync("m-new");
    }

    [Fact]
    public async Task ReminderMessage_CarriesDetailsLinkAndButtons()
    {
        var snooze = Snooze("m1", "GO bill", DateTimeOffset.UtcNow.AddMinutes(-5), threadId: "t9");
        _state.GetDueSnoozesAsync(Arg.Any<DateTimeOffset>()).Returns(new List<SnoozedEmailEntity> { snooze });

        string? message = null;
        IReadOnlyList<NotificationButton>? buttons = null;
        _notifications.When(n => n.SendPersonalAlertAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<NotificationButton>?>()))
            .Do(ci =>
            {
                message = ci.ArgAt<string>(0);
                buttons = ci.ArgAt<IReadOnlyList<NotificationButton>?>(1);
            });

        await CreateFunction().Run(new TimerInfo());

        Assert.NotNull(message);
        Assert.Contains("<b>GO bill</b> — Sender", message);
        Assert.Contains("the summary", message);
        Assert.Contains(GmailLinks.ForThread("t9"), message);

        Assert.NotNull(buttons);
        Assert.Equal(2, buttons.Count);
        Assert.Equal("mu:m1", buttons[0].CallbackData);
        Assert.Equal("sn1:m1", buttons[1].CallbackData);
    }

    [Fact]
    public async Task SnoozeWithoutThread_OmitsTheGmailLink()
    {
        var snooze = Snooze("m1", "No thread", DateTimeOffset.UtcNow.AddMinutes(-5), threadId: null);
        _state.GetDueSnoozesAsync(Arg.Any<DateTimeOffset>()).Returns(new List<SnoozedEmailEntity> { snooze });

        string? message = null;
        _notifications.When(n => n.SendPersonalAlertAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<NotificationButton>?>()))
            .Do(ci => message = ci.ArgAt<string>(0));

        await CreateFunction().Run(new TimerInfo());

        Assert.NotNull(message);
        Assert.DoesNotContain("Open in Gmail", message);
    }

    [Fact]
    public async Task StateFailure_ReportsPersonalError()
    {
        _state.GetDueSnoozesAsync(Arg.Any<DateTimeOffset>()).ThrowsAsync(new TimeoutException("tables down"));

        await CreateFunction().Run(new TimerInfo());

        await _notifications.Received(1).SendPersonalErrorAsync(Arg.Is<string>(m => m.Contains("tables down")));
    }
}
