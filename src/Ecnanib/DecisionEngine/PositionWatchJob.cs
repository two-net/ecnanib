using Ecnanib.Platform.BackgroundWork;
using Ecnanib.Platform.Settings;

namespace Ecnanib.DecisionEngine;

/// <summary>
/// Watches the positions of the Futures market, as the background work starts and then at the
/// time between reconciliations of the configuration file: the margin of every open position is
/// checked, and its user is told when it approaches liquidation (FR-BIN-F5), and the funding
/// that a position received and paid is read when it is due, and stored (FR-BIN-F4). See
/// "Funding" and "The margin of a position" in docs/orders.md.
/// </summary>
/// <remarks>
/// <para>
/// It is apart from what settles the orders, which every cycle asks first and which is to be
/// quick: this reads the exchange for every open position, and places nothing. It is apart from
/// what reconciles the agents as well, which runs at the same time between two runs and
/// corrects the records (FR-BIN-7): this corrects nothing. Each reads the position of an
/// account in a symbol for itself.
/// </para>
/// <para>
/// <b>A run reads the funding of so many positions and no more.</b> A funding time comes for
/// every position at once, and the read is the heaviest call that the service makes: all of
/// them in one run would take the part of the exchange's limit on requests that the orders and
/// the cycles need. The rest are read with the next runs. Where a run stopped, the next one goes
/// on, so that a position whose funding cannot be read keeps no other waiting.
/// </para>
/// <para>
/// The positions are watched a few at a time, each on its own: what cannot be read for one
/// keeps no other waiting.
/// </para>
/// </remarks>
internal sealed class PositionWatchJob(
    StoredOrders stored, PositionWatch watch, BinanceSettings binance, ILogger<PositionWatchJob> logger)
    : IBackgroundJob, IDueAtStart
{
    /// <summary>The name of the job, which is the key of its row of the <c>background_jobs</c> table.</summary>
    public const string JobName = "position-watch";

    /// <summary>The positions whose funding one run reads, at most.</summary>
    public const int FundingReadsARun = 20;

    // How many positions are watched at the same time.
    private const int AtOnce = 8;

    // The position whose funding the run before read last. The scheduler runs a job once at a time.
    private Guid _readLast;

    public string Name => JobName;

    public BackgroundJobSchedule Schedule { get; } = BackgroundJobSchedule.Every(TimeSpan.FromMinutes(binance.ReconcileMinutes));

    public async Task<bool> IsDueAtStartAsync(CancellationToken cancellationToken) => (await stored.ToWatchAsync(cancellationToken)).Count > 0;

    public async Task RunAsync(BackgroundJobRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);

        var positions = await stored.ToWatchAsync(cancellationToken);

        if (positions.Count == 0)
        {
            return;
        }

        var reading = FundingTurn(positions);

        logger.LogInformation(
            "{Positions} positions of the Futures market are watched, and the funding of {FundingReads} of them is read",
            positions.Count,
            reading.Count);

        await Parallel.ForEachAsync(
            positions,
            new ParallelOptions { MaxDegreeOfParallelism = AtOnce, CancellationToken = cancellationToken },
            async (position, stopping) =>
            {
                try
                {
                    await watch.WatchAsync(position, reading.Contains(position.Id), stopping);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    // Said once, with the agent. The others go on, and the next run tries again.
                    logger.LogError(
                        error, "The position of agent {AgentId} in {Symbol} could not be watched", position.AgentId, position.Symbol);
                }
            });
    }

    /// <summary>
    /// The positions whose funding this run reads: of those that are due, the ones after where
    /// the run before stopped, and then the ones from the start, up to as many as a run reads.
    /// </summary>
    private HashSet<Guid> FundingTurn(IReadOnlyList<Position> positions)
    {
        var due = positions.Where(watch.FundingIsDue).Select(position => position.Id).ToList();

        var turn = due.Where(id => id.CompareTo(_readLast) > 0)
            .Concat(due.Where(id => id.CompareTo(_readLast) <= 0))
            .Take(FundingReadsARun)
            .ToList();

        if (turn.Count > 0)
        {
            _readLast = turn[^1];
        }

        return [.. turn];
    }
}
