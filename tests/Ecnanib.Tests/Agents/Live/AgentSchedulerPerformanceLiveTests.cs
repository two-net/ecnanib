using System.Diagnostics;
using Ecnanib.Agents.Abstractions;
using Ecnanib.Agents.Services;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Tests.Database.Live;
using Ecnanib.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Ecnanib.Tests.Agents.Live;

/// <summary>
/// NFR-PERF-01 and NFR-PERF-02 through the scheduler and the pipeline against PostgreSQL, with the exchange, the
/// providers and the streams faked: the deadline cuts a council still deliberating and refuses a late entry, a late
/// close still executes, evaluations run up to the concurrency cap with shorter timeframes first, and a hundred
/// running agents are evaluated once per candle close.
/// </summary>
[Trait("Category", "Live")]
public class AgentSchedulerPerformanceLiveTests : IClassFixture<DatabaseLiveFixture>
{
    private static readonly DateTimeOffset LastOpen = new(2026, 1, 4, 23, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset LastOpen15m = new(2026, 1, 4, 23, 45, 0, TimeSpan.Zero);
    private static readonly MarketKey SpotEth = new(ExchangeEnvironment.Production, MarketType.Spot, "ETHUSDT");
    private readonly DatabaseLiveFixture _fixture;

    public AgentSchedulerPerformanceLiveTests(DatabaseLiveFixture fixture)
    {
        _fixture = fixture;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheDeadlineCancelsACouncilStillDeliberatingAndTheAgentStaysRunning()
    {
        await StartAsync();
        var user = await AgentsLiveSeed.NewUserAsync(_fixture, "perf-deadline@example.com");
        var agent = await AgentsLiveSeed.NewAgentAsync(_fixture, user.Id, "BTC", AgentState.Running, discretionary: true);
        var council = new FakeCouncil { WaitForCancellation = true };
        var (provider, logs, time, _, streams) = AgentsLiveSeed.Build(_fixture, configure: s => Replace(s, council));
        await using var _ = provider;
        var scheduler = provider.GetRequiredService<AgentScheduler>();
        using var events = new EventLog(provider, user.Id);
        await scheduler.StartAsync(Ct);
        await Wait.UntilAsync(() => scheduler.IsTracked(agent.Id), "tracked");

        await streams.PushCandleAsync(AgentsLiveSeed.SpotBtc, CandleInterval.OneHour, FakeMarketStreams.ClosedCandle("BTCUSDT", CandleInterval.OneHour, LastOpen));
        await Wait.UntilAsync(() => council.Calls.Count == 1, "council asked");
        Assert.Equal(1, scheduler.GetStatus().InFlightEvaluations);

        // The 1H deadline is 180 s after the close at 00:00:00; moving the fake clock there fires the scheduler's deadline timer.
        time.Advance(TimeSpan.FromMinutes(3));
        await Wait.UntilAsync(() => Outcome(agent.Id, LastOpen) == EvaluationOutcome.Failed, "failed by the deadline");
        await Wait.UntilAsync(() => scheduler.GetStatus().InFlightEvaluations == 0, "slot released");

        await using (var db = _fixture.CreateContext())
        {
            var evaluation = await db.Evaluations.AsNoTracking().Include(e => e.Decision).SingleAsync(e => e.AgentId == agent.Id, Ct);
            Assert.Equal("The deadline of 00:03:00 UTC passed before the evaluation finished.", evaluation.OutcomeDetail);
            Assert.True(evaluation.CouncilConsulted);
            Assert.Null(evaluation.Decision);
            Assert.Equal(AgentState.Running, (await db.Agents.AsNoTracking().SingleAsync(a => a.Id == agent.Id, Ct)).State);
        }

        Assert.True(scheduler.IsTracked(agent.Id));
        Assert.Equal(EvaluationOutcome.Failed, Assert.Single(events.Of<EvaluationFinished>()).Outcome);
        Assert.Empty(events.Of<AgentStateChanged>());
        var status = scheduler.GetStatus();
        Assert.Equal(1, status.DeadlineMisses);
        Assert.True(status.LastDeadlineMissAt >= LastOpen.AddHours(1).AddMinutes(3));
        var line = Assert.Single(logs.Entries, e => e.Message.Contains($"Agent {agent.Id} evaluated candle", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Warning, line.Level);
        Assert.Contains("past the deadline", line.Message, StringComparison.Ordinal);
        await scheduler.StopAsync(Ct);
        await AgentsLiveSeed.DeleteUsersAsync(_fixture, user.Id);
    }

    [Fact]
    public async Task ADecisionThatArrivesAfterTheDeadlineIsPersistedButNotExecuted()
    {
        await StartAsync();
        var user = await AgentsLiveSeed.NewUserAsync(_fixture, "perf-late-entry@example.com");
        var agent = await AgentsLiveSeed.NewAgentAsync(_fixture, user.Id, "BTC", AgentState.Running, discretionary: true);
        var council = new FakeCouncil();
        var execution = new RecordingExecutionService();
        var (provider, _, time, _, streams) = AgentsLiveSeed.Build(_fixture, configure: s => Replace(s, council, execution));
        await using var _ = provider;
        // The council answers only once the clock is past the deadline: the decision is recorded, the entry is not placed.
        council.BeforeDecision = _ => time.Advance(TimeSpan.FromMinutes(3));
        var scheduler = provider.GetRequiredService<AgentScheduler>();
        using var events = new EventLog(provider, user.Id);
        await scheduler.StartAsync(Ct);
        await Wait.UntilAsync(() => scheduler.IsTracked(agent.Id), "tracked");

        await streams.PushCandleAsync(AgentsLiveSeed.SpotBtc, CandleInterval.OneHour, FakeMarketStreams.ClosedCandle("BTCUSDT", CandleInterval.OneHour, LastOpen));
        await Wait.UntilAsync(() => Outcome(agent.Id, LastOpen) is not null, "evaluated");

        await using (var db = _fixture.CreateContext())
        {
            var evaluation = await db.Evaluations.AsNoTracking().Include(e => e.Decision).ThenInclude(d => d!.RoleOutputs).SingleAsync(e => e.AgentId == agent.Id, Ct);
            Assert.Equal(EvaluationOutcome.Failed, evaluation.Outcome);
            Assert.Equal("The deadline of 00:03:00 UTC passed before the decision could be executed; nothing was placed.", evaluation.OutcomeDetail);
            Assert.NotNull(evaluation.Decision);
            Assert.Equal(DecisionAction.OpenLong, evaluation.Decision.FinalAction);
            Assert.Single(evaluation.Decision.RoleOutputs);
            Assert.Empty(await db.Orders.Where(o => o.AgentId == agent.Id).ToListAsync(Ct));
            Assert.Equal(AgentState.Running, (await db.Agents.AsNoTracking().SingleAsync(a => a.Id == agent.Id, Ct)).State);
        }

        Assert.Equal(0, execution.Calls);
        Assert.Single(events.Of<DecisionRecorded>());
        await Wait.UntilAsync(() => scheduler.GetStatus().InFlightEvaluations == 0, "slot released");
        Assert.Equal(1, scheduler.GetStatus().DeadlineMisses);
        await scheduler.StopAsync(Ct);
        await AgentsLiveSeed.DeleteUsersAsync(_fixture, user.Id);
    }

    [Fact]
    public async Task ALateCloseDecisionIsStillExecuted()
    {
        await StartAsync();
        var user = await AgentsLiveSeed.NewUserAsync(_fixture, "perf-late-close@example.com");
        var agent = await AgentsLiveSeed.NewAgentAsync(_fixture, user.Id, "BTC", AgentState.Running);
        var decision = await AgentsLiveSeed.NewDecisionAsync(_fixture, agent, LastOpen.AddHours(-1));
        var trade = await AgentsLiveSeed.NewOpenTradeAsync(_fixture, agent, decision);
        var council = new FakeCouncil { Decide = _ => FakeCouncil.Close(trade.Id) };
        var execution = new RecordingExecutionService();
        var (provider, logs, time, _, streams) = AgentsLiveSeed.Build(_fixture, configure: s => Replace(s, council, execution));
        await using var _ = provider;
        council.BeforeDecision = _ => time.Advance(TimeSpan.FromMinutes(3));
        var scheduler = provider.GetRequiredService<AgentScheduler>();
        await scheduler.StartAsync(Ct);
        await Wait.UntilAsync(() => scheduler.IsTracked(agent.Id), "tracked");

        await streams.PushCandleAsync(AgentsLiveSeed.SpotBtc, CandleInterval.OneHour, FakeMarketStreams.ClosedCandle("BTCUSDT", CandleInterval.OneHour, LastOpen));
        await Wait.UntilAsync(() => Outcome(agent.Id, LastOpen) is not null, "evaluated");

        // The open position made the council necessary; its Close came late and was executed all the same, logged as a miss.
        Assert.Equal(1, execution.Calls);
        await using (var db = _fixture.CreateContext())
        {
            var evaluation = await db.Evaluations.AsNoTracking().Include(e => e.Decision).SingleAsync(e => e.AgentId == agent.Id && e.CandleOpenTime == LastOpen, Ct);
            Assert.Equal(EvaluationOutcome.Decided, evaluation.Outcome);
            Assert.Equal("executed", evaluation.OutcomeDetail);
            Assert.True(evaluation.CouncilConsulted);
            Assert.Equal(DecisionAction.Close, evaluation.Decision!.FinalAction);
        }

        var line = Assert.Single(logs.Entries, e => e.Message.Contains($"Agent {agent.Id} evaluated candle", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Warning, line.Level);
        Assert.Contains("Decided", line.Message, StringComparison.Ordinal);
        Assert.Contains("past the deadline", line.Message, StringComparison.Ordinal);
        await Wait.UntilAsync(() => scheduler.GetStatus() is { InFlightEvaluations: 0, DeadlineMisses: 1 }, "miss counted");
        await scheduler.StopAsync(Ct);
        await AgentsLiveSeed.DeleteUsersAsync(_fixture, user.Id);
    }

    [Fact]
    public async Task EvaluationsRunUpToTheConcurrencyCapAndTheRestQueue()
    {
        await StartAsync();
        var user = await AgentsLiveSeed.NewUserAsync(_fixture, "perf-cap@example.com");
        var agents = await AgentsLiveSeed.NewAgentsAsync(_fixture, user.Id, 5, discretionary: true);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var council = new FakeCouncil { Gate = gate };
        var (provider, _, _, _, streams) = AgentsLiveSeed.Build(_fixture, config: new() { ["Agents:MaxConcurrentEvaluations"] = "2" }, configure: s => Replace(s, council));
        await using var _ = provider;
        var scheduler = provider.GetRequiredService<AgentScheduler>();
        using var events = new EventLog(provider, user.Id);
        await scheduler.StartAsync(Ct);
        await Wait.UntilAsync(() => scheduler.GetStatus().TrackedAgents == 5, "tracked");

        // One close reaches all five agents; two evaluations hold the slots, three wait in the queue.
        await streams.PushCandleAsync(AgentsLiveSeed.SpotBtc, CandleInterval.OneHour, FakeMarketStreams.ClosedCandle("BTCUSDT", CandleInterval.OneHour, LastOpen));
        await Wait.UntilAsync(() => council.Calls.Count == 2 && scheduler.GetStatus() is { InFlightEvaluations: 2, QueuedEvaluations: 3 }, "two in flight, three queued");
        await Wait.SettleAsync(100);
        var held = scheduler.GetStatus();
        Assert.Equal(2, council.Calls.Count);
        Assert.Equal((2, 3, 2), (held.InFlightEvaluations, held.QueuedEvaluations, held.MaxConcurrentEvaluations));

        gate.SetResult();
        await Wait.UntilAsync(() => events.Count<EvaluationFinished>() == 5, "all evaluated");
        Assert.Equal(5, council.Calls.Distinct().Count());
        await using (var db = _fixture.CreateContext())
        {
            var ids = agents.Select(a => a.Id).ToList();
            var rows = await db.Evaluations.AsNoTracking().Where(e => ids.Contains(e.AgentId)).ToListAsync(Ct);
            Assert.Equal(5, rows.Count);
            Assert.Equal(5, rows.Select(r => r.AgentId).Distinct().Count());
            Assert.All(rows, r => Assert.Equal(EvaluationOutcome.Decided, r.Outcome));
        }

        await Wait.UntilAsync(() => scheduler.GetStatus() is { InFlightEvaluations: 0, QueuedEvaluations: 0 }, "idle");
        Assert.Equal(0, scheduler.GetStatus().DeadlineMisses);
        await scheduler.StopAsync(Ct);
        await AgentsLiveSeed.DeleteUsersAsync(_fixture, user.Id);
    }

    [Fact]
    public async Task AQueued15mCandleIsEvaluatedBeforeAQueued1HCandle()
    {
        await StartAsync();
        var user = await AgentsLiveSeed.NewUserAsync(_fixture, "perf-order@example.com");
        var btcHourly = await AgentsLiveSeed.NewAgentAsync(_fixture, user.Id, "BTC 1H", AgentState.Running, discretionary: true);
        var ethHourly = await AgentsLiveSeed.NewAgentAsync(_fixture, user.Id, "ETH 1H", AgentState.Running, symbol: "ETHUSDT", discretionary: true);
        var btcQuarter = await AgentsLiveSeed.NewAgentAsync(_fixture, user.Id, "BTC 15m", AgentState.Running, timeframe: CandleInterval.FifteenMinutes, strategyId: "intraday-trend-pullback", discretionary: true);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var council = new FakeCouncil { Gate = gate };
        var (provider, _, _, _, streams) = AgentsLiveSeed.Build(_fixture, config: new() { ["Agents:MaxConcurrentEvaluations"] = "1" }, configure: s => Replace(s, council));
        await using var _ = provider;
        var scheduler = provider.GetRequiredService<AgentScheduler>();
        using var events = new EventLog(provider, user.Id);
        await scheduler.StartAsync(Ct);
        await Wait.UntilAsync(() => scheduler.GetStatus().TrackedAgents == 3, "tracked");

        // The only slot is held by the first 1H evaluation; a second 1H candle queues first, the 15m candle after it.
        await streams.PushCandleAsync(AgentsLiveSeed.SpotBtc, CandleInterval.OneHour, FakeMarketStreams.ClosedCandle("BTCUSDT", CandleInterval.OneHour, LastOpen));
        await Wait.UntilAsync(() => council.Calls.Count == 1, "the slot is held");
        await streams.PushCandleAsync(SpotEth, CandleInterval.OneHour, FakeMarketStreams.ClosedCandle("ETHUSDT", CandleInterval.OneHour, LastOpen));
        await streams.PushCandleAsync(AgentsLiveSeed.SpotBtc, CandleInterval.FifteenMinutes, FakeMarketStreams.ClosedCandle("BTCUSDT", CandleInterval.FifteenMinutes, LastOpen15m));
        await Wait.UntilAsync(() => scheduler.GetStatus().QueuedEvaluations == 2, "two queued");

        gate.SetResult();
        await Wait.UntilAsync(() => council.Calls.Count == 3, "all asked");
        Assert.Equal(new[] { btcHourly.Id, btcQuarter.Id, ethHourly.Id }, council.Calls);
        await Wait.UntilAsync(() => events.Count<EvaluationFinished>() == 3, "all evaluated");
        Assert.Equal(EvaluationOutcome.Decided, Outcome(btcQuarter.Id, LastOpen15m));
        Assert.Equal(EvaluationOutcome.Decided, Outcome(ethHourly.Id, LastOpen));
        await scheduler.StopAsync(Ct);
        await AgentsLiveSeed.DeleteUsersAsync(_fixture, user.Id);
    }

    [Fact]
    public async Task OneHundredRunningPaperAgentsAreEvaluatedOncePerCandleCloseWithinTheCap()
    {
        await StartAsync();
        var user = await AgentsLiveSeed.NewUserAsync(_fixture, "perf-hundred@example.com");
        var agents = await AgentsLiveSeed.NewAgentsAsync(_fixture, user.Id, 100);
        var (provider, _, _, _, streams) = AgentsLiveSeed.Build(_fixture);
        await using var _ = provider;
        var scheduler = provider.GetRequiredService<AgentScheduler>();
        using var events = new EventLog(provider, user.Id);
        await scheduler.StartAsync(Ct);
        await Wait.UntilAsync(() => scheduler.GetStatus().TrackedAgents == 100, "all tracked", timeoutMs: 30_000);

        // The real engine sees three faked candles and reports insufficient history, so every agent is a strategy-only NoAction.
        var started = Stopwatch.GetTimestamp();
        var peak = 0;
        await streams.PushCandleAsync(AgentsLiveSeed.SpotBtc, CandleInterval.OneHour, FakeMarketStreams.ClosedCandle("BTCUSDT", CandleInterval.OneHour, LastOpen));
        await Wait.UntilAsync(() =>
        {
            peak = Math.Max(peak, scheduler.GetStatus().InFlightEvaluations);
            return events.Count<EvaluationFinished>() == 100;
        }, "a hundred evaluations", timeoutMs: 120_000);
        var elapsed = Stopwatch.GetElapsedTime(started);
        TestContext.Current.TestOutputHelper?.WriteLine($"100 agents evaluated in {elapsed.TotalSeconds:0.0} s with a peak of {peak} in flight");

        var status = scheduler.GetStatus();
        Assert.Equal(10, status.MaxConcurrentEvaluations);
        Assert.InRange(peak, 1, status.MaxConcurrentEvaluations);
        Assert.Equal((0, 0, 0), (status.InFlightEvaluations, status.QueuedEvaluations, status.DeadlineMisses));
        Assert.Null(status.LastError);
        await using (var db = _fixture.CreateContext())
        {
            var ids = agents.Select(a => a.Id).ToList();
            var rows = await db.Evaluations.AsNoTracking().Where(e => ids.Contains(e.AgentId)).Select(e => new { e.AgentId, e.Outcome, e.FinishedAt }).ToListAsync(Ct);
            Assert.Equal(100, rows.Count);
            Assert.Equal(100, rows.Select(r => r.AgentId).Distinct().Count());
            Assert.All(rows, r =>
            {
                Assert.Equal(EvaluationOutcome.NoAction, r.Outcome);
                Assert.NotNull(r.FinishedAt);
            });
        }

        await scheduler.StopAsync(Ct);
        await AgentsLiveSeed.DeleteUsersAsync(_fixture, user.Id);
    }

    private static void Replace(IServiceCollection services, ICouncil council, IExecutionService? execution = null)
    {
        services.Replace(ServiceDescriptor.Singleton(council));
        if (execution is not null)
            services.Replace(ServiceDescriptor.Singleton(execution));
    }

    private EvaluationOutcome? Outcome(Guid agentId, DateTimeOffset candleOpen)
    {
        using var db = _fixture.CreateContext();
        return db.Evaluations.AsNoTracking().Where(e => e.AgentId == agentId && e.CandleOpenTime == candleOpen && e.FinishedAt != null).Select(e => (EvaluationOutcome?)e.Outcome).SingleOrDefault();
    }

    private async Task StartAsync()
    {
        _fixture.Require();
        await _fixture.MigrateAsync();
    }

    /// <summary>The user's agent events as they arrive, counted under a lock because handlers run on the thread pool.</summary>
    private sealed class EventLog : IDisposable
    {
        private readonly Lock _lock = new();
        private readonly List<AgentEvent> _events = [];
        private readonly IDisposable _subscription;

        public EventLog(IServiceProvider provider, Guid userId)
        {
            _subscription = provider.GetRequiredService<IAgentEvents>().Subscribe(userId, e =>
            {
                lock (_lock)
                    _events.Add(e);
                return ValueTask.CompletedTask;
            });
        }

        public int Count<T>() where T : AgentEvent
        {
            lock (_lock)
                return _events.OfType<T>().Count();
        }

        public IReadOnlyList<T> Of<T>() where T : AgentEvent
        {
            lock (_lock)
                return _events.OfType<T>().ToList();
        }

        public void Dispose() => _subscription.Dispose();
    }
}
