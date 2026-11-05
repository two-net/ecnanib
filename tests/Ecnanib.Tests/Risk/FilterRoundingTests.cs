using Ecnanib.Modules.RiskEngine.Contracts;
using static Ecnanib.Tests.Risk.RiskHarness;

namespace Ecnanib.Tests.Risk;

/// <summary>
/// FR-RISK-04 and NFR-MNT-04's exchange-filter rounding. Binance's valid prices and quantities are the filter's
/// minimum plus whole steps, and each is rounded in the direction that can't add risk.
/// </summary>
public sealed class FilterRoundingTests
{
    [Fact]
    public void A_long_rounds_its_limit_down_its_stop_up_and_its_target_down_on_a_grid_from_the_minimum()
    {
        // The grid is 0.25, 0.75, 1.25, …, so 100 → 99.75, 98 → 98.25 and 104 → 103.75.
        var inputs = Spot() with { Filters = Spot().Filters with { Price = new TickRule(0.25m, 1_000_000m, 0.5m) } };
        var intent = SpotBuy() with { EntryType = EntryType.Limit, LimitPrice = 100m };

        var evaluation = Evaluate(intent, inputs);
        var order = evaluation.Passes();

        Assert.Equal(99.75m, order.LimitPrice);
        Assert.Equal(98.25m, order.StopPrice);
        Assert.Equal(103.75m, order.TargetPrice);
        Assert.Equal("98", evaluation.Rule(RiskRule.Prices).Input("StopAsked"));
        Assert.Equal("98.25", evaluation.Rule(RiskRule.Prices).Input("Stop"));

        // Sized with the rounded prices: 1.5 a unit, so 10 ÷ 1.5 = 6.666.
        Assert.Equal(6.666m, order.Quantity);
    }

    [Fact]
    public void A_short_rounds_its_limit_up_its_stop_down_and_its_target_up()
    {
        var inputs = Futures() with { Filters = Futures().Filters with { Price = new TickRule(0.05m, 1_000_000m, 0.1m) } };
        var intent = FuturesShort() with { EntryType = EntryType.Limit, LimitPrice = 1000m };

        var order = Evaluate(intent, inputs).Passes();

        Assert.Equal(1000.05m, order.LimitPrice);
        Assert.Equal(1019.95m, order.StopPrice);
        Assert.Equal(960.05m, order.TargetPrice);
        Assert.Equal(EntryType.Limit, order.EntryType);
    }

    [Fact]
    public void A_tick_of_zero_leaves_prices_as_they_are()
    {
        var inputs = Spot() with { Filters = Spot().Filters with { Price = new TickRule(0m, 0m, 0m) } };
        var intent = SpotBuy() with { EntryType = EntryType.Limit, LimitPrice = 99.987m, StopPrice = 97.9871m, TargetPrice = null };

        var order = Evaluate(intent, inputs).Passes();

        Assert.Equal(99.987m, order.LimitPrice);
        Assert.Equal(97.9871m, order.StopPrice);
    }

    [Fact]
    public void A_price_above_the_filter_maximum_fails()
    {
        var inputs = Spot() with { Filters = Spot().Filters with { Price = new TickRule(0.01m, 99m, 0.01m) } };
        var intent = SpotBuy() with { EntryType = EntryType.Limit, LimitPrice = 99.5m, TargetPrice = null };

        Evaluate(intent, inputs).FailsOn(RiskRule.Prices, RiskReason.PriceOutOfRange);
    }

    [Fact]
    public void A_stop_below_the_filter_minimum_fails()
    {
        var inputs = Spot() with { Filters = Spot().Filters with { Price = new TickRule(98.5m, 1000m, 0.01m) } };

        Evaluate(SpotBuy(), inputs).FailsOn(RiskRule.Prices, RiskReason.PriceOutOfRange);
    }

    [Fact]
    public void A_limit_buy_above_the_percent_price_band_fails_and_one_below_a_missing_bound_passes()
    {
        // Futures bounds a buy from above only: at most 1.05 × the mark of 1,000.
        var above = FuturesLong() with { EntryType = EntryType.Limit, LimitPrice = 1060m, StopPrice = 1040m, TargetPrice = null };
        var below = FuturesLong() with { EntryType = EntryType.Limit, LimitPrice = 900m, StopPrice = 880m, TargetPrice = null };

        Evaluate(above, Futures()).FailsOn(RiskRule.Prices, RiskReason.PriceOutsideBand);
        Evaluate(below, Futures()).Passes();
    }

    [Fact]
    public void A_limit_sell_below_the_percent_price_band_fails()
    {
        var intent = FuturesShort() with { EntryType = EntryType.Limit, LimitPrice = 940m, StopPrice = 960m, TargetPrice = null };

        Evaluate(intent, Futures()).FailsOn(RiskRule.Prices, RiskReason.PriceOutsideBand);
    }

    [Fact]
    public void A_spot_target_is_a_limit_sell_and_must_be_in_the_band()
    {
        var inputs = Spot() with { Filters = Spot().Filters with { PercentPrice = new PercentPriceRule(1.05m, 0.95m, 1.05m, 0.95m) } };

        Evaluate(SpotBuy() with { TargetPrice = 106m }, inputs).FailsOn(RiskRule.Prices, RiskReason.PriceOutsideBand);
        Evaluate(SpotBuy() with { TargetPrice = 105m }, inputs).Passes();
    }

    [Fact]
    public void A_futures_band_needs_the_mark_price()
    {
        var inputs = Futures() with { Prices = Futures().Prices with { Mark = null } };
        var intent = FuturesLong() with { EntryType = EntryType.Limit, LimitPrice = 1000m };

        Evaluate(intent, inputs).FailsOn(RiskRule.Prices, RiskReason.MarkPriceMissing);
    }

    [Fact]
    public void A_market_order_uses_the_market_lot_step_and_a_limit_order_the_lot_step()
    {
        var inputs = Spot() with { Filters = Spot().Filters with { MarketLot = new LotRule(0.01m, 100m, 0.01m) } };

        // Stop 97 risks 3 a unit: 3.3333….
        var market = Evaluate(SpotBuy() with { StopPrice = 97m, TargetPrice = null }, inputs).Passes();
        var limit = Evaluate(SpotBuy() with { EntryType = EntryType.Limit, LimitPrice = 100m, StopPrice = 97m, TargetPrice = null }, inputs).Passes();

        Assert.Equal(3.33m, market.Quantity);
        Assert.Equal(3.333m, limit.Quantity);
    }

    [Fact]
    public void A_market_lot_step_of_zero_means_the_lot_step()
    {
        // The baseline's MARKET_LOT_SIZE has a step of 0, as Binance's Spot sends it.
        var order = Evaluate(SpotBuy() with { StopPrice = 97m, TargetPrice = null }, Spot()).Passes();

        Assert.Equal(3.333m, order.Quantity);
    }

    [Fact]
    public void A_quantity_grid_starts_at_the_lot_minimum()
    {
        // The grid is 0.0015, 0.0025, …, so 5 rounds down to 4.9995.
        var inputs = Spot() with { Filters = Spot().Filters with { Lot = new LotRule(0.0015m, 9000m, 0.001m) } };

        Assert.Equal(4.9995m, Evaluate(SpotBuy(), inputs).Passes().Quantity);
    }

    [Fact]
    public void A_quantity_below_the_minimum_fails()
    {
        var inputs = Spot() with { Filters = Spot().Filters with { Lot = new LotRule(6m, 9000m, 0.001m) } };

        Evaluate(SpotBuy(), inputs).FailsOn(RiskRule.Quantity, RiskReason.QuantityTooSmall);
    }

    [Fact]
    public void The_market_lot_minimum_applies_too()
    {
        var inputs = Spot() with { Filters = Spot().Filters with { MarketLot = new LotRule(6m, 100m, 0m) } };

        Evaluate(SpotBuy(), inputs).FailsOn(RiskRule.Quantity, RiskReason.QuantityTooSmall);
    }

    [Fact]
    public void The_minimum_notional_is_checked_at_the_lower_of_the_entry_and_the_last_price()
    {
        var inputs = Spot() with { Filters = Spot().Filters with { Notional = new NotionalRule(500m, null) } };

        Evaluate(SpotBuy(), inputs).Passes();
        Evaluate(SpotBuy(), inputs with { Prices = inputs.Prices with { Last = 99.9m } })
            .FailsOn(RiskRule.Notional, RiskReason.NotionalTooSmall);
    }

    [Fact]
    public void The_maximum_notional_caps_the_size_and_is_checked_at_the_higher_price()
    {
        var inputs = Spot() with { Filters = Spot().Filters with { Notional = new NotionalRule(5m, 450m) } };

        Assert.Equal(4.5m, Evaluate(SpotBuy(), inputs).Passes().Quantity);
        Evaluate(SpotBuy(), inputs with { Prices = inputs.Prices with { Last = 101m } })
            .FailsOn(RiskRule.Notional, RiskReason.NotionalTooLarge);
    }

    [Fact]
    public void A_missing_price_filter_fails_an_entry()
    {
        var inputs = Spot() with { Filters = Spot().Filters with { Price = null } };

        Evaluate(SpotBuy(), inputs).FailsOn(RiskRule.Prices, RiskReason.FilterMissing);
    }

    [Fact]
    public void A_missing_lot_filter_fails_an_entry()
    {
        var inputs = Spot() with { Filters = Spot().Filters with { Lot = null } };

        Evaluate(SpotBuy(), inputs).FailsOn(RiskRule.Size, RiskReason.FilterMissing);
    }

    [Fact]
    public void A_limit_entry_without_a_price_fails()
    {
        Evaluate(SpotBuy() with { EntryType = EntryType.Limit }, Spot()).FailsOn(RiskRule.Prices, RiskReason.PriceMissing);
    }

    [Fact]
    public void A_market_entry_without_a_quote_fails()
    {
        var inputs = Spot() with { Prices = new MarketPrices(0m, 0m, 0m) };

        Evaluate(SpotBuy(), inputs).FailsOn(RiskRule.Prices, RiskReason.PriceMissing);
    }
}
