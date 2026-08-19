using Alfred.Functions.Models;
using Alfred.Functions.Services.Calendar;
using Alfred.Functions.Tests.Support;
using Google.Apis.Calendar.v3.Data;
using Xunit;

namespace Alfred.Functions.Tests;

// Pins the deterministic calendar logic: dedup title similarity, reminder placement,
// school-day windows, event construction, and the dedup hash.
public class GoogleCalendarServiceLogicTests
{
    // ---- Dedup title similarity ----

    [Theory]
    // Same event, reworded or re-prefixed
    [InlineData("Outing: Bristow Potteries Year 1", "Bristow Potteries visit", true)]
    [InlineData("Deadline: Sports Day", "Sports Day", true)]
    [InlineData("Sports Day", "Sports Day", true)]
    // The regression the two-thirds rule was introduced for: unrelated bills in the same window
    [InlineData("Deadline: Pay GO invoice €45.20", "Deadline: Pay Melita invoice €30.00", false)]
    // Completely different events
    [InlineData("Outing: Zoo Year 1", "Meeting: Online Safety", false)]
    public void TitlesAreSimilar_MatchesSameEventsAndSeparatesDifferentOnes(string a, string b, bool expected)
    {
        Assert.Equal(expected, GoogleCalendarService.TitlesAreSimilar(a, b));
    }

    [Fact]
    public void TitlesAreSimilar_TitlesMadeOnlyOfStopWords_NeverMatch()
    {
        Assert.False(GoogleCalendarService.TitlesAreSimilar("Year 1", "Year 1"));
        Assert.False(GoogleCalendarService.TitlesAreSimilar("", "Sports Day"));
    }

    [Fact]
    public void TitlesAreSimilar_IsCaseInsensitive()
    {
        Assert.True(GoogleCalendarService.TitlesAreSimilar("SPORTS DAY", "sports day"));
    }

    [Theory]
    [InlineData("Deadline: Pay GO invoice", " Pay GO invoice")]
    [InlineData("Appointment: Dentist", " Dentist")]
    [InlineData("Random: Something", "Random: Something")] // unknown prefixes stay
    [InlineData("No prefix at all", "No prefix at all")]
    public void StripCategoryPrefix_RemovesOnlyKnownAlfredPrefixes(string title, string expected)
    {
        Assert.Equal(expected, GoogleCalendarService.StripCategoryPrefix(title));
    }

    [Fact]
    public void ExtractSignificantWords_DropsStopWordsAndSingleLetters()
    {
        var words = GoogleCalendarService.ExtractSignificantWords(
            "The Year 1 Outing to Bristow Potteries (a fun day)");

        Assert.Contains("outing", words);
        Assert.Contains("bristow", words);
        Assert.Contains("potteries", words);
        Assert.Contains("fun", words);
        Assert.Contains("day", words);
        Assert.DoesNotContain("the", words);
        Assert.DoesNotContain("year", words);
        Assert.DoesNotContain("1", words);
        Assert.DoesNotContain("to", words);
        Assert.DoesNotContain("a", words);
    }

    // ---- School-day window ----

    [Theory]
    [InlineData("2026-08-21", 1, "2026-08-24")] // Friday + 1 school day = Monday
    [InlineData("2026-08-21", 3, "2026-08-26")] // Friday + 3 = Wednesday
    [InlineData("2026-08-24", 5, "2026-08-31")] // Monday + 5 = next Monday
    [InlineData("2026-08-22", 1, "2026-08-24")] // Saturday + 1 = Monday
    public void GetSchoolDaysFromNow_SkipsWeekends(string start, int schoolDays, string expected)
    {
        var result = GoogleCalendarService.GetSchoolDaysFromNow(DateTime.Parse(start), schoolDays);

        Assert.Equal(DateTime.Parse(expected), result);
    }

    // ---- Reminder placement ----

    [Fact]
    public void BuildReminder_FutureAllDayEvent_PopsUpAtSixTheEveningBefore()
    {
        // Midnight start; 18:00 the evening before is 6 hours earlier
        var reminders = GoogleCalendarService.BuildReminder(new DateTime(2100, 5, 10, 0, 0, 0));

        Assert.False(reminders.UseDefault);
        var reminder = Assert.Single(reminders.Overrides!);
        Assert.Equal("popup", reminder.Method);
        Assert.Equal(6 * 60, reminder.Minutes);
    }

    [Fact]
    public void BuildReminder_FutureTimedEvent_CountsBackFromTheStartTime()
    {
        // Start 09:00; reminder 18:00 the day before = 15 hours earlier
        var reminders = GoogleCalendarService.BuildReminder(new DateTime(2100, 5, 10, 9, 0, 0));

        Assert.Equal(15 * 60, Assert.Single(reminders.Overrides!).Minutes);
    }

    [Fact]
    public void BuildReminder_EventThatAlreadyStarted_GetsAZeroOffsetInsteadOfANegativeOne()
    {
        // Google rejects negative offsets; an already-started event cannot be nudged
        var reminders = GoogleCalendarService.BuildReminder(new DateTime(2020, 1, 1, 9, 0, 0));

        Assert.Equal(0, Assert.Single(reminders.Overrides!).Minutes);
    }

    [Fact]
    public void BuildReminder_IdealTimeAlreadyPassed_IsPulledForwardToImminent()
    {
        // Event starts ~2h from now (Malta): 18:00-the-evening-before has passed, so the
        // reminder moves to "a few minutes from now" — i.e. slightly less than 2h before start
        var maltaNow = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TestData.MaltaTz).DateTime;
        var start = maltaNow.AddHours(2);

        var reminders = GoogleCalendarService.BuildReminder(start);

        var minutes = Assert.Single(reminders.Overrides!).Minutes!.Value;
        Assert.InRange(minutes, 100, 118); // 120 minutes minus the ~5-minute lead, with slack
    }

    // ---- Event construction ----

    [Fact]
    public void BuildCalendarEvent_AllDay_SpansOneDayAndUsesDateOnlyFields()
    {
        var info = new CalendarEventInfo
        {
            Title = "Field Day",
            Description = "Wear sports kit",
            Date = new DateTime(2100, 5, 10),
            Action = CalendarEventAction.Create
        };

        var ev = GoogleCalendarService.BuildCalendarEvent(info, tagAsAlfred: false);

        Assert.Equal("Field Day", ev.Summary);
        Assert.Equal("Wear sports kit", ev.Description);
        Assert.Equal("2100-05-10", ev.Start!.Date);
        Assert.Equal("2100-05-11", ev.End!.Date);
        Assert.Null(ev.ExtendedProperties);
    }

    [Fact]
    public void BuildCalendarEvent_Timed_UsesMaltaTimezoneAndDefaultsToOneHour()
    {
        var info = new CalendarEventInfo
        {
            Title = "Appointment: Dentist",
            Description = "",
            Date = new DateTime(2100, 7, 10),
            StartTime = new TimeSpan(9, 0, 0),
            Action = CalendarEventAction.Create
        };

        var ev = GoogleCalendarService.BuildCalendarEvent(info, tagAsAlfred: false);

        Assert.Equal("Europe/Malta", ev.Start!.TimeZone);
        // July is CEST (UTC+2)
        Assert.Equal(new DateTimeOffset(2100, 7, 10, 9, 0, 0, TimeSpan.FromHours(2)), ev.Start.DateTimeDateTimeOffset);
        Assert.Equal(new DateTimeOffset(2100, 7, 10, 10, 0, 0, TimeSpan.FromHours(2)), ev.End!.DateTimeDateTimeOffset);
    }

    [Fact]
    public void BuildCalendarEvent_ExplicitEndTime_IsHonored()
    {
        var info = new CalendarEventInfo
        {
            Title = "Meeting",
            Description = "",
            Date = new DateTime(2100, 7, 10),
            StartTime = new TimeSpan(9, 0, 0),
            EndTime = new TimeSpan(11, 30, 0),
            Action = CalendarEventAction.Create
        };

        var ev = GoogleCalendarService.BuildCalendarEvent(info, tagAsAlfred: false);

        Assert.Equal(new DateTimeOffset(2100, 7, 10, 11, 30, 0, TimeSpan.FromHours(2)), ev.End!.DateTimeDateTimeOffset);
    }

    [Fact]
    public void BuildCalendarEvent_TaggedAsAlfred_CarriesThePrivateMarker()
    {
        var info = new CalendarEventInfo
        {
            Title = "Deadline: Pay GO invoice",
            Description = "",
            Date = new DateTime(2100, 5, 10),
            Action = CalendarEventAction.Create
        };

        var ev = GoogleCalendarService.BuildCalendarEvent(info, tagAsAlfred: true);

        Assert.NotNull(ev.ExtendedProperties?.Private__);
        Assert.Equal("true", ev.ExtendedProperties.Private__["alfred"]);
    }

    // ---- Dedup hash ----

    [Fact]
    public void ComputeHash_IsDeterministic16CharLowercaseHex()
    {
        var hash = GoogleCalendarService.ComputeHash("Sports Day_2026-09-10");

        Assert.Matches("^[0-9a-f]{16}$", hash);
        Assert.Equal(hash, GoogleCalendarService.ComputeHash("Sports Day_2026-09-10"));
        Assert.NotEqual(hash, GoogleCalendarService.ComputeHash("Sports Day_2026-09-11"));
    }
}
