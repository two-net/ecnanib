using Ecnanib.Modules.Notifications.Contracts;
using Ecnanib.Platform.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Modules.Execution;

/// <summary>
/// Execution's rows. A row is inserted before its order is sent, on the caller's token. Everything written once an
/// order may have been sent uses a token of its own, because the end of the shutdown drain must not cancel the record
/// of a trade (NFR-REL-04). See Order execution in docs/architecture.md.
/// </summary>
internal sealed class OrderStore(IDbContextFactory<EcnanibDbContext> contextFactory, INotifications notifications)
{
    /// <summary>How long a write after a send may take, whatever the caller's tokens say.</summary>
    public static readonly TimeSpan WriteLimit = TimeSpan.FromSeconds(5);

    /// <summary>Statuses of an order that may still rest on Binance, or whose outcome isn't known.</summary>
    public static readonly OrderStatus[] LiveStatuses =
        [OrderStatus.Sending, OrderStatus.Unknown, OrderStatus.Open, OrderStatus.PartiallyFilled, OrderStatus.Triggered];

    /// <summary>An entry that hasn't ended: being sent, unknown, or resting unfilled or partly filled.</summary>
    public static readonly OrderStatus[] PendingEntryStatuses =
        [OrderStatus.Sending, OrderStatus.Unknown, OrderStatus.Open, OrderStatus.PartiallyFilled];

    /// <summary>Protections that still need work.</summary>
    public static readonly ProtectionStatus[] UnsettledProtections = [ProtectionStatus.Pending, ProtectionStatus.Closing];

    /// <summary>Protections whose part of the position may still be held.</summary>
    public static readonly ProtectionStatus[] HeldProtections = [ProtectionStatus.Pending, ProtectionStatus.Resting, ProtectionStatus.Closing];

    /// <summary>Inserts rows before their orders are sent, on the caller's token: nothing has been sent yet.</summary>
    public async Task InsertAsync(IEnumerable<OrderRow> rows, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        context.AddRange(rows);
        await context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Inserts a protection's next attempt with its count, in one transaction, so a crash can't leave an attempt's
    /// client order ID to be used twice.
    /// </summary>
    public async Task InsertAsync(IEnumerable<OrderRow> rows, ProtectionRow protection, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        context.AddRange(rows);
        context.Update(protection);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task InsertAsync(ProtectionRow protection, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        context.Add(protection);
        await context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Saves what's changed, with a notification about it in the same transaction (FR-NTF-01). It has its own time
    /// limit, so a record of what Binance did is never cut off by shutdown.
    /// </summary>
    public async Task SaveAsync(IEnumerable<OrderRow> orders, IEnumerable<ProtectionRow> protections, Notice? notice = null)
    {
        using var limit = new CancellationTokenSource(WriteLimit);
        await using var context = await contextFactory.CreateDbContextAsync(limit.Token);

        context.UpdateRange(orders);
        context.UpdateRange(protections);

        if (notice is not null)
        {
            notifications.Add(context, notice);
        }

        await context.SaveChangesAsync(limit.Token).WaitAsync(limit.Token);
    }

    public Task SaveAsync(OrderRow order, Notice? notice = null) => SaveAsync([order], [], notice);

    public Task SaveAsync(ProtectionRow protection, Notice? notice = null) => SaveAsync([], [protection], notice);

    /// <summary>
    /// What an agent has that may need work: its protections that may still hold a position, the entries they belong
    /// to, its entries that haven't been resolved, and every order of those entries.
    /// </summary>
    public async Task<AgentOrders> LoadAsync(Guid userId, Guid agentId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var protections = await context.Set<ProtectionRow>()
            .AsNoTracking()
            .Where(row => row.UserId == userId && row.AgentId == agentId && HeldProtections.Contains(row.Status))
            .ToListAsync(cancellationToken);

        // An order that may still rest, or whose outcome isn't known, is loaded with its entry even when the protection
        // it belonged to has ended, so an orphan is cancelled.
        var liveLegEntries = await context.Set<OrderRow>()
            .AsNoTracking()
            .Where(row => row.UserId == userId && row.AgentId == agentId && row.EntryId != null && LiveStatuses.Contains(row.Status))
            .Select(row => row.EntryId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken);

        var protectedEntries = protections.Select(row => row.EntryId).Concat(liveLegEntries).Distinct().ToList();

        var entries = await context.Set<OrderRow>()
            .AsNoTracking()
            .Where(row => row.UserId == userId && row.AgentId == agentId && row.Leg == OrderLeg.Entry
                && (!row.Resolved || protectedEntries.Contains(row.Id)))
            .OrderBy(row => row.CreatedAt)
            .ToListAsync(cancellationToken);

        var entryIds = entries.Select(row => row.Id).ToList();

        var legs = await context.Set<OrderRow>()
            .AsNoTracking()
            .Where(row => row.UserId == userId && row.AgentId == agentId && row.EntryId != null && entryIds.Contains(row.EntryId.Value))
            .OrderBy(row => row.CreatedAt)
            .ToListAsync(cancellationToken);

        // A protection's siblings that ended are loaded too, because the next one's sequence follows them.
        var allProtections = await context.Set<ProtectionRow>()
            .AsNoTracking()
            .Where(row => row.UserId == userId && row.AgentId == agentId && entryIds.Contains(row.EntryId))
            .OrderBy(row => row.Sequence)
            .ToListAsync(cancellationToken);

        return new AgentOrders(entries, legs, allProtections);
    }

    /// <summary>Whether the agent has an entry that hasn't ended, for the Risk Engine's PendingEntry rule.</summary>
    public async Task<bool> HasPendingEntryAsync(Guid userId, Guid agentId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        return await context.Set<OrderRow>().AnyAsync(
            row => row.UserId == userId && row.AgentId == agentId && row.Leg == OrderLeg.Entry && PendingEntryStatuses.Contains(row.Status),
            cancellationToken);
    }

    /// <summary>
    /// Whether the agent has an order that may rest on Binance, or a part of a position that may still be held,
    /// from the database alone (FR-AGT-03).
    /// </summary>
    public async Task<bool> HasOpenPositionsOrOrdersAsync(Guid userId, Guid agentId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        return await context.Set<OrderRow>().AnyAsync(
                row => row.UserId == userId && row.AgentId == agentId && (LiveStatuses.Contains(row.Status) || (row.Leg == OrderLeg.Entry && !row.Resolved)),
                cancellationToken)
            || await context.Set<ProtectionRow>().AnyAsync(
                row => row.UserId == userId && row.AgentId == agentId && HeldProtections.Contains(row.Status),
                cancellationToken);
    }

    /// <summary>
    /// Every agent with work that nobody may be doing: an entry not yet resolved, an order whose outcome isn't
    /// known, or a protection not yet settled. It isn't scoped to a user, because the sweep looks after everyone's.
    /// </summary>
    public async Task<IReadOnlyList<AgentKey>> ListAgentsWithWorkAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var fromOrders = context.Set<OrderRow>()
            .Where(row => (row.Leg == OrderLeg.Entry && !row.Resolved) || row.Status == OrderStatus.Sending || row.Status == OrderStatus.Unknown)
            .Select(row => new { row.UserId, row.AgentId });

        var fromProtections = context.Set<ProtectionRow>()
            .Where(row => UnsettledProtections.Contains(row.Status))
            .Select(row => new { row.UserId, row.AgentId });

        var agents = await fromOrders.Union(fromProtections).Distinct().ToListAsync(cancellationToken);

        return [.. agents.Select(agent => new AgentKey(agent.UserId, agent.AgentId))];
    }

    /// <summary>The newest entry of the agent's, which tells the watch which key, market and pair its orders use.</summary>
    public async Task<OrderRow?> FindLatestEntryAsync(Guid userId, Guid agentId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        return await context.Set<OrderRow>()
            .AsNoTracking()
            .Where(row => row.UserId == userId && row.AgentId == agentId && row.Leg == OrderLeg.Entry)
            .OrderByDescending(row => row.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }
}

internal sealed record AgentKey(Guid UserId, Guid AgentId);

/// <summary>An agent's orders that may need work, as loaded. The rows are changed in place and saved by the keeper.</summary>
internal sealed class AgentOrders(List<OrderRow> entries, List<OrderRow> legs, List<ProtectionRow> protections)
{
    public List<OrderRow> Entries { get; } = entries;

    /// <summary>Stops, targets and closes of <see cref="Entries"/>.</summary>
    public List<OrderRow> Legs { get; } = legs;

    public List<ProtectionRow> Protections { get; } = protections;

    public IEnumerable<OrderRow> LegsOf(ProtectionRow protection) => Legs.Where(leg => leg.ProtectionId == protection.Id);

    public IEnumerable<ProtectionRow> ProtectionsOf(OrderRow entry) => Protections.Where(protection => protection.EntryId == entry.Id);

    public OrderRow EntryOf(ProtectionRow protection) => Entries.First(entry => entry.Id == protection.EntryId);
}
