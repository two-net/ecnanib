using Ecnanib.Platform.BackgroundWork;
using Ecnanib.Platform.Settings;

namespace Ecnanib.DecisionEngine;

/// <summary>
/// Settles the orders of agents with the exchange, every minute: an order of unknown outcome is
/// asked about, an open position without a stop-loss is protected or closed at market, and a
/// stop-loss that rests is asked about again (FR-BIN-6, FR-RSK-7). See "Settling" in
/// docs/orders.md.
/// </summary>
/// <remarks>
/// <para>
/// It is what finishes what a cycle could not: a call that ended without an answer, an order
/// that was under way when the application stopped, and a position whose stop-loss could not be
/// placed. Such an agent gets no cycle until it is settled, so this does not wait for one.
/// </para>
/// <para>
/// It runs for the agents that have something to settle, and for no other: most runs find none.
/// A stop-loss that rests is asked about when it was last asked about longer ago than the time
/// between reconciliations of the configuration file. It is due as the background work starts
/// when there is anything to settle, so a restart settles first (NFR-REL-3).
/// </para>
/// <para>
/// The agents are settled a few at a time, each on its own: what cannot be settled for one,
/// because its key is refused, keeps no other waiting.
/// </para>
/// </remarks>
internal sealed class OrderSettlingJob(
    StoredOrders stored, IPlanOrders orders, BinanceSettings binance, TimeProvider clock, ILogger<OrderSettlingJob> logger)
    : IBackgroundJob, IDueAtStart
{
    /// <summary>The name of the job, which is the key of its row of the <c>background_jobs</c> table.</summary>
    public const string JobName = "order-settling";

    /// <summary>The time between two runs.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    // How many agents are settled at the same time.
    private const int AtOnce = 8;

    public string Name => JobName;

    public BackgroundJobSchedule Schedule { get; } = BackgroundJobSchedule.Every(Interval);

    public async Task<bool> IsDueAtStartAsync(CancellationToken cancellationToken) => (await ToSettleAsync(cancellationToken)).Count > 0;

    public async Task RunAsync(BackgroundJobRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);

        var agents = await ToSettleAsync(cancellationToken);

        if (agents.Count == 0)
        {
            return;
        }

        logger.LogInformation("The orders of {Agents} agents are settled with the exchange", agents.Count);

        await Parallel.ForEachAsync(
            agents,
            new ParallelOptions { MaxDegreeOfParallelism = AtOnce, CancellationToken = cancellationToken },
            async (agentId, stopping) =>
            {
                try
                {
                    await orders.SettleAsync(agentId, stopping);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    // Said once, with the agent. The others go on, and the next run tries again.
                    logger.LogError(error, "The orders of agent {AgentId} could not be settled with the exchange", agentId);
                }
            });
    }

    private Task<IReadOnlyList<Guid>> ToSettleAsync(CancellationToken cancellationToken) =>
        stored.ToSettleAsync(clock.GetUtcNow() - TimeSpan.FromMinutes(binance.ReconcileMinutes), cancellationToken);
}
