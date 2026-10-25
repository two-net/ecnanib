using Ecnanib.Database.Entities;
using Ecnanib.Execution.Abstractions;

namespace Ecnanib.Tests.Execution.Services;

public class PnlMathTests
{
    [Theory]
    [InlineData(PositionSide.Long, 2, 100, 110, 20)]
    [InlineData(PositionSide.Long, 2, 100, 95, -10)]
    [InlineData(PositionSide.Short, 0.5, 100, 90, 5)]
    [InlineData(PositionSide.Short, 0.5, 100, 104, -2)]
    [InlineData(PositionSide.Long, 3, 100, 100, 0)]
    public void UnrealizedFollowsTheSide(PositionSide side, decimal size, decimal entry, decimal price, decimal expected) =>
        Assert.Equal(expected, PnlMath.Unrealized(side, size, entry, price));

    [Fact]
    public void TheDayStartsAtMidnightUtcWhateverTheOffset()
    {
        var late = new DateTimeOffset(2026, 1, 5, 1, 30, 0, TimeSpan.FromHours(2)); // 2026-01-04 23:30 UTC

        Assert.Equal(new DateTimeOffset(2026, 1, 4, 0, 0, 0, TimeSpan.Zero), PnlMath.UtcDayStart(late));
        Assert.Equal(new DateTimeOffset(2026, 1, 5, 0, 0, 0, TimeSpan.Zero), PnlMath.UtcDayStart(new DateTimeOffset(2026, 1, 5, 0, 0, 5, TimeSpan.Zero)));
    }

    [Theory]
    [InlineData(12.5, "pnl-up")]
    [InlineData(-0.01, "pnl-down")]
    [InlineData(0.0, "")]
    [InlineData(null, "")]
    public void SignClassColoursOnlyNonZeroFigures(double? value, string expected) =>
        Assert.Equal(expected, PnlMath.SignClass(value is { } v ? (decimal)v : null));
}
