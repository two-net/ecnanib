using Ecnanib.Agents.Abstractions;
using Ecnanib.Agents.Services;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Execution.Services.Live;
using Ecnanib.Tests.Agents.Live;
using Ecnanib.Tests.Database.Live;
using Ecnanib.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Ecnanib.Tests.Execution.Live;

/// <summary>
/// NFR-REL-01 through the scheduler, the pipeline and the real execution module: a second host over the same database and
/// the same scripted exchange is the restarted process. A live agent's exchange state is verified before anything is done
/// to it, a check that fails blocks every action this candle (NFR-REL-03), and paper agents are never gated.
/// </summary>
[Trait("Category", "Live")]
public class RestartLiveTests : IClassFixture<DatabaseLiveFixture>
{
    private static readonly DateTimeOffset LastOpen = ExecutionLiveSeed.LastOpen;

    /// <summary>The restarted host comes up half a minute after the first: later than every row the first one wrote, within the scheduler's tracking tolerance of the candle that closed at midnight.</summary>
    private static readonly DateTimeOffset Restarted = AgentsLiveSeed.Now.AddSeconds(30);

    private static readonly MarketKey TestnetBtc = new(ExchangeEnvironment.Testnet, MarketType.Spot, "BTCUSDT");

    private readonly DatabaseLiveFixture _fixture;

    public RestartLiveTests(DatabaseLiveFixture fixture)
    {
        _fixture = fixture;
    }

    private static CancellationToken Ct => ExecutionLiveSeed.Ct;

    [Fact]
    public async Task AfterARestartALiveAgentIsReconciledBeforeItIsManagedAndTheCouncilSeesTheAdoptedTrade()
    {
        await StartAsync();
        var user = await AgentsLiveSeed.NewUserAsync(_fixture, "restart-adopt@example.com");
        var (before, _, _, exchange, _) = ExecutionLiveSeed.Build(_fixture);
        Agent agent;
        FakeExchangeAccount account;
        await using (before)
        {
            agent = await ExecutionLiveSeed.LiveAgentAsync(_fixture, before, user.Id, "spot", MarketType.Spot);
            account = ExecutionLiveSeed.Account(exchange, agent);
            var venue = before.GetRequiredService<IOrderVenues>().For(AgentMode.Live);
            var decision = await ExecutionLiveSeed.NewDecisionAsync(_fixture, agent, LastOpen.AddHours(-2));
            var entry = await venue.PlaceEntryAsync(agent, new EntryPlacement(decision.Id, PositionSide.Long, OrderType.Limit, 100m, 0.1m, 95m, [110m, 130m], 0.33m, null), Ct);
            // The process dies mid-evaluation; while it is down the entry fills and nobody listens.
            await ExecutionLiveSeed.NewEvaluationAsync(_fixture, agent, LastOpen.AddHours(-1));
            await account.FillAsync(entry.ClientOrderId, 100m);
            account.Balances.Add(new AssetBalance("BTC", 0.1m, 0m, 0.1m, null));
        }

        var council = new FakeCouncil { Decide = _ => FakeCouncil.Hold() };
        var (provider, logs, _, _, streams) = ExecutionLiveSeed.Build(_fixture, now: Restarted, exchange: exchange, configure: s => s.Replace(ServiceDescriptor.Singleton<ICouncil>(council)));
        await using var _ = provider;
        var scheduler = provider.GetRequiredService<AgentScheduler>();
        await scheduler.StartAsync(Ct);
        await Wait.UntilAsync(() => scheduler.IsTracked(agent.Id), "resumed");
        Assert.Equal(1, ((ISchedulerStatus)scheduler).GetStatus().InterruptedAtStart);

        await streams.PushCandleAsync(TestnetBtc, CandleInterval.OneHour, FakeMarketStreams.ClosedCandle("BTCUSDT", CandleInterval.OneHour, LastOpen));
        await Wait.UntilAsync(() => Outcome(agent.Id, LastOpen) is not null, "evaluated");

        // The check adopted the fill before anything else happened to the agent: the council saw the trade and no pending entry, and the units are protected.
        var context = council.Context!;
        Assert.Single(context.OpenTrades);
        Assert.DoesNotContain(context.OpenOrders, o => o.Purpose == OrderPurpose.Entry);
        Assert.Equal(2, account.PlacedOcos.Count);
        var trade = (await ExecutionLiveSeed.TradeOfAsync(_fixture, agent.Id))!;
        Assert.Equal((100m, 0.1m, 95m), (trade.EntryPrice, trade.RemainingSize, trade.CurrentStop));
        Assert.Equal(EvaluationOutcome.Decided, Outcome(agent.Id, LastOpen));
        Assert.Contains("Reconciled with the exchange: 1 adoption(s).", Detail(agent.Id, LastOpen));
        Assert.Contains(logs.Entries, e => e.Message.Contains("exchange state verified after the restart with 1 adoption(s)"));
        var status = provider.GetRequiredService<IReconciliationStatus>().GetStatus();
        Assert.True(status.Available);
        Assert.Equal((1, 1), (status.CheckedAgents, status.Adoptions));
        Assert.Equal(Restarted, status.StartedAt);

        await scheduler.StopAsync(Ct);
        await AgentsLiveSeed.DeleteUsersAsync(_fixture, user.Id);
    }

    [Fact]
    public async Task WhenTheExchangeCannotBeReadAfterARestartNothingIsManagedAndNothingNewOpens()
    {
        await StartAsync();
        var user = await AgentsLiveSeed.NewUserAsync(_fixture, "restart-unverified@example.com");
        var (before, _, _, exchange, _) = ExecutionLiveSeed.Build(_fixture);
        Agent agent;
        FakeExchangeAccount account;
        await using (before)
        {
            agent = await ExecutionLiveSeed.LiveAgentAsync(_fixture, before, user.Id, "spot", MarketType.Spot);
            account = ExecutionLiveSeed.Account(exchange, agent);
            var venue = before.GetRequiredService<IOrderVenues>().For(AgentMode.Live);
            var decision = await ExecutionLiveSeed.NewDecisionAsync(_fixture, agent, LastOpen.AddHours(-2));
            await venue.PlaceEntryAsync(agent, new EntryPlacement(decision.Id, PositionSide.Long, OrderType.Market, null, 0.1m, 95m, [110m, 130m], 0.33m, null), Ct);
            account.Balances.Add(new AssetBalance("BTC", 0.1m, 0m, 0.1m, null));
        }

        account.OnRead = () => new ExchangeUnavailableException("Binance Spot open orders failed: request timed out", ExchangeErrorKind.Timeout);
        var council = new FakeCouncil();
        var (provider, logs, _, _, streams) = ExecutionLiveSeed.Build(_fixture, now: Restarted, exchange: exchange, configure: s => s.Replace(ServiceDescriptor.Singleton<ICouncil>(council)));
        await using var _ = provider;
        var scheduler = provider.GetRequiredService<AgentScheduler>();
        await scheduler.StartAsync(Ct);
        await Wait.UntilAsync(() => scheduler.IsTracked(agent.Id), "resumed");

        await streams.PushCandleAsync(TestnetBtc, CandleInterval.OneHour, FakeMarketStreams.ClosedCandle("BTCUSDT", CandleInterval.OneHour, LastOpen));
        await Wait.UntilAsync(() => Outcome(agent.Id, LastOpen) is not null, "evaluated");

        // Management skipped, the council told that entries are off, and the entry it proposed anyway refused by execution: nothing was placed.
        Assert.False(council.Context!.EntriesAllowed);
        await using (var db = _fixture.CreateContext())
        {
            var evaluation = await db.Evaluations.AsNoTracking().Include(e => e.Decision).SingleAsync(e => e.AgentId == agent.Id && e.CandleOpenTime == LastOpen, Ct);
            Assert.Equal(EvaluationOutcome.Decided, evaluation.Outcome);
            Assert.Contains("Management skipped: the exchange state could not be verified after the restart", evaluation.OutcomeDetail);
            Assert.Contains("protection is unchanged", evaluation.OutcomeDetail);
            Assert.Equal(DecisionAction.NoTrade, evaluation.Decision!.FinalAction);
            Assert.StartsWith("Rejected by execution limits (entriesAllowed): ", evaluation.Decision.Rationale);
            Assert.Equal("fail", ExecutionLiveSeed.Checks(evaluation.Decision)["entriesAllowed"]);
            Assert.Equal(AgentState.Running, (await db.Agents.AsNoTracking().SingleAsync(a => a.Id == agent.Id, Ct)).State);
        }

        Assert.Single(account.PlacedOrders);
        Assert.Equal(2, account.PlacedOcos.Count);
        Assert.Equal(2, (await ExecutionLiveSeed.OpenAsync(_fixture, agent.Id)).Orders.Count(o => o.Purpose == OrderPurpose.StopLoss));
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("could not be verified after the restart; nothing is placed this candle"));
        Assert.DoesNotContain(logs.Entries, e => e.Level == LogLevel.Critical);

        // The periodic round cannot read the key either: the agent stays on the waiting list and the error is on the status page.
        await provider.GetRequiredService<ExchangeReconciler>().RoundAsync(Ct);
        var status = provider.GetRequiredService<IReconciliationStatus>().GetStatus();
        Assert.Equal((0, 1, 1), (status.CheckedAgents, status.AwaitingCheck, status.LastRoundFailures));
        Assert.NotNull(status.LastError);
        Assert.NotNull(status.LastRoundAt);

        await scheduler.StopAsync(Ct);
        await AgentsLiveSeed.DeleteUsersAsync(_fixture, user.Id);
    }

    [Fact]
    public async Task ALiveAgentWhoseLastEvaluationFinishedIsStillVerifiedOnceBeforeItsFirstAction()
    {
        await StartAsync();
        var user = await AgentsLiveSeed.NewUserAsync(_fixture, "restart-once@example.com");
        var council = new FakeCouncil();
        var (provider, logs, time, exchange, streams) = ExecutionLiveSeed.Build(_fixture, configure: s => s.Replace(ServiceDescriptor.Singleton<ICouncil>(council)));
        await using var _ = provider;
        var agent = await ExecutionLiveSeed.LiveAgentAsync(_fixture, provider, user.Id, "spot", MarketType.Spot, discretionary: true);
        var account = ExecutionLiveSeed.Account(exchange, agent);
        var scheduler = provider.GetRequiredService<AgentScheduler>();
        await scheduler.StartAsync(Ct);
        await Wait.UntilAsync(() => scheduler.IsTracked(agent.Id), "tracked");

        await streams.PushCandleAsync(TestnetBtc, CandleInterval.OneHour, FakeMarketStreams.ClosedCandle("BTCUSDT", CandleInterval.OneHour, LastOpen));
        await Wait.UntilAsync(() => Outcome(agent.Id, LastOpen) is not null, "evaluated");

        // Nothing was open, so the check ran right before the decision was executed, and the entry was placed.
        Assert.Equal(EvaluationOutcome.Decided, Outcome(agent.Id, LastOpen));
        Assert.StartsWith("Limit long", Detail(agent.Id, LastOpen));
        Assert.Single(account.PlacedOrders);
        Assert.Single(logs.Entries, e => e.Message.Contains("exchange state verified after the restart"));
        Assert.Equal(1, provider.GetRequiredService<IReconciliationStatus>().GetStatus().CheckedAgents);

        // The next candle finds the agent verified: no second read before its management or its decision.
        time.Advance(TimeSpan.FromHours(1));
        var next = LastOpen.AddHours(1);
        await streams.PushCandleAsync(TestnetBtc, CandleInterval.OneHour, FakeMarketStreams.ClosedCandle("BTCUSDT", CandleInterval.OneHour, next));
        await Wait.UntilAsync(() => Outcome(agent.Id, next) is not null, "evaluated again");
        Assert.Single(logs.Entries, e => e.Message.Contains("exchange state verified after the restart"));
        Assert.DoesNotContain(logs.Entries, e => e.Message.Contains("could not be verified"));

        await scheduler.StopAsync(Ct);
        await AgentsLiveSeed.DeleteUsersAsync(_fixture, user.Id);
    }

    [Fact]
    public async Task PaperAgentsAreNotGated()
    {
        await StartAsync();
        var user = await AgentsLiveSeed.NewUserAsync(_fixture, "restart-paper@example.com");
        var agent = await AgentsLiveSeed.NewAgentAsync(_fixture, user.Id, "paper", AgentState.Running);
        var decision = await AgentsLiveSeed.NewDecisionAsync(_fixture, agent, LastOpen.AddHours(-5));
        var trade = await AgentsLiveSeed.NewOpenTradeAsync(_fixture, agent, decision);
        await ExecutionLiveSeed.NewEvaluationAsync(_fixture, agent, LastOpen.AddHours(-1));
        var council = new FakeCouncil { Decide = _ => FakeCouncil.Close(trade.Id) };
        var (provider, logs, _, _, streams) = ExecutionLiveSeed.Build(_fixture, now: Restarted, configure: s => s.Replace(ServiceDescriptor.Singleton<ICouncil>(council)));
        await using var _ = provider;
        var scheduler = provider.GetRequiredService<AgentScheduler>();
        await scheduler.StartAsync(Ct);
        await Wait.UntilAsync(() => scheduler.IsTracked(agent.Id), "resumed");
        Assert.Equal(1, ((ISchedulerStatus)scheduler).GetStatus().InterruptedAtStart);

        await streams.PushCandleAsync(AgentsLiveSeed.SpotBtc, CandleInterval.OneHour, FakeMarketStreams.ClosedCandle("BTCUSDT", CandleInterval.OneHour, LastOpen));
        await Wait.UntilAsync(() => Outcome(agent.Id, LastOpen) is not null, "evaluated");

        Assert.Equal(EvaluationOutcome.Decided, Outcome(agent.Id, LastOpen));
        Assert.Contains("Closing the position at market.", Detail(agent.Id, LastOpen));
        await using (var db = _fixture.CreateContext())
            Assert.Single(await db.Orders.AsNoTracking().Where(o => o.TradeId == trade.Id && o.Purpose == OrderPurpose.Close).ToListAsync(Ct));
        Assert.Equal(0, provider.GetRequiredService<IReconciliationStatus>().GetStatus().CheckedAgents);
        Assert.DoesNotContain(logs.Entries, e => e.Message.Contains("exchange state verified"));

        await scheduler.StopAsync(Ct);
        await AgentsLiveSeed.DeleteUsersAsync(_fixture, user.Id);
    }

    private EvaluationOutcome? Outcome(Guid agentId, DateTimeOffset candleOpen)
    {
        using var db = _fixture.CreateContext();
        return db.Evaluations.AsNoTracking().Where(e => e.AgentId == agentId && e.CandleOpenTime == candleOpen && e.FinishedAt != null).Select(e => (EvaluationOutcome?)e.Outcome).SingleOrDefault();
    }

    private string? Detail(Guid agentId, DateTimeOffset candleOpen)
    {
        using var db = _fixture.CreateContext();
        return db.Evaluations.AsNoTracking().Where(e => e.AgentId == agentId && e.CandleOpenTime == candleOpen).Select(e => e.OutcomeDetail).SingleOrDefault();
    }

    private async Task StartAsync()
    {
        _fixture.Require();
        await _fixture.MigrateAsync();
    }
}
