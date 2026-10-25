using Ecnanib.Agents.Abstractions;
using Ecnanib.Agents.Services;
using Ecnanib.Agents.Services.Placeholders;
using Ecnanib.Ai.Abstractions;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Keys.Abstractions;
using Ecnanib.Tests.Database.Live;
using Ecnanib.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Ecnanib.Tests.Agents.Live;

/// <summary>FR-AGT-05, FR-AGT-08..10, FR-AI-08, FR-AI-12, FR-KEY-06/07 and NFR-REL-01 through the scheduler and pipeline against PostgreSQL, with the exchange and streams faked.</summary>
[Trait("Category", "Live")]
public class AgentSchedulerLiveTests : IClassFixture<DatabaseLiveFixture>
{
    private static readonly DateTimeOffset LastOpen = new(2026, 1, 4, 23, 0, 0, TimeSpan.Zero);
    private readonly DatabaseLiveFixture _fixture;

    public AgentSchedulerLiveTests(DatabaseLiveFixture fixture)
    {
        _fixture = fixture;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RestartFinalisesInterruptedEvaluationsAndResumesRunningAndPausedAgents()
    {
        await StartAsync();
        var user = await AgentsLiveSeed.NewUserAsync(_fixture, "restart@example.com");
        var running = await AgentsLiveSeed.NewAgentAsync(_fixture, user.Id, "running", AgentState.Running);
        var paused = await AgentsLiveSeed.NewAgentAsync(_fixture, user.Id, "paused", AgentState.Paused, symbol: "ETHUSDT");
        var stopped = await AgentsLiveSeed.NewAgentAsync(_fixture, user.Id, "stopped", AgentState.Stopped);
        var error = await AgentsLiveSeed.NewAgentAsync(_fixture, user.Id, "error", AgentState.Error);
        Guid interruptedId;
        Guid finishedId;
        await using (var db = _fixture.CreateContext())
        {
            var interrupted = new Evaluation { AgentId = running.Id, Symbol = "BTCUSDT", MarketType = MarketType.Spot, Timeframe = CandleInterval.OneHour, CandleOpenTime = LastOpen.AddHours(-2), StartedAt = AgentsLiveSeed.Now.AddMinutes(-3), Outcome = EvaluationOutcome.InProgress };
            var finished = new Evaluation { AgentId = running.Id, Symbol = "BTCUSDT", MarketType = MarketType.Spot, Timeframe = CandleInterval.OneHour, CandleOpenTime = LastOpen.AddHours(-3), StartedAt = AgentsLiveSeed.Now.AddHours(-2), FinishedAt = AgentsLiveSeed.Now.AddHours(-2), Outcome = EvaluationOutcome.NoAction };
            db.AddRange(interrupted, finished);
            await db.SaveChangesAsync(Ct);
            interruptedId = interrupted.Id;
            finishedId = finished.Id;
        }

        var (provider, logs, _, _, streams) = AgentsLiveSeed.Build(_fixture);
        await using var _ = provider;
        var scheduler = provider.GetRequiredService<AgentScheduler>();
        var events = new List<AgentEvent>();
        using var subscription = provider.GetRequiredService<IAgentEvents>().Subscribe(user.Id, e =>
        {
            lock (events)
                events.Add(e);
            return ValueTask.CompletedTask;
        });
        await scheduler.StartAsync(Ct);
        await Wait.UntilAsync(() => scheduler.IsTracked(running.Id) && scheduler.IsTracked(paused.Id), "resumed");

        // FR-UI-04: the page that showed the interrupted evaluation as running learns that it ended.
        await Wait.UntilAsync(() => events.OfType<EvaluationFinished>().Any(e => e.EvaluationId == interruptedId && e.Outcome == EvaluationOutcome.Failed && e.AgentId == running.Id), "sweep event");
        // NFR-OBS-01: once the sweep and the resume are through, the scheduler reports itself Running with its counts.
        var status = ((ISchedulerStatus)scheduler).GetStatus();
        Assert.Equal(SchedulerPhase.Running, status.Phase);
        Assert.Equal(AgentsLiveSeed.Now, status.StartedAt);
        Assert.Equal(2, status.TrackedAgents);
        Assert.Null(status.LastError);
        Assert.False(scheduler.IsTracked(stopped.Id));
        Assert.False(scheduler.IsTracked(error.Id));
        Assert.True(streams.HasCandleSubscription(AgentsLiveSeed.SpotBtc, CandleInterval.OneHour));
        Assert.True(streams.HasCandleSubscription(new MarketKey(ExchangeEnvironment.Production, MarketType.Spot, "ETHUSDT"), CandleInterval.OneHour));
        await using (var db = _fixture.CreateContext())
        {
            var row = await db.Evaluations.AsNoTracking().SingleAsync(e => e.Id == interruptedId, Ct);
            Assert.Equal(EvaluationOutcome.Failed, row.Outcome);
            Assert.Equal(AgentScheduler.InterruptedByRestart, row.OutcomeDetail);
            Assert.Equal(AgentsLiveSeed.Now, row.FinishedAt);
            var untouched = await db.Evaluations.AsNoTracking().SingleAsync(e => e.Id == finishedId, Ct);
            Assert.Equal(EvaluationOutcome.NoAction, untouched.Outcome);
        }

        Assert.Contains(logs.Entries, e => e.Message.Contains("were in flight at the last stop", StringComparison.Ordinal));
        await scheduler.StopAsync(Ct);
        Assert.Equal(0, streams.CandleSubscriptions);
        await AgentsLiveSeed.DeleteUsersAsync(_fixture, user.Id);
    }

    [Fact]
    public async Task ACandleCloseIsEvaluatedExactlyOnceAndRecordedAsNoAction()
    {
        await StartAsync();
        var user = await AgentsLiveSeed.NewUserAsync(_fixture, "evaluate@example.com");
        var agent = await AgentsLiveSeed.NewAgentAsync(_fixture, user.Id, "BTC", AgentState.Running);
        var (provider, logs, _, exchange, streams) = AgentsLiveSeed.Build(_fixture);
        await using var _ = provider;
        var scheduler = provider.GetRequiredService<AgentScheduler>();
        var events = new List<AgentEvent>();
        using var subscription = provider.GetRequiredService<IAgentEvents>().Subscribe(user.Id, e =>
        {
            lock (events)
                events.Add(e);
            return ValueTask.CompletedTask;
        });
        await scheduler.StartAsync(Ct);
        await Wait.UntilAsync(() => scheduler.IsTracked(agent.Id), "tracked");

        var candle = FakeMarketStreams.ClosedCandle("BTCUSDT", CandleInterval.OneHour, LastOpen, 101m);
        await streams.PushCandleAsync(AgentsLiveSeed.SpotBtc, CandleInterval.OneHour, candle);
        await Wait.UntilAsync(() => events.OfType<EvaluationFinished>().Any(e => e.AgentId == agent.Id), "evaluated");
        await streams.PushCandleAsync(AgentsLiveSeed.SpotBtc, CandleInterval.OneHour, candle);
        await Wait.SettleAsync(200);

        await using (var db = _fixture.CreateContext())
        {
            var evaluation = Assert.Single(await db.Evaluations.AsNoTracking().Where(e => e.AgentId == agent.Id).ToListAsync(Ct));
            Assert.Equal(LastOpen, evaluation.CandleOpenTime);
            Assert.Equal(EvaluationOutcome.NoAction, evaluation.Outcome);
            // Three faked candles are not enough history for the real engine: no setup, and the reason is the detail.
            Assert.StartsWith("insufficient history", evaluation.OutcomeDetail, StringComparison.Ordinal);
            Assert.False(evaluation.CouncilConsulted);
            Assert.NotNull(evaluation.FinishedAt);
            var output = evaluation.StrategyOutput!.Value;
            Assert.StartsWith("insufficient history", output.GetProperty("note").GetString());
            Assert.Equal("trend-pullback", output.GetProperty("strategyId").GetString());
            Assert.Equal(1, output.GetProperty("strategyVersion").GetInt32());
            Assert.Equal("BTCUSDT", evaluation.Symbol);
            // FR-STR-03: the daily series the 1H strategy declares was read too (persistence is the market-data module's).
            Assert.Contains(exchange.RecentCandleReads, r => r.Symbol == "BTCUSDT" && r.Interval == CandleInterval.OneDay);
        }

        var finished = Assert.Single(events.OfType<EvaluationFinished>());
        // FR-UI-03 a: "evaluating" is announced when the row is committed, before the outcome.
        var started = Assert.Single(events.OfType<EvaluationStarted>());
        Assert.Equal(finished.EvaluationId, started.EvaluationId);
        Assert.Equal(LastOpen, started.CandleOpenTime);
        Assert.True(events.IndexOf(started) < events.IndexOf(finished));
        var evaluated = Assert.Single(logs.Entries, e => e.Message.Contains($"Agent {agent.Id} evaluated candle", StringComparison.Ordinal) && e.Message.Contains("NoAction", StringComparison.Ordinal));
        // FR-LOG-02: the scheduler's scope names the agent and its user, the pipeline's the evaluation, for every log of the run.
        Assert.Equal(new Dictionary<string, object?> { ["AgentId"] = agent.Id, ["UserId"] = user.Id, ["EvaluationId"] = finished.EvaluationId }, evaluated.Scope);
        await scheduler.StopAsync(Ct);
        await AgentsLiveSeed.DeleteUsersAsync(_fixture, user.Id);
    }

    [Fact]
    public async Task StaleSupersededAndLateCandlesAreNotEvaluated()
    {
        await StartAsync();
        var user = await AgentsLiveSeed.NewUserAsync(_fixture, "stale@example.com");
        var agent = await AgentsLiveSeed.NewAgentAsync(_fixture, user.Id, "BTC", AgentState.Running);
        var (provider, _, time, _, streams) = AgentsLiveSeed.Build(_fixture);
        await using var _ = provider;
        var scheduler = provider.GetRequiredService<AgentScheduler>();
        await scheduler.StartAsync(Ct);
        await Wait.UntilAsync(() => scheduler.IsTracked(agent.Id), "tracked");
        var midnight = new DateTimeOffset(2026, 1, 5, 0, 0, 0, TimeSpan.Zero);

        // Closed hours before the agent was tracked (at 00:00:05): ignored without a row.
        await streams.PushCandleAsync(AgentsLiveSeed.SpotBtc, CandleInterval.OneHour, FakeMarketStreams.ClosedCandle("BTCUSDT", CandleInterval.OneHour, LastOpen.AddHours(-3)));
        await Wait.SettleAsync(200);
        await using (var db = _fixture.CreateContext())
            Assert.Empty(await db.Evaluations.Where(e => e.AgentId == agent.Id).ToListAsync(Ct));

        // The 01:00 candle closes at 02:00 and is evaluated; the 00:00 candle arriving afterwards (a gap-fill burst) is superseded.
        time.SetUtcNow(midnight.AddHours(2).AddSeconds(5));
        await streams.PushCandleAsync(AgentsLiveSeed.SpotBtc, CandleInterval.OneHour, FakeMarketStreams.ClosedCandle("BTCUSDT", CandleInterval.OneHour, midnight.AddHours(1)));
        await Wait.UntilAsync(() => Outcome(agent.Id, midnight.AddHours(1)) == EvaluationOutcome.NoAction, "evaluated");
        await streams.PushCandleAsync(AgentsLiveSeed.SpotBtc, CandleInterval.OneHour, FakeMarketStreams.ClosedCandle("BTCUSDT", CandleInterval.OneHour, midnight));
        await Wait.UntilAsync(() => Outcome(agent.Id, midnight) == EvaluationOutcome.Skipped, "superseded");
        Assert.Equal("Superseded by a later candle.", Detail(agent.Id, midnight));

        // A second agent tracked 30 s after the close receives the 01:00 candle once its 180 s deadline has passed: too late to start.
        var late = await AgentsLiveSeed.NewAgentAsync(_fixture, user.Id, "late", AgentState.Running);
        time.SetUtcNow(midnight.AddHours(2).AddSeconds(30));
        await scheduler.ResumeAgentsAsync(Ct);
        time.SetUtcNow(midnight.AddHours(2).AddMinutes(3).AddSeconds(30));
        await streams.PushCandleAsync(AgentsLiveSeed.SpotBtc, CandleInterval.OneHour, FakeMarketStreams.ClosedCandle("BTCUSDT", CandleInterval.OneHour, midnight.AddHours(1)));
        await Wait.UntilAsync(() => Outcome(late.Id, midnight.AddHours(1)) == EvaluationOutcome.Skipped, "too late");
        Assert.StartsWith("Too late to start", Detail(late.Id, midnight.AddHours(1)), StringComparison.Ordinal);
        await using (var db = _fixture.CreateContext())
            Assert.Equal(2, await db.Evaluations.CountAsync(e => e.AgentId == agent.Id, Ct));

        await scheduler.StopAsync(Ct);
        await AgentsLiveSeed.DeleteUsersAsync(_fixture, user.Id);
    }

    [Fact]
    public async Task APausedAgentIsEvaluatedAndAnOpenPositionConsultsTheCouncilWhichFailsSafely()
    {
        await StartAsync();
        var user = await AgentsLiveSeed.NewUserAsync(_fixture, "paused@example.com");
        var paused = await AgentsLiveSeed.NewAgentAsync(_fixture, user.Id, "paused", AgentState.Paused);
        var positioned = await AgentsLiveSeed.NewAgentAsync(_fixture, user.Id, "positioned", AgentState.Running, symbol: "ETHUSDT");
        var decision = await AgentsLiveSeed.NewDecisionAsync(_fixture, positioned, LastOpen.AddHours(-5));
        await AgentsLiveSeed.NewOpenTradeAsync(_fixture, positioned, decision);
        var (provider, _, _, _, streams) = AgentsLiveSeed.Build(_fixture);
        await using var _ = provider;
        var scheduler = provider.GetRequiredService<AgentScheduler>();
        await scheduler.StartAsync(Ct);
        await Wait.UntilAsync(() => scheduler.IsTracked(paused.Id) && scheduler.IsTracked(positioned.Id), "tracked");

        await streams.PushCandleAsync(AgentsLiveSeed.SpotBtc, CandleInterval.OneHour, FakeMarketStreams.ClosedCandle("BTCUSDT", CandleInterval.OneHour, LastOpen));
        await streams.PushCandleAsync(new MarketKey(ExchangeEnvironment.Production, MarketType.Spot, "ETHUSDT"), CandleInterval.OneHour, FakeMarketStreams.ClosedCandle("ETHUSDT", CandleInterval.OneHour, LastOpen));
        await Wait.UntilAsync(() => Outcome(paused.Id, LastOpen) is not null && Outcome(positioned.Id, LastOpen) is not null, "both evaluated");

        await using (var db = _fixture.CreateContext())
        {
            var quiet = await db.Evaluations.AsNoTracking().SingleAsync(e => e.AgentId == paused.Id, Ct);
            Assert.Equal(EvaluationOutcome.NoAction, quiet.Outcome);
            Assert.False(quiet.CouncilConsulted);

            var consulted = await db.Evaluations.AsNoTracking().Include(e => e.Decision).SingleAsync(e => e.AgentId == positioned.Id && e.CandleOpenTime == LastOpen, Ct);
            Assert.Equal(EvaluationOutcome.Failed, consulted.Outcome);
            Assert.True(consulted.CouncilConsulted);
            Assert.Equal("No decision: " + PendingCouncil.Failure, consulted.OutcomeDetail);
            Assert.Null(consulted.Decision);
            Assert.Equal(2, await db.Orders.CountAsync(o => o.AgentId == positioned.Id && o.Status == OrderStatus.New, Ct));
            Assert.Equal(AgentState.Running, (await db.Agents.AsNoTracking().SingleAsync(a => a.Id == positioned.Id, Ct)).State);
        }

        await scheduler.StopAsync(Ct);
        await AgentsLiveSeed.DeleteUsersAsync(_fixture, user.Id);
    }

    [Fact]
    public async Task ADecisionIsPersistedAndTheExecutionStageIsRecorded()
    {
        await StartAsync();
        var user = await AgentsLiveSeed.NewUserAsync(_fixture, "decided@example.com");
        var agent = await AgentsLiveSeed.NewAgentAsync(_fixture, user.Id, "BTC", AgentState.Running, discretionary: true);
        var council = new FakeCouncil();
        var (provider, _, _, _, streams) = AgentsLiveSeed.Build(_fixture, configure: s => s.Replace(ServiceDescriptor.Singleton<ICouncil>(council)));
        await using var _ = provider;
        var scheduler = provider.GetRequiredService<AgentScheduler>();
        await scheduler.StartAsync(Ct);
        await Wait.UntilAsync(() => scheduler.IsTracked(agent.Id), "tracked");

        await streams.PushCandleAsync(AgentsLiveSeed.SpotBtc, CandleInterval.OneHour, FakeMarketStreams.ClosedCandle("BTCUSDT", CandleInterval.OneHour, LastOpen));
        await Wait.UntilAsync(() => Outcome(agent.Id, LastOpen) is not null, "evaluated");

        await using (var db = _fixture.CreateContext())
        {
            var evaluation = await db.Evaluations.AsNoTracking().Include(e => e.Decision).ThenInclude(d => d!.RoleOutputs).SingleAsync(e => e.AgentId == agent.Id, Ct);
            Assert.Equal(EvaluationOutcome.Decided, evaluation.Outcome);
            Assert.Equal(PendingExecutionService.Note, evaluation.OutcomeDetail);
            Assert.True(evaluation.CouncilConsulted);
            Assert.NotNull(evaluation.Decision);
            Assert.Equal(DecisionAction.OpenLong, evaluation.Decision.FinalAction);
            Assert.Single(evaluation.Decision.RoleOutputs);
            Assert.Empty(await db.Orders.Where(o => o.AgentId == agent.Id).ToListAsync(Ct));
        }

        Assert.True(council.Context!.EntriesAllowed);
        Assert.Equal(3, council.Context.Market.Candles.Count);
        await scheduler.StopAsync(Ct);
        await AgentsLiveSeed.DeleteUsersAsync(_fixture, user.Id);
    }

    [Fact]
    public async Task UnrecoverableFailuresMoveTheAgentToErrorAndHaltEvaluation()
    {
        await StartAsync();
        var user = await AgentsLiveSeed.NewUserAsync(_fixture, "error@example.com");
        var gone = await AgentsLiveSeed.NewAgentAsync(_fixture, user.Id, "gone", AgentState.Running, strategyId: "no-such-strategy");
        var keyless = await AgentsLiveSeed.NewAgentAsync(_fixture, user.Id, "keyless", AgentState.Paused, mode: AgentMode.Live, symbol: "ETHUSDT");
        var (provider, logs, _, _, streams) = AgentsLiveSeed.Build(_fixture);
        await using var _ = provider;
        var scheduler = provider.GetRequiredService<AgentScheduler>();
        var events = new List<AgentEvent>();
        using var subscription = provider.GetRequiredService<IAgentEvents>().Subscribe(user.Id, e =>
        {
            lock (events)
                events.Add(e);
            return ValueTask.CompletedTask;
        });
        await scheduler.StartAsync(Ct);
        await Wait.UntilAsync(() => scheduler.IsTracked(gone.Id) && scheduler.IsTracked(keyless.Id), "tracked");

        await streams.PushCandleAsync(AgentsLiveSeed.SpotBtc, CandleInterval.OneHour, FakeMarketStreams.ClosedCandle("BTCUSDT", CandleInterval.OneHour, LastOpen));
        await streams.PushCandleAsync(new MarketKey(ExchangeEnvironment.Production, MarketType.Spot, "ETHUSDT"), CandleInterval.OneHour, FakeMarketStreams.ClosedCandle("ETHUSDT", CandleInterval.OneHour, LastOpen));
        await Wait.UntilAsync(() => events.OfType<AgentStateChanged>().Count(e => e.State == AgentState.Error) == 2, "events");
        Assert.False(scheduler.IsTracked(gone.Id));
        Assert.False(scheduler.IsTracked(keyless.Id));

        var goneRow = await AgentsLiveSeed.AgentAsync(_fixture, gone.Id);
        Assert.Equal(AgentState.Error, goneRow.State);
        Assert.Contains("not in the catalog", goneRow.StateReason);
        var keylessRow = await AgentsLiveSeed.AgentAsync(_fixture, keyless.Id);
        Assert.Equal(AgentState.Error, keylessRow.State);
        Assert.Contains("exchange key is gone", keylessRow.StateReason);
        Assert.Null(keylessRow.PauseCause);
        await using (var db = _fixture.CreateContext())
            Assert.Empty(await db.Evaluations.Where(e => e.AgentId == gone.Id || e.AgentId == keyless.Id).ToListAsync(Ct));
        Assert.Contains(await AgentsLiveSeed.ChangesAsync(_fixture, gone.Id), c => c.Field == ChangeSet.StateField);
        Assert.Contains(logs.Entries, e => e.Message.Contains($"Agent {gone.Id} entered Error", StringComparison.Ordinal));

        await scheduler.StopAsync(Ct);
        await AgentsLiveSeed.DeleteUsersAsync(_fixture, user.Id);
    }

    [Fact]
    public async Task TheMinuteTickLiftsDueDailyLossPausesAndFinalisesOrphanedEvaluations()
    {
        await StartAsync();
        var user = await AgentsLiveSeed.NewUserAsync(_fixture, "tick@example.com");
        var due = await AgentsLiveSeed.NewAgentAsync(_fixture, user.Id, "due", AgentState.Paused);
        var later = await AgentsLiveSeed.NewAgentAsync(_fixture, user.Id, "later", AgentState.Paused);
        Guid orphanId;
        await using (var db = _fixture.CreateContext())
        {
            await db.Agents.Where(a => a.Id == due.Id).ExecuteUpdateAsync(s => s.SetProperty(a => a.PauseCause, PauseCause.DailyLoss).SetProperty(a => a.AutoResumeAt, AgentsLiveSeed.Now.AddMinutes(-1)), Ct);
            await db.Agents.Where(a => a.Id == later.Id).ExecuteUpdateAsync(s => s.SetProperty(a => a.PauseCause, PauseCause.DailyLoss).SetProperty(a => a.AutoResumeAt, AgentsLiveSeed.Now.AddHours(1)), Ct);
            var orphan = new Evaluation { AgentId = due.Id, Symbol = "BTCUSDT", MarketType = MarketType.Spot, Timeframe = CandleInterval.OneHour, CandleOpenTime = LastOpen.AddHours(-9), StartedAt = AgentsLiveSeed.Now.AddHours(-1), Outcome = EvaluationOutcome.InProgress };
            var fresh = new Evaluation { AgentId = due.Id, Symbol = "BTCUSDT", MarketType = MarketType.Spot, Timeframe = CandleInterval.OneHour, CandleOpenTime = LastOpen.AddHours(-8), StartedAt = AgentsLiveSeed.Now.AddMinutes(-1), Outcome = EvaluationOutcome.InProgress };
            db.AddRange(orphan, fresh);
            await db.SaveChangesAsync(Ct);
            orphanId = orphan.Id;
        }

        var (provider, _, _, _, _) = AgentsLiveSeed.Build(_fixture);
        await using var _ = provider;
        var scheduler = provider.GetRequiredService<AgentScheduler>();
        var events = new List<AgentEvent>();
        using var subscription = provider.GetRequiredService<IAgentEvents>().Subscribe(user.Id, e =>
        {
            lock (events)
                events.Add(e);
            return ValueTask.CompletedTask;
        });

        var (resumed, finalised) = await scheduler.TickOnceAsync(Ct);

        Assert.Equal(1, resumed);
        Assert.Equal(1, finalised);
        await Wait.UntilAsync(() => events.OfType<AgentStateChanged>().Any(e => e.AgentId == due.Id && e.State == AgentState.Running)
                                    && events.OfType<EvaluationFinished>().Any(e => e.EvaluationId == orphanId && e.Outcome == EvaluationOutcome.Failed), "tick events");
        Assert.DoesNotContain(events.OfType<EvaluationFinished>(), e => e.EvaluationId != orphanId);
        var dueRow = await AgentsLiveSeed.AgentAsync(_fixture, due.Id);
        Assert.Equal(AgentState.Running, dueRow.State);
        Assert.Null(dueRow.AutoResumeAt);
        Assert.Equal(AgentState.Paused, (await AgentsLiveSeed.AgentAsync(_fixture, later.Id)).State);
        Assert.Contains(await AgentsLiveSeed.ChangesAsync(_fixture, due.Id), c => c.Field == ChangeSet.StateField);
        await using (var db = _fixture.CreateContext())
        {
            Assert.Equal(AgentScheduler.DidNotFinish, (await db.Evaluations.AsNoTracking().SingleAsync(e => e.Id == orphanId, Ct)).OutcomeDetail);
            Assert.Equal(1, await db.Evaluations.CountAsync(e => e.AgentId == due.Id && e.FinishedAt == null, Ct));
        }

        await AgentsLiveSeed.DeleteUsersAsync(_fixture, user.Id);
    }

    [Fact]
    public async Task KeyChangesStopOrFailAgentsThroughTheRuntime()
    {
        await StartAsync();
        var user = await AgentsLiveSeed.NewUserAsync(_fixture, "keys@example.com");
        var stopped = await AgentsLiveSeed.NewAgentAsync(_fixture, user.Id, "stopped", AgentState.Running);
        var failed = await AgentsLiveSeed.NewAgentAsync(_fixture, user.Id, "failed", AgentState.Paused, symbol: "ETHUSDT");
        var cascaded = await AgentsLiveSeed.NewAgentAsync(_fixture, user.Id, "cascaded", AgentState.Running, symbol: "ETHUSDT");
        var (provider, _, _, _, streams) = AgentsLiveSeed.Build(_fixture);
        await using var _ = provider;
        var scheduler = provider.GetRequiredService<AgentScheduler>();
        var keys = provider.GetRequiredService<IKeyService>();
        var dependency = provider.GetRequiredService<IKeyDependentAgents>();
        Assert.IsType<AgentKeyDependency>(dependency);
        await AgentsLiveSeed.AddProviderKeysAsync(keys, user.Id, AiProvider.Claude);
        await scheduler.StartAsync(Ct);
        await Wait.UntilAsync(() => scheduler.IsTracked(stopped.Id) && scheduler.IsTracked(failed.Id) && scheduler.IsTracked(cascaded.Id), "tracked");

        Assert.Equal(1, await dependency.StopAsync([stopped.Id, Guid.NewGuid()], "Stopped: its exchange key was deleted.", Ct));
        var stoppedRow = await AgentsLiveSeed.AgentAsync(_fixture, stopped.Id);
        Assert.Equal(AgentState.Stopped, stoppedRow.State);
        Assert.Equal("Stopped: its exchange key was deleted.", stoppedRow.StateReason);
        Assert.False(scheduler.IsTracked(stopped.Id));
        Assert.Equal(0, await dependency.StopAsync([stopped.Id], "again", Ct));

        Assert.Equal(1, await dependency.FailAsync([failed.Id], "Claude key is invalid: revoked", Ct));
        var failedRow = await AgentsLiveSeed.AgentAsync(_fixture, failed.Id);
        Assert.Equal(AgentState.Error, failedRow.State);
        Assert.Equal("Claude key is invalid: revoked", failedRow.StateReason);
        Assert.False(scheduler.IsTracked(failed.Id));

        var deletion = await keys.DeleteProviderKeyAsync(user.Id, AiProvider.Claude, stopAgents: true, Ct);
        Assert.Equal(KeyDeletionStatus.Succeeded, deletion.Status);
        Assert.Equal(1, deletion.Agents);
        Assert.Equal(AgentState.Stopped, (await AgentsLiveSeed.AgentAsync(_fixture, cascaded.Id)).State);
        Assert.False(scheduler.IsTracked(cascaded.Id));

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

    private sealed class FakeCouncil : ICouncil
    {
        public CouncilContext? Context { get; private set; }

        public Task<CouncilResult> DeliberateAsync(CouncilContext context, CancellationToken cancellationToken)
        {
            Context = context;
            var decision = new Decision
            {
                FinalAction = DecisionAction.OpenLong, EntryType = OrderType.Limit, EntryPrice = 100m, StopPrice = 90m, Size = 0.1m, Rationale = "fake council",
                RoleOutputs = [new DecisionRoleOutput { Role = CouncilRole.RiskManager, Sequence = 4, Provider = AiProvider.Claude, Model = "m", Status = RoleOutputStatus.Succeeded, ProposedAction = DecisionAction.OpenLong }],
            };
            return Task.FromResult(CouncilResult.Decided(decision));
        }
    }
}
