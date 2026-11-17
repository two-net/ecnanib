using Ecnanib.RiskGuard;
using Ecnanib.Shared;

namespace Ecnanib.Tests.RiskGuard;

/// <summary>
/// Models do not choose quantity or leverage. The system computes quantity from the approved
/// risk fraction, the agent's equity, and the stop distance. On Spot, position value is capped at
/// the agent's equity. On Futures, the system uses the lowest leverage setting that fits the
/// position's margin within the agent's equity, up to the leverage cap, and reduces quantity if
/// the cap would be exceeded (FR-RSK-5). See "The size of an order" in docs/risk-guard.md.
/// </summary>
/// <remarks>
/// The size is what a check computed: <c>GuardCheck.Of(case).Size</c>. Nothing hands a check a
/// size, so a test changes the facts that it follows from. The price is 60000, the quantity step
/// 0.001, the equity 1000, the agent's risk per trade 2 percent, and its leverage cap 5, unless
/// a test says otherwise.
/// </remarks>
public class OrderSizeTests
{
    private static readonly OrderSize NoQuantity = new(Quantity: 0m, Leverage: 1);

    public static TheoryData<string, GuardCase, decimal, int> KnownSizes => new()
    {
        // The quantity is the risk over the distance to the stop-loss: 1 percent of 1000 is 10,
        // and 10 over 1000 is 0.01.
        { "1 percent of 1000 over a distance of 1000", GuardCases.OpenLong, 0.01m, 1 },
        { "a stop-loss twice as far", GuardCases.OpenLong with { StopLoss = 58000m }, 0.005m, 1 },
        { "half the risk fraction", GuardCases.OpenLong with { RiskFraction = 0.005m }, 0.005m, 1 },
        { "twice the equity", GuardCases.OpenLong with { Equity = 2000m }, 0.02m, 1 },
        { "a limit entry, measured from its own price", GuardCases.OpenLong with { EntryPrice = 60100m }, 0.009m, 1 },
        { "a limit entry while the exchange says no price", GuardCases.OpenLong with { EntryPrice = 60000m, CurrentPrice = null }, 0.01m, 1 },

        // It is rounded down to the quantity step: 9.5 over 1000 is 0.0095, and 10 over 600 is 0.0166.
        { "a quantity between two steps", GuardCases.OpenLong with { Equity = 950m }, 0.009m, 1 },
        { "a quantity that does not end", GuardCases.OpenLong with { StopLoss = 59400m }, 0.016m, 1 },

        // The risk fraction is held to the agent's risk per trade: 5 percent counts as 2.
        { "a risk fraction above the risk per trade", GuardCases.OpenLong with { RiskFraction = 0.05m, StopLoss = 58000m }, 0.01m, 1 },

        // On Spot, the position is worth at most the equity: 1000 buys 0.0166 at 60000.
        { "a stop-loss so near that the position would be worth 60 times the equity", GuardCases.OpenLong with { StopLoss = 59990m }, 0.016m, 1 },
        {
            "a position worth exactly the equity",
            GuardCases.OpenLong with { Equity = 1200m, RiskFraction = 0.02m },
            0.02m,
            1
        },

        // An order is for at most what the exchange takes, on its step.
        { "more than the exchange takes in an order", GuardCases.OpenLong with { Symbol = GuardCases.Symbol with { MaxQuantity = 0.005m } }, 0.005m, 1 },
        { "a most quantity off the step", GuardCases.OpenLong with { Symbol = GuardCases.Symbol with { MaxQuantity = 0.0055m } }, 0.005m, 1 },

        // On Futures, the lowest leverage whose margin fits within the equity. 0.02 is worth
        // 1200, which takes 2, and 0.01 is worth 600, which takes none.
        { "a position worth more than the equity", GuardCases.OpenLongOnFutures, 0.02m, 2 },
        { "a position worth less than the equity", GuardCases.OpenLongOnFutures with { RiskFraction = 0.01m }, 0.01m, 1 },
        {
            "a position worth exactly three times the equity",
            GuardCases.OpenLongOnFutures with { RiskFraction = 0.01m, StopLoss = 59800m },
            0.05m,
            3
        },
        {
            "a position worth a little more than three times the equity",
            GuardCases.OpenLongOnFutures with { RiskFraction = 0.0102m, StopLoss = 59800m },
            0.051m,
            4
        },

        // Up to the leverage cap: a quantity that would take more is reduced. 0.2 would be
        // worth 12000, and 5 times the equity buys 0.0833.
        { "a position that would take more than the leverage cap", GuardCases.OpenLongOnFutures with { StopLoss = 59900m }, 0.083m, 5 },
        {
            "a leverage cap of 4",
            GuardCases.OpenLongOnFutures with { RiskFraction = 0.01m, StopLoss = 59900m, Limits = GuardCases.Limits with { LeverageCap = 4 } },
            0.066m,
            4
        },
        {
            "a leverage cap of 1",
            GuardCases.OpenLongOnFutures with { StopLoss = 59900m, Limits = GuardCases.Limits with { LeverageCap = 1 } },
            0.016m,
            1
        },
        {
            "the highest leverage cap that a file allows",
            GuardCases.OpenLongOnFutures with
            {
                RiskFraction = 0.05m, StopLoss = 59999m, Limits = GuardCases.Limits with { RiskPerTradePercent = 5m, LeverageCap = 125 },
            },
            2.083m,
            125
        },

        // A short is sized by the distance up to its stop-loss, and a reverse as what it opens.
        { "a short position", GuardCases.OpenShort, 0.02m, 2 },
        { "a short position with a stop-loss 100 above", GuardCases.OpenShort with { StopLoss = 60100m }, 0.083m, 5 },
        { "a reverse", GuardCases.Reverse, 0.02m, 2 },
    };

    [Theory]
    [MemberData(nameof(KnownSizes))]
    public void The_quantity_follows_from_the_risk_fraction_the_equity_and_the_stop_distance(
        string what, GuardCase opens, decimal quantity, int leverage)
    {
        var size = GuardCheck.Of(opens).Size;

        Assert.True(size == new OrderSize(quantity, leverage), $"The order of {what} came to {size}, and not to {quantity} at {leverage}.");
    }

    [Fact]
    public void A_plan_that_opens_nothing_has_no_size_and_neither_has_one_with_a_fact_missing()
    {
        // A plan that only closes a position or adjusts protection has no order of its own to size.
        Assert.All(GuardCases.ThatReduceRisk, reduces => Assert.Null(GuardCheck.Of(reduces).Size));

        GuardCase[] unknown =
        [
            // No risk fraction to compute it from.
            GuardCases.OpenLong with { RiskFraction = null },

            // The exchange did not say the price that an entry at the market is.
            GuardCases.OpenLong with { CurrentPrice = null },

            // Nor the rules of the symbol, whose step a quantity is rounded to.
            GuardCases.OpenLong with { SymbolsKnown = false, Symbol = null },
            GuardCases.OpenLong with { Symbol = null },

            // A reverse of a position that is gone opens no side that is known.
            GuardCases.Reverse with { Position = null },
        ];

        Assert.All(unknown, opens =>
        {
            var result = GuardCheck.Of(opens);

            // What is not known rejects a plan that opens a position, and is no size of zero.
            Assert.Null(result.Size);
            Assert.Equal(GuardVerdict.Rejected, result.Verdict);
            Assert.Equal(RuleOutcome.Unknown, result.Of(GuardRule.MinimumQuantity).Outcome);
        });
    }

    public static TheoryData<string, GuardCase> WhatGivesNoOrder => new()
    {
        { "no stop-loss", GuardCases.OpenLong with { StopLoss = null } },
        { "a stop-loss at the entry", GuardCases.OpenLong with { StopLoss = 60000m } },
        { "a stop-loss above the entry of a long", GuardCases.OpenLong with { StopLoss = 60500m } },
        { "a stop-loss below the entry of a short", GuardCases.OpenShort with { StopLoss = 59500m } },
        { "a limit price that is no price", GuardCases.OpenLong with { EntryPrice = 0m } },
        { "no equity", GuardCases.OpenLong with { Equity = 0m } },
        { "a risk fraction of zero", GuardCases.OpenLong with { RiskFraction = 0m } },
        { "a risk that buys less than one step", GuardCases.OpenLong with { Equity = 50m } },
        { "a step larger than the quantity", GuardCases.OpenLong with { Symbol = GuardCases.Symbol with { QuantityStep = 0.1m } } },
        { "a leverage cap below 1", GuardCases.OpenLongOnFutures with { Limits = GuardCases.Limits with { LeverageCap = 0 } } },
        { "an equity beyond every number", GuardCases.OpenLongOnFutures with { Equity = decimal.MaxValue } },
    };

    [Theory]
    [MemberData(nameof(WhatGivesNoOrder))]
    public void Facts_that_give_no_order_come_to_no_quantity_and_the_plan_is_rejected(string what, GuardCase opens)
    {
        var result = GuardCheck.Of(opens);

        Assert.True(result.Size == NoQuantity, $"The order of a plan with {what} came to {result.Size}.");

        // The rules of a quantity do not hold for it: they failed, and are not unknown.
        Assert.Equal(GuardVerdict.Rejected, result.Verdict);
        Assert.Equal(RuleOutcome.Failed, result.Of(GuardRule.QuantityStep).Outcome);
        Assert.Equal(RuleOutcome.Failed, result.Of(GuardRule.MinimumQuantity).Outcome);
        Assert.Equal(RuleOutcome.Failed, result.Of(GuardRule.MinimumValue).Outcome);
    }

    [Fact]
    public void A_quantity_is_never_raised_to_a_minimum_of_the_exchange()
    {
        // 0.01 is less than the least quantity, and worth less than the least value: the size
        // stays what the risk allows, and the rules reject the plan.
        var tooSmall = GuardCheck.Of(GuardCases.OpenLong with { Symbol = GuardCases.Symbol with { MinQuantity = 0.05m, MinValue = 5000m } });

        Assert.Equal(new OrderSize(0.01m, 1), tooSmall.Size);
        Assert.Equal(GuardVerdict.Rejected, tooSmall.Verdict);
        Assert.Equal([GuardRule.MinimumQuantity, GuardRule.MinimumValue], tooSmall.InTheWay.Select(rule => rule.Rule));
    }

    [Fact]
    public void Every_size_keeps_the_risk_and_the_caps_and_is_the_most_that_does()
    {
        decimal[] equities = [50m, 333.33m, 1000m, 12345.67m];
        decimal[] fractions = [0.005m, 0.01m, 0.0137m, 0.02m, 0.05m];
        decimal[] distances = [7.3m, 100m, 250m, 600m, 1000m, 3333.3m];
        int[] caps = [1, 3, 5, 10];
        const decimal Entry = 60000m;
        var step = GuardCases.Symbol.QuantityStep;
        var sized = 0;

        foreach (var opens in new[] { GuardCases.OpenLong, GuardCases.OpenLongOnFutures, GuardCases.OpenShort })
        {
            foreach (var equity in equities)
            {
                foreach (var fraction in fractions)
                {
                    foreach (var distance in distances)
                    {
                        foreach (var cap in caps)
                        {
                            var stop = opens.Action == GuardedAction.OpenShort ? Entry + distance : Entry - distance;
                            var size = GuardCheck.Of(opens with
                            {
                                Equity = equity, RiskFraction = fraction, StopLoss = stop, Limits = GuardCases.Limits with { LeverageCap = cap },
                            }).Size;

                            Assert.NotNull(size);

                            // What the size is held to: the risk that was approved, at most the
                            // agent's 2 percent, and the most that the position is worth.
                            var onFutures = opens.Market == Market.Futures;
                            var risked = equity * Math.Min(fraction, 0.02m);
                            var mostValue = onFutures ? equity * cap : equity;
                            var value = size.Quantity * Entry;
                            var said = $"{size} for an equity of {equity}, a fraction of {fraction}, a distance of {distance}, and a cap of {cap}";

                            Assert.True(size.Quantity >= 0m && size.Quantity % step == 0m, $"Off the step: {said}.");
                            Assert.True(size.Quantity * distance <= risked, $"More risk than was approved: {said}.");
                            Assert.True(value <= mostValue, $"Worth more than the cap: {said}.");

                            // One step more would break one of them: nothing is left unused.
                            var more = size.Quantity + step;

                            Assert.True(more * distance > risked || more * Entry > mostValue, $"A step more would fit: {said}.");

                            // The leverage is the lowest whose margin fits within the equity.
                            Assert.InRange(size.Leverage, 1, onFutures ? cap : 1);
                            Assert.True(value <= equity * size.Leverage, $"The margin does not fit: {said}.");
                            Assert.True(size.Leverage == 1 || value > equity * (size.Leverage - 1), $"A lower leverage would fit: {said}.");

                            sized += size.Quantity > 0m ? 1 : 0;
                        }
                    }
                }
            }
        }

        // Most of them come to an order: the test is no test of sizes of nothing.
        Assert.True(sized > 1000, $"Only {sized} sizes came to a quantity.");
    }
}
