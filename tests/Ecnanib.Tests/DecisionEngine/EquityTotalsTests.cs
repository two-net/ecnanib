using Ecnanib.DecisionEngine;
using Ecnanib.ExchangeGateway;
using Ecnanib.Shared;

namespace Ecnanib.Tests.DecisionEngine;

/// <summary>
/// The dashboard shows, per mode, total equity across agents and profit and loss for the day
/// (FR-RPT-1), and Demo and Live figures are never combined (FR-BIN-11). This is the one rule
/// for what the equities of agents add up to: each mode on its own, each asset on its own, and
/// a total that has a part which is not known is not known. See docs/dashboard.md.
/// </summary>
public class EquityTotalsTests
{
    private static readonly DateTimeOffset Morning = new(2026, 11, 24, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Demo_and_Live_are_never_added_up_and_neither_are_two_assets()
    {
        var totals = EquityTotals.Of(
        [
            Of(TradingMode.Demo, "USDT", 1000m, 989.3m, -10.7m),
            Of(TradingMode.Live, "USDT", 500m, 512.25m, 12.25m),
            Of(TradingMode.Demo, "USDT", 2000m, 2100m, 40m),
            Of(TradingMode.Live, "USDC", 300m, 290m, -10m),
            Of(TradingMode.Live, "USDT", 250m, 250m, 0m),
        ]);

        // One total for each mode and asset that an agent is of, by the mode and then by the
        // asset: each adds up its own agents, and nothing of another mode or another asset.
        Assert.Equal(
            [
                new EquityTotal(TradingMode.Demo, "USDT", Agents: 2, Capital: 3000m, Equity: 3089.3m, MadeToday: 29.3m, PricedAt: null),
                new EquityTotal(TradingMode.Live, "USDC", Agents: 1, Capital: 300m, Equity: 290m, MadeToday: -10m, PricedAt: null),
                new EquityTotal(TradingMode.Live, "USDT", Agents: 2, Capital: 750m, Equity: 762.25m, MadeToday: 12.25m, PricedAt: null),
            ],
            totals);

        // No agent at all adds up to nothing: there is no total of a mode that has none.
        Assert.Empty(EquityTotals.Of([]));
    }

    [Fact]
    public void A_total_with_an_equity_that_is_not_known_is_not_known_and_is_not_the_sum_of_the_rest()
    {
        var totals = EquityTotals.Of(
        [
            Of(TradingMode.Demo, "USDT", 1000m, 989.3m, -10.7m),
            Of(TradingMode.Demo, "USDT", 2000m, equity: null, madeToday: null),
            Of(TradingMode.Live, "USDT", 500m, 512.25m, 12.25m),
        ]);

        // The capital is known whatever a price says. The equity and the day of Demo are not,
        // and are no number: Live has nothing of it, and is known.
        Assert.Equal(
            [
                new EquityTotal(TradingMode.Demo, "USDT", Agents: 2, Capital: 3000m, Equity: null, MadeToday: null, PricedAt: null),
                new EquityTotal(TradingMode.Live, "USDT", Agents: 1, Capital: 500m, Equity: 512.25m, MadeToday: 12.25m, PricedAt: null),
            ],
            totals);
    }

    [Fact]
    public void A_total_says_when_the_oldest_of_its_prices_was_read()
    {
        var totals = EquityTotals.Of(
        [
            Of(TradingMode.Demo, "USDT", 1000m, 1002.5m, 2.5m, pricedAt: Morning.AddSeconds(7)),
            Of(TradingMode.Demo, "USDT", 1000m, 994m, -6m, pricedAt: Morning.AddSeconds(2)),
            Of(TradingMode.Demo, "USDT", 1000m, 1000m, 0m),
            Of(TradingMode.Live, "USDT", 1000m, 1000m, 0m),
        ]);

        // A figure is as old as the oldest price in it. A total that no price is in says none.
        Assert.Equal([Morning.AddSeconds(2), null], totals.Select(total => total.PricedAt));
        Assert.Equal(2996.5m, totals[0].Equity);
    }

    private static AgentEquity Of(
        TradingMode mode, string asset, decimal capital, decimal? equity, decimal? madeToday, DateTimeOffset? pricedAt = null) =>
        new(
            Guid.NewGuid(),
            mode,
            asset,
            capital,
            equity,
            madeToday,
            Position: pricedAt is null
                ? null
                : new OpenPosition(
                    Market.Spot, mode, "BTC" + asset, asset, PositionSide.Long, 0.01m, 100_000m, StopLoss: null, TakeProfit: null, Morning),
            Price: pricedAt is { } readAt ? new WatchedPrice(100_250m, readAt) : null);
}
