using Alfred.Functions.Models;
using Xunit;

namespace Alfred.Functions.Tests;

public class ModelTests
{
    [Fact]
    public void CalendarEventInfo_IsAllDay_TrueOnlyWhenStartTimeMissing()
    {
        var allDay = new CalendarEventInfo
        {
            Title = "Deadline: Pay bill",
            Description = "",
            Date = new DateTime(2026, 9, 1),
            Action = CalendarEventAction.Create
        };
        var timed = new CalendarEventInfo
        {
            Title = "Appointment: Dentist",
            Description = "",
            Date = new DateTime(2026, 9, 1),
            StartTime = new TimeSpan(9, 0, 0),
            Action = CalendarEventAction.Create
        };

        Assert.True(allDay.IsAllDay);
        Assert.False(timed.IsAllDay);
    }

    [Fact]
    public void SenderStats_RowKeyFor_IsDeterministicAnd16LowercaseHexChars()
    {
        var key = SenderStatsEntity.RowKeyFor("billing@go.com.mt");

        Assert.Equal(16, key.Length);
        Assert.Matches("^[0-9a-f]{16}$", key);
        Assert.Equal(key, SenderStatsEntity.RowKeyFor("billing@go.com.mt"));
    }

    [Fact]
    public void SenderStats_RowKeyFor_NormalizesCaseAndWhitespace()
    {
        var key = SenderStatsEntity.RowKeyFor("billing@go.com.mt");

        Assert.Equal(key, SenderStatsEntity.RowKeyFor("  Billing@GO.com.MT  "));
        Assert.NotEqual(key, SenderStatsEntity.RowKeyFor("other@go.com.mt"));
    }

    [Fact]
    public void EmailDigest_DefaultsToNonUrgentOtherCategory()
    {
        var digest = new EmailDigest { TelegramMessage = "m" };

        Assert.False(digest.RequiresImmediateAlert);
        Assert.Equal("other", digest.Category);
        Assert.Empty(digest.CalendarEvents);
        Assert.Null(digest.Homework);
    }

    [Fact]
    public void BackfillStateEntity_UsesFixedSingleRowKeys()
    {
        var entity = new BackfillStateEntity();

        // PersonalEmailMonitor and the /backfill command both rely on this exact
        // single-row address to find the in-progress marker.
        Assert.Equal("personal", entity.PartitionKey);
        Assert.Equal("backfill", entity.RowKey);
    }
}
