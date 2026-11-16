using Ecnanib.Agents;
using Ecnanib.DecisionEngine;
using Ecnanib.Shared;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.Agents;
using Ecnanib.Tests.BackgroundWork;
using static Ecnanib.Tests.DecisionEngine.ComposedPrompts;

namespace Ecnanib.Tests.DecisionEngine;

/// <summary>
/// The prompt of a role call is composed from the instructions of the role, the modules of the
/// market, the timeframe, and the profile, the context of the agent, the data package, and
/// recalled lessons (FR-PRM-1). The modules follow from the settings of the agent, for each of
/// the eight combinations of a market and a timeframe, and no other can be chosen (FR-PRM-4).
/// </summary>
public class PromptCompositionTests
{
    private const string Ada = "ada@example.com";

    private const string Data = "Closed candles, indicators, and the context of the higher timeframes: the data package of the cycle.";

    private static readonly DateTimeOffset Morning = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_prompt_is_composed_from_its_seven_parts_in_the_order_of_the_specification()
    {
        using var application = new AccountsApplication();
        var inUse = await application.PromptTemplates.InUseAsync(
            Market.Futures, Timeframe.FourHours, TestContext.Current.CancellationToken);

        var parts = new PromptParts(
            Context(Market.Futures, Timeframe.FourHours, new PositionContext(PositionSide.Long, 61000m, 0.015m, 59500m, 64000m)),
            Data,
            ["The first lesson.", "The second lesson."]);

        foreach (var role in Enum.GetValues<Role>())
        {
            var prompt = await application.PromptComposition.ComposeAsync(role, parts, TestContext.Current.CancellationToken);

            // Each part once, under its heading, and each after the one before it.
            AssertInOrder(
                prompt.Text,
                $"# Role instructions\n{inUse.Roles[role].Text}\n\n",
                $"# Market\n{inUse.Market.Text}\n\n",
                $"# Timeframe\n{inUse.Timeframe.Text}\n\n",
                $"# Profile\n{inUse.Profile.Text}\n\n",
                "# Agent context\nSymbol: BTCUSDT, priced in USDT\n",
                "Equity: 1040.5 USDT\n",
                "Position: long, quantity 0.015, entry price 61000, stop-loss 59500, take-profit 64000\n",
                "Limits:\n",
                $"# Data package\n{Data}\n\n",
                "# Recalled lessons\n1. The first lesson.\n2. The second lesson.");

            Assert.StartsWith("# Role instructions\n", prompt.Text, StringComparison.Ordinal);
            Assert.EndsWith("2. The second lesson.", prompt.Text, StringComparison.Ordinal);
            Assert.Equal(Headings, HeadingsOf(prompt.Text));

            // The instructions of its own role, and of no other.
            Assert.All(
                Enum.GetValues<Role>().Where(other => other != role),
                other => Assert.DoesNotContain(inUse.Roles[other].Text, prompt.Text, StringComparison.Ordinal));

            // A line ends the same way on every system, and the same parts give the same prompt.
            Assert.DoesNotContain('\r', prompt.Text);
            Assert.Equal(
                prompt.Text,
                (await application.PromptComposition.ComposeAsync(role, parts, TestContext.Current.CancellationToken)).Text);
        }
    }

    [Fact]
    public async Task Each_of_the_eight_combinations_is_composed_with_the_modules_of_its_own_market_and_timeframe()
    {
        using var application = new AccountsApplication();

        // Asked for first: the application migrates its database as it starts.
        var composition = application.PromptComposition;

        // The six modules that a market or a timeframe has, each in the version in use.
        var modules = StoredTemplate.All(application.Database)
            .Where(template => template.IsActive && template.Kind is "Market" or "Timeframe")
            .ToList();
        var composed = new List<(int Market, int Timeframe)>();

        Assert.Equal(6, modules.Count);

        // Every market with every timeframe: the eight combinations of the specification.
        foreach (var market in Enum.GetValues<Market>())
        {
            foreach (var timeframe in Timeframes.All)
            {
                var inUse = await application.PromptTemplates.InUseAsync(market, timeframe, TestContext.Current.CancellationToken);
                var parts = new PromptParts(Context(market, timeframe), Data, []);

                foreach (var role in Enum.GetValues<Role>())
                {
                    var prompt = await composition.ComposeAsync(role, parts, TestContext.Current.CancellationToken);

                    // The module of its market and the module of its timeframe, and none of the
                    // other market or of the other three timeframes.
                    Assert.Contains($"# Market\n{inUse.Market.Text}\n\n", prompt.Text, StringComparison.Ordinal);
                    Assert.Contains($"# Timeframe\n{inUse.Timeframe.Text}\n\n", prompt.Text, StringComparison.Ordinal);
                    Assert.All(
                        modules.Where(module => module.Id != inUse.Market.Id && module.Id != inUse.Timeframe.Id),
                        other => Assert.DoesNotContain(other.Text, prompt.Text, StringComparison.Ordinal));

                    // The context says the same market and timeframe as the modules are of.
                    Assert.Contains($"\nMarket: {market}\nTimeframe: {timeframe.Code()}\n", prompt.Text, StringComparison.Ordinal);

                    // What the output of the call records (FR-PRM-6): the four templates that
                    // the prompt was composed from, in its order.
                    Assert.Equal(
                        [
                            (PromptTemplateKind.Role, role.ToString()),
                            (PromptTemplateKind.Market, market.ToString()),
                            (PromptTemplateKind.Timeframe, timeframe.Code()),
                            (PromptTemplateKind.Profile, "Aggressive"),
                        ],
                        prompt.Templates.Select(template => (template.Kind, template.Subject)));
                    Assert.Equal(inUse.For(role).Select(template => template.Id), prompt.Templates.Select(template => template.Id));

                    composed.Add((prompt.Templates[1].Id, prompt.Templates[2].Id));
                }
            }
        }

        // No two combinations were composed with the same two modules.
        Assert.Equal(8, composed.Distinct().Count());
    }

    [Fact]
    public async Task The_modules_follow_from_the_settings_of_the_agent_as_it_is_stored()
    {
        using var application = AgentsApplication.With(new StandInMarketData(), new TestClock(Morning));
        var user = await application.RegisterVerifiedAsync(Ada);

        var swing = await StoredAsync(application, await application.GiveAgentAsync(
            user.Id, Morning, name: "Swing", market: Market.Futures, timeframe: Timeframe.FourHours));
        var scalp = await StoredAsync(application, await application.GiveAgentAsync(
            user.Id, Morning, name: "Scalp", market: Market.Spot, symbol: "ETHUSDT", timeframe: Timeframe.FifteenMinutes));

        // The context of an agent is its own settings: nothing here names a market or a
        // timeframe but the agent.
        var context = AgentContext.Of(swing, equity: 1012.5m, position: null);

        Assert.Equal(
            new AgentContext(
                Market.Futures,
                Timeframe.FourHours,
                "BTCUSDT",
                "USDT",
                1012.5m,
                Position: null,
                new AgentRisk(1.5m, 1.75m, 4, 2.5m, 5m, 20m, 0.8m)),
            context);

        var ofSwing = await application.PromptComposition.ComposeAsync(
            Role.Strategist, new PromptParts(context, Data, []), TestContext.Current.CancellationToken);

        Assert.Equal(
            [
                (PromptTemplateKind.Role, "Strategist"),
                (PromptTemplateKind.Market, "Futures"),
                (PromptTemplateKind.Timeframe, "4H"),
                (PromptTemplateKind.Profile, "Aggressive"),
            ],
            ofSwing.Templates.Select(template => (template.Kind, template.Subject)));
        Assert.Contains("Symbol: BTCUSDT, priced in USDT\nMarket: Futures\nTimeframe: 4H\nEquity: 1012.5 USDT\n", ofSwing.Text, StringComparison.Ordinal);

        // An agent on another market and another timeframe is composed with the modules of its
        // own, and with the same role and the same profile.
        var ofScalp = await application.PromptComposition.ComposeAsync(
            Role.Strategist,
            new PromptParts(AgentContext.Of(scalp, scalp.AllocatedCapital, position: null), Data, []),
            TestContext.Current.CancellationToken);

        Assert.Equal(
            [
                (PromptTemplateKind.Role, "Strategist"),
                (PromptTemplateKind.Market, "Spot"),
                (PromptTemplateKind.Timeframe, "15m"),
                (PromptTemplateKind.Profile, "Aggressive"),
            ],
            ofScalp.Templates.Select(template => (template.Kind, template.Subject)));
        Assert.Contains("Symbol: ETHUSDT, priced in USDT\nMarket: Spot\nTimeframe: 15m\nEquity: 1000 USDT\n", ofScalp.Text, StringComparison.Ordinal);

        Assert.Equal(ofSwing.Templates[0].Id, ofScalp.Templates[0].Id);
        Assert.Equal(ofSwing.Templates[3].Id, ofScalp.Templates[3].Id);
        Assert.NotEqual(ofSwing.Templates[1].Id, ofScalp.Templates[1].Id);
        Assert.NotEqual(ofSwing.Templates[2].Id, ofScalp.Templates[2].Id);
    }

    [Fact]
    public async Task The_context_states_the_limits_of_the_agent_as_its_market_has_them()
    {
        using var application = new AccountsApplication();

        var spot = await application.PromptComposition.ComposeAsync(
            Role.RiskManager, new PromptParts(Context(Market.Spot, Timeframe.OneHour), Data, []), TestContext.Current.CancellationToken);

        // The settings of the agent, and the two limits that the specification fixes: one open
        // position, and on Spot a position worth at most the equity (SRS Appendix B). A number
        // is written the same in every country, with no zero after its last digit.
        Assert.Equal(
            "Symbol: BTCUSDT, priced in USDT\n" +
            "Market: Spot\n" +
            "Timeframe: 1H\n" +
            "Equity: 1040.5 USDT\n" +
            "Position: none\n" +
            "Limits:\n" +
            "- Risk per trade: at most 1.5% of equity\n" +
            "- Reward-to-risk ratio at entry: at least 1.75\n" +
            "- Distance of an entry from the current price: at most 0.8%\n" +
            "- Daily loss limit: 5% of equity\n" +
            "- Drawdown limit: 20% from peak equity\n" +
            "- Open positions: at most 1\n" +
            "- Position value: at most 100% of equity",
            PartOf(spot.Text, "Agent context"));

        var futures = await application.PromptComposition.ComposeAsync(
            Role.RiskManager, new PromptParts(Context(Market.Futures, Timeframe.OneHour), Data, []), TestContext.Current.CancellationToken);

        // The leverage cap and the liquidation buffer count on Futures only.
        Assert.Equal(
            "Symbol: BTCUSDT, priced in USDT\n" +
            "Market: Futures\n" +
            "Timeframe: 1H\n" +
            "Equity: 1040.5 USDT\n" +
            "Position: none\n" +
            "Limits:\n" +
            "- Risk per trade: at most 1.5% of equity\n" +
            "- Reward-to-risk ratio at entry: at least 1.75\n" +
            "- Distance of an entry from the current price: at most 0.8%\n" +
            "- Daily loss limit: 5% of equity\n" +
            "- Drawdown limit: 20% from peak equity\n" +
            "- Open positions: at most 1\n" +
            "- Leverage: at most 4x\n" +
            "- Liquidation buffer: the liquidation price at least 2.5 times as far from the entry as the stop-loss",
            PartOf(futures.Text, "Agent context"));
    }

    [Fact]
    public async Task The_context_states_the_position_of_the_agent_and_a_short_one_on_Futures_only()
    {
        using var application = new AccountsApplication();
        var composition = application.PromptComposition;

        // A short position, whose take-profit does not rest on the exchange.
        var shortPosition = new PositionContext(PositionSide.Short, 3050.25m, 2.00m, 3120m, TakeProfit: null);
        var onFutures = Context(Market.Futures, Timeframe.OneDay, shortPosition);

        var prompt = await composition.ComposeAsync(
            Role.Strategist, new PromptParts(onFutures, Data, []), TestContext.Current.CancellationToken);

        Assert.Contains(
            "\nPosition: short, quantity 2, entry price 3050.25, stop-loss 3120, take-profit none\n", prompt.Text, StringComparison.Ordinal);

        // An agent on Spot is long-only (FR-BIN-S1): no prompt is composed for a short position
        // of one, however the context came to say it.
        foreach (var onSpot in new[] { Context(Market.Spot, Timeframe.OneDay, shortPosition), onFutures with { Market = Market.Spot } })
        {
            var refused = await Assert.ThrowsAsync<ArgumentException>(() =>
                composition.ComposeAsync(Role.Strategist, new PromptParts(onSpot, Data, []), TestContext.Current.CancellationToken));

            Assert.Contains("long-only", refused.Message, StringComparison.Ordinal);
        }

        // A long one is stated on either market.
        var longOnSpot = await composition.ComposeAsync(
            Role.Strategist,
            new PromptParts(Context(Market.Spot, Timeframe.OneDay, shortPosition with { Side = PositionSide.Long }), Data, []),
            TestContext.Current.CancellationToken);

        Assert.Contains("\nPosition: long, quantity 2, entry price 3050.25, stop-loss 3120, take-profit none\n", longOnSpot.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  \n ")]
    public async Task A_call_that_is_given_no_data_and_no_lessons_has_all_seven_parts_and_says_so(string noData)
    {
        using var application = new AccountsApplication();

        var prompt = await application.PromptComposition.ComposeAsync(
            Role.SentimentAnalyst,
            new PromptParts(Context(Market.Spot, Timeframe.FifteenMinutes), noData, []),
            TestContext.Current.CancellationToken);

        Assert.Equal(Headings, HeadingsOf(prompt.Text));
        Assert.Equal("None.", PartOf(prompt.Text, "Data package"));
        Assert.Equal("None.", PartOf(prompt.Text, "Recalled lessons"));
    }

    [Fact]
    public async Task The_latest_version_of_a_module_is_the_one_that_a_prompt_is_composed_from()
    {
        using var application = new AccountsApplication();
        var composition = application.PromptComposition;

        var oneHour = new PromptParts(Context(Market.Spot, Timeframe.OneHour), Data, []);
        var fourHours = new PromptParts(Context(Market.Spot, Timeframe.FourHours), Data, []);

        var before = await composition.ComposeAsync(Role.MarketAnalyst, oneHour, TestContext.Current.CancellationToken);
        var otherBefore = await composition.ComposeAsync(Role.MarketAnalyst, fourHours, TestContext.Current.CancellationToken);

        // The module of the timeframe is edited, which is a new version.
        const string Edited = "Style: short swing, said anew.";
        var next = StoredTemplate.GiveNextVersion(application.Database, PromptTemplateKind.Timeframe, "1H", Edited);

        var after = await composition.ComposeAsync(Role.MarketAnalyst, oneHour, TestContext.Current.CancellationToken);

        Assert.Equal(Edited, PartOf(after.Text, "Timeframe"));
        Assert.DoesNotContain(before.Templates[2].Text, after.Text, StringComparison.Ordinal);

        // The prompt says which version it was composed from, and nothing else of it changed.
        Assert.Equal((next.Id, before.Templates[2].Version + 1), (after.Templates[2].Id, after.Templates[2].Version));
        Assert.Equal(
            before.Templates.Select(template => template.Id).Where((_, index) => index != 2),
            after.Templates.Select(template => template.Id).Where((_, index) => index != 2));
        Assert.Equal(
            before.Text.Replace(before.Templates[2].Text, Edited, StringComparison.Ordinal),
            after.Text);

        // An agent on another timeframe is composed as before.
        Assert.Equal(otherBefore.Text, (await composition.ComposeAsync(Role.MarketAnalyst, fourHours, TestContext.Current.CancellationToken)).Text);
    }

    /// <summary>Holds a text to having each of the parts once, and each after the one before it.</summary>
    private static void AssertInOrder(string text, params string[] parts)
    {
        var from = 0;

        foreach (var part in parts)
        {
            var at = text.IndexOf(part, StringComparison.Ordinal);

            Assert.True(at >= 0, $"The prompt does not have: {part}");
            Assert.True(at == text.LastIndexOf(part, StringComparison.Ordinal), $"The prompt has more than once: {part}");
            Assert.True(at >= from, $"The prompt has out of its order: {part}");

            from = at + part.Length;
        }
    }
}
