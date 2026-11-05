using System.Globalization;
using Ecnanib.Kernel;
using Ecnanib.Modules.AiCouncil.Contracts;
using Ecnanib.Modules.MarketData.Contracts;
using Ecnanib.Modules.RiskEngine.Contracts;
using static Ecnanib.Tests.Prompts.PromptHarness;

namespace Ecnanib.Tests.Prompts;

/// <summary>
/// FR-PRM-01's runtime context: market data, indicators, account and position state, and risk settings, written
/// the same way whatever the server's culture, with as many candles as each role needs.
/// </summary>
public sealed class PromptContextTests
{
    [Theory]
    [InlineData(AiRole.TechnicalAnalyst, 120, 60)]
    [InlineData(AiRole.Strategist, 24, 12)]
    [InlineData(AiRole.RiskCritic, 24, 12)]
    [InlineData(AiRole.SentimentAnalyst, 0, 0)]
    [InlineData(AiRole.Reviewer, 0, 0)]
    public void Each_role_gets_its_window_of_candles_and_every_role_its_indicators(AiRole role, int candles, int higher)
    {
        var prompt = Compose(Request(role, context: new PromptContext { Snapshot = Snapshot() }));

        Assert.Equal((candles, higher), PromptWindows.For(role));
        Assert.Equal(candles, CandleRows(prompt.Message, "15m"));
        Assert.Equal(higher, CandleRows(prompt.Message, "1H"));
        Assert.Contains("\nindicators_15m: ema20=", prompt.Message, StringComparison.Ordinal);
        Assert.Contains("\nindicators_1H: ema20=", prompt.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_newest_candles_are_kept_oldest_first()
    {
        var snapshot = Snapshot();

        var message = Compose(Request(AiRole.Strategist, context: new PromptContext { Snapshot = snapshot })).Message;

        var first = snapshot.Candles.Candles[^24];
        var last = snapshot.Candles.Candles[^1];
        Assert.Contains(
            "candles_15m (last 24, oldest first):\nopen_time,open,high,low,close,volume\n"
            + $"2026-11-02T20:00:00Z,{first.Open.ToString(CultureInfo.InvariantCulture)},",
            message,
            StringComparison.Ordinal);
        Assert.Contains("\n2026-11-03T01:45:00Z,62004.12345678,62029.62345678,61983.87345678,62009.22345678,211.34\n", message, StringComparison.Ordinal);
        Assert.Equal(new DateTimeOffset(2026, 11, 3, 1, 45, 0, TimeSpan.Zero), last.OpenTime);
    }

    [Fact]
    public void A_short_series_gives_what_it_has()
    {
        var prompt = Compose(Request(AiRole.TechnicalAnalyst, context: new PromptContext { Snapshot = Snapshot(count: 10) }));

        Assert.Contains("candles_15m (last 10, oldest first):", prompt.Message, StringComparison.Ordinal);
        Assert.Equal(10, CandleRows(prompt.Message, "15m"));
        Assert.Contains("ema20=n/a, ema50=n/a, ema200=n/a", prompt.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Market_data_names_the_candle_and_writes_prices_as_given_and_indicators_to_eight_digits()
    {
        var snapshot = Snapshot(Market.Futures, Timeframe.OneDay);

        var message = Compose(Request(AiRole.Strategist, Market.Futures, Timeframe.OneDay, new PromptContext { Snapshot = snapshot })).Message;

        Assert.Contains(
            "<market_data symbol=\"BTCUSDT\" market=\"Futures\" timeframe=\"1D\" higher_timeframe=\"1W\" candle_close=\"2027-05-20T00:00:00Z\">\n"
            + "quote_at: 2026-12-01T00:00:00Z\n"
            + "last_price: 67050.1\n"
            + "bid: 67050 (quantity 1.234)\n"
            + "ask: 67050.2 (quantity 0.5)\n"
            + "mark_price: 67049.8\n"
            + "index_price: 67045.1\n"
            + "funding_rate: 0.01% (last)\n"
            + "next_funding: 2026-12-01T06:00:00Z\n",
            message,
            StringComparison.Ordinal);
        var ema20 = snapshot.Candles.Indicators.Ema20!.Value;
        Assert.Contains($"ema20={Math.Round(ema20, 3).ToString(CultureInfo.InvariantCulture).TrimEnd('0').TrimEnd('.')},", message, StringComparison.Ordinal);
        Assert.Contains("\nvolume_1D: latest=211.34, average20=", message, StringComparison.Ordinal);
        Assert.Contains("\nswing_highs_1D (newest first): ", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Futures_only_fields_appear_only_on_Futures()
    {
        string[] futuresOnly = ["mark_price:", "funding_rate:", "next_funding:", "liquidation_price:", "max_leverage:", "margin_mode:", "liquidation_buffer:", "funding_filter:", "leverage:", "available_margin:"];

        var futures = Compose(Request(market: Market.Futures, context: Everything(Market.Futures))).Message;
        var spot = Compose(Request(market: Market.Spot, context: Everything(Market.Spot))).Message;

        Assert.All(futuresOnly, field => Assert.Contains(field, futures, StringComparison.Ordinal));
        Assert.All(futuresOnly, field => Assert.DoesNotContain(field, spot, StringComparison.Ordinal));
        Assert.Contains("available_quote: 950", spot, StringComparison.Ordinal);
        Assert.Contains("base_held_by_agent: 0.01", spot, StringComparison.Ordinal);
    }

    [Fact]
    public void Account_position_record_and_risk_settings_are_written_out()
    {
        var message = Compose(Request(market: Market.Futures, context: Everything(Market.Futures))).Message;

        Assert.Contains(
            "<account base_asset=\"BTC\" quote_asset=\"USDT\">\ncapital_allocation: 1000\navailable_margin: 950\nleverage: 2x, isolated margin\n</account>",
            message,
            StringComparison.Ordinal);
        Assert.Contains(
            "<position side=\"Long\">\nquantity: 0.01\naverage_entry: 66000\nopened_at: 2026-11-02T00:00:00Z\nstop: 65500\n"
            + "targets: 67500, 68000\nunrealized_pnl: 10.5\nliquidation_price: 33500\n</position>",
            message,
            StringComparison.Ordinal);
        Assert.Contains(
            "<pending_entry side=\"Short\" type=\"Limit\">\nprice: 68500\nquantity: 0.02\nplaced_at: 2026-11-02T12:00:00Z\n</pending_entry>",
            message,
            StringComparison.Ordinal);
        Assert.Contains(
            "<trading_record>\nrealized_pnl_today: -5.25\nrealized_pnl_this_week: 12\nconsecutive_losses: 1\nentries_today: 2\ncycles_since_last_loss: 3\n</trading_record>",
            message,
            StringComparison.Ordinal);
        Assert.Contains(
            "<risk_settings>\nmax_risk_per_trade: 1% of the capital allocation\nmax_position_notional: the capital allocation\nmax_leverage: 2x\n"
            + "margin_mode: isolated\ndaily_loss_limit: 3% of the capital allocation\nweekly_loss_limit: 6% of the capital allocation\n"
            + "consecutive_losses_before_pause: 3\nmin_reward_to_risk: 1.5\nmax_new_entries_per_day: 4\n"
            + "stop_distance: 0.5 to 4 x ATR(14) of the 15m candles\n"
            + "liquidation_buffer: liquidation at least 3 x the entry-to-stop distance from the entry\n"
            + "funding_filter: no entry when funding against the position is above 0.05% per 8 hours\n"
            + "cooldown_cycles_after_loss: 1\nprotective_stop: always required\n</risk_settings>",
            message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_runtime_context_follows_FR_PRM_01s_order()
    {
        var message = Compose(Request(context: Everything(Market.Futures) with
        {
            Reflections = [new PromptReflection(Start, 1m, "Lesson.")],
            External = [new UntrustedContent(UntrustedKind.News, "Source", "News.")],
        })).Message;

        string[] order = ["<market_data", "<account", "<position", "<pending_entry", "<trading_record", "<risk_settings", "kind=\"reflection\"", "kind=\"news\""];
        var positions = order.Select(tag => message.IndexOf(tag, StringComparison.Ordinal)).ToList();

        Assert.DoesNotContain(-1, positions);
        Assert.Equal(positions.Order(), positions);
    }

    [Fact]
    public void An_account_without_a_position_or_pending_entry_says_so()
    {
        var context = Everything(Market.Spot) with { Position = null, PendingEntry = null };

        var message = Compose(Request(market: Market.Spot, context: context)).Message;

        Assert.Contains("<position>\nnone\n</position>", message, StringComparison.Ordinal);
        Assert.Contains("<pending_entry>\nnone\n</pending_entry>", message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_prompt_is_the_same_in_any_culture_and_on_every_call()
    {
        var request = Request(AiRole.TechnicalAnalyst, context: Everything(Market.Futures)) with { CustomInstructions = "Be brief." };
        var original = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            var invariant = Compose(request).Message;
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var german = Compose(request).Message;

            Assert.Equal(invariant, german);
            Assert.Equal(german, Compose(request).Message);
            Assert.Contains("last_price: 67050.1\n", german, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    /// <summary>Every part of the runtime context, for <paramref name="market"/>.</summary>
    private static PromptContext Everything(Market market) => new()
    {
        Snapshot = Snapshot(market),
        Account = new PromptAccount("BTC", "USDT", 1000m, 950.00m)
        {
            BaseHeld = market == Market.Spot ? 0.01m : null,
            Leverage = market == Market.Futures ? 2 : null,
        },
        Position = new PromptPosition(PositionSide.Long, 0.01m, 66000m, Start.AddDays(1))
        {
            Stop = 65500m,
            Targets = [67500m, 68000.0m],
            UnrealizedPnl = 10.50m,
            LiquidationPrice = market == Market.Futures ? 33500m : null,
        },
        PendingEntry = new PromptPendingEntry(PositionSide.Short, EntryType.Limit, 68500m, 0.02m, Start.AddDays(1.5)),
        Record = new TradingRecord(-5.25m, 12m, 1, 2, 3),
        Risk = RiskSettings.Default,
    };
}
