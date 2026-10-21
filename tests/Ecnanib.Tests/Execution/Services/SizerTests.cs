using Ecnanib.Agents.Abstractions;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Execution.Services;

namespace Ecnanib.Tests.Execution.Services;

/// <summary>The FR-EXE-04 arithmetic: (equity × risk) ÷ stop distance, rounded down to the filters, then the caps and the balance.</summary>
public class SizerTests
{
    private static readonly SymbolFilters Filters = new(0.01m, 0.01m, 1_000_000m, 0.001m, 0.001m, 1000m, 0.001m, 0.001m, 500m, 5m, 50_000m, true);

    private static SizingInput Input(decimal price = 100m, decimal stop = 95m, decimal risk = 1m, decimal equity = 10_000m, decimal available = 10_000m, decimal multiplier = 1m,
        decimal? maxNotional = null, int leverage = 1, bool market = false, MarketType marketType = MarketType.Spot, PositionSide side = PositionSide.Long, SymbolFilters? filters = null) =>
        new(side, price, stop, risk, equity, available, multiplier, filters ?? Filters, maxNotional, leverage, market, marketType);

    [Fact]
    public void SizeIsEquityTimesRiskOverTheStopDistanceRoundedDownToTheStep()
    {
        var result = Sizer.Size(Input(price: 100.004m, stop: 95.006m));

        // 10000 × 1% = 100 of risk; 100.00 − 95.00 = 5 per unit after tick rounding → 20 units.
        Assert.Equal(100m, result.Price);
        Assert.Equal(95m, result.Stop);
        Assert.Equal(20m, result.Quantity);
        Assert.False(result.Rejected);
        Assert.Equal(["size", "notional", "minQuantity", "minNotional", "balance"], result.Checks.Select(c => c.Limit));
        Assert.All(result.Checks, c => Assert.Equal(LimitCheckStatus.Pass, c.Status));
        Assert.Contains("10000 × 1% ÷ 5", result.Checks[0].Detail);
    }

    [Fact]
    public void TheStopRoundsAwayFromThePriceAndNeverUp()
    {
        var longer = Sizer.Size(Input(price: 100m, stop: 95.006m, risk: 1.2345m));
        var shorter = Sizer.Size(Input(price: 100m, stop: 105.004m, side: PositionSide.Short));

        Assert.Equal(95m, longer.Stop);
        Assert.Equal(105.01m, shorter.Stop);
        Assert.Equal(24.69m, longer.Quantity);
        Assert.Equal(19.96m, shorter.Quantity);
    }

    [Fact]
    public void TheWeekendMultiplierScalesTheSize()
    {
        Assert.Equal(10m, Sizer.Size(Input(multiplier: 0.5m)).Quantity);
    }

    [Fact]
    public void TheNotionalCapClampsAndTheExchangeMaximumClamps()
    {
        var capped = Sizer.Size(Input(maxNotional: 500m));
        var maxed = Sizer.Size(Input(equity: 1_000_000m, available: 1_000_000m));

        Assert.Equal(5m, capped.Quantity);
        Assert.Equal(LimitCheckStatus.Clamped, capped.Checks.Single(c => c.Limit == "notional").Status);
        // 1,000,000 × 1% ÷ 5 = 2000, over the symbol's 1000 maximum, then the exchange's 50,000 maximum notional wins: 500.
        Assert.Equal(500m, maxed.Quantity);
        Assert.Equal(LimitCheckStatus.Clamped, maxed.Checks.Single(c => c.Limit == "notional").Status);
    }

    [Fact]
    public void TheMarketLotSizeAppliesToMarketEntries()
    {
        var filters = Filters with { MarketMaxQuantity = 10m };

        var market = Sizer.Size(Input(market: true, filters: filters));
        var limit = Sizer.Size(Input(market: false, filters: filters));

        Assert.Equal(10m, market.Quantity);
        Assert.Equal(LimitCheckStatus.Clamped, market.Checks.Single(c => c.Limit == "maxQuantity").Status);
        Assert.Equal(20m, limit.Quantity);
    }

    [Fact]
    public void BelowTheMinimumQuantityOrNotionalNothingIsPlaced()
    {
        var tiny = Sizer.Size(Input(equity: 0.1m, available: 0.1m));
        var cheap = Sizer.Size(Input(price: 1m, stop: 0.5m, equity: 2m, available: 2m));

        Assert.True(tiny.Rejected);
        Assert.Equal(0m, tiny.Quantity);
        Assert.Equal("minQuantity", Assert.Single(tiny.FailedLimits));
        Assert.True(cheap.Rejected);
        Assert.Equal("minNotional", Assert.Single(cheap.FailedLimits));
    }

    [Fact]
    public void TheFreeBalanceIsTheWholeNotionalOnSpotAndTheInitialMarginOnFutures()
    {
        var spot = Sizer.Size(Input(available: 1500m));
        var futures = Sizer.Size(Input(available: 250m, leverage: 10, marketType: MarketType.UsdFutures));
        var futuresShort = Sizer.Size(Input(available: 150m, leverage: 10, marketType: MarketType.UsdFutures));

        Assert.True(spot.Rejected);
        Assert.Contains("2000 needed, 1500 available", spot.Checks.Single(c => c.Limit == "balance").Detail);
        Assert.False(futures.Rejected);
        Assert.Contains("200 needed, 250 available", futures.Checks.Single(c => c.Limit == "balance").Detail);
        Assert.True(futuresShort.Rejected);
    }

    [Fact]
    public void AStopOnTheWrongSideAfterRoundingIsRefused()
    {
        var result = Sizer.Size(Input(price: 100m, stop: 100.004m));

        Assert.True(result.Rejected);
        Assert.Equal("size", Assert.Single(result.FailedLimits));
        Assert.Equal(0m, result.Quantity);
    }
}
