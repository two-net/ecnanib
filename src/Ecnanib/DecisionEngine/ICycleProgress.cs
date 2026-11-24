using System.Collections.Concurrent;
using Ecnanib.Platform.LiveUpdates;
using Ecnanib.Shared;

namespace Ecnanib.DecisionEngine;

/// <summary>
/// The cycles that run now, each with how far it has come: cycle progress updates in the UI
/// without a manual refresh (FR-NTF-2). A cycle is recorded once, when it ends, so what it does
/// until then is said here, and nowhere else. See "The progress of a cycle" in docs/cycles.md.
/// </summary>
/// <remarks>
/// <para>
/// It is asked for the user that a page hands over, which is the one of its connection: the
/// Decision Engine may not use Accounts. The cycle of an agent of another user is not found.
/// </para>
/// <para>
/// It answers from memory, at once, for this process: the cycles that this instance runs. A
/// cycle that a candle close started runs on the one instance that runs the background work.
/// Nothing of it is stored, and it holds nothing of what a model answered: the record of the
/// cycle has that, when the cycle has ended.
/// </para>
/// </remarks>
public interface ICycleProgress
{
    /// <summary>The cycles of an agent of a user that run now, in the order that they started in.</summary>
    /// <param name="userId">The user whose agent it is.</param>
    /// <param name="agentId">The agent.</param>
    IReadOnlyList<RunningCycle> OfAgent(Guid userId, Guid agentId);
}

/// <summary>A cycle that runs, with the steps that it has come to so far.</summary>
/// <param name="CycleId">The id of the cycle, which its record will have.</param>
/// <param name="Kind">What started it: the close of a candle, or its user, as a test.</param>
/// <param name="ClosedAt">The close of the candle that it is for.</param>
/// <param name="StartedAt">When it started.</param>
/// <param name="Steps">The steps that it has come to, in their order. The last of them is what it does now.</param>
public sealed record RunningCycle(
    Guid CycleId, CycleKind Kind, DateTimeOffset ClosedAt, DateTimeOffset StartedAt, IReadOnlyList<ReachedStep> Steps);

/// <summary>A step that a cycle came to.</summary>
/// <param name="Step">The step.</param>
/// <param name="At">When the cycle came to it.</param>
public sealed record ReachedStep(CycleStep Step, DateTimeOffset At);

/// <summary>
/// A step of a cycle (SRS 3.2). A cycle comes to those that its decision leads to: one that a
/// loss limit ends calls no model, a plan that opens no position is not reviewed, and a test
/// cycle settles nothing and places nothing.
/// </summary>
public enum CycleStep
{
    /// <summary>The orders of the agent are settled with the exchange, and its loss limits are evaluated.</summary>
    Settling,

    /// <summary>The data package is built.</summary>
    DataPackage,

    /// <summary>The Market Analyst and the Sentiment Analyst are called, at the same time.</summary>
    Analysts,

    /// <summary>The Strategist is called.</summary>
    Strategist,

    /// <summary>The Risk Manager is called, for a plan that opens a position.</summary>
    RiskManager,

    /// <summary>The Risk Guard checks what goes on of the plan.</summary>
    RiskGuard,

    /// <summary>The orders of what the Risk Guard accepted are placed.</summary>
    Orders,

    /// <summary>The cycle is recorded.</summary>
    Recording,
}

/// <summary>
/// Keeps the cycles that run, in memory, and says to the open pages of their users that one
/// moved on. See <see cref="ICycleProgress"/>.
/// </summary>
/// <remarks>
/// What a cycle says here is beside what it does: nothing of it fails a cycle, or holds one up.
/// </remarks>
internal sealed class RunningCycles(ILiveSignals signals, TimeProvider clock) : ICycleProgress
{
    private readonly ConcurrentDictionary<Guid, Running> _running = new();
    private readonly ILiveSignals _signals = signals;
    private readonly TimeProvider _clock = clock;

    public IReadOnlyList<RunningCycle> OfAgent(Guid userId, Guid agentId) =>
    [
        .. _running.Values
            .Where(running => running.UserId == userId && running.AgentId == agentId)
            .Select(running => running.Now())
            .OrderBy(cycle => cycle.StartedAt)
            .ThenBy(cycle => cycle.CycleId),
    ];

    /// <summary>
    /// Begins to keep a cycle that starts, until what it returns is disposed of, which is when
    /// the cycle has ended and is recorded.
    /// </summary>
    /// <param name="cycle">The cycle.</param>
    /// <param name="startedAt">When it started.</param>
    public Running Begin(CycleStart cycle, DateTimeOffset startedAt)
    {
        ArgumentNullException.ThrowIfNull(cycle);

        var running = new Running(this, cycle, startedAt);

        _running[cycle.CycleId] = running;
        _signals.Raise(running.UserId, running.AgentId, LiveSubject.CycleStep);

        return running;
    }

    /// <summary>A cycle that runs: what it tells of itself as it goes.</summary>
    internal sealed class Running(RunningCycles cycles, CycleStart cycle, DateTimeOffset startedAt) : IDisposable
    {
        private readonly Lock _gate = new();
        private readonly List<ReachedStep> _steps = [];

        public Guid UserId { get; } = cycle.Agent.UserId;

        public Guid AgentId { get; } = cycle.Agent.Id;

        /// <summary>Says that the cycle has come to a step, to the open pages of its user.</summary>
        public void At(CycleStep step)
        {
            lock (_gate)
            {
                _steps.Add(new ReachedStep(step, cycles._clock.GetUtcNow()));
            }

            cycles._signals.Raise(UserId, AgentId, LiveSubject.CycleStep);
        }

        public RunningCycle Now()
        {
            lock (_gate)
            {
                return new RunningCycle(
                    cycle.CycleId, cycle.IsTest ? CycleKind.Test : CycleKind.Scheduled, cycle.ClosedAt, startedAt, [.. _steps]);
            }
        }

        /// <summary>The cycle has ended: it runs no longer.</summary>
        public void Dispose()
        {
            if (cycles._running.TryRemove(new KeyValuePair<Guid, Running>(cycle.CycleId, this)))
            {
                cycles._signals.Raise(UserId, AgentId, LiveSubject.CycleStep);
            }
        }
    }
}
