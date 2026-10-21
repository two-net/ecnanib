namespace Ecnanib.Agents.Abstractions;

/// <summary>
/// The agent's standing against its daily loss limit (FR-EXE-09). <see cref="LossToday"/> is the realized loss of the
/// current UTC day as a positive amount (0 when the day is in profit), <see cref="LimitAmount"/> the limit in money at
/// the day's starting equity, <see cref="Paused"/> whether this check paused the agent.
/// </summary>
public sealed record DailyLossStatus(bool Reached, decimal LossToday, decimal LimitAmount, decimal LimitPercent, bool Paused);

/// <summary>
/// Enforces the daily loss limit (FR-EXE-09): realized P&amp;L of the current UTC day against the limit, and the
/// auto-pause until the next UTC day when it is reached. Gate-free: safe from inside an evaluation and from the fill
/// loops, because the pause is one atomic conditional update. Called after every exit fill and before every entry.
/// </summary>
public interface IDailyLossLimit
{
    Task<DailyLossStatus> CheckAsync(Guid agentId, CancellationToken cancellationToken);
}
