using System.Text.RegularExpressions;
using Ecnanib.Agents.Abstractions;
using Ecnanib.Council.Abstractions;
using Ecnanib.Council.Configuration;
using Ecnanib.Council.Services;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;

namespace Ecnanib.Tests.Council.Services;

/// <summary>What every role receives (FR-AI-06, FR-AI-09, FR-AI-12) and how the Risk Manager is told its options (FR-AI-10).</summary>
public partial class RolePromptsTests
{
    private static readonly CouncilOptions Options = new();

    [Fact]
    public void TheMessageCarriesEverySectionInOrder()
    {
        var prompt = RolePrompts.User(CouncilTestHost.Context(mark: new MarkPrice("BTCUSDT", 100.5m, 100.4m, 0.0001m, CouncilTestHost.CandleClose.AddHours(4), CouncilTestHost.Now)), CouncilRole.MarketAnalyst, null, [], Options);

        string[] sections =
        [
            "# Evaluation of BTCUSDT (Spot, 1h) at the candle close of 2026-01-05 12:00Z", "## Agent configuration", "## Strategy engine output", "## Recent candles (1h",
            "## Higher timeframe 1d", "## Futures mark price and funding", "## Position and open orders", "## Your experience in this role", "## Preceding roles", "## Your task",
        ];
        var positions = sections.Select(s => prompt.IndexOf(s, StringComparison.Ordinal)).ToList();
        Assert.All(positions, p => Assert.True(p >= 0));
        Assert.Equal(positions.Order(), positions);
        Assert.Contains("Candidate setup: long limit entry at 100, stop 96, targets [108].", prompt);
        Assert.Contains("\"strategyId\":\"trend-pullback\"", prompt);
        Assert.Contains("\"maxStopDistanceAtr\":3", prompt);
        Assert.Contains("risk per trade 1% of equity, at most 1 open position(s), daily loss limit 3%", prompt);
        Assert.Contains("No open position.", prompt);
        Assert.Contains("No open orders.", prompt);
        Assert.Contains("No experience recorded yet.", prompt);
        Assert.Contains("You are the first role.", prompt);
        Assert.Contains("funding rate 0.0100% per interval", prompt);
    }

    [Fact]
    public void TheCandleSummaryIsBounded()
    {
        var options = new CouncilOptions { RecentCandles = 5, HigherTimeframeCandles = 3 };

        var prompt = RolePrompts.User(CouncilTestHost.Context(), CouncilRole.SentimentFlow, null, [], options);

        Assert.Equal(8, CandleLine().Matches(prompt).Count);
        Assert.Contains("2026-01-05 11:00Z 99 101 98 100 100", prompt);
        Assert.DoesNotContain("2026-01-05 06:00Z", prompt);
    }

    [Fact]
    public void DiscretionaryAndPausedStatesAreStated()
    {
        var paused = RolePrompts.User(CouncilTestHost.Context(CouncilTestHost.NewAgent(state: AgentState.Paused)), CouncilRole.RiskManager, null, [], Options);
        Assert.Contains("New entries: NOT allowed", paused);
        Assert.Contains("the only valid decision is no_trade", paused);

        var discretionary = RolePrompts.User(CouncilTestHost.Context(CouncilTestHost.NewAgent(discretionary: true), CouncilTestHost.Output(null)), CouncilRole.RiskManager, null, [], Options);
        Assert.Contains("Discretionary mode: ON", discretionary);
        Assert.Contains("No candidate setup this candle.", discretionary);
        Assert.Contains("decide open_long with", discretionary);

        var plain = RolePrompts.User(CouncilTestHost.Context(output: CouncilTestHost.Output(null)), CouncilRole.RiskManager, null, [], Options);
        Assert.Contains("Discretionary mode: OFF", plain);
        Assert.Contains("discretionary mode is off, so the only valid decision is no_trade", plain);
    }

    [Fact]
    public void TheRiskManagerIsToldTheDirectionsAndThePosition()
    {
        var spot = RolePrompts.User(CouncilTestHost.Context(), CouncilRole.RiskManager, null, [], Options);
        Assert.Contains("decide open_long with", spot);
        Assert.DoesNotContain("open_long or open_short", spot);
        Assert.Contains("Direction: long only (Spot).", spot);

        var futures = RolePrompts.User(CouncilTestHost.Context(CouncilTestHost.NewAgent(MarketType.UsdFutures)), CouncilRole.RiskManager, null, [], Options);
        Assert.Contains("decide open_long or open_short with", futures);
        Assert.Contains("Leverage 3x, isolated margin", futures);

        var agent = CouncilTestHost.NewAgent();
        var position = RolePrompts.User(CouncilTestHost.Context(agent, trades: [CouncilTestHost.OpenTrade(agent)], orders: [CouncilTestHost.EntryOrder(agent)]), CouncilRole.RiskManager, null, [], Options);
        Assert.Contains("A position is open: decide hold, modify", position);
        Assert.Contains("long 0.5 BTCUSDT entered at 100 on 2026-01-05 08:00Z; current stop 95; targets [110]; +0% against the entry", position);
        Assert.Contains("entry limit buy 0.5 at 99, new.", position);
    }

    [Fact]
    public void PrecedingRolesAreListedWithTheirStatus()
    {
        var document = new RoleOutputDocument("Weak.", 0.3m, DecisionAction.NoTrade, null, null, null, [], ["setup_weak"]);
        PrecedingTurn[] preceding =
        [
            new(CouncilRole.MarketAnalyst, RoleOutputStatus.Succeeded, null, document),
            new(CouncilRole.SentimentFlow, RoleOutputStatus.Failed, "the answer was rejected twice", null),
            new(CouncilRole.CrossMarketContext, RoleOutputStatus.Disabled, null, null),
        ];

        var prompt = RolePrompts.User(CouncilTestHost.Context(), CouncilRole.RiskManager, "Win rate 60%.", preceding, Options).ReplaceLineEndings("\n");

        Assert.Contains("### Market Analyst\n{\"assessment\":\"Weak.\",\"confidence\":0.3,\"proposed_action\":\"no_trade\",\"entry_type\":null,\"entry_price\":null,\"stop_price\":null,\"targets\":[],\"flags\":[\"setup_weak\"]}", prompt);
        Assert.Contains("### Sentiment & Flow\nNot available: failed (the answer was rejected twice).", prompt);
        Assert.Contains("### Cross-Market Context\nNot available: disabled for this agent.", prompt);
        Assert.Contains("## Your experience in this role\nWin rate 60%.", prompt);
    }

    [Fact]
    public void TheInstructionsNameTheRoleAndTheBounds()
    {
        var options = new CouncilOptions { MaxAssessmentLength = 333, MaxRationaleLength = 222 };

        foreach (var role in CouncilRoles.Order)
        {
            var system = RolePrompts.System(role, options);
            Assert.Contains($"You are the {CouncilRoles.Display(role)}", system);
            Assert.Contains(CouncilRoles.Responsibility(role), system);
            Assert.Contains("at most 333 characters", system);
            Assert.Contains("never widened", system);
            Assert.Contains("never propose open_short on Spot", system);
        }

        Assert.Contains("at most 222 characters", RolePrompts.System(CouncilRole.RiskManager, options));
        Assert.DoesNotContain("at most 222 characters", RolePrompts.System(CouncilRole.MarketAnalyst, options));
    }

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}Z 99 101 98 100 100$", RegexOptions.Multiline)]
    private static partial Regex CandleLine();
}
