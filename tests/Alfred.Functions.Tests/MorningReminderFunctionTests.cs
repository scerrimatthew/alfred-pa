using Alfred.Functions.Configuration;
using Alfred.Functions.Functions;
using Alfred.Functions.Services.Calendar;
using Alfred.Functions.Services.Notifications;
using Google.Apis.Calendar.v3.Data;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;
using static Alfred.Functions.Tests.Support.TestData;

namespace Alfred.Functions.Tests;

public class MorningReminderFunctionTests
{
    private readonly ICalendarService _calendar = Substitute.For<ICalendarService>();
    private readonly INotificationService _notifications = Substitute.For<INotificationService>();

    private static readonly DateTime TodayMalta =
        TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, MaltaTz).Date;

    public MorningReminderFunctionTests()
    {
        _calendar.GetUpcomingPersonalEventsAsync(Arg.Any<int>()).Returns(new List<Event>());
    }

    private MorningReminderFunction CreateFunction(Action<AlfredOptions>? mutate = null) =>
        new(_calendar, _notifications,
            Options(o =>
            {
                o.PersonalTelegramChatId = "777";
                mutate?.Invoke(o);
            }),
            NullLogger<MorningReminderFunction>.Instance);

    private static Event AllDayEvent(string summary, DateTime maltaDate) =>
        new() { Summary = summary, Start = new EventDateTime { Date = maltaDate.ToString("yyyy-MM-dd") } };

    private static Event TimedEvent(string summary, DateTime maltaDate, int hour, int minute)
    {
        var local = maltaDate.AddHours(hour).AddMinutes(minute);
        return new Event
        {
            Summary = summary,
            Start = new EventDateTime
            {
                DateTimeDateTimeOffset = new DateTimeOffset(local, MaltaTz.GetUtcOffset(local))
            }
        };
    }

    [Fact]
    public async Task WithoutPersonalChatId_DoesNothing()
    {
        var function = new MorningReminderFunction(
            _calendar, _notifications, Options(), NullLogger<MorningReminderFunction>.Instance);

        await function.Run(new TimerInfo());

        await _calendar.DidNotReceiveWithAnyArgs().GetUpcomingPersonalEventsAsync(default);
    }

    [Fact]
    public async Task NothingDueTodayOrTomorrow_StaysSilent()
    {
        _calendar.GetUpcomingPersonalEventsAsync(2).Returns(new List<Event>
        {
            AllDayEvent("Far away deadline", TodayMalta.AddDays(5))
        });

        await CreateFunction().Run(new TimerInfo());

        await _notifications.DidNotReceiveWithAnyArgs().SendPersonalAlertAsync(default!);
    }

    [Fact]
    public async Task GroupsEventsIntoTodayAndTomorrowSections()
    {
        _calendar.GetUpcomingPersonalEventsAsync(2).Returns(new List<Event>
        {
            AllDayEvent("Pay GO bill", TodayMalta),
            TimedEvent("Dentist", TodayMalta.AddDays(1), 14, 30)
        });

        string? message = null;
        _notifications.When(n => n.SendPersonalAlertAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<NotificationButton>?>()))
            .Do(ci => message = ci.ArgAt<string>(0));

        await CreateFunction().Run(new TimerInfo());

        Assert.NotNull(message);
        Assert.Contains("<b>Today</b>", message);
        Assert.Contains("• Pay GO bill", message);
        Assert.Contains("<b>Tomorrow</b>", message);
        Assert.Contains("• Dentist — 14:30", message);
        Assert.True(
            message.IndexOf("Today", StringComparison.Ordinal) < message.IndexOf("Tomorrow", StringComparison.Ordinal),
            "Today section must come before Tomorrow");
    }

    [Fact]
    public async Task OnlyTomorrowDue_OmitsTheTodaySection()
    {
        _calendar.GetUpcomingPersonalEventsAsync(2).Returns(new List<Event>
        {
            AllDayEvent("Renew insurance", TodayMalta.AddDays(1))
        });

        string? message = null;
        _notifications.When(n => n.SendPersonalAlertAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<NotificationButton>?>()))
            .Do(ci => message = ci.ArgAt<string>(0));

        await CreateFunction().Run(new TimerInfo());

        Assert.NotNull(message);
        Assert.DoesNotContain("<b>Today</b>", message);
        Assert.Contains("<b>Tomorrow</b>", message);
        Assert.Contains("• Renew insurance", message);
    }

    [Fact]
    public async Task EventWithNoStart_IsIgnored()
    {
        _calendar.GetUpcomingPersonalEventsAsync(2).Returns(new List<Event>
        {
            new() { Summary = "Ghost event", Start = new EventDateTime() }
        });

        await CreateFunction().Run(new TimerInfo());

        await _notifications.DidNotReceiveWithAnyArgs().SendPersonalAlertAsync(default!);
    }

    [Fact]
    public async Task CalendarFailure_ReportsPersonalError()
    {
        _calendar.GetUpcomingPersonalEventsAsync(Arg.Any<int>())
            .ThrowsAsync(new HttpRequestException("calendar down"));

        await CreateFunction().Run(new TimerInfo());

        await _notifications.Received(1).SendPersonalErrorAsync(Arg.Is<string>(m => m.Contains("calendar down")));
    }
}
