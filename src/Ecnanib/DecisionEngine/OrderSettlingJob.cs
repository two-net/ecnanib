using Ecnanib.Platform.BackgroundWork;
using Ecnanib.Platform.Settings;

namespace Ecnanib.DecisionEngine;

/// <summary>
/// Settles the orders of agents with the exchange, every minute: an order of unknown outcome is
/// asked about, an open position without a stop-loss is protected or closed at market, a
/// stop-loss that rests is asked about again, with its take-profit, and an entry at a price that
/// rests is asked about, so that what it bought is protected, and taken off when it is not to
/// rest any longer (FR-BIN-6, FR-RSK-7, FR-BIN-S2). See "Settling" in docs/orders.md.
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
/// between reconciliations of the configuration file, and an entry that rests with every run.
/// </para>
/// <para>
/// <b>It runs first as the background work starts,</b> before what reconciles the agents, and
/// before any other job: a restart settles first (NFR-REL-3). An order that was under way when
/// the application stopped is asked about before anything is scheduled.
/// </para>
/// <para>
/// <b>It settles once more as the application stops,</b> when every cycle has ended: what a
/// stopped cycle left is finished then, and not at the next start, and every entry that rests
/// is taken off the exchange, so that no entry is left without protection while the service is
/// down (NFR-REL-5). See "Stopping" in docs/reliability.md.
/// </para>
/// <para>
/// The agents are settled a few at a time, each on its own: what cannot be settled for one,
/// because its key is refused, keeps no other waiting.
/// </para>
/// <para>
/// Every run first values the fees of the executions that were stored without a value, because
/// what values a fee could not be read then (FR-BIN-10): see <see cref="FeeValuation"/>. There
/// are seldom any, and it asks about no order for them.
/// </para>
/// </remarks>
internal sealed class OrderSettlingJob(
    StoredOrders stored,
    IPlanOrders orders,
    FeeValuation fees,
    BinanceSettings binance,
    TimeProvider clock,
    ILogger<OrderSettlingJob> logger)
    : IBackgroundJob, IRunsFirst, IRunsLast
{
    /// <summary>The name of the job, which is the key of its row of the <c>background_jobs</c> table.</summary>
    public const string JobName = "order-settling";

    /// <summary>The time between two runs.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    // How many agents are settled at the same time.
    private const int AtOnce = 8;

    public string Name => JobName;

    public BackgroundJobSchedule Schedule { get; } = BackgroundJobSchedule.Every(Interval);

    public void TakenUp()
    {
        // Nothing is remembered from run to run: what is to settle is read from the rows.
    }

    public async Task RunAsync(BackgroundJobRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);

        try
        {
            await fees.ValueStoredAsync(cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // Said once. The orders are settled all the same, and the next run values again.
            logger.LogError(error, "The fees of the executions that were stored without a value could not be valued");
        }

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

    public async Task RunLastAsync(CancellationToken cancellationToken)
    {
        // Every agent with something to settle, and so every agent with an entry that rests.
        var agents = await ToSettleAsync(cancellationToken);

        if (agents.Count == 0)
        {
            return;
        }

        logger.LogInformation(
            "The orders of {Agents} agents are settled with the exchange as the application stops, and their entries that rest are taken off",
            agents.Count);

        var left = new List<Guid>();

        // Not canceled with the time: an agent that is begun is brought to its end, and one
        // whose turn comes after the time is up is asked nothing, and counts as left.
        await Parallel.ForEachAsync(
            agents,
            new ParallelOptions { MaxDegreeOfParallelism = AtOnce },
            async (agentId, _) =>
            {
                var settled = false;

                try
                {
                    settled = await orders.SettleBeforeStopAsync(agentId, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // The time is up.
                }
                catch (Exception error)
                {
                    // Said once, with the agent. The others go on, and the next start settles first.
                    logger.LogError(
                        error, "The orders of agent {AgentId} could not be settled with the exchange as the application stops", agentId);
                }

                if (!settled)
                {
                    lock (left)
                    {
                        left.Add(agentId);
                    }
                }
            });

        if (left.Count == 0)
        {
            return;
        }

        foreach (var agentId in left.Order())
        {
            logger.LogWarning(
                "Agent {AgentId} is not settled with the exchange as the application stops: it has an order of unknown outcome, an entry that rests, or an open position without a stop-loss that rests",
                agentId);
        }

        logger.LogWarning(
            "{NotSettled} of {Agents} agents are not settled with the exchange as the application stops. The next start settles them first",
            left.Count,
            agents.Count);
    }

    private Task<IReadOnlyList<Guid>> ToSettleAsync(CancellationToken cancellationToken) =>
        stored.ToSettleAsync(clock.GetUtcNow() - TimeSpan.FromMinutes(binance.ReconcileMinutes), cancellationToken);
}
