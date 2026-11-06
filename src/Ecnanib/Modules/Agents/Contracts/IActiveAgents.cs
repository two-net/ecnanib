using Ecnanib.Kernel;

namespace Ecnanib.Modules.Agents.Contracts;

/// <summary>
/// The agents that run decision cycles, across every user, for the scheduler (FR-CYC-01). It isn't scoped to a
/// user, unlike <see cref="IAgents"/>, so only Agents and the scheduler may use it, which BannedApis enforces
/// (NFR-SEC-04). See Scheduler in docs/architecture.md.
/// </summary>
public interface IActiveAgents
{
    /// <summary>Every Active agent on <paramref name="timeframe"/>, in no particular order.</summary>
    Task<IReadOnlyList<ActiveAgent>> ListAsync(Timeframe timeframe, CancellationToken cancellationToken);
}

/// <summary>An Active agent, and the user it belongs to, which every later call about it needs.</summary>
public sealed record ActiveAgent(Guid Id, Guid UserId);
