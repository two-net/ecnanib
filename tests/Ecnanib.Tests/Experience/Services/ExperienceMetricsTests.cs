using System.Text.Json;
using Ecnanib.Ai.Abstractions;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Experience.Services;

namespace Ecnanib.Tests.Experience.Services;

/// <summary>The metrics' arithmetic (FR-EXP-04) and the roll-up to the three scopes (FR-EXP-05).</summary>
public class ExperienceMetricsTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 5, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void DerivesEveryRatioFromTheCountsAndSums()
    {
        var metrics = new ExperienceMetrics
        {
            Decisions = 8, Approvals = 3, Rejections = 4, Failures = 2, TotalLatencyMs = 16_000, RiskManagerTurns = 6, Clamped = 1,
            Trades = 4, Wins = 3, Losses = 1, SumR = 5m, GrossProfitR = 6m, GrossLossR = 1m, NetTrades = 4, SumNetR = 4.2m, ScopeGrossLossR = 4m,
            RejectionsResolved = 3, RejectionsMissedWins = 1, RejectionsSumR = 0.5m,
        }.Derive();

        Assert.Equal(0.75m, metrics.WinRate);
        Assert.Equal(1.25m, metrics.AverageR);
        Assert.Equal(1.05m, metrics.Expectancy);
        Assert.Equal(6m, metrics.ProfitFactor);
        Assert.Equal(0.25m, metrics.DrawdownContribution);
        Assert.Equal(0.1667m, metrics.ClampRate);
        Assert.Equal(0.25m, metrics.FailureRate);
        Assert.Equal(2000, metrics.AverageLatencyMs);
        Assert.Equal(0.6667m, metrics.RejectionAccuracy);
    }

    [Fact]
    public void ARatioWithoutADenominatorIsNull()
    {
        var metrics = new ExperienceMetrics { Decisions = 2, Trades = 1, Wins = 1, SumR = 2m, GrossProfitR = 2m }.Derive();

        Assert.Equal(1m, metrics.WinRate);
        Assert.Null(metrics.ProfitFactor);
        Assert.Null(metrics.Expectancy);
        Assert.Null(metrics.DrawdownContribution);
        Assert.Null(metrics.ClampRate);
        Assert.Null(metrics.RejectionAccuracy);
        Assert.Equal(0m, metrics.FailureRate);
        var empty = new ExperienceMetrics().Derive();
        Assert.Null(empty.WinRate);
        Assert.Null(empty.AverageR);
        Assert.Null(empty.FailureRate);
        Assert.Null(empty.AverageLatencyMs);
    }

    [Fact]
    public void AddSumsEverythingButTheOwnersLossDenominator()
    {
        var a = new ExperienceMetrics { Decisions = 1, Trades = 1, SumR = 1m, ScopeGrossLossR = 5m, RejectionsResolved = 1 };
        var b = new ExperienceMetrics { Decisions = 2, Trades = 3, SumR = -0.5m, ScopeGrossLossR = 7m, Clamped = 1, TotalLatencyMs = 10 };

        var sum = a.Clone().Add(b);

        Assert.Equal(3, sum.Decisions);
        Assert.Equal(4, sum.Trades);
        Assert.Equal(0.5m, sum.SumR);
        Assert.Equal(1, sum.Clamped);
        Assert.Equal(10, sum.TotalLatencyMs);
        Assert.Equal(1, sum.RejectionsResolved);
        Assert.Equal(5m, sum.ScopeGrossLossR);
        Assert.Equal(1, a.Decisions);
    }

    [Fact]
    public void RecordTradeAttributesAnEntryProposalAndAnOverruledRejectionDifferently()
    {
        var approver = new ExperienceMetrics();
        var objector = new ExperienceMetrics();

        ExperienceAggregates.RecordTrade(approver, DecisionAction.OpenLong, 2m, 18m, 100m, 1m, 90m);
        ExperienceAggregates.RecordTrade(approver, DecisionAction.OpenShort, -0.5m, -6m, 100m, 1m, 110m);
        ExperienceAggregates.RecordTrade(approver, DecisionAction.OpenLong, 0m, -1m, 100m, 1m, null);
        ExperienceAggregates.RecordTrade(objector, DecisionAction.NoTrade, 2m, 18m, 100m, 1m, 90m);
        ExperienceAggregates.RecordTrade(objector, DecisionAction.Hold, 2m, 18m, 100m, 1m, 90m);

        Assert.Equal(3, approver.Trades);
        Assert.Equal(1, approver.Wins);
        Assert.Equal(1, approver.Losses);
        Assert.Equal(1.5m, approver.SumR);
        Assert.Equal(2m, approver.GrossProfitR);
        Assert.Equal(0.5m, approver.GrossLossR);
        Assert.Equal(2, approver.NetTrades);
        Assert.Equal(1.2m, approver.SumNetR);
        Assert.Equal(0, approver.RejectionsResolved);
        Assert.Equal(0, objector.Trades);
        Assert.Equal(1, objector.RejectionsResolved);
        Assert.Equal(1, objector.RejectionsMissedWins);
        Assert.Equal(2m, objector.RejectionsSumR);
    }

    [Fact]
    public void RecordShadowCountsAMissedWinAndAnAvoidedLoss()
    {
        var metrics = new ExperienceMetrics();

        ExperienceAggregates.RecordShadow(metrics, ShadowResult.HitTarget, 2m);
        ExperienceAggregates.RecordShadow(metrics, ShadowResult.HitStop, -1m);

        Assert.Equal(2, metrics.RejectionsResolved);
        Assert.Equal(1, metrics.RejectionsMissedWins);
        Assert.Equal(1m, metrics.RejectionsSumR);
        Assert.Equal(0.5m, metrics.Derive().RejectionAccuracy);
    }

    [Fact]
    public void TheInitialStopComesFromTheStopHistoryThenTheDecision()
    {
        var history = JsonDocument.Parse("""[{"at":"2026-01-05T00:00:00Z","price":95,"reason":"initial"},{"at":"2026-01-05T01:00:00Z","price":98,"reason":"trail"}]""").RootElement;

        Assert.Equal(95m, ExperienceAggregates.InitialStop(history, 90m));
        Assert.Equal(90m, ExperienceAggregates.InitialStop(JsonValues.EmptyArray, 90m));
        Assert.Null(ExperienceAggregates.InitialStop(JsonValues.EmptyArray, null));
        Assert.Equal(90m, ExperienceAggregates.InitialStop(JsonDocument.Parse("""[{"price":"x"}]""").RootElement, 90m));
    }

    [Fact]
    public void AssembleRollsAgentRowsUpToUsersAndTheSystemWithTheirOwnLossDenominators()
    {
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        var agentA1 = Guid.NewGuid();
        var agentA2 = Guid.NewGuid();
        var agentB1 = Guid.NewGuid();
        var rows = new Dictionary<AgentStatKey, ExperienceMetrics>
        {
            [Key(agentA1, userA, "c1")] = new() { Decisions = 2, Trades = 2, Wins = 1, Losses = 1, SumR = 1m, GrossProfitR = 2m, GrossLossR = 1m, RiskManagerTurns = 2, Clamped = 1 },
            [Key(agentA2, userA, "c1")] = new() { Decisions = 3, Trades = 1, Losses = 1, SumR = -1m, GrossLossR = 1m, RiskManagerTurns = 3 },
            [Key(agentB1, userB, "c1")] = new() { Decisions = 1, Trades = 1, Wins = 1, SumR = 3m, GrossProfitR = 3m, RiskManagerTurns = 1 },
            [Key(agentB1, userB, "c2")] = new() { Decisions = 4, Failures = 4 },
        };
        var losses = new Dictionary<LossKey, decimal>
        {
            [new LossKey(agentA1, userA, CandleInterval.OneHour, MarketType.Spot)] = 4m,
            [new LossKey(agentA2, userA, CandleInterval.OneHour, MarketType.Spot)] = 1m,
        };

        var stats = ExperienceAggregates.Assemble(rows, losses, Now);

        Assert.Equal(4, stats.Count(s => s.Scope == StatScope.Agent));
        Assert.Equal(3, stats.Count(s => s.Scope == StatScope.User));
        Assert.Equal(2, stats.Count(s => s.Scope == StatScope.System));
        Assert.All(stats, s => Assert.Equal(Now, s.ComputedAt));
        Assert.All(stats.Where(s => s.Scope == StatScope.System), s => { Assert.Null(s.AgentId); Assert.Null(s.UserId); });
        Assert.All(stats.Where(s => s.Scope == StatScope.User), s => { Assert.Null(s.AgentId); Assert.NotNull(s.UserId); });

        var a1 = stats.Single(s => s.Scope == StatScope.Agent && s.AgentId == agentA1);
        Assert.Equal(4m, a1.Metrics.ScopeGrossLossR);
        Assert.Equal(0.25m, a1.Metrics.DrawdownContribution);
        Assert.Equal(0.5m, a1.Metrics.ClampRate);

        var userARow = stats.Single(s => s.Scope == StatScope.User && s.UserId == userA);
        Assert.Equal(5, userARow.Metrics.Decisions);
        Assert.Equal(3, userARow.Metrics.Trades);
        Assert.Equal(0m, userARow.Metrics.SumR);
        Assert.Equal(5m, userARow.Metrics.ScopeGrossLossR);
        Assert.Equal(0.4m, userARow.Metrics.DrawdownContribution);
        Assert.Equal(0.2m, userARow.Metrics.ClampRate);

        var system = stats.Single(s => s.Scope == StatScope.System && s.Model == "c1");
        Assert.Equal(6, system.Metrics.Decisions);
        Assert.Equal(4, system.Metrics.Trades);
        Assert.Equal(3m, system.Metrics.SumR);
        Assert.Equal(0.75m, system.Metrics.AverageR);
        Assert.Equal(5m, system.Metrics.ScopeGrossLossR);
        Assert.Equal(0.4m, system.Metrics.DrawdownContribution);
        var failing = stats.Single(s => s.Scope == StatScope.System && s.Model == "c2");
        Assert.Equal(1m, failing.Metrics.FailureRate);
        Assert.Null(failing.Metrics.WinRate);
    }

    private static AgentStatKey Key(Guid agentId, Guid userId, string model) =>
        new(agentId, userId, AiProvider.Claude, model, CouncilRole.RiskManager, CandleInterval.OneHour, MarketType.Spot);
}
