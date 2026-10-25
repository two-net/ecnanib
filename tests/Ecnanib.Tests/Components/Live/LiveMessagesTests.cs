using Ecnanib.Components.Live;

namespace Ecnanib.Tests.Components.Live;

public class LiveMessagesTests
{
    [Theory]
    [InlineData(0, "now")]
    [InlineData(-5, "now")]
    [InlineData(725, "in 12 min 05 s")]
    [InlineData(59, "in 0 min 59 s")]
    [InlineData(3720, "in 1 h 02 min")]
    [InlineData(100_800, "in 1 d 04 h")]
    public void TheCountdownToTheNextEvaluationReadsNaturally(int seconds, string expected) =>
        Assert.Equal(expected, LiveMessages.NextEvaluation(TimeSpan.FromSeconds(seconds)));

    [Theory]
    [InlineData(null, "waiting for a price")]
    [InlineData(64120.5, "64120.5")]
    [InlineData(0.00001234, "0.00001234")]
    [InlineData(100.0, "100")]
    public void PricesDropTrailingZeros(double? price, string expected) =>
        Assert.Equal(expected, LiveMessages.Price(price is { } p ? (decimal)p : null));

    [Fact]
    public void TheIndicatorTextsAreExact()
    {
        Assert.Equal("Evaluating…", LiveMessages.Evaluating);
        Assert.Equal("Today (UTC)", LiveMessages.TodayUtc);
    }
}
