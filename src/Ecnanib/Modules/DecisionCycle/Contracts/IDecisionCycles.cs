using Ecnanib.Kernel;
using Ecnanib.Platform.Contracts;

namespace Ecnanib.Modules.DecisionCycle.Contracts;

/// <summary>
/// Runs one agent's decision cycle (FR-CYC-02). Only the scheduler starts cycles, at a candle close, so that each
/// one goes through the concurrency limit, the fair queue and the one-cycle-per-agent rule (FR-CYC-08). BannedApis
/// enforces that. See Scheduler in docs/architecture.md.
/// </summary>
public interface IDecisionCycles
{
    /// <summary>
    /// One cycle. It runs inside its own job run, so the signals are a job's: pass <paramref name="cancellationToken"/>
    /// anywhere, including to exchange calls, and check <see cref="JobContext.Stopping"/> between steps. Returning
    /// means the cycle finished, so one that stops early must throw.
    /// </summary>
    Task RunAsync(CycleRequest request, JobContext context, CancellationToken cancellationToken);
}

/// <summary>What the scheduler asks of a cycle.</summary>
/// <param name="Timeframe">The agent's timeframe when the cycle was scheduled.</param>
/// <param name="CandleClose">
/// When the cycle's candle closed (FR-CYC-01), without the delay. It's what <c>IMarketData</c> takes.
/// </param>
/// <param name="Deadline">After this, the cycle executes no new entry (FR-CYC-06). See <see cref="AllowsNewEntry"/>.</param>
/// <param name="CatchUp">
/// The cycle runs late, because the application wasn't running at the close (FR-CYC-04). Its deadline counts from
/// when it was dispatched rather than from the close.
/// </param>
public sealed record CycleRequest(
    Guid UserId,
    Guid AgentId,
    Timeframe Timeframe,
    DateTimeOffset CandleClose,
    DateTimeOffset Deadline,
    bool CatchUp)
{
    /// <summary>
    /// Whether a new entry may still be executed (FR-CYC-06). Ask right before executing one, with the time then.
    /// Managing a position, such as closing it or tightening its stop, isn't an entry, so it doesn't ask.
    /// </summary>
    public bool AllowsNewEntry(DateTimeOffset now) => now < Deadline;
}
