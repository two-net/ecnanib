using Ecnanib.Ai.Abstractions;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Experience.Services;
using Ecnanib.Tests.Agents.Live;
using Ecnanib.Tests.Database.Live;
using Ecnanib.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ecnanib.Tests.Experience.Live;

/// <summary>The statistics computed from real rows at the three scopes (FR-EXP-04, FR-EXP-05) and the stale-shadow sweep (FR-EXP-03).</summary>
[Trait("Category", "Live")]
public class ExperienceRefresherLiveTests : IClassFixture<DatabaseLiveFixture>
{
    private static readonly DateTimeOffset Now = AgentsLiveSeed.Now;

    private readonly DatabaseLiveFixture _fixture;

    public ExperienceRefresherLiveTests(DatabaseLiveFixture fixture)
    {
        _fixture = fixture;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ComputesTheThreeScopesFromTurnsTradesAndShadowsAndReplacesThemNextRun()
    {
        _fixture.Require();
        await _fixture.MigrateAsync();
        var userA = await AgentsLiveSeed.NewUserAsync(_fixture, "exp-refresh-a@example.com");
        var userB = await AgentsLiveSeed.NewUserAsync(_fixture, "exp-refresh-b@example.com");
        var a1 = await AgentsLiveSeed.NewAgentAsync(_fixture, userA.Id, "A1", model: "c1");
        var b1 = await AgentsLiveSeed.NewAgentAsync(_fixture, userB.Id, "B1", model: "c1");
        var t0 = Now.AddDays(-3);
        var rm = (DecisionAction? proposed, int latency) => ExperienceLiveSeed.Turn(CouncilRole.RiskManager, AiProvider.Claude, "c1", proposed, latencyMs: latency);

        // D1: everyone but Sentiment & Flow proposed the entry; the hard limits clamped the risk; the trade won +2R (net +1.8R).
        var d1 = await ExperienceLiveSeed.NewDecisionAsync(_fixture, a1, t0, DecisionAction.OpenLong,
        [
            ExperienceLiveSeed.Turn(CouncilRole.MarketAnalyst, AiProvider.Gpt, "g1", DecisionAction.OpenLong, latencyMs: 1200),
            ExperienceLiveSeed.Turn(CouncilRole.SentimentFlow, AiProvider.Grok, "k1", DecisionAction.NoTrade, latencyMs: 800),
            ExperienceLiveSeed.Turn(CouncilRole.CrossMarketContext, AiProvider.Gemini, "m1", DecisionAction.OpenLong),
            rm(DecisionAction.OpenLong, 2000),
        ], ExperienceLiveSeed.LimitsClamped);
        await ExperienceLiveSeed.NewClosedTradeAsync(_fixture, a1, d1, 100m, 90m, 120m, 1m, 2m, t0.AddHours(5));
        // D2: Sentiment & Flow failed; the trade lost −0.5R (net −0.6R).
        var d2 = await ExperienceLiveSeed.NewDecisionAsync(_fixture, a1, t0.AddHours(1), DecisionAction.OpenLong,
        [
            ExperienceLiveSeed.Turn(CouncilRole.MarketAnalyst, AiProvider.Gpt, "g1", DecisionAction.OpenLong, latencyMs: 1200),
            ExperienceLiveSeed.Turn(CouncilRole.SentimentFlow, AiProvider.Grok, "k1", null, RoleOutputStatus.Failed, 800),
            rm(DecisionAction.OpenLong, 1000),
        ]);
        await ExperienceLiveSeed.NewClosedTradeAsync(_fixture, a1, d2, 100m, 90m, 95m, 1m, 1m, t0.AddHours(8), TradeExitReason.Stop);
        // D3: the Risk Manager vetoed the analyst's entry; the shadow hit the stop (a loss avoided).
        var d3 = await ExperienceLiveSeed.NewDecisionAsync(_fixture, a1, t0.AddHours(2), DecisionAction.NoTrade,
            [ExperienceLiveSeed.Turn(CouncilRole.MarketAnalyst, AiProvider.Gpt, "g1", DecisionAction.OpenLong, latencyMs: 1200), rm(DecisionAction.NoTrade, 1000)]);
        await ExperienceLiveSeed.NewShadowAsync(_fixture, d3, t0.AddHours(2), ShadowResult.HitStop, -1m);
        // D4: both said no; the shadow hit the target (a missed win for both).
        var d4 = await ExperienceLiveSeed.NewDecisionAsync(_fixture, a1, t0.AddHours(3), DecisionAction.NoTrade,
            [ExperienceLiveSeed.Turn(CouncilRole.MarketAnalyst, AiProvider.Gpt, "g1", DecisionAction.NoTrade, latencyMs: 1200), rm(DecisionAction.NoTrade, 1000)]);
        await ExperienceLiveSeed.NewShadowAsync(_fixture, d4, t0.AddHours(3), ShadowResult.HitTarget, 2m);
        // D5 and D6: rejections whose shadows nobody will walk any more: one long past its horizon, one without a detail.
        var d5 = await ExperienceLiveSeed.NewDecisionAsync(_fixture, a1, Now.AddDays(-60), DecisionAction.NoTrade, [rm(DecisionAction.NoTrade, 1000)]);
        await ExperienceLiveSeed.NewShadowAsync(_fixture, d5, Now.AddDays(-60), ShadowResult.Pending);
        var d6 = await ExperienceLiveSeed.NewDecisionAsync(_fixture, a1, t0.AddHours(4), DecisionAction.NoTrade, [rm(DecisionAction.NoTrade, 1000)]);
        await ExperienceLiveSeed.NewShadowAsync(_fixture, d6, t0.AddHours(4), ShadowResult.Pending, withDetail: false);
        // The other user's agent: one approval that won +1R.
        var d7 = await ExperienceLiveSeed.NewDecisionAsync(_fixture, b1, t0, DecisionAction.OpenLong, [rm(DecisionAction.OpenLong, 1000)]);
        await ExperienceLiveSeed.NewClosedTradeAsync(_fixture, b1, d7, 100m, 90m, 110m, 2m, 0m, t0.AddHours(6));

        var time = new FakeTimeProvider(Now);
        var logs = new TestLoggerProvider();
        await using var provider = ExperienceTestHost.BuildServices(_fixture.ConnectionString, logs, time: time);
        var refresher = provider.GetRequiredService<ExperienceRefresher>();

        var result = await refresher.RefreshOnceAsync(Ct);

        Assert.Equal(new ExperienceRefreshResult(14, 2, 0), result);
        var stats = await ExperienceLiveSeed.StatsAsync(_fixture);
        Assert.Equal(5, stats.Count(s => s.Scope == StatScope.Agent));
        Assert.Equal(5, stats.Count(s => s.Scope == StatScope.User));
        Assert.Equal(4, stats.Count(s => s.Scope == StatScope.System));
        Assert.All(stats, s => Assert.Equal(Now, s.ComputedAt));

        var riskManager = stats.Single(s => s.Scope == StatScope.Agent && s.AgentId == a1.Id && s.Role == CouncilRole.RiskManager);
        Assert.Equal((AiProvider.Claude, "c1", CandleInterval.OneHour, MarketType.Spot, userA.Id), (riskManager.Provider, riskManager.Model, riskManager.Timeframe, riskManager.MarketType, riskManager.UserId));
        var m = riskManager.Metrics;
        Assert.Equal((6, 2, 4, 0, 6, 1), (m.Decisions, m.Approvals, m.Rejections, m.Failures, m.RiskManagerTurns, m.Clamped));
        Assert.Equal((2, 1, 1, 1.5m, 2m, 0.5m, 2, 1.2m, 0.5m), (m.Trades, m.Wins, m.Losses, m.SumR, m.GrossProfitR, m.GrossLossR, m.NetTrades, m.SumNetR, m.ScopeGrossLossR));
        Assert.Equal((2, 1, 1m), (m.RejectionsResolved, m.RejectionsMissedWins, m.RejectionsSumR));
        Assert.Equal((0.5m, 0.75m, 0.6m, 4m, 1m, 0.1667m, 0m, 1166, 0.5m), (m.WinRate, m.AverageR, m.Expectancy, m.ProfitFactor, m.DrawdownContribution, m.ClampRate, m.FailureRate, m.AverageLatencyMs, m.RejectionAccuracy));

        var analyst = stats.Single(s => s.Scope == StatScope.Agent && s.AgentId == a1.Id && s.Role == CouncilRole.MarketAnalyst).Metrics;
        Assert.Equal((4, 3, 1, 0, 0, 0), (analyst.Decisions, analyst.Approvals, analyst.Rejections, analyst.Failures, analyst.RiskManagerTurns, analyst.Clamped));
        Assert.Equal((2, 1.5m, 1, 1, 2m), (analyst.Trades, analyst.SumR, analyst.RejectionsResolved, analyst.RejectionsMissedWins, analyst.RejectionsSumR));
        Assert.Null(analyst.ClampRate);
        Assert.Equal(1200, analyst.AverageLatencyMs);

        var sentiment = stats.Single(s => s.Scope == StatScope.Agent && s.AgentId == a1.Id && s.Role == CouncilRole.SentimentFlow).Metrics;
        Assert.Equal((2, 0, 1, 1, 0.5m, 0), (sentiment.Decisions, sentiment.Approvals, sentiment.Rejections, sentiment.Failures, sentiment.FailureRate, sentiment.Trades));
        Assert.Equal((1, 1, 2m, 0m), (sentiment.RejectionsResolved, sentiment.RejectionsMissedWins, sentiment.RejectionsSumR, sentiment.RejectionAccuracy));

        var context = stats.Single(s => s.Scope == StatScope.Agent && s.AgentId == a1.Id && s.Role == CouncilRole.CrossMarketContext).Metrics;
        Assert.Equal((1, 1, 1, 1, 2m, 1, 1.8m), (context.Decisions, context.Approvals, context.Trades, context.Wins, context.SumR, context.NetTrades, context.SumNetR));

        var userRow = stats.Single(s => s.Scope == StatScope.User && s.UserId == userA.Id && s.Role == CouncilRole.RiskManager);
        Assert.Null(userRow.AgentId);
        Assert.Equal(riskManager.Metrics.Decisions, userRow.Metrics.Decisions);
        Assert.Equal(riskManager.Metrics.Expectancy, userRow.Metrics.Expectancy);
        var system = stats.Single(s => s.Scope == StatScope.System && s.Role == CouncilRole.RiskManager);
        Assert.Null(system.AgentId);
        Assert.Null(system.UserId);
        Assert.Equal((7, 3, 4, 3, 2, 1, 2.5m, 0.5m), (system.Metrics.Decisions, system.Metrics.Approvals, system.Metrics.Rejections, system.Metrics.Trades, system.Metrics.Wins, system.Metrics.Losses, system.Metrics.SumR, system.Metrics.ScopeGrossLossR));
        Assert.Equal(0.8333m, system.Metrics.AverageR);

        var stale = await ExperienceLiveSeed.ShadowAsync(_fixture, d5.Id);
        Assert.Equal(ShadowResult.Expired, stale.Result);
        Assert.Equal(Now, stale.ResolvedAt);
        Assert.Equal(ShadowDetail.Stale, ShadowDetail.Parse(stale.Detail)!.Resolution);
        var blank = await ExperienceLiveSeed.ShadowAsync(_fixture, d6.Id);
        Assert.Equal(ShadowResult.Expired, blank.Result);
        Assert.Null(ShadowDetail.Parse(blank.Detail));
        Assert.Contains("no-detail", blank.Detail!.Value.GetRawText());
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Information && e.Message == "Experience refresh wrote 14 statistic row(s) and expired 2 stale shadow outcome(s); 0 step(s) failed");

        // The next run replaces every row: the other user is gone, so are their rows and their share of the system-wide figures.
        await AgentsLiveSeed.DeleteUsersAsync(_fixture, userB.Id);
        time.Advance(TimeSpan.FromMinutes(15));
        var second = await refresher.RefreshOnceAsync(Ct);

        Assert.Equal(new ExperienceRefreshResult(12, 0, 0), second);
        var again = await ExperienceLiveSeed.StatsAsync(_fixture);
        Assert.Equal(12, again.Count);
        Assert.All(again, s => Assert.Equal(Now.AddMinutes(15), s.ComputedAt));
        Assert.DoesNotContain(again, s => s.UserId == userB.Id);
        Assert.Equal(6, again.Single(s => s.Scope == StatScope.System && s.Role == CouncilRole.RiskManager).Metrics.Decisions);

        await AgentsLiveSeed.DeleteUsersAsync(_fixture, userA.Id);
    }
}
