using System.Threading.Channels;
using Ecnanib.Agents;
using Ecnanib.ExchangeGateway;
using Ecnanib.Platform.BackgroundWork;
using Ecnanib.Platform.Logging;
using Ecnanib.Platform.Settings;
using Ecnanib.Shared;

namespace Ecnanib.DecisionEngine;

/// <summary>
/// Starts a cycle for each running agent when a candle of its timeframe closes (FR-CYC-1), and
/// lets the cycles that share a close wait in a queue for their turns (FR-CYC-11). See
/// docs/cycles.md.
/// </summary>
/// <remarks>
/// <para>
/// It runs at every quarter hour in UTC, which is the close of the shortest candle. One run is
/// for one close, and has the cycles of every timeframe of which a candle closed then: the hour
/// has those of 15 minutes and of an hour, and midnight has those of all four. So the cycles
/// that share a boundary are in one queue, the shortest timeframe first.
/// </para>
/// <para>
/// A close that passed while the application was down has no run: the scheduler skips a boundary
/// that passed, and never makes up for it (FR-CYC-8). Nothing of a queue is stored, so a restart
/// finds no cycle to go on with, and waits for the next close.
/// </para>
/// <para>
/// The cycles of a close are over before the next close. The scheduler runs a job once at a
/// time, and skips a boundary whose run could not start, so a run that went on past the quarter
/// hour would cost every agent its next cycle. A cycle that could not start in time is skipped,
/// and one that still runs as the next close nears is stopped.
/// </para>
/// </remarks>
internal sealed class DecisionCyclesJob(
    IRunningAgents agents,
    ClosedCandles candles,
    IDecisionCycle work,
    IEnumerable<IReconciliation> reconciliations,
    TradingSettings trading,
    TimeProvider clock,
    ILogger<DecisionCyclesJob> logger) : IBackgroundJob
{
    /// <summary>The name of the job, which is the key of its row of the <c>background_jobs</c> table.</summary>
    public const string JobName = "decision-cycles";

    /// <summary>
    /// How long after its close a cycle may still start. A cycle whose turn has not come by then
    /// is skipped. The longest cycle is three calls of the team one after the other, each made
    /// twice at most (FR-TEAM-9): with the time limit of a minute of the tracked file that is
    /// six minutes, so a cycle that starts within five is over long before it would be stopped.
    /// </summary>
    internal static readonly TimeSpan StartsWithin = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long before the next close the cycles that still run are stopped. A cycle that has
    /// just placed an entry needs one more call to the exchange to protect it, which takes its
    /// time limit and its wait at the limiter at most, and is over before the next run is due.
    /// </summary>
    internal static readonly TimeSpan EndsBefore = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long the first cycles wait for the candles of the others. The cycles of a symbol
    /// whose candle the exchange gives a moment later than another's then find their place in
    /// the order, and not a queue that the longer timeframes have gone ahead in.
    /// </summary>
    internal static readonly TimeSpan Gather = TimeSpan.FromSeconds(5);

    /// <summary>How long after its close a cycle is to have ended, for 95 of 100 (NFR-PRF-2).</summary>
    internal static readonly TimeSpan Target = TimeSpan.FromSeconds(90);

    // The close of the shortest candle. Every other timeframe is a multiple of it.
    private static readonly TimeSpan Period = Timeframes.All.Min(timeframe => timeframe.Length());

    private readonly IReadOnlyList<IReconciliation> _reconciliations = [.. reconciliations];

    public string Name => JobName;

    public BackgroundJobSchedule Schedule { get; } = BackgroundJobSchedule.AtUtcBoundaries(Period);

    public async Task RunAsync(BackgroundJobRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);

        var closedAt = run.ScheduledFor;

        // The scheduler starts a run that is late for its boundary, as long as the following one
        // has not passed, and leaves it to the job whether it is too late to act.
        if (clock.GetUtcNow() >= closedAt + StartsWithin)
        {
            logger.LogWarning(
                "The cycles of the close at {ClosedAt} could not start within {StartsWithinMinutes} minutes of it, so none starts",
                closedAt,
                StartsWithin.TotalMinutes);

            return;
        }

        // Which agents, and whose: read first, so that nothing of the database is held while
        // the exchange is asked and the cycles run.
        var running = await agents.OfTimeframesAsync(Timeframes.ClosingAt(closedAt), cancellationToken);

        if (running.Count == 0)
        {
            // Nobody is asked for anything: no agent runs on a timeframe that closed.
            logger.LogDebug("No agent runs on a timeframe of which a candle closed at {ClosedAt}", closedAt);

            return;
        }

        var batch = new CycleBatch(
            agents,
            candles,
            work,
            _reconciliations,
            new CycleQueue(trading.Cycles.MaxPerProvider, trading.Cycles.MaxPerKey),
            clock,
            logger,
            closedAt,
            closedAt + Period - EndsBefore,
            cancellationToken);

        await batch.RunAsync(running);
    }
}

/// <summary>
/// The cycles of one close, from the candles that they start with to the last of them that
/// ends: one run of <see cref="DecisionCyclesJob"/>. See docs/cycles.md.
/// </summary>
/// <remarks>
/// <para>
/// Everything that happens comes in as an event, to one loop: the candles of a symbol arrived,
/// or a cycle is over. Only the loop touches the queue and the counts, so nothing here takes a
/// lock. The loop wakes at least once a second and looks at the clock, as the scheduler does:
/// what is due by the time of day is due when the clock says so, also after the clock was set.
/// </para>
/// <para>
/// It returns only when every cycle that it started has ended. A cycle is never left behind: it
/// would run on beside the next cycle of its agent, and outside the lock of the background work.
/// </para>
/// </remarks>
internal sealed class CycleBatch(
    IRunningAgents agents,
    ClosedCandles candles,
    IDecisionCycle work,
    IReadOnlyList<IReconciliation> reconciliations,
    CycleQueue queue,
    TimeProvider clock,
    ILogger logger,
    DateTimeOffset closedAt,
    DateTimeOffset endsAt,
    CancellationToken stopping)
{
    // The longest that the loop sleeps before it looks at the clock again.
    private static readonly TimeSpan Beat = TimeSpan.FromSeconds(1);

    private readonly DateTimeOffset _startsBy = closedAt + DecisionCyclesJob.StartsWithin;

    private readonly Channel<Happened> _events =
        Channel.CreateUnbounded<Happened>(new UnboundedChannelOptions { SingleReader = true });

    // The cycles whose candles the exchange has not given yet, by what the candles are of.
    private readonly Dictionary<CandleGroup, List<QueuedCycle>> _unsettled = [];

    // The candles that arrived, which every cycle of their group is handed.
    private readonly Dictionary<CandleGroup, ExchangeCandles> _candles = [];

    // Everything that was started: the asking for candles, and the cycles. None of them throws.
    private readonly List<Task> _started = [];

    private int _running;
    private bool _admitting;
    private bool _outOfTime;
    private Counts _counts;

    /// <summary>Runs the cycles of the close for the agents that run on a timeframe of it.</summary>
    /// <exception cref="OperationCanceledException">The application stopped the run. Every cycle has ended by then.</exception>
    public async Task RunAsync(IReadOnlyList<RunningAgent> running)
    {
        var began = clock.GetTimestamp();
        var beganAt = clock.GetUtcNow();

        // Canceled when the run is stopped, and when the cycles are out of time.
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(stopping);

        // A stop wakes the loop at once, so that nothing more starts.
        using var woken = stopping.Register(() => _events.Writer.TryWrite(new Woken()));

        Queue(running);

        logger.LogInformation(
            "{CycleCount} cycles are due for the close at {ClosedAt}, with candles of {GroupCount} symbols and timeframes",
            _unsettled.Values.Sum(cycles => cycles.Count),
            closedAt,
            _unsettled.Count);

        try
        {
            foreach (var group in _unsettled.Keys)
            {
                _started.Add(Task.Run(() => AskAsync(group, stop.Token), CancellationToken.None));
            }

            while (_unsettled.Count > 0 || queue.Count > 0 || _running > 0)
            {
                Take(await NextAsync());

                var now = clock.GetUtcNow();

                if (stopping.IsCancellationRequested)
                {
                    // The application stops, or is no longer the instance that runs the
                    // background work. Nothing more starts, and what runs is being stopped.
                    _counts.Stopped += Drop();
                }
                else if (now >= _startsBy && Drop() is var late and > 0)
                {
                    _counts.TooLate += late;

                    logger.LogWarning(
                        "{CycleCount} cycles of the close at {ClosedAt} could not start within {StartsWithinMinutes} minutes of it, and are skipped",
                        late,
                        closedAt,
                        DecisionCyclesJob.StartsWithin.TotalMinutes);
                }

                if (!_outOfTime && now >= endsAt)
                {
                    _outOfTime = true;

                    if (_running > 0)
                    {
                        logger.LogWarning(
                            "{CycleCount} cycles of the close at {ClosedAt} still run {EndsBeforeSeconds} seconds before the next close, and are stopped",
                            _running,
                            closedAt,
                            DecisionCyclesJob.EndsBefore.TotalSeconds);
                    }

                    await stop.CancelAsync();
                }

                if (stop.IsCancellationRequested)
                {
                    continue;
                }

                // The first cycles start once the candles of every symbol are in, or have been
                // waited for long enough. From then on a cycle starts as soon as it fits.
                _admitting = _admitting || _unsettled.Count == 0 || now >= beganAt + DecisionCyclesJob.Gather;

                if (_admitting)
                {
                    foreach (var cycle in queue.TakeWhatFits())
                    {
                        // Looked up here, by the loop: the cycle runs beside it.
                        var through = _candles[cycle.Group];

                        _running++;
                        _started.Add(Task.Run(() => RunCycleAsync(cycle, through, stop.Token), CancellationToken.None));
                    }
                }
            }
        }
        finally
        {
            // Whatever ended the loop, nothing outlives the run.
            await stop.CancelAsync();
            await Task.WhenAll(_started);
        }

        Summarize(Math.Round(clock.GetElapsedTime(began).TotalMilliseconds, 1));

        // A run that was stopped says so, and the scheduler records it as stopped.
        stopping.ThrowIfCancellationRequested();
    }

    /// <summary>Makes a cycle for each agent, and sorts them by the candles that they wait for.</summary>
    private void Queue(IReadOnlyList<RunningAgent> running)
    {
        var turns = new Dictionary<(Guid UserId, Timeframe Timeframe), int>();

        // In the order of the ids, whatever order they came in: the turn of an agent among those
        // of its user is the same at every close.
        foreach (var (agent, roles) in running.OrderBy(one => one.Agent.Id))
        {
            var turn = turns.GetValueOrDefault((agent.UserId, agent.Timeframe));
            turns[(agent.UserId, agent.Timeframe)] = turn + 1;

            var group = GroupOf(agent);
            var cycle = new QueuedCycle(
                Guid.CreateVersion7(clock.GetUtcNow()),
                agent.Id,
                agent.UserId,
                group,
                turn,
                roles.Select(role => role.Provider).ToHashSet());

            if (!_unsettled.TryGetValue(group, out var cycles))
            {
                _unsettled[group] = cycles = [];
            }

            cycles.Add(cycle);
        }
    }

    private static CandleGroup GroupOf(Agent agent) => new(agent.Market, agent.Mode, agent.Symbol, agent.Timeframe);

    /// <summary>The next event, or nothing when a beat passed without one.</summary>
    private async Task<Happened?> NextAsync()
    {
        if (_events.Reader.TryRead(out var happened))
        {
            return happened;
        }

        using var beat = new CancellationTokenSource(Beat, clock);

        try
        {
            return await _events.Reader.ReadAsync(beat.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private void Take(Happened? happened)
    {
        switch (happened)
        {
            case CandlesSettled settled:
                Settle(settled);
                break;

            case CycleOver over:
                queue.Release(over.Cycle);
                _running--;
                _counts.Add(over.End);
                break;
        }
    }

    /// <summary>The candles of a group arrived, or did not: its cycles join the queue, or are skipped.</summary>
    private void Settle(CandlesSettled settled)
    {
        // Not there when its cycles were dropped meanwhile.
        if (!_unsettled.Remove(settled.Group, out var cycles))
        {
            return;
        }

        if (settled.Candles is { } through)
        {
            _candles[settled.Group] = through;

            foreach (var cycle in cycles)
            {
                queue.Add(cycle);
            }

            return;
        }

        if (stopping.IsCancellationRequested)
        {
            _counts.Stopped += cycles.Count;

            return;
        }

        _counts.WithoutCandle += cycles.Count;

        logger.LogWarning(
            "The exchange did not give the candle of {Symbol} on {Market} in {TradingMode} that closed at {ClosedAt} ({Timeframe}) " +
            "within {CandleWaitSeconds} seconds of the close: {ExchangeCallOutcome}. {CycleCount} cycles are skipped",
            settled.Group.Symbol,
            settled.Group.Market,
            settled.Group.Mode,
            closedAt,
            settled.Group.Timeframe.Code(),
            ClosedCandles.Wait.TotalSeconds,
            settled.Last,
            cycles.Count);
    }

    /// <summary>Takes every cycle that has not started out of the run, and says how many they were.</summary>
    private int Drop()
    {
        var dropped = queue.Drain().Count + _unsettled.Values.Sum(cycles => cycles.Count);
        _unsettled.Clear();

        return dropped;
    }

    /// <summary>Asks the exchange for the candles of a group, and says what came of it. It does not throw.</summary>
    private async Task AskAsync(CandleGroup group, CancellationToken stop)
    {
        ExchangeCandles? through = null;
        ExchangeCallOutcome? last = null;

        try
        {
            (through, last) = await candles.ThroughAsync(group, closedAt, stop);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // The cycles of the close are stopped.
        }
        catch (Exception error)
        {
            // A symbol that the configuration file no longer lists, for one. The other symbols go on.
            logger.LogError(
                error,
                "The candles of {Symbol} on {Market} in {TradingMode} could not be asked for ({Timeframe})",
                group.Symbol,
                group.Market,
                group.Mode,
                group.Timeframe.Code());
        }

        _events.Writer.TryWrite(new CandlesSettled(group, through, last));
    }

    /// <summary>Runs a cycle whose turn has come, and says how it ended. It does not throw.</summary>
    private async Task RunCycleAsync(QueuedCycle cycle, ExchangeCandles through, CancellationToken stop)
    {
        // Work that runs outside a request opens its own scope, and logs its own failures
        // inside it, so that every event of the cycle carries the user, the agent, and the cycle.
        using var scope = logger.BeginCorrelation(new Correlation(
            UserId: cycle.UserId.ToString(), AgentId: cycle.AgentId.ToString(), CycleId: cycle.CycleId.ToString()));

        CycleEnd end;

        try
        {
            end = await TurnAsync(cycle, through, stop);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            end = CycleEnd.Stopped;
        }
        catch (Exception error)
        {
            // The database is away, most likely. The cycle is not tried again: the next close
            // starts the next one.
            logger.LogError(
                error, "The cycle of agent {AgentId} for the close at {ClosedAt} could not start", cycle.AgentId, closedAt);

            end = CycleEnd.Failed;
        }

        _events.Writer.TryWrite(new CycleOver(cycle, end));
    }

    private async Task<CycleEnd> TurnAsync(QueuedCycle cycle, ExchangeCandles through, CancellationToken stop)
    {
        // Read again, now that the turn has come: only an agent that runs gets a cycle
        // (SRS 3.3), and its user may have paused or stopped it while the cycle waited.
        var running = await agents.FindAsync(cycle.AgentId, stop);

        if (running is null)
        {
            logger.LogInformation(
                "Agent {AgentId} no longer runs, so it gets no cycle for the close at {ClosedAt}", cycle.AgentId, closedAt);

            return CycleEnd.NotRunning;
        }

        if (GroupOf(running.Agent) != cycle.Group)
        {
            // It was stopped, given another market, symbol, or timeframe, and started again.
            // The candles are not those of the agent that it is now.
            logger.LogInformation(
                "Agent {AgentId} was given other settings since the close at {ClosedAt}, so it gets no cycle for that close",
                cycle.AgentId,
                closedAt);

            return CycleEnd.NotRunning;
        }

        foreach (var reconciliation in reconciliations)
        {
            if (!await reconciliation.HasReconciledAsync(running, stop))
            {
                // After a restart the system reconciles first (FR-CYC-8).
                logger.LogInformation(
                    "Agent {AgentId} is not reconciled with the exchange yet, so it gets no cycle for the close at {ClosedAt}",
                    cycle.AgentId,
                    closedAt);

                return CycleEnd.NotReconciled;
            }
        }

        var began = clock.GetTimestamp();

        logger.LogInformation(
            "The cycle of agent {AgentId} for the close at {ClosedAt} ({Timeframe}) started {StartDelayMs} ms after the close",
            cycle.AgentId,
            closedAt,
            cycle.Group.Timeframe.Code(),
            Milliseconds(clock.GetUtcNow() - closedAt));

        try
        {
            await work.RunAsync(new CycleStart(cycle.CycleId, running.Agent, running.Roles, closedAt, through), stop);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            if (stopping.IsCancellationRequested)
            {
                logger.LogInformation(
                    "The cycle of agent {AgentId} for the close at {ClosedAt} was stopped after {DurationMs} ms, with the application",
                    cycle.AgentId,
                    closedAt,
                    Milliseconds(clock.GetElapsedTime(began)));

                return CycleEnd.Stopped;
            }

            logger.LogWarning(
                "The cycle of agent {AgentId} for the close at {ClosedAt} was stopped after {DurationMs} ms: the next close is near",
                cycle.AgentId,
                closedAt,
                Milliseconds(clock.GetElapsedTime(began)));

            return CycleEnd.OutOfTime;
        }
        catch (Exception error)
        {
            // Logged here, once, inside the scope of the cycle. The other cycles go on.
            logger.LogError(
                error,
                "The cycle of agent {AgentId} for the close at {ClosedAt} failed after {DurationMs} ms, with no decision",
                cycle.AgentId,
                closedAt,
                Milliseconds(clock.GetElapsedTime(began)));

            return CycleEnd.Failed;
        }

        var sinceClose = clock.GetUtcNow() - closedAt;

        logger.LogInformation(
            "The cycle of agent {AgentId} for the close at {ClosedAt} ended {EndDelayMs} ms after the close, in {DurationMs} ms",
            cycle.AgentId,
            closedAt,
            Milliseconds(sinceClose),
            Milliseconds(clock.GetElapsedTime(began)));

        return sinceClose > DecisionCyclesJob.Target ? CycleEnd.EndedLate : CycleEnd.Ended;
    }

    /// <summary>
    /// Says what became of the cycles of the close, once. It is a warning when fewer than 95 of
    /// 100 ended within the target (NFR-PRF-2), which the log is then asked about.
    /// </summary>
    private void Summarize(double durationMs)
    {
        var late = _counts.EndedLate + _counts.OutOfTime + _counts.TooLate;
        var timed = _counts.Ended + late;

        // More than one in twenty: 5 of 100.
        var behind = !stopping.IsCancellationRequested && late * 20 > timed;

        logger.Log(
            behind ? LogLevel.Warning : LogLevel.Information,
            "The cycles of the close at {ClosedAt} are over after {DurationMs} ms: {Ended} ended within {TargetSeconds} seconds of the close " +
            "and {EndedLate} later, {Failed} failed, {OutOfTime} were stopped before the next close, {TooLate} could not start in time, " +
            "{WithoutCandle} had no candle, {NotReconciled} were not reconciled, {NotRunning} no longer ran, and {Stopped} were stopped with the application",
            closedAt,
            durationMs,
            _counts.Ended,
            DecisionCyclesJob.Target.TotalSeconds,
            _counts.EndedLate,
            _counts.Failed,
            _counts.OutOfTime,
            _counts.TooLate,
            _counts.WithoutCandle,
            _counts.NotReconciled,
            _counts.NotRunning,
            _counts.Stopped);
    }

    private static double Milliseconds(TimeSpan time) => Math.Round(time.TotalMilliseconds, 1);

    /// <summary>What the loop is told.</summary>
    private abstract record Happened;

    /// <summary>The asking for the candles of a group is over: with the candles through the close, or without.</summary>
    private sealed record CandlesSettled(CandleGroup Group, ExchangeCandles? Candles, ExchangeCallOutcome? Last) : Happened;

    /// <summary>A cycle that was taken from the queue is over, and its places are free.</summary>
    private sealed record CycleOver(QueuedCycle Cycle, CycleEnd End) : Happened;

    /// <summary>The run is being stopped.</summary>
    private sealed record Woken : Happened;

    /// <summary>How a cycle whose turn came ended.</summary>
    private enum CycleEnd
    {
        /// <summary>It ran, and ended within the target.</summary>
        Ended,

        /// <summary>It ran, and ended later than the target.</summary>
        EndedLate,

        /// <summary>An error ended it.</summary>
        Failed,

        /// <summary>It was stopped because the next close was near.</summary>
        OutOfTime,

        /// <summary>Its agent no longer ran, or was another agent by then.</summary>
        NotRunning,

        /// <summary>Its agent was not reconciled with the exchange yet.</summary>
        NotReconciled,

        /// <summary>It was stopped with the application, before its turn or during it.</summary>
        Stopped,
    }

    /// <summary>What became of the cycles of the close.</summary>
    private struct Counts
    {
        public int Ended;
        public int EndedLate;
        public int Failed;
        public int OutOfTime;
        public int TooLate;
        public int WithoutCandle;
        public int NotReconciled;
        public int NotRunning;
        public int Stopped;

        public void Add(CycleEnd end)
        {
            switch (end)
            {
                case CycleEnd.Ended: Ended++; break;
                case CycleEnd.EndedLate: EndedLate++; break;
                case CycleEnd.Failed: Failed++; break;
                case CycleEnd.OutOfTime: OutOfTime++; break;
                case CycleEnd.NotRunning: NotRunning++; break;
                case CycleEnd.NotReconciled: NotReconciled++; break;
                case CycleEnd.Stopped: Stopped++; break;
            }
        }
    }
}
