using Ecnanib.Catalog;
using Ecnanib.DecisionEngine;

namespace Ecnanib.Tests.DecisionEngine;

/// <summary>
/// What the calls of AI models add up to: calls, tokens, and estimated cost where pricing is
/// known (FR-RPT-3, FR-KEY-11). Where it is not known, the cost is unknown, and not zero.
/// </summary>
public class UsageFiguresTests
{
    private static readonly ModelPrice Sonnet = new(InputPerMillionTokens: 3m, OutputPerMillionTokens: 15m);
    private static readonly ModelPrice Opus = new(InputPerMillionTokens: 15m, OutputPerMillionTokens: 75m);

    [Fact]
    public void The_calls_of_priced_models_add_up_to_calls_tokens_and_a_cost()
    {
        var figures = UsageFigures.Of(
        [
            new ModelUsage(Calls: 96, Counted: 96, InputTokens: 1_000_000, OutputTokens: 100_000, LiveSearches: 0, Sonnet),
            new ModelUsage(Calls: 4, Counted: 4, InputTokens: 200_000, OutputTokens: 20_000, LiveSearches: 12, Opus),
        ]);

        // 3 + 1.5 for the first, and 3 + 1.5 for the second.
        Assert.Equal(new UsageFigures(Calls: 100, NotCounted: 0, InputTokens: 1_200_000, OutputTokens: 120_000, LiveSearches: 12, EstimatedCost: 9m), figures);
    }

    [Fact]
    public void A_model_with_no_price_makes_the_cost_unknown_and_not_smaller()
    {
        var figures = UsageFigures.Of(
        [
            new ModelUsage(Calls: 96, Counted: 96, InputTokens: 1_000_000, OutputTokens: 100_000, LiveSearches: 0, Sonnet),
            new ModelUsage(Calls: 4, Counted: 4, InputTokens: 200_000, OutputTokens: 20_000, LiveSearches: 0, Price: null),
        ]);

        // The tokens are known. What they cost is not: the cost of the priced model alone would
        // read as the cost of them all.
        Assert.Equal(100, figures.Calls);
        Assert.Equal(1_200_000, figures.InputTokens);
        Assert.Equal(120_000, figures.OutputTokens);
        Assert.Null(figures.EstimatedCost);
    }

    [Fact]
    public void Calls_that_were_not_counted_are_calls_and_the_cost_is_of_the_others()
    {
        var figures = UsageFigures.Of(
        [
            new ModelUsage(Calls: 10, Counted: 7, InputTokens: 700_000, OutputTokens: 70_000, LiveSearches: 0, Sonnet),
        ]);

        // Three calls timed out or failed. They are counted as calls, and said to be without a
        // count, and the tokens and the cost are of the seven: 2.1 + 1.05.
        Assert.Equal(new UsageFigures(Calls: 10, NotCounted: 3, InputTokens: 700_000, OutputTokens: 70_000, LiveSearches: 0, EstimatedCost: 3.15m), figures);
    }

    [Fact]
    public void Where_no_call_was_counted_the_tokens_and_the_cost_are_unknown_and_not_zero()
    {
        var figures = UsageFigures.Of(
        [
            new ModelUsage(Calls: 2, Counted: 0, InputTokens: 0, OutputTokens: 0, LiveSearches: 0, Sonnet),
        ]);

        // A call that timed out may be charged for, so nothing here says "no tokens" or "no cost".
        Assert.Equal(new UsageFigures(Calls: 2, NotCounted: 2, InputTokens: null, OutputTokens: null, LiveSearches: 0, EstimatedCost: null), figures);
    }

    [Fact]
    public void A_model_that_was_not_counted_needs_no_price()
    {
        var figures = UsageFigures.Of(
        [
            new ModelUsage(Calls: 5, Counted: 5, InputTokens: 1_000_000, OutputTokens: 0, LiveSearches: 0, Sonnet),

            // It added no tokens, so its missing price hides nothing of the cost.
            new ModelUsage(Calls: 1, Counted: 0, InputTokens: 0, OutputTokens: 0, LiveSearches: 0, Price: null),
        ]);

        Assert.Equal(new UsageFigures(Calls: 6, NotCounted: 1, InputTokens: 1_000_000, OutputTokens: 0, LiveSearches: 0, EstimatedCost: 3m), figures);
    }

    [Fact]
    public void A_model_that_costs_nothing_has_a_cost_of_nothing_which_is_known()
    {
        var figures = UsageFigures.Of(
        [
            new ModelUsage(Calls: 5, Counted: 5, InputTokens: 1_000_000, OutputTokens: 100_000, LiveSearches: 0, new ModelPrice(0m, 0m)),
        ]);

        // Zero here is a price, and no missing one.
        Assert.Equal(0m, figures.EstimatedCost);
    }

    [Fact]
    public void No_calls_add_up_to_no_calls_and_nothing_known_of_them()
    {
        Assert.Equal(
            new UsageFigures(Calls: 0, NotCounted: 0, InputTokens: null, OutputTokens: null, LiveSearches: 0, EstimatedCost: null),
            UsageFigures.Of([]));
    }
}
