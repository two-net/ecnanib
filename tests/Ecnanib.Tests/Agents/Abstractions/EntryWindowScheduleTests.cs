using Ecnanib.Agents.Abstractions;
using Ecnanib.Database.Entities;

namespace Ecnanib.Tests.Agents.Abstractions;

/// <summary>"No new entry" window semantics (FR-STR-05): start inclusive, end exclusive, recurring windows wrap past Sunday, all in UTC.</summary>
public class EntryWindowScheduleTests
{
    private static readonly EntryWindow Weekend = new() { Kind = EntryWindowKind.Recurring, StartDay = DayOfWeek.Friday, StartTime = new TimeOnly(21, 0), EndDay = DayOfWeek.Monday, EndTime = new TimeOnly(1, 0) };

    private static readonly EntryWindow Tuesday = new() { Kind = EntryWindowKind.Recurring, StartDay = DayOfWeek.Tuesday, StartTime = new TimeOnly(10, 0), EndDay = DayOfWeek.Tuesday, EndTime = new TimeOnly(12, 0) };

    [Theory]
    [InlineData("2026-01-10T10:00:00Z", true)]   // Saturday
    [InlineData("2026-01-12T00:30:00Z", true)]   // Monday before the end
    [InlineData("2026-01-12T01:00:00Z", false)]  // the end is exclusive
    [InlineData("2026-01-09T20:59:00Z", false)]  // Friday before the start
    [InlineData("2026-01-09T21:00:00Z", true)]   // the start is inclusive
    [InlineData("2026-01-07T12:00:00Z", false)]  // Wednesday
    public void ARecurringWindowMayWrapPastSunday(string at, bool active)
    {
        Assert.Equal(active, EntryWindowSchedule.IsActive(Weekend, DateTimeOffset.Parse(at)));
    }

    [Theory]
    [InlineData("2026-01-06T10:00:00Z", true)]
    [InlineData("2026-01-06T11:59:59Z", true)]
    [InlineData("2026-01-06T12:00:00Z", false)]
    [InlineData("2026-01-06T09:59:59Z", false)]
    [InlineData("2026-01-13T11:00:00+02:00", false)] // 09:00 UTC
    public void ANonWrappingWindowEndsExclusively(string at, bool active)
    {
        Assert.Equal(active, EntryWindowSchedule.IsActive(Tuesday, DateTimeOffset.Parse(at)));
    }

    [Fact]
    public void OneOffWindowsAndEmptyOrIncompleteOnes()
    {
        var oneOff = new EntryWindow { Kind = EntryWindowKind.OneOff, StartAt = new DateTimeOffset(2026, 11, 5, 13, 0, 0, TimeSpan.Zero), EndAt = new DateTimeOffset(2026, 11, 5, 15, 0, 0, TimeSpan.Zero) };
        Assert.True(EntryWindowSchedule.IsActive(oneOff, oneOff.StartAt.Value));
        Assert.True(EntryWindowSchedule.IsActive(oneOff, oneOff.EndAt.Value.AddSeconds(-1)));
        Assert.False(EntryWindowSchedule.IsActive(oneOff, oneOff.EndAt.Value));
        Assert.False(EntryWindowSchedule.IsActive(oneOff, oneOff.StartAt.Value.AddSeconds(-1)));

        var empty = new EntryWindow { Kind = EntryWindowKind.Recurring, StartDay = DayOfWeek.Monday, StartTime = new TimeOnly(9, 0), EndDay = DayOfWeek.Monday, EndTime = new TimeOnly(9, 0) };
        Assert.False(EntryWindowSchedule.IsActive(empty, new DateTimeOffset(2026, 1, 5, 9, 0, 0, TimeSpan.Zero)));
        var incomplete = new EntryWindow { Kind = EntryWindowKind.Recurring, StartDay = DayOfWeek.Monday };
        Assert.False(EntryWindowSchedule.IsActive(incomplete, new DateTimeOffset(2026, 1, 5, 9, 0, 0, TimeSpan.Zero)));

        Assert.True(EntryWindowSchedule.AnyActive([Tuesday, Weekend], new DateTimeOffset(2026, 1, 10, 10, 0, 0, TimeSpan.Zero)));
        Assert.False(EntryWindowSchedule.AnyActive([], new DateTimeOffset(2026, 1, 10, 10, 0, 0, TimeSpan.Zero)));
        Assert.Equal(0, EntryWindowSchedule.WeekSecond(DayOfWeek.Monday, TimeOnly.MinValue));
        Assert.Equal(6 * 86_400 + 3_600, EntryWindowSchedule.WeekSecond(DayOfWeek.Sunday, new TimeOnly(1, 0)));
    }
}
