using Ecnanib.Experience;
using Ecnanib.Shared;

namespace Ecnanib.Tests.Experience;

/// <summary>
/// Recommendations use Live data where there is enough, otherwise Demo data, and say which was
/// used (FR-EXP-9). See "Live or Demo" in docs/experience.md.
/// </summary>
public class ExperienceBasisTests
{
    [Theory]

    // Live has the minimum sample, however much more there is in Demo.
    [InlineData(20, 0, TradingMode.Live, true)]
    [InlineData(20, 500, TradingMode.Live, true)]
    [InlineData(21, 20, TradingMode.Live, true)]

    // Live has one fewer, so Demo is gone by, which has enough.
    [InlineData(19, 20, TradingMode.Demo, true)]
    [InlineData(0, 500, TradingMode.Demo, true)]

    // Neither has enough: Demo is gone by all the same, and says that it has not.
    [InlineData(19, 19, TradingMode.Demo, false)]
    [InlineData(5, 0, TradingMode.Demo, false)]
    [InlineData(0, 0, TradingMode.Demo, false)]
    public void Live_is_used_where_it_has_enough_samples_and_Demo_otherwise_and_it_says_whether_that_is_enough(
        int live, int demo, TradingMode mode, bool enough)
    {
        Assert.Equal(new ModeBasis(mode, enough), ExperienceBasis.Choose(live, demo, minSamples: 20));
    }

    [Fact]
    public void A_number_of_samples_below_zero_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ExperienceBasis.Choose(-1, 0, minSamples: 20));
        Assert.Throws<ArgumentOutOfRangeException>(() => ExperienceBasis.Choose(0, -1, minSamples: 20));
    }
}
