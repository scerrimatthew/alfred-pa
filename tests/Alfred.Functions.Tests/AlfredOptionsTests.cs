using Alfred.Functions.Configuration;
using Xunit;

namespace Alfred.Functions.Tests;

public class AlfredOptionsTests
{
    [Fact]
    public void Defaults_MatchDocumentedValues()
    {
        var options = new AlfredOptions();

        Assert.Equal("noreply@myschoolmanagement.com", options.SchoolEmailSender);
        Assert.Equal(25, options.LookbackHours);
        Assert.Equal(3, options.SchoolDaysAhead);
        Assert.Equal(30, options.ChatLookbackDays);
        Assert.Equal(5, options.ChatHistoryMaxTurns);
        Assert.Equal(60, options.ChatHistoryMaxAgeMinutes);
        Assert.Equal("primary", options.PersonalCalendarId);
        Assert.Equal(0, options.PersonalLookbackHours);
        Assert.Equal(7, options.PersonalDigestDaysAhead);
        Assert.Equal("07-01", options.SummerBreakStart);
        Assert.Equal("09-20", options.SummerBreakEnd);
        Assert.True(options.IncludeReadEmails);
        Assert.False(options.NotifyAllPersonalEmails);
        Assert.False(options.SendEmptyDigest);
        Assert.Equal(string.Empty, options.PersonalTelegramChatId);
        Assert.Equal(string.Empty, options.AllowedTelegramUserIds);
    }

    [Theory]
    [InlineData("2026-06-30", false)] // day before the window opens
    [InlineData("2026-07-01", true)]  // start day is inclusive
    [InlineData("2026-08-15", true)]  // middle of the break
    [InlineData("2026-09-20", true)]  // end day is inclusive
    [InlineData("2026-09-21", false)] // day after the window closes
    [InlineData("2026-01-10", false)] // deep winter
    public void IsInSummerBreak_DefaultWindow_IsInclusiveOnBothEnds(string date, bool expected)
    {
        var options = new AlfredOptions(); // 07-01 .. 09-20

        Assert.Equal(expected, options.IsInSummerBreak(DateTime.Parse(date)));
    }

    [Fact]
    public void IsInSummerBreak_UsesTheYearOfTheGivenDate()
    {
        var options = new AlfredOptions();

        Assert.True(options.IsInSummerBreak(new DateTime(2030, 7, 15)));
        Assert.True(options.IsInSummerBreak(new DateTime(1999, 7, 15)));
    }

    [Theory]
    [InlineData("", "09-20")]
    [InlineData("07-01", "")]
    [InlineData("July 1", "09-20")]
    [InlineData("07-01", "banana")]
    public void IsInSummerBreak_InvalidOrEmptyBounds_DisableThePause(string start, string end)
    {
        var options = new AlfredOptions { SummerBreakStart = start, SummerBreakEnd = end };

        // Mid-July would be a break day if the window were valid
        Assert.False(options.IsInSummerBreak(new DateTime(2026, 7, 15)));
    }

    [Fact]
    public void IsInSummerBreak_IgnoresTimeOfDay()
    {
        var options = new AlfredOptions();

        Assert.True(options.IsInSummerBreak(new DateTime(2026, 9, 20, 23, 59, 0)));
    }
}
