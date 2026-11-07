using Ecnanib.Kernel;
using Ecnanib.Modules.Execution.Contracts;
using Ecnanib.Platform.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Modules.Execution;

/// <summary>
/// Keeps the trade journal up to date every minute (FR-EXE-07). Every keeper pass books its agent's trades, so this is
/// the recovery path: it books an entry whose booking failed, or whose agent has no more passes, such as after an exit
/// sold everything, and books every existing entry the first time it runs. Then it settles each closed trade from
/// Binance, a few at a time, and reads what each open trade has cost so far when that's due (FR-AGT-11). Booking holds
/// the agent's lock, skipping an agent whose lock is held because its holder books, and settling and reading don't.
/// See The trade journal in docs/architecture.md.
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
        var runs = new List<Task<(int Settled, int Accrued)?>>();

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
        var failed = outcomes.Count(outcome => outcome is null);

        if (failed > 0)
        {
            logger.LogWarning("{Failed} of {Count} agents' trades couldn't be booked or settled, so they're tried again next time", failed, agents.Count);
        }

        context.Details =
            $"Agents: {agents.Count}, settled: {outcomes.Sum(outcome => outcome?.Settled ?? 0)}, open trades read: {outcomes.Sum(outcome => outcome?.Accrued ?? 0)}, failed: {failed}";
        context.Stopping.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// Agents with an entry that has a protected part and no trade, an open trade whose entry has ended and whose parts
    /// all have, a closed trade that's due to be settled, or an open trade whose entry's fees haven't been read, or whose
    /// funding hasn't been read since the last boundary. It isn't scoped to a user, because the sweep looks after
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

        // Each agent's own spread is worked out when it's read, so an agent is listed from the boundary on.
        var boundary = TradeMath.Floor(now, TradeSettlement.FundingStep);
        var accruable = context.Set<TradeRow>()
            .Where(trade => trade.Status == TradeStatus.Open
                && ((trade.EntryFeesReadAt == null && context.Set<OrderRow>().Any(entry => entry.Id == trade.EntryId && entry.Resolved))
                    || (trade.Market == Market.Futures && (trade.FundingReadTo ?? trade.OpenedAt) < boundary)))
            .Select(trade => new { trade.UserId, trade.AgentId });

        var agents = await unbooked.Union(closable).Union(unsettled).Union(accruable).Distinct().ToListAsync(cancellationToken);

        return [.. agents.Select(agent => new AgentKey(agent.UserId, agent.AgentId))];
    }

    /// <summary>One agent, on its own: one that fails is logged and the rest go on (NFR-REL-05). Null when it failed.</summary>
    private async Task<(int Settled, int Accrued)?> OneAsync(AgentKey agent, SemaphoreSlim gate, CancellationToken cancellationToken)
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

            var settled = await settlement.SettleDueAsync(agent.UserId, agent.AgentId, cancellationToken);
            var accrued = await settlement.AccrueOpenAsync(agent.UserId, agent.AgentId, cancellationToken);

            return (settled, accrued);
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
