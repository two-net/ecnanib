using Ecnanib.Ai.Abstractions;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Experience.Abstractions;
using Ecnanib.Tests.Agents.Live;
using Ecnanib.Tests.Database.Live;
using Ecnanib.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Experience.Live;

/// <summary>The leaderboard, the provider detail, the agent view and the shadow view (FR-EXP-07, FR-EXP-02, FR-EXP-03), each scoped to the signed-in user (NFR-SEC-03).</summary>
[Trait("Category", "Live")]
public class ExperienceReaderLiveTests : IClassFixture<DatabaseLiveFixture>
{
    private static readonly DateTimeOffset Now = AgentsLiveSeed.Now;

    private readonly DatabaseLiveFixture _fixture;

    public ExperienceReaderLiveTests(DatabaseLiveFixture fixture)
    {
        _fixture = fixture;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ReadsOnlyTheUsersOwnRowsBesideTheSystemWideOnesAndAggregatesOverTheFilter()
    {
        _fixture.Require();
        await _fixture.MigrateAsync();
        var userA = await AgentsLiveSeed.NewUserAsync(_fixture, "exp-reader-a@example.com");
        var userB = await AgentsLiveSeed.NewUserAsync(_fixture, "exp-reader-b@example.com");
        var a1 = await AgentsLiveSeed.NewAgentAsync(_fixture, userA.Id, "Alpha", model: "c1");
        var a2 = await AgentsLiveSeed.NewAgentAsync(_fixture, userA.Id, "Beta", market: MarketType.UsdFutures, timeframe: CandleInterval.FourHours, model: "c1");
        var b1 = await AgentsLiveSeed.NewAgentAsync(_fixture, userB.Id, "Gamma", model: "g1");
        var wins = (int trades, decimal r) => new ExperienceMetrics { Decisions = trades * 2, Approvals = trades, Trades = trades, Wins = trades, SumR = r * trades, GrossProfitR = r * trades, NetTrades = trades, SumNetR = (r - 0.1m) * trades, ScopeGrossLossR = 1m };
        await ExperienceLiveSeed.AddStatAsync(_fixture, StatScope.Agent, userA.Id, a1.Id, AiProvider.Claude, "c1", CouncilRole.RiskManager, CandleInterval.OneHour, MarketType.Spot, wins(4, 1m), Now);
        await ExperienceLiveSeed.AddStatAsync(_fixture, StatScope.Agent, userA.Id, a2.Id, AiProvider.Claude, "c1", CouncilRole.RiskManager, CandleInterval.FourHours, MarketType.UsdFutures, wins(2, 0.5m), Now);
        await ExperienceLiveSeed.AddStatAsync(_fixture, StatScope.User, userA.Id, null, AiProvider.Claude, "c1", CouncilRole.RiskManager, CandleInterval.OneHour, MarketType.Spot, wins(4, 1m), Now);
        await ExperienceLiveSeed.AddStatAsync(_fixture, StatScope.User, userA.Id, null, AiProvider.Claude, "c1", CouncilRole.RiskManager, CandleInterval.FourHours, MarketType.UsdFutures, wins(2, 0.5m), Now);
        await ExperienceLiveSeed.AddStatAsync(_fixture, StatScope.User, userA.Id, null, AiProvider.Gpt, "g1", CouncilRole.MarketAnalyst, CandleInterval.OneHour, MarketType.Spot, new ExperienceMetrics { Decisions = 5 }, Now);
        await ExperienceLiveSeed.AddStatAsync(_fixture, StatScope.User, userB.Id, null, AiProvider.Gpt, "g1", CouncilRole.RiskManager, CandleInterval.OneHour, MarketType.Spot, wins(20, 1.5m), Now);
        await ExperienceLiveSeed.AddStatAsync(_fixture, StatScope.System, null, null, AiProvider.Claude, "c1", CouncilRole.RiskManager, CandleInterval.OneHour, MarketType.Spot, wins(4, 1m), Now);
        await ExperienceLiveSeed.AddStatAsync(_fixture, StatScope.System, null, null, AiProvider.Claude, "c1", CouncilRole.RiskManager, CandleInterval.FourHours, MarketType.UsdFutures, wins(2, 0.5m), Now);
        await ExperienceLiveSeed.AddStatAsync(_fixture, StatScope.System, null, null, AiProvider.Gpt, "g1", CouncilRole.RiskManager, CandleInterval.OneHour, MarketType.Spot, wins(20, 1.5m), Now);
        await ExperienceLiveSeed.AddStatAsync(_fixture, StatScope.System, null, null, AiProvider.Gpt, "g1", CouncilRole.MarketAnalyst, CandleInterval.OneHour, MarketType.Spot, new ExperienceMetrics { Decisions = 5 }, Now);
        var entry = await ExperienceLiveSeed.NewDecisionAsync(_fixture, a1, Now.AddDays(-2), DecisionAction.OpenLong, [ExperienceLiveSeed.Turn(CouncilRole.RiskManager, AiProvider.Claude, "c1", DecisionAction.OpenLong)]);
        var trade = await ExperienceLiveSeed.NewClosedTradeAsync(_fixture, a1, entry, 100m, 90m, 120m, 1m, 2m, Now.AddDays(-1));
        await ExperienceLiveSeed.NewDecisionAsync(_fixture, a1, Now.AddDays(-2).AddHours(1), DecisionAction.Hold, [ExperienceLiveSeed.Turn(CouncilRole.RiskManager, AiProvider.Claude, "c1", DecisionAction.Hold)], tradeId: trade.Id);
        var rejected = await ExperienceLiveSeed.NewDecisionAsync(_fixture, a1, Now.AddDays(-3), DecisionAction.NoTrade, [ExperienceLiveSeed.Turn(CouncilRole.RiskManager, AiProvider.Claude, "c1", DecisionAction.NoTrade)]);
        await ExperienceLiveSeed.NewShadowAsync(_fixture, rejected, Now.AddDays(-3), ShadowResult.HitStop, -1m);

        await using var provider = ExperienceTestHost.BuildServices(_fixture.ConnectionString, time: new FakeTimeProvider(Now));
        var reader = provider.GetRequiredService<IExperienceReader>();

        var board = await reader.GetLeaderboardAsync(userA.Id, ExperienceFilter.None, Ct);
        Assert.Equal(Now, board.ComputedAt);
        Assert.Equal([(AiProvider.Claude, "c1"), (AiProvider.Gpt, "g1")], board.Mine.Select(r => (r.Provider, r.Model)));
        Assert.Equal(6, board.Mine[0].Metrics.Trades);
        Assert.Equal(2m, board.Mine[0].Metrics.ScopeGrossLossR);
        Assert.Equal(0, board.Mine[1].Metrics.Trades);
        Assert.Equal([(AiProvider.Gpt, "g1"), (AiProvider.Claude, "c1")], board.SystemWide.Select(r => (r.Provider, r.Model)));
        Assert.Equal(20, board.SystemWide[0].Metrics.Trades);
        Assert.Equal(1.4m, board.SystemWide[0].Metrics.Expectancy);

        var analysts = await reader.GetLeaderboardAsync(userA.Id, new ExperienceFilter(CouncilRole.MarketAnalyst, null, null), Ct);
        Assert.Equal(["g1"], analysts.Mine.Select(r => r.Model));
        Assert.Equal(5, analysts.Mine[0].Metrics.Decisions);
        var fourHours = await reader.GetLeaderboardAsync(userA.Id, new ExperienceFilter(null, CandleInterval.FourHours, MarketType.UsdFutures), Ct);
        Assert.Equal(["c1"], fourHours.Mine.Select(r => r.Model));
        Assert.Equal(2, fourHours.Mine[0].Metrics.Trades);
        var strangerBoard = await reader.GetLeaderboardAsync(userB.Id, ExperienceFilter.None, Ct);
        Assert.Equal(["g1"], strangerBoard.Mine.Select(r => r.Model));

        var claude = await reader.GetProviderAsync(userA.Id, AiProvider.Claude, ExperienceFilter.None, Ct);
        var mine = Assert.Single(claude.Mine);
        Assert.Equal(("c1", CouncilRole.RiskManager, 6), (mine.Model, mine.Role, mine.Metrics.Trades));
        Assert.Equal(6, Assert.Single(claude.SystemWide).Metrics.Trades);
        Assert.Equal(["Alpha", "Beta"], claude.Agents.Select(a => a.AgentName));
        Assert.Equal((a2.Id, CandleInterval.FourHours, MarketType.UsdFutures, 2), (claude.Agents[1].AgentId, claude.Agents[1].Timeframe, claude.Agents[1].MarketType, claude.Agents[1].Metrics.Trades));
        Assert.Empty((await reader.GetProviderAsync(userB.Id, AiProvider.Claude, ExperienceFilter.None, Ct)).Agents);

        var agentView = Assert.IsType<AgentExperienceView>(await reader.GetAgentAsync(userA.Id, a1.Id, Ct));
        var role = Assert.Single(agentView.Roles);
        Assert.Equal((CouncilRole.RiskManager, AiProvider.Claude, "c1", true, 4), (role.Role, role.Provider, role.Model, role.IsCurrent, role.Metrics.Trades));
        var outcome = Assert.Single(agentView.RecentTrades);
        Assert.Equal((trade.Id, entry.EvaluationId, 2m, 18m, 2m, 1, TradeExitReason.Target, TimeSpan.FromHours(3)), (outcome.TradeId, outcome.EntryEvaluationId, outcome.RMultiple, outcome.RealizedPnl, outcome.Fees, outcome.ManagingDecisions, outcome.ExitReason, outcome.Duration));
        Assert.Null(await reader.GetAgentAsync(userB.Id, a1.Id, Ct));
        Assert.Null(await reader.GetAgentAsync(userA.Id, Guid.NewGuid(), Ct));
        var fresh = Assert.IsType<AgentExperienceView>(await reader.GetAgentAsync(userB.Id, b1.Id, Ct));
        Assert.Empty(fresh.Roles);
        Assert.Null(fresh.ComputedAt);

        var shadow = Assert.IsType<ShadowOutcomeView>(await reader.GetShadowOutcomeAsync(userA.Id, rejected.EvaluationId, Ct));
        Assert.Equal((ShadowResult.HitStop, "veto", "stop", -1m, 1, 30), (shadow.Result, shadow.Reason, shadow.Resolution, shadow.RMultiple, shadow.EntryTimeoutCandles, shadow.HorizonCandles));
        Assert.Null(await reader.GetShadowOutcomeAsync(userB.Id, rejected.EvaluationId, Ct));
        Assert.Null(await reader.GetShadowOutcomeAsync(userA.Id, entry.EvaluationId, Ct));

        await AgentsLiveSeed.DeleteUsersAsync(_fixture, userA.Id, userB.Id);
    }
}
