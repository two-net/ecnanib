using System.Collections.Concurrent;
using System.Threading.Channels;
using Ecnanib.Agents.Abstractions;
using Ecnanib.Agents.Configuration;
using Ecnanib.Database;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Logging.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Candle = Ecnanib.Exchange.Abstractions.Candle;

namespace Ecnanib.Agents.Services;

/// <summary>
/// Runs the agents server-side (FR-AGT-08): follows one candle stream per tracked agent, evaluates each Running or
/// Paused agent once per close of its timeframe (FR-AGT-09) with at most <c>Agents:MaxConcurrentEvaluations</c> at a
/// time and a deadline per timeframe measured from the close (NFR-PERF-01/02), resumes every Running and Paused agent
/// on start (NFR-REL-01), lifts daily-loss pauses at their time (FR-EXE-09) and finalises evaluations a crash left
/// open. Shorter timeframes are served first so a 00:00 UTC burst never starves 15m agents behind 1D councils.
/// </summary>
internal sealed class AgentScheduler : BackgroundService, IAgentRuntime, IEvaluationHost, IAgentGates, ISchedulerStatus
{
    /// <summary>A candle that closed this long before the agent was tracked is still evaluated: the hub's safety poll hands over the candle that closed just before a stream opened.</summary>
    internal static readonly TimeSpan TrackingTolerance = TimeSpan.FromMinutes(1);

    internal static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(1);

    /// <summary>Added to the longest deadline before an unfinished evaluation row counts as orphaned.</summary>
    internal static readonly TimeSpan OrphanGrace = TimeSpan.FromMinutes(5);

    internal static readonly TimeSpan StartupRetry = TimeSpan.FromSeconds(30);

    internal const string InterruptedByRestart = "Interrupted by a restart.";

    internal const string DidNotFinish = "The evaluation did not finish.";

    internal const string ResumedAfterDailyLoss = "Resumed after the daily-loss pause.";

    private static readonly IReadOnlyList<CandleInterval> Priority = AgentsOptions.Timeframes;

    private readonly IDbContextFactory<EcnanibDbContext> _factory;
    private readonly IMarketStreams _streams;
    private readonly EvaluationPipeline _pipeline;
    private readonly IMarketDataLoader _loader;
    private readonly IStrategyCatalog _catalog;
    private readonly IAgentEvents _events;
    private readonly IOptions<AgentsOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger<AgentScheduler> _logger;

    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _gates = new();
    private readonly ConcurrentDictionary<Guid, TrackedAgent> _tracked = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _inFlight = new();
    private readonly ConcurrentDictionary<(MarketKey Key, CandleInterval Interval), DateTimeOffset> _latestOpen = new();
    private readonly ConcurrentDictionary<Guid, byte> _interrupted = new();
    private readonly Channel<CandleArrival> _intake = Channel.CreateUnbounded<CandleArrival>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Dictionary<CandleInterval, Channel<CandleArrival>> _queues = Priority.ToDictionary(t => t, _ => Channel.CreateUnbounded<CandleArrival>());
    private readonly SemaphoreSlim _available = new(0);
    private volatile bool _stopping;

    // NFR-OBS-01: written by the loops, read by the status page; a lock because the timestamps are wider than a word.
    private readonly Lock _statusLock = new();
    private SchedulerPhase _phase = SchedulerPhase.NotStarted;
    private DateTimeOffset? _startedAt;
    private DateTimeOffset? _lastTickAt;
    private DateTimeOffset? _lastErrorAt;
    private string? _lastError;

    public AgentScheduler(
        IDbContextFactory<EcnanibDbContext> factory,
        IMarketStreams streams,
        EvaluationPipeline pipeline,
        IMarketDataLoader loader,
        IStrategyCatalog catalog,
        IAgentEvents events,
        IOptions<AgentsOptions> options,
        TimeProvider time,
        ILogger<AgentScheduler> logger)
    {
        _factory = factory;
        _streams = streams;
        _pipeline = pipeline;
        _loader = loader;
        _catalog = catalog;
        _events = events;
        _options = options;
        _time = time;
        _logger = logger;
    }

    public int TrackedCount => _tracked.Count;

    public IReadOnlyCollection<Guid> TrackedAgents => _tracked.Keys.ToArray();

    // --- ISchedulerStatus ------------------------------------------------------------------------------------------

    public SchedulerStatus GetStatus()
    {
        SchedulerPhase phase;
        DateTimeOffset? startedAt, lastTickAt, lastErrorAt;
        string? lastError;
        lock (_statusLock)
        {
            phase = _phase;
            startedAt = _startedAt;
            lastTickAt = _lastTickAt;
            lastErrorAt = _lastErrorAt;
            lastError = _lastError;
        }

        var queued = _queues.Values.Sum(q => q.Reader.CanCount ? q.Reader.Count : 0);
        return new SchedulerStatus(phase, startedAt, lastTickAt, _tracked.Count, _inFlight.Count, queued, _options.Value.MaxConcurrentEvaluations, lastError, lastErrorAt);
    }

    private void SetPhase(SchedulerPhase phase)
    {
        lock (_statusLock)
        {
            _phase = phase;
            if (phase == SchedulerPhase.Running)
                _startedAt = _time.GetUtcNow();
        }
    }

    private void RecordError(string message)
    {
        lock (_statusLock)
        {
            _lastError = message;
            _lastErrorAt = _time.GetUtcNow();
        }
    }

    private void RecordTick()
    {
        lock (_statusLock)
            _lastTickAt = _time.GetUtcNow();
    }

    // --- IAgentRuntime -------------------------------------------------------------------------------------------

    public async Task<IDisposable> LockAsync(Guid agentId, CancellationToken cancellationToken)
    {
        var gate = GateFor(agentId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Releaser(gate);
    }

    public IDisposable? TryLock(Guid agentId)
    {
        var gate = GateFor(agentId);
        return gate.Wait(0) ? new Releaser(gate) : null;
    }

    public void CancelEvaluation(Guid agentId)
    {
        if (!_inFlight.TryGetValue(agentId, out var cts))
            return;
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The run finished between the lookup and the cancel.
        }
    }

    public bool IsTracked(Guid agentId) => _tracked.ContainsKey(agentId);

    public bool Track(AgentTrackingInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (_stopping)
            return false;

        var tracked = new TrackedAgent(info, _time.GetUtcNow());
        if (!_tracked.TryAdd(info.AgentId, tracked))
            return true;

        try
        {
            // The loader subscribes first, so on the shared stream its handler runs ahead of the intake (FR-DATA-02/03).
            _loader.Track(new MarketDataNeeds(info.AgentId, info.Key, info.Timeframe, _catalog.Find(info.StrategyId, info.StrategyVersion)));
            tracked.Subscription = _streams.SubscribeCandles(info.Key, info.Timeframe, (candle, _) =>
            {
                _intake.Writer.TryWrite(new CandleArrival(tracked, candle));
                return ValueTask.CompletedTask;
            });
        }
        catch
        {
            _tracked.TryRemove(info.AgentId, out _);
            _loader.Untrack(info.AgentId);
            throw;
        }

        if (_stopping)
        {
            // Shutdown began meanwhile and StopAsync may already have released the entry: release what this call made.
            _tracked.TryRemove(new KeyValuePair<Guid, TrackedAgent>(info.AgentId, tracked));
            tracked.Subscription?.Dispose();
            _loader.Untrack(info.AgentId);
            return false;
        }

        _logger.LogInformation("Agent {AgentId} is tracked on {Key} {Timeframe}", info.AgentId, info.Key, info.Timeframe.ToLabel());
        return true;
    }

    public void Untrack(Guid agentId)
    {
        if (Release(agentId))
            _logger.LogInformation("Agent {AgentId} is no longer tracked", agentId);
    }

    /// <summary>Drops the agent's stream and lets the loader release its series; false when it was not tracked.</summary>
    private bool Release(Guid agentId)
    {
        if (!_tracked.TryRemove(agentId, out var tracked))
            return false;
        tracked.Subscription?.Dispose();
        _loader.Untrack(agentId);
        return true;
    }

    public void Forget(Guid agentId)
    {
        Untrack(agentId);
        _gates.TryRemove(agentId, out _);
    }

    public async Task<bool> FailAsync(Guid agentId, string reason, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        Untrack(agentId);
        var text = reason.Length <= EvaluationPipeline.MaxDetailLength ? reason : reason[..EvaluationPipeline.MaxDetailLength];
        var now = _time.GetUtcNow();
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var agent = await db.Agents.Where(a => a.Id == agentId).Select(a => new { a.UserId, a.State }).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (agent is null || !AgentStateMachine.IsEvaluated(agent.State))
            return false;

        var rows = await db.Agents
            .Where(a => a.Id == agentId && (a.State == AgentState.Running || a.State == AgentState.Paused))
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.State, AgentState.Error)
                .SetProperty(a => a.PauseCause, (PauseCause?)null)
                .SetProperty(a => a.AutoResumeAt, (DateTimeOffset?)null)
                .SetProperty(a => a.StateReason, text)
                .SetProperty(a => a.UpdatedAt, now), cancellationToken)
            .ConfigureAwait(false);
        if (rows == 0)
            return false;

        db.AgentChanges.Add(ChangeSet.State(agentId, now, agent.State, AgentState.Error, text));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogWarning("Agent {AgentId} entered Error: {Reason}", agentId, text);
        _events.Publish(new AgentStateChanged(agentId, agent.UserId, now, AgentState.Error, text));
        return true;
    }

    // --- Hosted service -------------------------------------------------------------------------------------------

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        SetPhase(SchedulerPhase.Starting);
        if (!await StartupAsync(stoppingToken).ConfigureAwait(false))
        {
            SetPhase(SchedulerPhase.Stopped);
            return;
        }

        SetPhase(SchedulerPhase.Running);
        var loops = new List<Task> { Guard(DispatchAsync(stoppingToken)), Guard(TickAsync(stoppingToken)) };
        for (var i = 0; i < _options.Value.MaxConcurrentEvaluations; i++)
            loops.Add(Guard(WorkAsync(stoppingToken)));
        await Task.WhenAll(loops).ConfigureAwait(false);

        async Task Guard(Task loop)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                RecordError(ex.Message);
                _logger.LogError(ex, "An agent scheduler loop stopped unexpectedly");
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping = true;
        _intake.Writer.TryComplete();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        SetPhase(SchedulerPhase.Stopped);
        // Released one by one so the loader lets go of every series before the hub closes the streams.
        foreach (var agentId in _tracked.Keys.ToArray())
            Release(agentId);
    }

    /// <summary>Finalises evaluations a crash left open and re-tracks every Running and Paused agent (NFR-REL-01); retried until the database answers.</summary>
    private async Task<bool> StartupAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepInterruptedAsync(stoppingToken).ConfigureAwait(false);
                await ResumeAgentsAsync(stoppingToken).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception ex)
            {
                RecordError(ex.Message);
                _logger.LogWarning(ex, "The agent scheduler could not start; next attempt in {Delay}", StartupRetry);
                if (!await DelayAsync(StartupRetry, stoppingToken).ConfigureAwait(false))
                    return false;
            }
        }

        return false;
    }

    internal async Task<int> SweepInterruptedAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var orphans = await db.Evaluations.Where(e => e.FinishedAt == null)
            .Select(e => new { e.Id, e.AgentId, e.Agent.UserId, e.CandleOpenTime })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var agentIds = orphans.Select(o => o.AgentId).Distinct().ToList();
        foreach (var id in agentIds)
            _interrupted[id] = 0;
        var rows = await db.Evaluations.Where(e => e.FinishedAt == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.Outcome, EvaluationOutcome.Failed)
                .SetProperty(e => e.OutcomeDetail, InterruptedByRestart)
                .SetProperty(e => e.FinishedAt, now), cancellationToken)
            .ConfigureAwait(false);
        if (rows > 0)
            _logger.LogWarning("{Evaluations} evaluation(s) of {Agents} agent(s) were in flight at the last stop and are recorded as failed", rows, agentIds.Count);
        // FR-UI-04: a page showing one of these as "evaluating" learns that it ended.
        foreach (var orphan in orphans)
            _events.Publish(new EvaluationFinished(orphan.AgentId, orphan.UserId, now, orphan.Id, orphan.CandleOpenTime, EvaluationOutcome.Failed));
        return rows;
    }

    internal async Task<int> ResumeAgentsAsync(CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var agents = await db.Agents.AsNoTracking()
            .Where(a => a.State == AgentState.Running || a.State == AgentState.Paused)
            .Select(a => new { a.Id, a.UserId, a.MarketType, a.Symbol, a.Timeframe, a.StrategyId, a.StrategyVersion, a.Mode, Environment = a.ExchangeKey != null ? a.ExchangeKey.Environment : (ExchangeEnvironment?)null })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var tracked = 0;
        foreach (var agent in agents)
        {
            var environment = agent.Mode == AgentMode.Live ? agent.Environment ?? ExchangeEnvironment.Production : ExchangeEnvironment.Production;
            if (Track(new AgentTrackingInfo(agent.Id, agent.UserId, new MarketKey(environment, agent.MarketType, agent.Symbol), agent.Timeframe, agent.StrategyId, agent.StrategyVersion)))
                tracked++;
        }

        _logger.LogInformation("Resumed {Agents} running or paused agent(s)", tracked);
        return tracked;
    }

    private async Task DispatchAsync(CancellationToken stoppingToken)
    {
        await foreach (var arrival in _intake.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            var info = arrival.Agent.Info;
            var open = arrival.Candle.OpenTime;
            _latestOpen.AddOrUpdate((info.Key, info.Timeframe), open, (_, previous) => open > previous ? open : previous);
            Enqueue(arrival);
        }
    }

    private void Enqueue(CandleArrival arrival)
    {
        _queues[arrival.Agent.Info.Timeframe].Writer.TryWrite(arrival);
        _available.Release();
    }

    private async Task WorkAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await _available.WaitAsync(stoppingToken).ConfigureAwait(false);
            var arrival = TakeNext();
            if (arrival is null)
                continue;
            try
            {
                await ProcessAsync(arrival, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Evaluation of agent {AgentId} for candle {CandleOpen:O} failed unexpectedly", arrival.Agent.Info.AgentId, arrival.Candle.OpenTime);
            }
        }
    }

    private CandleArrival? TakeNext()
    {
        foreach (var timeframe in Priority)
        {
            if (_queues[timeframe].Reader.TryRead(out var arrival))
                return arrival;
        }

        return null;
    }

    private async Task ProcessAsync(CandleArrival arrival, CancellationToken stoppingToken)
    {
        var tracked = arrival.Agent;
        var info = tracked.Info;
        if (!IsCurrent(tracked))
            return;

        // FR-LOG-02: every log of this arrival, down to the council, the providers and the venue, carries the agent and its user.
        using var scope = _logger.BeginScope(agentId: info.AgentId, userId: info.UserId);
        var gate = GateFor(info.AgentId);
        if (!gate.Wait(0))
        {
            // A lifecycle action holds the agent; wait for it without holding an evaluation slot.
            _ = RequeueWhenFreeAsync(arrival, gate, stoppingToken);
            return;
        }

        try
        {
            if (!IsCurrent(tracked))
                return;

            var candle = arrival.Candle;
            var interval = info.Timeframe;
            var deadlineLength = _options.Value.DeadlineFor(interval);
            var deadlineAt = candle.OpenTime + interval.ToTimeSpan() + deadlineLength;
            var now = _time.GetUtcNow();
            _latestOpen.TryGetValue((info.Key, interval), out var latest);

            switch (Classify(candle.OpenTime, interval, tracked.TrackedSince, latest, now, deadlineLength))
            {
                case ArrivalDisposition.ClosedBeforeTracking:
                    _logger.LogDebug("Agent {AgentId} ignores candle {CandleOpen:O}, which closed before the agent was tracked", info.AgentId, candle.OpenTime);
                    return;
                case ArrivalDisposition.Superseded:
                    await _pipeline.RecordSkippedAsync(info, candle, "Superseded by a later candle.", stoppingToken).ConfigureAwait(false);
                    return;
                case ArrivalDisposition.TooLate:
                    await _pipeline.RecordSkippedAsync(info, candle, $"Too late to start: the {deadlineLength.TotalSeconds:0} s deadline passed at {deadlineAt:HH:mm:ss} UTC.", stoppingToken).ConfigureAwait(false);
                    return;
            }

            using var deadline = new CancellationTokenSource(deadlineAt - now, _time);
            using var inFlight = new CancellationTokenSource();
            _inFlight[info.AgentId] = inFlight;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, inFlight.Token, deadline.Token);
            try
            {
                var interrupted = _interrupted.TryRemove(info.AgentId, out _);
                await _pipeline.RunAsync(new EvaluationRun(info, candle, deadlineAt, interrupted), this, linked.Token, stoppingToken).ConfigureAwait(false);
            }
            finally
            {
                _inFlight.TryRemove(new KeyValuePair<Guid, CancellationTokenSource>(info.AgentId, inFlight));
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// The staleness rules (FR-AGT-09): a candle that closed more than <see cref="TrackingTolerance"/> before the agent
    /// was tracked is ignored; one older than the newest candle seen on its stream (a gap-fill burst) is superseded; one
    /// whose deadline, measured from its close, has already passed is too late to start (NFR-PERF-01).
    /// </summary>
    internal static ArrivalDisposition Classify(DateTimeOffset candleOpen, CandleInterval interval, DateTimeOffset trackedSince, DateTimeOffset? latestOpenSeen, DateTimeOffset now, TimeSpan deadline)
    {
        var boundary = candleOpen + interval.ToTimeSpan();
        if (boundary < trackedSince - TrackingTolerance)
            return ArrivalDisposition.ClosedBeforeTracking;
        if (latestOpenSeen is { } latest && candleOpen < latest)
            return ArrivalDisposition.Superseded;
        if (now >= boundary + deadline)
            return ArrivalDisposition.TooLate;
        return ArrivalDisposition.Evaluate;
    }

    internal enum ArrivalDisposition
    {
        Evaluate,
        ClosedBeforeTracking,
        Superseded,
        TooLate,
    }

    private bool IsCurrent(TrackedAgent tracked) =>
        _tracked.TryGetValue(tracked.Info.AgentId, out var current) && ReferenceEquals(current, tracked);

    private async Task RequeueWhenFreeAsync(CandleArrival arrival, SemaphoreSlim gate, CancellationToken stoppingToken)
    {
        try
        {
            await gate.WaitAsync(stoppingToken).ConfigureAwait(false);
            gate.Release();
            if (IsCurrent(arrival.Agent))
                Enqueue(arrival);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task TickAsync(CancellationToken stoppingToken)
    {
        while (await DelayAsync(TickInterval, stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await TickOnceAsync(stoppingToken).ConfigureAwait(false);
                RecordTick();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                RecordError(ex.Message);
                _logger.LogWarning(ex, "The agent scheduler's minute tick failed; next attempt in {Interval}", TickInterval);
            }
        }
    }

    /// <summary>Lifts daily-loss pauses that are due (FR-EXE-09) and finalises evaluation rows nobody will finish.</summary>
    internal async Task<(int Resumed, int Finalised)> TickOnceAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var due = await db.Agents
            .Where(a => a.State == AgentState.Paused && a.PauseCause == PauseCause.DailyLoss && a.AutoResumeAt != null && a.AutoResumeAt <= now)
            .Select(a => new { a.Id, a.UserId })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var resumedAgents = new List<(Guid Id, Guid UserId)>();
        foreach (var agent in due)
        {
            var rows = await db.Agents
                .Where(a => a.Id == agent.Id && a.State == AgentState.Paused && a.PauseCause == PauseCause.DailyLoss)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(a => a.State, AgentState.Running)
                    .SetProperty(a => a.PauseCause, (PauseCause?)null)
                    .SetProperty(a => a.AutoResumeAt, (DateTimeOffset?)null)
                    .SetProperty(a => a.StateReason, (string?)null)
                    .SetProperty(a => a.UpdatedAt, now), cancellationToken)
                .ConfigureAwait(false);
            if (rows == 0)
                continue;
            resumedAgents.Add((agent.Id, agent.UserId));
            db.AgentChanges.Add(ChangeSet.State(agent.Id, now, AgentState.Paused, AgentState.Running, ResumedAfterDailyLoss));
            _logger.LogInformation("Agent {AgentId} resumed after its daily-loss pause", agent.Id);
        }

        if (resumedAgents.Count > 0)
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        // Published after the change history is committed, so a page that re-reads on the event sees all of it.
        foreach (var (id, userId) in resumedAgents)
            _events.Publish(new AgentStateChanged(id, userId, now, AgentState.Running, ResumedAfterDailyLoss));

        var cutoff = now - (_options.Value.LongestDeadline + OrphanGrace);
        var orphans = await db.Evaluations.Where(e => e.FinishedAt == null && e.StartedAt < cutoff)
            .Select(e => new { e.Id, e.AgentId, e.Agent.UserId, e.CandleOpenTime })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var finalised = await db.Evaluations.Where(e => e.FinishedAt == null && e.StartedAt < cutoff)
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.Outcome, EvaluationOutcome.Failed)
                .SetProperty(e => e.OutcomeDetail, DidNotFinish)
                .SetProperty(e => e.FinishedAt, now), cancellationToken)
            .ConfigureAwait(false);
        if (finalised > 0)
            _logger.LogWarning("{Evaluations} evaluation(s) never finished and are recorded as failed", finalised);
        foreach (var orphan in orphans)
            _events.Publish(new EvaluationFinished(orphan.AgentId, orphan.UserId, now, orphan.Id, orphan.CandleOpenTime, EvaluationOutcome.Failed));

        return (resumedAgents.Count, finalised);
    }

    private SemaphoreSlim GateFor(Guid agentId) => _gates.GetOrAdd(agentId, _ => new SemaphoreSlim(1, 1));

    private async Task<bool> DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, _time, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private sealed record CandleArrival(TrackedAgent Agent, Candle Candle);

    private sealed class TrackedAgent(AgentTrackingInfo info, DateTimeOffset trackedSince)
    {
        public AgentTrackingInfo Info { get; } = info;

        public DateTimeOffset TrackedSince { get; } = trackedSince;

        public IDisposable? Subscription { get; set; }
    }

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                gate.Release();
        }
    }
}
