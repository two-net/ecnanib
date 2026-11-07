using System.Globalization;
using Ecnanib.Components;
using Ecnanib.Components.Agents;
using Ecnanib.Kernel;
using Ecnanib.Modules.AiCouncil.Contracts;
using Ecnanib.Modules.DecisionCycle.Contracts;
using Ecnanib.Modules.Execution.Contracts;
using Ecnanib.Modules.MarketData.Contracts;
using Ecnanib.Modules.RiskEngine.Contracts;

namespace Ecnanib.Tests.Agents;

/// <summary>
/// FR-UI-03 and FR-PREF-05: where the chart puts its candles, lines and markers, as percentages of its box, and what each
/// says, in the user's time zone. ChartViewTests draws it.
/// </summary>
public sealed class ChartLayoutTests
{
    internal static readonly DateTimeOffset Start = new(2026, 11, 7, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Candles_fill_the_width_and_prices_run_from_the_top_with_the_stop_and_target_on_the_chart() => InCulture(() =>
    {
        var layout = ChartLayout.Of(Model(), DisplayTime.Utc)!;

        Assert.Equal(["13.25%", "37.75%", "62.25%", "86.75%"], layout.Candles.Select(candle => candle.X));
        Assert.Equal(["5.9%", "30.4%", "54.9%", "79.4%"], layout.Candles.Select(candle => candle.BodyX));
        Assert.All(layout.Candles, candle => Assert.Equal("14.7%", candle.BodyWidth));
        Assert.Equal([true, true, false, true], layout.Candles.Select(candle => candle.Up));
        Assert.Equal([false, false, false, true], layout.Candles.Select(candle => candle.Forming));
        Assert.Equal("2026-11-07 01:00 UTC+00:00: open 105, high 120, low 100, close 115 USDT", layout.Candles[1].Title);
        Assert.StartsWith("2026-11-07 03:00 UTC+00:00, still open:", layout.Candles[3].Title, StringComparison.Ordinal);

        // From 88.25 to 126.75: the lows, the highs, the stop, the target and the trade's prices, padded by 5%.
        Assert.Equal(("72.695%", "Stop 92"), (layout.Stop!.Y, layout.Stop.Label));
        Assert.Equal([("8.409%", "Target 125")], layout.Targets.Select(target => (target.Y, target.Label)));
        Assert.Equal(["90", "100", "110", "120"], layout.PriceTicks.Select(tick => tick.Label));
        Assert.Equal(["76.591%", "18.149%"], [layout.PriceTicks[0].At, layout.PriceTicks[^1].At]);
        Assert.Equal("BTCUSDT 1H candles from 2026-11-07 00:00 UTC+00:00 to 2026-11-07 03:00 UTC+00:00. The last price is 100 USDT.", layout.Summary);
    });

    [Fact]
    public void Trades_are_marked_where_they_entered_and_exited_and_cycles_where_their_candle_closed() => InCulture(() =>
    {
        var layout = ChartLayout.Of(Model(), DisplayTime.Utc)!;

        Assert.Equal(
            [
                (TradeMarkKind.EntryLong, "37.75%", "45.422%", "2026-11-07 01:30 UTC+00:00: Long entry, 0.5 at 106 USDT"),
                (TradeMarkKind.Exit, "86.75%", "59.058%", "2026-11-07 03:10 UTC+00:00: exit, Stop, at 99 USDT"),
            ],
            layout.Trades.Select(mark => (mark.Kind, mark.X, mark.Y, mark.Title)));

        // The cycle after the window has no candle to sit on yet.
        Assert.Equal(
            [
                (CycleMarkKind.Acted, "13.25%", "2026-11-07 01:00 UTC+00:00: Executed, after proposing to open a long"),
                (CycleMarkKind.Held, "37.75%", "2026-11-07 02:00 UTC+00:00: Held"),
                (CycleMarkKind.Deciding, "86.75%", "2026-11-07 04:00 UTC+00:00: deciding now"),
            ],
            layout.Cycles.Select(mark => (mark.Kind, mark.X, mark.Title)));
    });

    [Fact]
    public void The_time_axis_is_in_the_users_zone() => InCulture(() =>
    {
        var tokyo = DisplayTime.For("Asia/Tokyo");

        var hourly = ChartLayout.Of(Model(), tokyo)!;
        var daily = ChartLayout.Of(Model() with { Timeframe = Timeframe.OneDay }, tokyo)!;

        Assert.Equal(["09:00", "10:00", "11:00", "12:00"], hourly.TimeTicks.Select(tick => tick.Label));
        Assert.Equal("7 Nov", daily.TimeTicks[0].Label);
        Assert.StartsWith("2026-11-07 10:00 UTC+09:00:", hourly.Candles[1].Title, StringComparison.Ordinal);
    });

    [Fact]
    public void Nothing_is_drawn_without_candles_and_a_flat_market_still_has_a_range() => InCulture(() =>
    {
        Assert.Null(ChartLayout.Of(Model() with { Closed = [], Forming = null }, DisplayTime.Utc));

        var flat = ChartLayout.Of(
            Model() with { Closed = [Candle(0, 100, 100, 100, 100)], Forming = null, Stop = null, Targets = [], Trades = [], Cycles = [] },
            DisplayTime.Utc)!;

        // From 99 to 101, so the candle is a thin line in the middle of the band.
        Assert.Equal(("42.5%", "0.2%"), (flat.Candles[0].BodyY, flat.Candles[0].BodyHeight));
        Assert.Contains("100", flat.PriceTicks.Select(tick => tick.Label));
    });

    [Fact]
    public void Every_outcome_has_a_marker_and_ticks_are_round_numbers()
    {
        Assert.All(Enum.GetValues<CycleOutcome>(), outcome => ChartLayout.KindOf(outcome));
        Assert.Equal([59_900m, 60_000m, 60_100m, 60_200m], ChartLayout.Ticks(59_850m, 60_250m));
        Assert.Equal([0.0012m, 0.0014m, 0.0016m, 0.0018m], ChartLayout.Ticks(0.00115m, 0.00185m));
    }

    /// <summary>
    /// Four hourly candles from midnight UTC, the last still open; a long from 01:30 to 03:10 and one from before the
    /// window; and cycles at three closes, one still deciding, and one after the window.
    /// </summary>
    internal static ChartModel Model() => new(
        Timeframe.OneHour,
        TradingEnvironment.Demo,
        "BTCUSDT",
        "USDT",
        [Candle(0, 100, 110, 90, 105), Candle(1, 105, 120, 100, 115), Candle(2, 115, 118, 95, 98)],
        Candle(3, 98, 104, 96, 100),
        PositionSide.Long,
        92m,
        [125m],
        [
            Trade(Start.AddMinutes(90), 106m, Start.AddMinutes(190), 99m),
            Trade(Start.AddHours(-5), 80m, Start.AddHours(-4), 85m),
        ],
        [
            new CycleMarker(Guid.NewGuid(), Start.AddHours(1), TradingEnvironment.Demo, CycleOutcome.Executed, null, TradeAction.OpenLong),
            new CycleMarker(Guid.NewGuid(), Start.AddHours(2), TradingEnvironment.Demo, CycleOutcome.Hold, null, TradeAction.Hold),
            new CycleMarker(Guid.NewGuid(), Start.AddHours(4), TradingEnvironment.Demo, null, null, null),
            new CycleMarker(Guid.NewGuid(), Start.AddHours(6), TradingEnvironment.Demo, CycleOutcome.Hold, null, null),
        ]);

    internal static Candle Candle(int hour, decimal open, decimal high, decimal low, decimal close) =>
        new(Start.AddHours(hour), open, high, low, close, 1m, close, 1);

    /// <summary>The numbers and dates in the expected text are the invariant culture's.</summary>
    internal static void InCulture(Action test)
    {
        var original = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            test();
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    private static TradeRecord Trade(DateTimeOffset openedAt, decimal entry, DateTimeOffset closedAt, decimal exit) => new(
        Guid.NewGuid(), Market.Spot, TradingEnvironment.Demo, "BTCUSDT", "BTC", "USDT", PositionSide.Long, TradeStatus.Closed, openedAt, closedAt,
        0.5m, entry, 90m, 0.5m, exit, 0m, 0.1m, 0m, (exit - entry) * 0.5m, null, TradeExit.StopFilled, true);
}
