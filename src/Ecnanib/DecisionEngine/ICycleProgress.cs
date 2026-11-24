using System.Collections.Concurrent;
using Ecnanib.Platform.LiveUpdates;
using Ecnanib.Shared;

namespace Ecnanib.DecisionEngine;

/// <summary>
/// The cycles that run now, each with how far it has come and what each role of its team does:
/// cycle progress updates in the UI without a manual refresh (FR-NTF-2), and the agent screen
/// shows the team at work (FR-ANI-1). A cycle is recorded once, when it ends, so what it does
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
/// <para>
/// A cycle that has ended is kept for a moment, <see cref="RunningCycle.KeptAfterEnd"/>, with
/// how each role ended, so that a page shows that before its team is at rest again: without
/// it, what the last role gave would be gone as it is said.
/// </para>
/// </remarks>
public interface ICycleProgress
{
    /// <summary>
    /// The cycles of an agent of a user that run now, and those that ended a moment ago, in the
    /// order that they started in.
    /// </summary>
    /// <param name="userId">The user whose agent it is.</param>
    /// <param name="agentId">The agent.</param>
    IReadOnlyList<RunningCycle> OfAgent(Guid userId, Guid agentId);
}

/// <summary>
/// A cycle that runs, with the steps that it has come to so far and what each role of its team
/// does, or one that ended a moment ago, with how each role ended.
/// </summary>
/// <param name="CycleId">The id of the cycle, which its record will have.</param>
/// <param name="Kind">What started it: the close of a candle, or its user, as a test.</param>
/// <param name="ClosedAt">The close of the candle that it is for.</param>
/// <param name="StartedAt">When it started.</param>
/// <param name="Steps">The steps that it has come to, in their order. The last of them is what it does now.</param>
/// <param name="Roles">The four roles of its team, in the order of <see cref="Role"/>, each with what it does now.</param>
/// <param name="EndedAt">When it ended, or null while it runs.</param>
public sealed record RunningCycle(
    Guid CycleId,
    CycleKind Kind,
    DateTimeOffset ClosedAt,
    DateTimeOffset StartedAt,
    IReadOnlyList<ReachedStep> Steps,
    IReadOnlyList<RoleProgress> Roles,
    DateTimeOffset? EndedAt = null)
{
    /// <summary>
    /// How long a cycle that has ended is kept, so that a page shows how each role ended
    /// before its team is at rest again.
    /// </summary>
    public static readonly TimeSpan KeptAfterEnd = TimeSpan.FromSeconds(5);

    /// <summary>Whether the cycle ended so long ago that it is kept no longer.</summary>
    /// <param name="now">The time that it is.</param>
    public bool IsGoneAt(DateTimeOffset now) => EndedAt is { } endedAt && endedAt + KeptAfterEnd <= now;
}

/// <summary>A step that a cycle came to.</summary>
/// <param name="Step">The step.</param>
/// <param name="At">When the cycle came to it.</param>
public sealed record ReachedStep(CycleStep Step, DateTimeOffset At);

/// <summary>A role of the team of a cycle, with what it does now.</summary>
/// <param name="Role">The role.</param>
/// <param name="State">What it does now.</param>
public sealed record RoleProgress(Role Role, RoleState State);

/// <summary>
/// What a role of a team does in a cycle (FR-ANI-1). A role moves from waiting to working when
/// the cycle comes to its step, and from there to done or to failed.
/// </summary>
public enum RoleState
{
    /// <summary>
    /// Its model is not called: the cycle has not come to it yet, or does not come to it, as
    /// to the Risk Manager of a plan that opens no position.
    /// </summary>
    Waiting,

    /// <summary>Its model is being called, also when it is called once more.</summary>
    Working,

    /// <summary>It gave a valid result.</summary>
    Done,

    /// <summary>
    /// It gave no valid result, also when it was called once more (FR-TEAM-9), or the cycle
    /// ended while it worked.
    /// </summary>
    Failed,
}

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
/// <para>
/// What a cycle says here is beside what it does: nothing of it fails a cycle, or holds one up.
/// </para>
/// <para>
/// A cycle that has ended is kept for <see cref="RunningCycle.KeptAfterEnd"/>, and no timer
/// takes it away: it is left out of what is answered once that time is over, by the clock, and
/// is let go of when a cycle begins. So nothing here runs by itself, and what follows a cycle
/// asks again when the time is over: see LiveFeed.
/// </para>
/// </remarks>
internal sealed class RunningCycles(ILiveSignals signals, TimeProvider clock) : ICycleProgress
{
    private static readonly Role[] Team = Enum.GetValues<Role>();

    private readonly ConcurrentDictionary<Guid, Running> _running = new();
    private readonly ILiveSignals _signals = signals;
    private readonly TimeProvider _clock = clock;

    public IReadOnlyList<RunningCycle> OfAgent(Guid userId, Guid agentId)
    {
        var now = _clock.GetUtcNow();

        return
        [
            .. _running.Values
                .Where(running => running.UserId == userId && running.AgentId == agentId)
                .Select(running => running.Now())
                .Where(cycle => !cycle.IsGoneAt(now))
                .OrderBy(cycle => cycle.StartedAt)
                .ThenBy(cycle => cycle.CycleId),
        ];
    }

    /// <summary>
    /// Begins to keep a cycle that starts. What it returns is disposed of when the cycle has
    /// ended and is recorded, and the cycle is kept for a moment longer, as one that has ended.
    /// </summary>
    /// <param name="cycle">The cycle.</param>
    /// <param name="startedAt">When it started.</param>
    public Running Begin(CycleStart cycle, DateTimeOffset startedAt)
    {
        ArgumentNullException.ThrowIfNull(cycle);

        var running = new Running(this, cycle, startedAt);
        var now = _clock.GetUtcNow();

        // The cycles that ended are let go of here, so that none stays for good: those whose
        // time is over, of whatever agent, and those of this agent, whose team works again.
        foreach (var kept in _running)
        {
            if (kept.Value.EndedAt is { } endedAt
                && (kept.Value.AgentId == running.AgentId || endedAt + RunningCycle.KeptAfterEnd <= now))
            {
                _running.TryRemove(kept);
            }
        }

        _running[cycle.CycleId] = running;
        _signals.Raise(running.UserId, running.AgentId, LiveSubject.CycleStep);

        return running;
    }

    /// <summary>The roles whose models a cycle calls as it comes to a step.</summary>
    private static Role[] CalledAt(CycleStep step) => step switch
    {
        CycleStep.Analysts => [Role.MarketAnalyst, Role.SentimentAnalyst],
        CycleStep.Strategist => [Role.Strategist],
        CycleStep.RiskManager => [Role.RiskManager],
        _ => [],
    };

    /// <summary>A cycle that runs: what it tells of itself as it goes.</summary>
    internal sealed class Running(RunningCycles cycles, CycleStart cycle, DateTimeOffset startedAt) : IDisposable
    {
        private readonly Lock _gate = new();
        private readonly List<ReachedStep> _steps = [];

        // What each role does, by the number of the role. Each waits as the cycle starts.
        private readonly RoleState[] _roles = new RoleState[Team.Length];
        private DateTimeOffset? _endedAt;

        public Guid UserId { get; } = cycle.Agent.UserId;

        public Guid AgentId { get; } = cycle.Agent.Id;

        /// <summary>When the cycle ended, or null while it runs.</summary>
        public DateTimeOffset? EndedAt
        {
            get
            {
                lock (_gate)
                {
                    return _endedAt;
                }
            }
        }

        /// <summary>
        /// Says that the cycle has come to a step, to the open pages of its user. The roles
        /// whose models the step calls work from here on.
        /// </summary>
        public void At(CycleStep step)
        {
            lock (_gate)
            {
                _steps.Add(new ReachedStep(step, cycles._clock.GetUtcNow()));

                foreach (var role in CalledAt(step))
                {
                    _roles[(int)role] = RoleState.Working;
                }
            }

            cycles._signals.Raise(UserId, AgentId, LiveSubject.CycleStep);
        }

        /// <summary>
        /// Says that a role is over, to the open pages of its user: it is done when it gave a
        /// valid result, and has failed when it gave none (FR-TEAM-9).
        /// </summary>
        /// <param name="role">The role.</param>
        /// <param name="valid">Whether it gave a valid result.</param>
        public void Over(Role role, bool valid)
        {
            lock (_gate)
            {
                _roles[(int)role] = valid ? RoleState.Done : RoleState.Failed;
            }

            cycles._signals.Raise(UserId, AgentId, LiveSubject.CycleStep);
        }

        public RunningCycle Now()
        {
            lock (_gate)
            {
                return new RunningCycle(
                    cycle.CycleId,
                    cycle.IsTest ? CycleKind.Test : CycleKind.Scheduled,
                    cycle.ClosedAt,
                    startedAt,
                    [.. _steps],
                    [.. Team.Select(role => new RoleProgress(role, _roles[(int)role]))],
                    _endedAt);
            }
        }

        /// <summary>
        /// The cycle has ended: it runs no longer, and is kept for a moment as one that has
        /// ended. A role that still worked has failed: the cycle was stopped, or ended with an
        /// error, before the role gave a result.
        /// </summary>
        public void Dispose()
        {
            lock (_gate)
            {
                if (_endedAt is not null)
                {
                    return;
                }

                _endedAt = cycles._clock.GetUtcNow();

                for (var role = 0; role < _roles.Length; role++)
                {
                    if (_roles[role] == RoleState.Working)
                    {
                        _roles[role] = RoleState.Failed;
                    }
                }
            }

            cycles._signals.Raise(UserId, AgentId, LiveSubject.CycleStep);
        }
    }
}
