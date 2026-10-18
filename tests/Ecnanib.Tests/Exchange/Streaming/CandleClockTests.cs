using Ecnanib.Exchange.Abstractions;
using Ecnanib.Exchange.Streaming;

namespace Ecnanib.Tests.Exchange.Streaming;

public class CandleClockTests
{
    [Theory]
    [InlineData("2026-10-18T10:07:13Z", CandleInterval.FifteenMinutes, "2026-10-18T10:00:00Z")]
    [InlineData("2026-10-18T10:15:00Z", CandleInterval.FifteenMinutes, "2026-10-18T10:15:00Z")]
    [InlineData("2026-10-18T10:59:59Z", CandleInterval.OneHour, "2026-10-18T10:00:00Z")]
    [InlineData("2026-10-18T10:07:13Z", CandleInterval.FourHours, "2026-10-18T08:00:00Z")]
    [InlineData("2026-10-18T23:59:59Z", CandleInterval.OneDay, "2026-10-18T00:00:00Z")]
    [InlineData("2026-10-18T23:59:59Z", CandleInterval.OneWeek, "2026-10-12T00:00:00Z")] // Sunday -> previous Monday
    [InlineData("2026-10-19T00:00:00Z", CandleInterval.OneWeek, "2026-10-19T00:00:00Z")] // Monday boundary
    [InlineData("2026-10-24T12:00:00Z", CandleInterval.OneWeek, "2026-10-19T00:00:00Z")] // Saturday
    [InlineData("1970-01-01T12:00:00Z", CandleInterval.OneWeek, "1969-12-29T00:00:00Z")] // before the week epoch
    public void OpenTimeFloorsToUtcBoundary(string time, CandleInterval interval, string expected)
    {
        var open = CandleClock.OpenTimeOf(DateTimeOffset.Parse(time), interval);

        Assert.Equal(DateTimeOffset.Parse(expected), open);
        Assert.Equal(DayOfWeek.Monday, CandleClock.OpenTimeOf(DateTimeOffset.Parse(time), CandleInterval.OneWeek).DayOfWeek);
    }

    [Fact]
    public void NextCloseIsStrictlyAfterTheBoundary()
    {
        var boundary = DateTimeOffset.Parse("2026-10-18T10:15:00Z");

        Assert.Equal(boundary.AddMinutes(15), CandleClock.NextCloseAfter(boundary, CandleInterval.FifteenMinutes));
        Assert.Equal(boundary.AddMinutes(15), CandleClock.NextCloseAfter(boundary.AddSeconds(1), CandleInterval.FifteenMinutes));
    }

    [Fact]
    public void NonUtcOffsetsAreHandled()
    {
        var plusTwo = new DateTimeOffset(2026, 10, 18, 12, 07, 0, TimeSpan.FromHours(2)); // 10:07Z
        Assert.Equal(DateTimeOffset.Parse("2026-10-18T10:00:00Z"), CandleClock.OpenTimeOf(plusTwo, CandleInterval.OneHour));
    }
}
