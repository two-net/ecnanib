using Ecnanib.Platform.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Modules.Execution;

/// <summary>
/// Keeps the trade journal up to date every minute (FR-EXE-07). Every keeper pass books its agent's trades, so this is
/// the recovery path: it books an entry whose booking failed, or whose agent has no more passes, such as after an exit
/// sold everything, and books every existing entry the first time it runs. Then it settles each closed trade from
/// Binance, a few at a time. Booking holds the agent's lock, skipping an agent whose lock is held because its holder
/// books, and settling doesn't. See The trade journal in docs/architecture.md.
/// </summary>
internal sealed class TradeJournalSweep(
    IDbContextFactory<EcnanibDbContext> contextFactory,
    AgentLocks locks,
    TradeBook book,
    TradeSettlement settlement,
    TimeProvider time,
    ILogger<TradeJournalSweep> logger) : IRecurringJob
{
    public const string JobName = "trade-journal";

    public string Name => JobName;

    public JobSchedule Schedule { get; } = JobSchedule.Every(TimeSpan.FromMinutes(1));

    /// <summary>A few reads per trade, a few trades per agent, a few agents at a time, with room for a backlog.</summary>
    public TimeSpan Timeout { get; } = TimeSpan.FromMinutes(5);

    public async Task RunAsync(JobContext context, CancellationToken cancellationToken)
    {
        var agents = await ListAsync(cancellationToken);
        using var gate = new SemaphoreSlim(AgentReconciliation.Concurrency);
        var runs = new List<Task<int?>>();

        foreach (var agent in agents)
        {
            if (context.Stopping.IsCancellationRequested)
            {
                break;
            }

            try
            {
                await gate.WaitAsync(context.Stopping);
            }
            catch (OperationCanceledException) when (context.Stopping.IsCancellationRequested)
            {
                break;
            }

            runs.Add(OneAsync(agent, gate, cancellationToken));
        }

        var outcomes = await Task.WhenAll(runs);
        var failed = outcomes.Count(settled => settled is null);

        if (failed > 0)
        {
            logger.LogWarning("{Failed} of {Count} agents' trades couldn't be booked or settled, so they're tried again next time", failed, agents.Count);
        }

        context.Details = $"Agents: {agents.Count}, settled: {outcomes.Sum(settled => settled ?? 0)}, failed: {failed}";
        context.Stopping.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// Agents with an entry that has a protected part and no trade, an open trade whose entry has ended and whose parts
    /// all have, or a closed trade that's due to be settled. It isn't scoped to a user, because the sweep looks after
    /// everyone's.
    /// </summary>
    private async Task<IReadOnlyList<AgentKey>> ListAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var unbooked = context.Set<OrderRow>()
            .Where(entry => entry.Leg == OrderLeg.Entry
                && context.Set<ProtectionRow>().Any(part => part.EntryId == entry.Id)
                && !context.Set<TradeRow>().Any(trade => trade.EntryId == entry.Id))
            .Select(entry => new { entry.UserId, entry.AgentId });

        // Written out, so the statuses are constants that the partial indexes ix_trades_open and ix_trades_unsettled match.
        var closable = context.Set<TradeRow>()
            .Where(trade => trade.Status == TradeStatus.Open
                && context.Set<OrderRow>().Any(entry => entry.Id == trade.EntryId && entry.Resolved)
                && !context.Set<ProtectionRow>().Any(part => part.EntryId == trade.EntryId
                    && (part.Status == ProtectionStatus.Pending || part.Status == ProtectionStatus.Resting || part.Status == ProtectionStatus.Closing)))
            .Select(trade => new { trade.UserId, trade.AgentId });

        var unsettled = context.Set<TradeRow>()
            .Where(trade => trade.Status == TradeStatus.Closed && trade.SettledAt == null && (trade.SettleAfter == null || trade.SettleAfter <= now))
            .Select(trade => new { trade.UserId, trade.AgentId });

        var agents = await unbooked.Union(closable).Union(unsettled).Distinct().ToListAsync(cancellationToken);

        return [.. agents.Select(agent => new AgentKey(agent.UserId, agent.AgentId))];
    }

    /// <summary>One agent, on its own: one that fails is logged and the rest go on (NFR-REL-05). Null when it failed.</summary>
    private async Task<int?> OneAsync(AgentKey agent, SemaphoreSlim gate, CancellationToken cancellationToken)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object?> { [LogProperties.AgentId] = agent.AgentId });

        try
        {
            using (var held = locks.TryHold(agent.AgentId))
            {
                if (held is not null)
                {
                    await book.BookAsync(agent.UserId, agent.AgentId, pair: null, cancellationToken);
                }
            }

            return await settlement.SettleDueAsync(agent.UserId, agent.AgentId, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Booking or settling the agent's trades failed");
            return null;
        }
        finally
        {
            gate.Release();
        }
    }
}
