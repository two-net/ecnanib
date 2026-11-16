using Ecnanib.DecisionEngine;
using Ecnanib.Shared;
using Ecnanib.Tests.Persistence;

namespace Ecnanib.Tests.DecisionEngine;

/// <summary>
/// There is a distinct module for each timeframe (FR-PRM-2), for each market (FR-PRM-3), and for
/// the Aggressive profile (FR-PRM-5), and each says what the specification outlines for it (SRS
/// 4.6). They are read here as the migrations stored them, in the version that is in use.
/// </summary>
public class PromptModuleTests
{
    private static readonly Lazy<TestDatabase> Migrated = new(() =>
    {
        var database = PostgresServer.CreateDatabase();
        database.Migrate();

        return database;
    });

    /// <summary>The codes of the candles that a module may name: the four timeframes, and the week, for context alone.</summary>
    private static readonly string[] CandleCodes = ["15m", "1H", "4H", "1D", "1W"];

    /// <summary>The expected holding periods of the four timeframes.</summary>
    private static readonly string[] HoldingPeriods = ["minutes to hours", "hours to days", "days to weeks", "weeks to months"];

    /// <summary>The table of the timeframe modules of the specification: the style, the context, and the holding period of each.</summary>
    public static TheoryData<string, string[], string[], string> TimeframeModules => new()
    {
        { "15m", ["intraday momentum and pullbacks", "close attention to fees and spread"], ["1H", "4H"], "minutes to hours" },
        { "1H", ["intraday to short swing"], ["4H", "1D"], "hours to days" },
        { "4H", ["Style: swing."], ["1D", "1W"], "days to weeks" },
        { "1D", ["Style: position."], ["1W"], "weeks to months" },
    };

    [Theory]
    [MemberData(nameof(TimeframeModules))]
    public void The_module_of_a_timeframe_sets_its_style_its_context_and_its_expected_holding_period(
        string timeframe, string[] style, string[] context, string holdingPeriod)
    {
        var module = StoredTemplate.InUse(Migrated.Value, PromptTemplateKind.Timeframe, timeframe);

        // The full wording, and no longer the outline that version 1 has.
        Assert.True(module.Version >= 2, $"The module of {timeframe} is in version {module.Version}.");

        // Three paragraphs, one for each thing that the module sets.
        var paragraphs = module.Text.Split('\n');

        Assert.Equal(3, paragraphs.Length);
        Assert.StartsWith("Style: ", paragraphs[0], StringComparison.Ordinal);
        Assert.StartsWith("Context: ", paragraphs[1], StringComparison.Ordinal);
        Assert.StartsWith("Expected holding period: ", paragraphs[2], StringComparison.Ordinal);

        // The style of the specification.
        Assert.All(style, phrase => Assert.Contains(phrase, paragraphs[0], StringComparison.OrdinalIgnoreCase));

        // The candles of the timeframe itself, with those of the higher timeframes of the
        // specification for context, and no others.
        Assert.StartsWith($"Context: closed {timeframe} candles, with ", paragraphs[1], StringComparison.Ordinal);
        Assert.Equal(
            CandleCodes.Where(code => code == timeframe || context.Contains(code)),
            CandleCodes.Where(code => paragraphs[1].Contains(code, StringComparison.Ordinal)));

        // Its own holding period, and that of no other timeframe.
        Assert.StartsWith($"Expected holding period: {holdingPeriod}.", paragraphs[2], StringComparison.Ordinal);
        Assert.Equal([holdingPeriod], HoldingPeriods.Where(period => module.Text.Contains(period, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void Each_timeframe_and_each_market_has_a_module_of_its_own()
    {
        var timeframes = Timeframes.All
            .Select(timeframe => StoredTemplate.InUse(Migrated.Value, PromptTemplateKind.Timeframe, timeframe.Code()))
            .ToList();
        var markets = Enum.GetValues<Market>()
            .Select(market => StoredTemplate.InUse(Migrated.Value, PromptTemplateKind.Market, market.ToString()))
            .ToList();

        Assert.Equal(4, timeframes.Select(module => module.Text).Distinct().Count());
        Assert.Equal(2, markets.Select(module => module.Text).Distinct().Count());
        Assert.Equal(6, timeframes.Concat(markets).Select(module => module.Id).Distinct().Count());

        // A module says what its kind is about, and nothing of the other: the module of a
        // timeframe names no market, and the module of a market no timeframe. So any of the
        // eight combinations is two modules that do not speak against each other.
        Assert.All(timeframes, module =>
            Assert.All(Enum.GetNames<Market>(), market => Assert.DoesNotContain(market, module.Text, StringComparison.OrdinalIgnoreCase)));
        Assert.All(markets, module =>
            Assert.All(CandleCodes, code => Assert.DoesNotContain(code, module.Text, StringComparison.Ordinal)));
    }

    [Fact]
    public void The_module_of_Spot_is_long_only_with_no_leverage_and_no_shorting()
    {
        var module = StoredTemplate.InUse(Migrated.Value, PromptTemplateKind.Market, "Spot");

        Assert.True(module.Version >= 2, $"The module of Spot is in version {module.Version}.");

        // Long-only, with the four actions of the specification and no other (FR-CYC-4).
        Assert.Contains("Long-only.", module.Text, StringComparison.Ordinal);
        Assert.Contains("The allowed actions are open long, hold, close, and adjust protection.", module.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("open short", module.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("reverse", module.Text, StringComparison.OrdinalIgnoreCase);

        // No leverage and no shorting: a bearish view means staying in the quote currency.
        Assert.Contains("No leverage and no shorting.", module.Text, StringComparison.Ordinal);
        Assert.Contains("A bearish view means staying in the quote currency", module.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_module_of_Futures_is_long_and_short_and_covers_what_a_leveraged_position_takes()
    {
        var module = StoredTemplate.InUse(Migrated.Value, PromptTemplateKind.Market, "Futures");

        Assert.True(module.Version >= 2, $"The module of Futures is in version {module.Version}.");

        // Long and short: the actions of Spot, with open short and reverse added (FR-CYC-4).
        Assert.Contains("Long and short.", module.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Long-only", module.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "The allowed actions are open long, open short, hold, close, reverse, and adjust protection.", module.Text, StringComparison.Ordinal);

        // Leverage within the cap, isolated margin, liquidation distance, funding cost over the
        // expected holding period, and reduce-only exits.
        Assert.All(
            new[]
            {
                "never goes above the leverage cap",
                "Isolated margin:",
                "Liquidation distance:",
                "the liquidation buffer",
                "funding cost over the expected holding period",
                "Exits are reduce-only",
            },
            covered => Assert.Contains(covered, module.Text, StringComparison.Ordinal));
    }

    [Fact]
    public void The_profile_module_says_how_the_Aggressive_profile_trades()
    {
        var module = StoredTemplate.InUse(Migrated.Value, PromptTemplateKind.Profile, "Aggressive");

        Assert.True(module.Version >= 2, $"The module of the profile is in version {module.Version}.");

        // Each instruction of the requirement starts a paragraph of its own (FR-PRM-5): look for
        // opportunities actively, act on moderate-conviction setups, use the full risk allowed,
        // and trade both directions on Futures, while always defining a stop-loss and staying
        // within the stated limits.
        var paragraphs = module.Text.Split('\n');

        Assert.All(
            new[]
            {
                "Look for opportunities actively.",
                "Act on moderate-conviction setups.",
                "Use the full risk allowed.",
                "Trade both directions on Futures:",
                "Always define a stop-loss.",
                "Stay within the stated limits.",
            },
            instruction => Assert.Single(paragraphs, paragraph => paragraph.StartsWith(instruction, StringComparison.Ordinal)));
    }
}
