using MatOS.Core.Backups;
using MatOS.Core.Entities;

namespace MatOS.Tests;

public class BackupScheduleTests
{
    // Friday, 2026-10-09 12:00 local time.
    private static readonly DateTime Now = new(2026, 10, 9, 12, 0, 0, DateTimeKind.Local);

    private static BackupSchedule Daily(int hour = 3, DateTime? lastRun = null, DateTime? created = null) => new()
    {
        Frequency = BackupFrequency.Daily,
        MinuteOfDay = hour * 60,
        LastRunAt = lastRun?.ToUniversalTime(),
        CreateDate = (created ?? Now.AddDays(-30)).ToUniversalTime(),
    };

    [Fact]
    public void Daily_MostRecentOccurrence_IsTodayWhenItsTimeHasPassed()
        => Assert.Equal(new DateTime(2026, 10, 9, 3, 0, 0), BackupScheduleCalculator.MostRecentOccurrence(Daily(3), Now));

    [Fact]
    public void Daily_MostRecentOccurrence_IsYesterdayWhenItsTimeComesLater()
        => Assert.Equal(new DateTime(2026, 10, 8, 18, 0, 0), BackupScheduleCalculator.MostRecentOccurrence(Daily(18), Now));

    [Fact]
    public void Daily_IsDue_WhenItNeverRan_AndWasCreatedEarlier()
        => Assert.True(BackupScheduleCalculator.IsDue(Daily(3), Now));

    [Fact]
    public void Daily_IsNotDue_AfterTodaysRun()
        => Assert.False(BackupScheduleCalculator.IsDue(Daily(3, lastRun: new DateTime(2026, 10, 9, 3, 0, 5, DateTimeKind.Local)), Now));

    [Fact]
    public void Daily_IsNotDue_RightAfterCreation_EvenThoughTodaysTimePassed()
        => Assert.False(BackupScheduleCalculator.IsDue(Daily(3, created: Now.AddMinutes(-5)), Now));

    [Fact]
    public void Disabled_IsNeverDue()
    {
        var schedule = Daily(3);
        schedule.Enabled = false;

        Assert.False(BackupScheduleCalculator.IsDue(schedule, Now));
    }

    [Theory]
    [InlineData(DayOfWeek.Friday, 2026, 10, 9)]    // today at 03:00 already passed
    [InlineData(DayOfWeek.Sunday, 2026, 10, 4)]
    [InlineData(DayOfWeek.Saturday, 2026, 10, 3)]
    public void Weekly_MostRecentOccurrence_IsTheLastMatchingWeekday(DayOfWeek weekday, int year, int month, int day)
    {
        var schedule = new BackupSchedule { Frequency = BackupFrequency.Weekly, Weekday = weekday, MinuteOfDay = 3 * 60 };

        Assert.Equal(new DateTime(year, month, day, 3, 0, 0), BackupScheduleCalculator.MostRecentOccurrence(schedule, Now));
    }

    [Fact]
    public void Weekly_TodayBeforeItsTime_UsesLastWeek()
    {
        var schedule = new BackupSchedule { Frequency = BackupFrequency.Weekly, Weekday = DayOfWeek.Friday, MinuteOfDay = 18 * 60 };

        Assert.Equal(new DateTime(2026, 10, 2, 18, 0, 0), BackupScheduleCalculator.MostRecentOccurrence(schedule, Now));
    }

    [Fact]
    public void NextOccurrence_IsOnePeriodAfterTheLastOne()
    {
        Assert.Equal(new DateTime(2026, 10, 10, 3, 0, 0), BackupScheduleCalculator.NextOccurrence(Daily(3), Now));
        var weekly = new BackupSchedule { Frequency = BackupFrequency.Weekly, Weekday = DayOfWeek.Friday, MinuteOfDay = 3 * 60 };
        Assert.Equal(new DateTime(2026, 10, 16, 3, 0, 0), BackupScheduleCalculator.NextOccurrence(weekly, Now));
    }
}
