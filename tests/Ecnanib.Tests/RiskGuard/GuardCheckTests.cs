using Ecnanib.RiskGuard;
using Ecnanib.Shared;

namespace Ecnanib.Tests.RiskGuard;

/// <summary>
/// The Risk Guard rejects a plan to open a position unless all of these hold: a stop-loss is
/// present and on the correct side of entry; the reward-to-risk ratio meets the minimum; the
/// entry price is within the allowed distance of the current price; the order satisfies the
/// exchange's rules for price step, quantity step, and minimum value; the daily loss limit and
/// drawdown limit are not reached; the decision is not stale; the agent's mode, market, and
/// symbol are permitted; and no global halt is active. A plan that only closes a position or
/// adjusts protection is checked against the exchange's rules and FR-CYC-6 only, so that risk
/// can always be reduced (FR-RSK-4). On Futures, the estimated liquidation price must be further
/// from entry than the stop-loss by at least the liquidation buffer factor (FR-RSK-6). See "The
/// rules" in docs/risk-guard.md.
/// </summary>
/// <remarks>
/// The rules of a quantity and the liquidation buffer are asked of the size that the check
/// computes (FR-RSK-5), which no test hands it: a test changes a fact that the size follows
/// from. <see cref="OrderSizeTests"/> shows the sizes themselves.
/// </remarks>
public class GuardCheckTests
{
    private static readonly GuardRule[] EveryRule = Enum.GetValues<GuardRule>();

    [Fact]
    public void A_plan_to_open_a_position_is_accepted_when_every_rule_holds()
    {
        Assert.All(GuardCases.ThatOpen, opens =>
        {
            var result = GuardCheck.Of(opens);

            Assert.Equal(GuardVerdict.Accepted, result.Verdict);
            Assert.True(result.IsAccepted);
            Assert.Empty(result.InTheWay);

            // Every rule holds but the two that such a plan is not held to: the liquidation
            // buffer on Spot, where no position is liquidated, and protection that only
            // tightens, which is asked of an adjustment.
            GuardRule[] notAsked = opens.Market == Market.Spot
                ? [GuardRule.ProtectionTightens, GuardRule.LiquidationBuffer]
                : [GuardRule.ProtectionTightens];

            Assert.All(result.Rules, rule => Assert.Equal(
                notAsked.Contains(rule.Rule) ? RuleOutcome.NotApplied : RuleOutcome.Passed, rule.Outcome));

            // What was checked is in the result, with the size that the check computed for the
            // order: an order is made from those, and nothing else.
            Assert.Same(opens, result.Case);
            Assert.NotNull(result.Size);
            Assert.True(result.Size.Quantity > 0m);
            Assert.Equal(opens.Market == Market.Spot ? 1 : 2, result.Size.Leverage);
        });
    }

    [Fact]
    public void A_check_says_the_outcome_of_every_rule_whatever_became_of_the_plan()
    {
        GuardCase[] checkedCases =
        [
            .. GuardCases.ThatOpen,
            .. GuardCases.ThatReduceRisk,

            // A plan that many rules reject at once: none is skipped after the first that failed.
            GuardCases.OpenLong with { StopLoss = null, GlobalHalt = true, ModePermitted = false },
        ];

        Assert.All(checkedCases, checkedCase =>
            Assert.Equal(EveryRule, GuardCheck.Of(checkedCase).Rules.Select(rule => rule.Rule)));

        // The rules of the specification, pinned: a rule that is added or taken away shows in a
        // review as what it is.
        Assert.Equal(
            [
                "StopLossSide", "RewardToRisk", "EntryDistance", "PriceStep", "QuantityStep", "MinimumQuantity", "MinimumValue",
                "HeldPosition", "ProtectionTightens", "DailyLossLimit", "DrawdownLimit", "NotStale", "ModePermitted",
                "MarketPermitted", "SymbolPermitted", "NoGlobalHalt", "LiquidationBuffer",
            ],
            Enum.GetNames<GuardRule>());
    }

    /// <summary>The rules of a quantity, which an order of no quantity does not keep.</summary>
    private static readonly GuardRule[] OfAQuantity = [GuardRule.QuantityStep, GuardRule.MinimumQuantity, GuardRule.MinimumValue];

    public static TheoryData<string, GuardCase, GuardRule[]> WhatARuleRejects => new()
    {
        // A stop-loss is present and on the correct side of entry. A plan with no stop-loss on
        // the correct side has no risk to measure its reward against either, and none to size
        // an order by: its order comes to no quantity.
        { "no stop-loss", GuardCases.OpenLong with { StopLoss = null }, [GuardRule.StopLossSide, GuardRule.RewardToRisk, .. OfAQuantity] },
        {
            "a stop-loss above the entry of a long",
            GuardCases.OpenLong with { StopLoss = 60500m },
            [GuardRule.StopLossSide, GuardRule.RewardToRisk, .. OfAQuantity]
        },
        {
            "a stop-loss at the entry",
            GuardCases.OpenLong with { StopLoss = 60000m },
            [GuardRule.StopLossSide, GuardRule.RewardToRisk, .. OfAQuantity]
        },
        {
            "a stop-loss below the entry of a short",
            GuardCases.OpenShort with { StopLoss = 59500m },
            [GuardRule.StopLossSide, GuardRule.RewardToRisk, .. OfAQuantity, GuardRule.LiquidationBuffer]
        },

        // The reward-to-risk ratio meets the minimum, which is 1.5: 1400 against 1000 is 1.4.
        { "a reward-to-risk ratio below the minimum", GuardCases.OpenLong with { TakeProfit = 61400m }, [GuardRule.RewardToRisk] },
        { "a take-profit on the losing side", GuardCases.OpenLong with { TakeProfit = 59500m }, [GuardRule.RewardToRisk] },
        { "no take-profit", GuardCases.OpenLong with { TakeProfit = null }, [GuardRule.RewardToRisk] },
        { "a short whose take-profit is too near", GuardCases.OpenShort with { TakeProfit = 58600m }, [GuardRule.RewardToRisk] },

        // The entry price is within the allowed distance of the current price, which is 1
        // percent: 700 from 60000 is more.
        {
            "an entry too far from the current price",
            GuardCases.OpenLong with { EntryPrice = 60700m, StopLoss = 59700m, TakeProfit = 62700m },
            [GuardRule.EntryDistance]
        },
        {
            "an entry too far below the current price",
            GuardCases.OpenLong with { EntryPrice = 59300m, StopLoss = 58300m, TakeProfit = 61300m },
            [GuardRule.EntryDistance]
        },

        // The order satisfies the exchange's rules for price step, quantity step, and minimum value.
        { "a stop-loss off the price step", GuardCases.OpenLong with { StopLoss = 59000.05m }, [GuardRule.PriceStep] },
        { "a take-profit off the price step", GuardCases.OpenLong with { TakeProfit = 62000.01m }, [GuardRule.PriceStep] },
        {
            "a limit entry off the price step",
            GuardCases.OpenLong with { EntryPrice = 60000.07m },
            [GuardRule.PriceStep]
        },
        // The quantity of an entry is computed onto the quantity step, so it is never off it.
        // A risk that buys less than one step comes to no quantity: 0.01 on a step of 0.1.
        {
            "a risk that buys less than one step of the quantity",
            GuardCases.OpenLong with { Symbol = GuardCases.Symbol with { QuantityStep = 0.1m } },
            OfAQuantity
        },
        {
            "an entry of less than the least quantity",
            GuardCases.OpenLong with { Symbol = GuardCases.Symbol with { MinQuantity = 0.011m } },
            [GuardRule.MinimumQuantity]
        },
        {
            "an entry worth less than the least value",
            GuardCases.OpenLong with { Symbol = GuardCases.Symbol with { MinValue = 600.01m } },
            [GuardRule.MinimumValue]
        },

        // An agent has one position at most.
        { "a position that is open already", GuardCases.OpenLong with { Position = GuardCases.Long }, [GuardRule.HeldPosition] },

        // The daily loss limit and drawdown limit are not reached: 6 percent of 1000 in the
        // day, and 25 percent from the peak. A loss at the limit has reached it.
        { "a loss of the day at the limit", GuardCases.OpenLong with { Equity = 940m }, [GuardRule.DailyLossLimit] },
        { "a loss of the day beyond the limit", GuardCases.OpenLong with { Equity = 900m }, [GuardRule.DailyLossLimit] },
        {
            "a drawdown at the limit",
            GuardCases.OpenLong with { Equity = 750m, EquityAtDayStart = 750m },
            [GuardRule.DrawdownLimit]
        },
        {
            "a drawdown beyond the limit, on a day with a gain",
            GuardCases.OpenLong with { Equity = 700m, EquityAtDayStart = 690m },
            [GuardRule.DrawdownLimit]
        },

        // The decision is not stale: the limit is 120 seconds after the close.
        {
            "a decision that is stale by the least",
            GuardCases.OpenLong with { SinceClose = TimeSpan.FromSeconds(120) + TimeSpan.FromTicks(1) },
            [GuardRule.NotStale]
        },
        { "a decision that is stale by much", GuardCases.OpenLong with { SinceClose = TimeSpan.FromHours(1) }, [GuardRule.NotStale] },

        // The agent's mode, market, and symbol are permitted.
        { "a mode that is not permitted", GuardCases.OpenLong with { ModePermitted = false }, [GuardRule.ModePermitted] },
        { "a market that is not permitted", GuardCases.OpenLong with { MarketPermitted = false }, [GuardRule.MarketPermitted] },
        {
            "a symbol that the exchange takes no orders in",
            GuardCases.OpenLong with { Symbol = GuardCases.Symbol with { Trading = false } },
            [GuardRule.SymbolPermitted]
        },

        // No global halt is active.
        { "a global halt", GuardCases.OpenLong with { GlobalHalt = true }, [GuardRule.NoGlobalHalt] },
        { "a global halt, for a reverse", GuardCases.Reverse with { GlobalHalt = true }, [GuardRule.NoGlobalHalt] },

        // On Futures, the liquidation is further from the entry than the stop-loss by the
        // buffer. At the leverage of 2 that the order takes it is 30000 away, and the stop-loss
        // 1000: 30 times as far, which is less than an agent that asks for 40 allows.
        {
            "a liquidation price too near the stop-loss",
            GuardCases.OpenLongOnFutures with { Limits = GuardCases.Limits with { LiquidationBuffer = 40m } },
            [GuardRule.LiquidationBuffer]
        },
        {
            "a liquidation price too near the stop-loss of a short",
            GuardCases.OpenShort with { Limits = GuardCases.Limits with { LiquidationBuffer = 31m } },
            [GuardRule.LiquidationBuffer]
        },
    };

    [Theory]
    [MemberData(nameof(WhatARuleRejects))]
    public void A_plan_to_open_a_position_is_rejected_when_a_rule_does_not_hold(string what, GuardCase opens, GuardRule[] broken)
    {
        var result = GuardCheck.Of(opens);

        Assert.True(result.Verdict == GuardVerdict.Rejected, $"A plan with {what} was accepted.");
        Assert.False(result.IsAccepted);

        // Exactly the rules that do not hold stand in its way, each as failed: every other
        // rule holds as it did, or is not asked.
        Assert.Equal(broken, result.InTheWay.Select(rule => rule.Rule));
        Assert.All(result.InTheWay, rule => Assert.Equal(RuleOutcome.Failed, rule.Outcome));
        Assert.All(
            result.Rules.Where(rule => !broken.Contains(rule.Rule)),
            rule => Assert.Contains(rule.Outcome, new[] { RuleOutcome.Passed, RuleOutcome.NotApplied }));
    }

    [Fact]
    public void A_symbol_that_its_market_does_not_have_is_not_permitted_and_has_no_rules_to_keep()
    {
        var result = GuardCheck.Of(GuardCases.OpenLong with { Symbol = null });

        Assert.Equal(GuardVerdict.Rejected, result.Verdict);
        Assert.Equal(RuleOutcome.Failed, result.Of(GuardRule.SymbolPermitted).Outcome);

        // The rules of the exchange cannot be asked of a symbol that it does not list, and no
        // order is sized without them.
        Assert.Equal(
            [GuardRule.PriceStep, .. OfAQuantity],
            result.InTheWay.Where(rule => rule.Outcome == RuleOutcome.Unknown).Select(rule => rule.Rule));
        Assert.Null(result.Size);
    }

    [Fact]
    public void A_rule_holds_at_its_limit()
    {
        // The limits are inclusive, as the bounds of an agent's settings are: a plan that is at
        // one keeps it.
        GuardCase[] atTheLimit =
        [
            // A ratio of exactly 1.5.
            GuardCases.OpenLong with { TakeProfit = 61500m },

            // An entry exactly 1 percent from the current price, above it and below it.
            GuardCases.OpenLong with { EntryPrice = 60600m, StopLoss = 59600m, TakeProfit = 62600m },
            GuardCases.OpenLong with { EntryPrice = 59400m, StopLoss = 58400m, TakeProfit = 61400m },

            // An entry of exactly the least quantity, and one worth exactly the least value.
            GuardCases.OpenLong with { Symbol = GuardCases.Symbol with { MinQuantity = 0.01m } },
            GuardCases.OpenLong with { Symbol = GuardCases.Symbol with { MinValue = 600m } },

            // A decision that is checked at the stale limit itself.
            GuardCases.OpenLong with { SinceClose = TimeSpan.FromSeconds(120) },

            // A liquidation exactly as far from the entry as the agent asks for: 30000 at the
            // leverage of 2, which is 30 times the 1000 to the stop-loss.
            GuardCases.OpenLongOnFutures with { Limits = GuardCases.Limits with { LiquidationBuffer = 30m } },
            GuardCases.OpenShort with { Limits = GuardCases.Limits with { LiquidationBuffer = 30m } },

            // A loss that is a little short of the limit, of the day and from the peak.
            GuardCases.OpenLong with { Equity = 940.01m },
            GuardCases.OpenLong with { Equity = 750.01m, EquityAtDayStart = 750.01m },

            // A gain: nothing was lost.
            GuardCases.OpenLong with { Equity = 1200m },
        ];

        Assert.All(atTheLimit, opens => Assert.Empty(opens.InTheWayOf()));

        // And a step past each rejects.
        Assert.Equal([GuardRule.EntryDistance], (GuardCases.OpenLong with { EntryPrice = 60600.1m, StopLoss = 59600m, TakeProfit = 62600m }).InTheWayOf());
        Assert.Equal([GuardRule.RewardToRisk], (GuardCases.OpenLong with { TakeProfit = 61499.9m }).InTheWayOf());
        Assert.Equal(
            [GuardRule.MinimumQuantity],
            (GuardCases.OpenLong with { Symbol = GuardCases.Symbol with { MinQuantity = 0.0101m } }).InTheWayOf());
        Assert.Equal(
            [GuardRule.LiquidationBuffer],
            (GuardCases.OpenLongOnFutures with { Limits = GuardCases.Limits with { LiquidationBuffer = 30.01m } }).InTheWayOf());
    }

    [Fact]
    public void An_entry_at_the_market_is_held_to_the_current_price_and_a_limit_entry_to_its_own()
    {
        // At the market, the entry is the price that the symbol last traded at: a stop-loss
        // that the price has fallen through since the plan was made is on the wrong side now.
        Assert.Contains(GuardRule.StopLossSide, (GuardCases.OpenLong with { CurrentPrice = 58900m }).InTheWayOf());

        var atMarket = GuardCheck.Of(GuardCases.OpenLong);

        Assert.Equal((59000m, 60000m), (atMarket.Of(GuardRule.StopLossSide).Measured, atMarket.Of(GuardRule.StopLossSide).Threshold));
        Assert.Equal(0m, atMarket.Of(GuardRule.EntryDistance).Measured);

        // A limit entry is measured from its own price: 2000 of reward against 1100 of risk.
        var atLimit = GuardCheck.Of(GuardCases.OpenLong with { EntryPrice = 60100m, TakeProfit = 62100m });

        Assert.Empty(atLimit.InTheWay);
        Assert.Equal(60100m, atLimit.Of(GuardRule.StopLossSide).Threshold);
        Assert.Equal(Math.Round(2000m / 1100m, 8), atLimit.Of(GuardRule.RewardToRisk).Measured);
    }

    [Fact]
    public void A_check_says_what_each_rule_measured_and_what_it_held_that_to()
    {
        // So that a user sees exactly why a plan was accepted or rejected (FR-RSK-9). With an
        // equity of 970 the order comes to 0.019: 2 percent of it over 1000, down to the step.
        var result = GuardCheck.Of(GuardCases.OpenLongOnFutures with { Equity = 970m, PeakEquity = 1100m });

        Assert.Equal(new OrderSize(0.019m, Leverage: 2), result.Size);

        (GuardRule Rule, decimal? Measured, decimal? Threshold)[] said =
            [.. result.Rules.Select(rule => (rule.Rule, rule.Measured, rule.Threshold))];

        Assert.Equal(
            [
                (GuardRule.StopLossSide, 59000m, 60000m),
                (GuardRule.RewardToRisk, 2m, 1.5m),
                (GuardRule.EntryDistance, 0m, 1m),
                (GuardRule.PriceStep, null, 0.1m),
                (GuardRule.QuantityStep, null, 0.001m),
                (GuardRule.MinimumQuantity, 0.019m, 0.001m),
                (GuardRule.MinimumValue, 1140m, 5m),
                (GuardRule.HeldPosition, 0m, FixedLimits.OpenPositionsPerAgent),
                (GuardRule.ProtectionTightens, null, null),

                // 30 of 1000 in the day, and 130 of 1100 from the peak.
                (GuardRule.DailyLossLimit, 3m, 6m),
                (GuardRule.DrawdownLimit, Math.Round(13000m / 1100m, 8), 25m),
                (GuardRule.NotStale, 30m, 120m),
                (GuardRule.ModePermitted, null, null),
                (GuardRule.MarketPermitted, null, null),
                (GuardRule.SymbolPermitted, null, null),
                (GuardRule.NoGlobalHalt, null, null),

                // At the leverage of 2 the liquidation is 30000 from the entry, and the stop-loss 1000.
                (GuardRule.LiquidationBuffer, 30m, 2m),
            ],
            said);

        // A rule that failed says which price or quantity it was.
        var offStep = GuardCheck.Of(GuardCases.OpenLong with { TakeProfit = 62000.01m }).Of(GuardRule.PriceStep);

        Assert.Equal((RuleOutcome.Failed, 62000.01m, 0.1m), (offStep.Outcome, offStep.Measured, offStep.Threshold));
    }

    [Fact]
    public void A_plan_to_open_a_position_is_rejected_for_a_rule_that_is_not_known_to_hold()
    {
        // The size of the order is not known: the plan says no risk fraction to compute it from.
        var unsized = GuardCheck.Of(GuardCases.OpenLong with { RiskFraction = null });

        Assert.Equal(GuardVerdict.Rejected, unsized.Verdict);
        Assert.Null(unsized.Size);
        Assert.Equal(OfAQuantity, unsized.InTheWay.Select(rule => rule.Rule));
        Assert.All(unsized.InTheWay, rule => Assert.Equal(RuleOutcome.Unknown, rule.Outcome));

        // On Futures the leverage is not known either.
        Assert.Equal(
            [.. OfAQuantity, GuardRule.LiquidationBuffer],
            (GuardCases.OpenShort with { RiskFraction = null }).InTheWayOf());

        // The exchange did not say the current price, which an entry at the market is: so the
        // distance to the stop-loss is not known, and neither is the size.
        var unpriced = GuardCheck.Of(GuardCases.OpenLong with { CurrentPrice = null });

        Assert.Null(unpriced.Size);
        Assert.Equal(
            [GuardRule.StopLossSide, GuardRule.RewardToRisk, GuardRule.EntryDistance, .. OfAQuantity],
            unpriced.InTheWay.Select(rule => rule.Rule));
        Assert.All(unpriced.InTheWay, rule => Assert.Equal(RuleOutcome.Unknown, rule.Outcome));

        // A limit entry has its own price, and only its distance from the current one is not known.
        Assert.Equal(
            [GuardRule.EntryDistance],
            (GuardCases.OpenLong with { CurrentPrice = null, EntryPrice = 60000m }).InTheWayOf());

        // The exchange did not say which symbols the market has, nor their rules.
        var unlisted = GuardCheck.Of(GuardCases.OpenLong with { SymbolsKnown = false, Symbol = null });

        Assert.Equal(GuardVerdict.Rejected, unlisted.Verdict);
        Assert.Null(unlisted.Size);
        Assert.Equal(
            [GuardRule.PriceStep, .. OfAQuantity, GuardRule.SymbolPermitted],
            unlisted.InTheWay.Select(rule => rule.Rule));
        Assert.All(unlisted.InTheWay, rule => Assert.Equal(RuleOutcome.Unknown, rule.Outcome));
    }

    [Fact]
    public void A_plan_that_only_closes_a_position_or_adjusts_protection_is_checked_against_the_rules_of_the_exchange_and_the_stop_loss_direction_only()
    {
        Assert.All(GuardCases.ThatReduceRisk, reduces =>
        {
            // Everything that would reject a plan which opens a position: a halt, a stale
            // decision, both loss limits reached, no key, a symbol that takes no orders, and a
            // price that the exchange did not say.
            var result = GuardCheck.Of(reduces with
            {
                GlobalHalt = true,
                SinceClose = TimeSpan.FromHours(6),
                Equity = 100m,
                ModePermitted = false,
                MarketPermitted = false,
                Symbol = GuardCases.Symbol with { Trading = false, MinValue = 1_000_000m },
                CurrentPrice = null,
            });

            // So that risk can always be reduced.
            Assert.Equal(GuardVerdict.Accepted, result.Verdict);
            Assert.Empty(result.InTheWay);

            Assert.All(result.Rules, rule =>
            {
                if (GuardCases.AskedOfWhatReducesRisk.Contains(rule.Rule))
                {
                    // Protection that only tightens is asked of an adjustment, and not of a close.
                    Assert.Equal(
                        rule.Rule == GuardRule.ProtectionTightens && reduces.Action == GuardedAction.Close
                            ? RuleOutcome.NotApplied
                            : RuleOutcome.Passed,
                        rule.Outcome);
                }
                else
                {
                    Assert.Equal(RuleOutcome.NotApplied, rule.Outcome);
                }
            });
        });
    }

    [Fact]
    public void A_plan_that_only_closes_a_position_or_adjusts_protection_is_rejected_by_the_rules_that_it_is_held_to()
    {
        // The rules of the exchange: a price that the adjustment names off its step, and a
        // quantity off its step.
        Assert.Equal([GuardRule.PriceStep], (GuardCases.Adjust with { StopLoss = 59800.05m }).InTheWayOf());
        Assert.Equal([GuardRule.PriceStep], (GuardCases.Adjust with { TakeProfit = 64000.01m }).InTheWayOf());
        Assert.Equal(
            [GuardRule.QuantityStep],
            (GuardCases.Close with { Position = GuardCases.Long with { Quantity = 0.0105m } }).InTheWayOf());

        // A close names no price, so none of it is off the step.
        Assert.Equal(RuleOutcome.Passed, GuardCases.Close.OutcomeOf(GuardRule.PriceStep));

        // What the agent holds now: a position that is gone has nothing to close or to protect.
        Assert.Equal([GuardRule.HeldPosition], (GuardCases.Close with { Position = null }).InTheWayOf());
        Assert.Equal([GuardRule.HeldPosition], (GuardCases.Adjust with { Position = null }).InTheWayOf());
    }

    [Theory]

    // A long position, whose stop-loss rests at 59500: up reduces risk, and down adds it.
    [InlineData(TradeSide.Long, 59800, true)]
    [InlineData(TradeSide.Long, 59500, true)]
    [InlineData(TradeSide.Long, 59499.9, false)]
    [InlineData(TradeSide.Long, 58000, false)]

    // A short position, whose stop-loss rests at 61000: down reduces risk, and up adds it.
    [InlineData(TradeSide.Short, 60500, true)]
    [InlineData(TradeSide.Short, 61000, true)]
    [InlineData(TradeSide.Short, 61000.1, false)]
    [InlineData(TradeSide.Short, 63000, false)]
    public void Adjust_protection_moves_the_stop_loss_only_in_the_direction_that_reduces_risk(TradeSide side, double movedTo, bool tightens)
    {
        var position = side == TradeSide.Long ? GuardCases.Long : GuardCases.Short;
        var result = GuardCheck.Of(GuardCases.Adjust with { Position = position, StopLoss = (decimal)movedTo });

        Assert.Equal(tightens ? GuardVerdict.Accepted : GuardVerdict.Rejected, result.Verdict);
        Assert.Equal(tightens ? RuleOutcome.Passed : RuleOutcome.Failed, result.Of(GuardRule.ProtectionTightens).Outcome);
        Assert.Equal(((decimal)movedTo, position.StopLoss), (result.Of(GuardRule.ProtectionTightens).Measured, result.Of(GuardRule.ProtectionTightens).Threshold));
    }

    [Fact]
    public void The_take_profit_moves_in_either_direction_and_a_position_with_no_stop_loss_takes_any()
    {
        // The take-profit alone, up and down.
        Assert.All(new[] { 70000m, 60100m }, movedTo =>
            Assert.Empty((GuardCases.Adjust with { StopLoss = null, TakeProfit = movedTo }).InTheWayOf()));

        // No stop-loss rests on the exchange: whatever is placed reduces the risk of having none.
        Assert.Empty((GuardCases.Adjust with { Position = GuardCases.Long with { StopLoss = null }, StopLoss = 50000m }).InTheWayOf());
    }

    [Fact]
    public void A_plan_that_only_reduces_risk_is_not_rejected_for_what_is_not_known()
    {
        // The exchange gave no answer: nothing is known of the symbol or of the price.
        Assert.All(GuardCases.ThatReduceRisk, reduces =>
        {
            var result = GuardCheck.Of(reduces with { SymbolsKnown = false, Symbol = null, CurrentPrice = null });

            // A rule of the exchange could not be asked, which stands in the way of no close.
            Assert.Equal(RuleOutcome.Unknown, result.Of(GuardRule.QuantityStep).Outcome);
            Assert.Equal(GuardVerdict.Accepted, result.Verdict);
            Assert.Empty(result.InTheWay);
        });

        // The same facts reject a plan that opens a position.
        Assert.Equal(
            GuardVerdict.Rejected,
            GuardCheck.Of(GuardCases.OpenLong with { SymbolsKnown = false, Symbol = null, CurrentPrice = null }).Verdict);
    }

    [Fact]
    public void A_reverse_is_held_to_every_rule_for_the_side_opposite_to_the_position_that_it_closes()
    {
        // It closes a long, and opens a short: the stop-loss stands above the entry.
        Assert.Empty(GuardCases.Reverse.InTheWayOf());

        // It closes a short, and opens a long: the stop-loss stands below the entry.
        var fromShort = GuardCases.Reverse with { Position = GuardCases.Short, StopLoss = 59000m, TakeProfit = 62000m };

        Assert.Empty(fromShort.InTheWayOf());
        Assert.Contains(GuardRule.StopLossSide, (fromShort with { StopLoss = 61000m, TakeProfit = 58000m }).InTheWayOf());

        // The order that closes is held to the quantity step as well as the one that opens.
        Assert.Equal(
            [GuardRule.QuantityStep],
            (GuardCases.Reverse with { Position = GuardCases.Long with { Quantity = 0.0105m } }).InTheWayOf());

        // With no position there is nothing to reverse, and no side to open.
        var nothingHeld = GuardCheck.Of(GuardCases.Reverse with { Position = null });

        Assert.Equal(GuardVerdict.Rejected, nothingHeld.Verdict);
        Assert.Equal(RuleOutcome.Failed, nothingHeld.Of(GuardRule.HeldPosition).Outcome);
        Assert.Equal(RuleOutcome.Unknown, nothingHeld.Of(GuardRule.StopLossSide).Outcome);
    }

    [Fact]
    public void The_liquidation_buffer_is_asked_on_Futures_only()
    {
        // A buffer that the plan would not keep on Futures.
        var strict = GuardCases.Limits with { LiquidationBuffer = 100m };
        var onSpot = GuardCheck.Of(GuardCases.OpenLong with { Limits = strict });

        Assert.Equal(RuleOutcome.NotApplied, onSpot.Of(GuardRule.LiquidationBuffer).Outcome);
        Assert.Equal(GuardVerdict.Accepted, onSpot.Verdict);

        // A position on Spot has no leverage: the order is sized with 1.
        Assert.Equal(1, onSpot.Size!.Leverage);

        Assert.Equal([GuardRule.LiquidationBuffer], (GuardCases.OpenLongOnFutures with { Limits = strict }).InTheWayOf());
    }

    [Fact]
    public void A_step_of_zero_is_no_rule()
    {
        // The exchange writes zero where it has no step, and no least or most.
        var noSteps = GuardCases.Symbol with { PriceStep = 0m, QuantityStep = 0m, MinQuantity = 0m, MaxQuantity = 0m, MinValue = 0m };
        var result = GuardCheck.Of(GuardCases.OpenLong with { Symbol = noSteps, StopLoss = 59000.123456m });

        Assert.Empty(result.InTheWay);

        // With no step to round down to, the quantity is kept to 8 decimal places: 10 over
        // 999.876544 is 0.0100012347 and more.
        Assert.Equal(0.01000123m, result.Size!.Quantity);
    }

    public static TheoryData<string, GuardCase> NumbersThatMakeNoSense => new()
    {
        { "a current price of zero", GuardCases.OpenLong with { CurrentPrice = 0m } },
        { "a current price below zero", GuardCases.OpenLong with { CurrentPrice = -1m } },
        { "a limit price of zero", GuardCases.OpenLong with { EntryPrice = 0m } },
        { "a limit price below zero", GuardCases.OpenLongOnFutures with { EntryPrice = -60000m } },
        { "a stop-loss below zero", GuardCases.OpenLong with { StopLoss = -59000m } },
        { "a take-profit of zero", GuardCases.OpenLong with { TakeProfit = 0m } },
        { "a risk fraction of zero", GuardCases.OpenLong with { RiskFraction = 0m } },
        { "a risk fraction below zero", GuardCases.OpenLong with { RiskFraction = -0.01m } },
        { "a risk per trade of zero", GuardCases.OpenLong with { Limits = GuardCases.Limits with { RiskPerTradePercent = 0m } } },
        { "a leverage cap of zero", GuardCases.OpenLongOnFutures with { Limits = GuardCases.Limits with { LeverageCap = 0 } } },
        { "an equity of zero", GuardCases.OpenLong with { Equity = 0m, EquityAtDayStart = 0m, PeakEquity = 0m } },
        { "an equity below zero", GuardCases.OpenLongOnFutures with { Equity = -1000m } },
        { "an equity of the day's start of zero", GuardCases.OpenLong with { EquityAtDayStart = 0m } },
        { "a peak equity below zero", GuardCases.OpenLong with { PeakEquity = -5m } },
        { "a price beyond every number", GuardCases.OpenLong with { EntryPrice = decimal.MaxValue } },
        { "a take-profit beyond every number", GuardCases.OpenLongOnFutures with { TakeProfit = decimal.MaxValue, CurrentPrice = decimal.MaxValue } },
        {
            "an equity beyond every number",
            GuardCases.OpenLong with { Equity = decimal.MaxValue, EquityAtDayStart = decimal.MaxValue, PeakEquity = decimal.MaxValue }
        },
        {
            "an equity beyond every number, on Futures",
            GuardCases.OpenLongOnFutures with { Equity = decimal.MaxValue, EquityAtDayStart = decimal.MaxValue, PeakEquity = decimal.MaxValue }
        },
        { "a position of no quantity to close", GuardCases.Close with { Position = GuardCases.Long with { Quantity = 0m } } },
        { "a stop-loss of zero to move to", GuardCases.Adjust with { StopLoss = 0m } },
    };

    [Theory]
    [MemberData(nameof(NumbersThatMakeNoSense))]
    public void A_number_that_makes_no_sense_is_a_rule_that_does_not_hold_and_no_error(string what, GuardCase checkedCase)
    {
        // Nothing that a check is handed makes it throw: it answers, with every rule, and a
        // plan with such a number does not go on.
        var result = GuardCheck.Of(checkedCase);

        Assert.Equal(EveryRule, result.Rules.Select(rule => rule.Rule));
        Assert.True(result.Verdict == GuardVerdict.Rejected, $"A plan with {what} was accepted.");
    }
}
