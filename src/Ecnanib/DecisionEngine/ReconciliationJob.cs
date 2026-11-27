using Ecnanib.Platform.BackgroundWork;
using Ecnanib.Platform.Settings;

namespace Ecnanib.DecisionEngine;

/// <summary>
/// Reconciles the agents with the exchange, as the background work starts and then at the time
/// between reconciliations of the configuration file, which the specification gives as 5 minutes
/// (FR-BIN-7, NFR-REL-3): every agent that holds an open position, or an order that is not over.
/// The exchange is the source of truth, so what its user did at the exchange by hand is in the
/// records after a run (FR-BIN-8). See "Reconciling" in docs/orders.md.
/// </summary>
/// <remarks>
/// <para>
/// The agents are reconciled a few at a time, each on its own: one that cannot be reconciled,
/// because the key of its user is gone or the exchange does not answer for it, keeps no other
/// waiting. Those that were not reconciled are tried once more before the run ends: a call that
/// the limiter held back, or an account that the exchange had not brought up to date, is often
/// answered a moment later, and an agent that is not reconciled after a start gets no cycle.
/// </para>
/// <para>
/// <b>It runs first as the background work starts,</b> once what settles the orders has, and
/// before any other job: on startup the application reconciles every agent that holds
/// something with the exchange, and then resumes scheduling (NFR-REL-3). The scheduler says
/// when this instance takes up the work, and what was reconciled before does not count from
/// then on. See "Starting" in docs/reliability.md.
/// </para>
/// <para>
/// It is apart from what settles the orders, which runs every minute for the agents that have
/// something to settle, and from what watches the margin and the funding of the positions of
/// the Futures market: this asks about every order that is not over, and compares what the
/// account holds.
/// </para>
/// </remarks>
internal sealed class ReconciliationJob(
    AccountReconciliation reconciliation, StoredOrders stored, BinanceSettings binance, TimeProvider clock, ILogger<ReconciliationJob> logger)
    : IBackgroundJob, IRunsFirst
{
    /// <summary>The name of the job, which is the key of its row of the <c>background_jobs</c> table.</summary>
    public const string JobName = "reconciliation";

    /// <summary>How long a run waits before it tries the agents once more that it could not reconcile.</summary>
    public static readonly TimeSpan Again = TimeSpan.FromSeconds(15);

    // How many agents are reconciled at the same time.
    private const int AtOnce = 8;

    public string Name => JobName;

    public BackgroundJobSchedule Schedule { get; } = BackgroundJobSchedule.Every(TimeSpan.FromMinutes(binance.ReconcileMinutes));

    /// <summary>
    /// This instance takes up the background work: at the start of the application, and when
    /// the lock was lost and taken back. What it reconciled before does not count any more:
    /// another instance may have placed orders meanwhile.
    /// </summary>
    public void TakenUp() => reconciliation.TakenUp();

    public async Task RunAsync(BackgroundJobRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);

        var agents = await stored.ToReconcileAsync(cancellationToken);

        if (agents.Count == 0)
        {
            return;
        }

        var notYet = await ReconcileAsync(agents, cancellationToken);

        if (notYet.Count > 0)
        {
            await Task.Delay(Again, clock, cancellationToken);

            notYet = await ReconcileAsync(notYet, cancellationToken);
        }

        logger.LogInformation(
            "{Agents} agents were reconciled with the exchange, and {NotReconciled} were not: those are tried again with the next run",
            agents.Count - notYet.Count,
            notYet.Count);
    }

    /// <summary>Reconciles agents, a few at a time, and gives those that are not reconciled, in the order of their ids.</summary>
    private async Task<IReadOnlyList<Guid>> ReconcileAsync(IReadOnlyList<Guid> agents, CancellationToken cancellationToken)
    {
        var notYet = new List<Guid>();

        await Parallel.ForEachAsync(
            agents,
            new ParallelOptions { MaxDegreeOfParallelism = AtOnce, CancellationToken = cancellationToken },
            async (agentId, stopping) =>
            {
                var reconciled = false;

                try
                {
                    reconciled = await reconciliation.ReconcileAsync(agentId, stopping);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    // Said once, with the agent. The others go on, and the next run tries again.
                    logger.LogError(error, "Agent {AgentId} could not be reconciled with the exchange", agentId);
                }

                if (!reconciled)
                {
                    lock (notYet)
                    {
                        notYet.Add(agentId);
                    }
                }
            });

        return [.. notYet.Order()];
    }
}
