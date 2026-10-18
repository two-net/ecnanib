using Ecnanib.Exchange.Abstractions;

namespace Ecnanib.Tests.Exchange.Abstractions;

public class SymbolFiltersTests
{
    // BTCUSDT-like spot filters as Binance returns them (8-decimal scale).
    private static readonly SymbolFilters Filters = new(
        TickSize: 0.01000000m, MinPrice: 0.01000000m, MaxPrice: 1000000.00000000m,
        StepSize: 0.00001000m, MinQuantity: 0.00001000m, MaxQuantity: 9000.00000000m,
        MarketStepSize: 0.00000001m, MarketMinQuantity: 0.00000001m, MarketMaxQuantity: 100.00000000m,
        MinNotional: 5.00000000m, MaxNotional: 9000000.00000000m, MinNotionalAppliesToMarketOrders: true);

    [Theory]
    [InlineData("0.123456", "0.12345")]
    [InlineData("0.12349999", "0.12349")]
    [InlineData("1.5", "1.5")]
    [InlineData("0.000001", "0")]
    public void QuantityIsRoundedDownToStep(string input, string expected)
    {
        var result = Filters.RoundQuantityDownToStep(decimal.Parse(input));

        Assert.Equal(decimal.Parse(expected), result);
        Assert.Equal(expected, result.ToString());
    }

    [Fact]
    public void MarketOrdersObeyLotSizeAndMarketLotSize()
    {
        Assert.Equal(0.12345m, Filters.RoundQuantityDownToStep(0.123456789m, marketOrder: true));

        var coarseMarket = Filters with { MarketStepSize = 0.001m };
        Assert.Equal(0.123m, coarseMarket.RoundQuantityDownToStep(0.123456789m, marketOrder: true));
        Assert.Equal(0.12345m, coarseMarket.RoundQuantityDownToStep(0.123456789m, marketOrder: false));
    }

    [Theory]
    [InlineData("100.019", "100.01")]
    [InlineData("100.01", "100.01")]
    public void PriceIsRoundedDownToTick(string input, string expected) =>
        Assert.Equal(decimal.Parse(expected), Filters.RoundPriceDownToTick(decimal.Parse(input)));

    [Theory]
    [InlineData("100.011", "100.02")]
    [InlineData("100.02", "100.02")]
    public void PriceIsRoundedUpToTick(string input, string expected) =>
        Assert.Equal(decimal.Parse(expected), Filters.RoundPriceUpToTick(decimal.Parse(input)));

    [Theory]
    [InlineData("100.015", "100.02")]
    [InlineData("100.014", "100.01")]
    public void PriceIsRoundedToNearestTick(string input, string expected) =>
        Assert.Equal(decimal.Parse(expected), Filters.RoundPriceToTick(decimal.Parse(input)));

    [Fact]
    public void RoundingOutputIsNormalised()
    {
        Assert.Equal("100.5", Filters.RoundPriceDownToTick(100.5m).ToString());
        Assert.Equal("2", Filters.RoundQuantityDownToStep(2.0000000m).ToString());
    }

    [Theory]
    [InlineData("100.01", true)]
    [InlineData("100.015", false)]
    [InlineData("0.005", false)]
    [InlineData("2000000", false)]
    [InlineData("0", false)]
    public void ValidatesPrice(string price, bool expected) =>
        Assert.Equal(expected, Filters.IsValidPrice(decimal.Parse(price)));

    [Theory]
    [InlineData("0.00001", false, true)]
    [InlineData("0.000015", false, false)]
    [InlineData("0.000001", false, false)]
    [InlineData("0.000001", true, false)]
    [InlineData("0.00001", true, true)]
    [InlineData("150", true, false)]
    [InlineData("150", false, true)]
    public void ValidatesQuantity(string quantity, bool market, bool expected) =>
        Assert.Equal(expected, Filters.IsValidQuantity(decimal.Parse(quantity), market));

    [Fact]
    public void ValidatesMinNotional()
    {
        Assert.True(Filters.MeetsMinNotional(50000m, 0.0001m));
        Assert.False(Filters.MeetsMinNotional(50000m, 0.00001m));
        Assert.False(Filters.MeetsMinNotional(50000m, 1000m));

        var marketExempt = Filters with { MinNotionalAppliesToMarketOrders = false };
        Assert.True(marketExempt.MeetsMinNotional(50000m, 0.00001m, marketOrder: true));
        Assert.False(marketExempt.MeetsMinNotional(50000m, 0.00001m, marketOrder: false));
    }

    [Fact]
    public void ZeroIncrementsLeaveValuesUntouched()
    {
        var none = Filters with { TickSize = 0m, StepSize = 0m };
        Assert.Equal(123.456789m, none.RoundPriceDownToTick(123.456789m));
        Assert.Equal(0.123456789m, none.RoundQuantityDownToStep(0.123456789m));
        Assert.True(none.IsValidPrice(123.456789m));
    }
}
