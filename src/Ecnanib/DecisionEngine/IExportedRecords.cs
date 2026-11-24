using System.Runtime.CompilerServices;
using Ecnanib.Agents;
using Ecnanib.Catalog;
using Ecnanib.Platform.Persistence;
using Ecnanib.Shared;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.DecisionEngine;

/// <summary>
/// The trades and the decisions of a user, every one of them, for the user to take out of the
/// service as CSV (FR-RPT-4). See docs/export.md.
/// </summary>
/// <remarks>
/// <para>
/// <b>A trade is a closed position, and a decision is a recorded cycle,</b> each as the screen of
/// its agent says it: what makes the trade list and the timeline makes these, so an export says
/// nothing else of a trade or of a cycle than a page does (FR-RPT-2).
/// </para>
/// <para>
/// <b>They are of every agent of the user, in either mode, and each says its mode</b>: the one
/// that its agent was in then (FR-BIN-11). Nothing is added up, so nothing of Demo is combined
/// with anything of Live. A test cycle is among the decisions, and says that it is one
/// (FR-CYC-12).
/// </para>
/// <para>
/// <b>They are read a part at a time, latest first,</b> and handed over as they are read: a user
/// may have more cycles than are held at once. Each part is read at one moment, in a snapshot of
/// its own, so a row and what hangs on it agree, and nothing holds the database while a
/// download is written. What is recorded while an export runs may not be in it.
/// </para>
/// <para>
/// It is asked for the user that an endpoint hands over, which is the one who is signed in: the
/// Decision Engine may not use Accounts. Everything is asked for by owner (NFR-SEC-3), and no
/// row that it reads holds a secret (FR-KEY-8). It only reads.
/// </para>
/// </remarks>
public interface IExportedRecords
{
    /// <summary>How many trades, and how many decisions, are read at a time.</summary>
    const int ChunkSize = 500;

    /// <summary>The closed positions of every agent of a user, latest close first.</summary>
    /// <param name="userId">The user who is signed in.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    IAsyncEnumerable<ExportedTrade> TradesOfUserAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>The recorded cycles of every agent of a user, latest close first.</summary>
    /// <param name="userId">The user who is signed in.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    IAsyncEnumerable<ExportedDecision> DecisionsOfUserAsync(Guid userId, CancellationToken cancellationToken);
}

/// <summary>One closed position of a user, as an export holds it.</summary>
/// <param name="AgentId">The agent whose position it was.</param>
/// <param name="AgentName">The name that the agent has now.</param>
/// <param name="Timeframe">The timeframe that the agent worked on when the position was opened.</param>
/// <param name="StopLoss">The price of its stop-loss, as it last was.</param>
/// <param name="TakeProfit">The price of its take-profit, or null when its plan named none.</param>
/// <param name="EntryCycleId">The cycle whose order opened it, which is a decision of the export. Null where no entry of the agent is stored for it.</param>
/// <param name="Trade">The trade, as the list of the trades of its agent says it, with the mode that it was of.</param>
/// <param name="Team">The model that held each role of the agent when the position was opened, in the order of the roles. A role that had none is not among them.</param>
public sealed record ExportedTrade(
    Guid AgentId,
    string AgentName,
    Timeframe Timeframe,
    decimal StopLoss,
    decimal? TakeProfit,
    Guid? EntryCycleId,
    AgentTrade Trade,
    IReadOnlyList<ExportedRoleModel> Team);

/// <summary>The model that held a role when a position was opened.</summary>
/// <param name="Role">The role.</param>
/// <param name="ProviderName">The name of the provider of the model, as users read it.</param>
/// <param name="ModelIdentifier">The identifier that the provider gives the model.</param>
public sealed record ExportedRoleModel(Role Role, string ProviderName, string ModelIdentifier);

/// <summary>One recorded cycle of a user, as an export holds it.</summary>
/// <param name="AgentId">The agent whose cycle it was.</param>
/// <param name="AgentName">The name that the agent has now.</param>
/// <param name="Record">The cycle as a complete record, as its timeline shows it, with the mode that it was of.</param>
public sealed record ExportedDecision(Guid AgentId, string AgentName, CycleRecord Record);

/// <summary>Reads the trades and the decisions of a user from the rows that the Decision Engine keeps. See <see cref="IExportedRecords"/>.</summary>
internal sealed class ExportedRecords(
    IDbContextFactory<EcnanibDbContext> contexts, ICatalogModels models, ILogger<ExportedRecords> logger) : IExportedRecords
{
    public async IAsyncEnumerable<ExportedTrade> TradesOfUserAsync(Guid userId, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Who exported, by id, and nothing of what was exported.
        logger.LogInformation("User {UserId} exported their trades", userId);

        var names = new Dictionary<Guid, string>();
        var called = new Dictionary<Guid, CatalogModel?>();
        (DateTimeOffset ClosedAt, Guid Id)? last = null;

        while (true)
        {
            // Read and let go of before any of it is handed over: what takes the trades may be slow.
            var part = await TradesBeforeAsync(userId, last, names, called, cancellationToken);

            foreach (var trade in part)
            {
                yield return trade;
            }

            if (part.Count < IExportedRecords.ChunkSize)
            {
                yield break;
            }

            last = (part[^1].Trade.ClosedAt, part[^1].Trade.PositionId);
        }
    }

    public async IAsyncEnumerable<ExportedDecision> DecisionsOfUserAsync(Guid userId, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        logger.LogInformation("User {UserId} exported their decisions", userId);

        var names = new Dictionary<Guid, string>();
        var called = new Dictionary<Guid, CatalogModel?>();
        (DateTimeOffset ClosedAt, DateTimeOffset StartedAt, Guid Id)? last = null;

        while (true)
        {
            var part = await DecisionsBeforeAsync(userId, last, names, called, cancellationToken);

            foreach (var decision in part)
            {
                yield return decision;
            }

            if (part.Count < IExportedRecords.ChunkSize)
            {
                yield break;
            }

            last = (part[^1].Record.ClosedAt, part[^1].Record.StartedAt, part[^1].Record.Id);
        }
    }

    /// <summary>The next part of the trades of a user: the latest that come after the last of the part before.</summary>
    private async Task<IReadOnlyList<ExportedTrade>> TradesBeforeAsync(
        Guid userId,
        (DateTimeOffset ClosedAt, Guid Id)? last,
        Dictionary<Guid, string> names,
        Dictionary<Guid, CatalogModel?> called,
        CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        // A trade is rows of four tables, and a result may be written while they are read: the
        // part is read as it was at one moment.
        await using var snapshot = await context.BeginSnapshotAsync(cancellationToken);

        // Asked for by owner: a position of another user is not among them.
        var closed = context.Set<Position>().AsNoTracking().OwnedBy(userId).Where(position => position.ClosedAt != null);

        if (last is { } before)
        {
            // In the order of the list: an earlier close, or the same close and an id that comes after.
            var (closedAt, id) = (before.ClosedAt.ToUniversalTime(), before.Id);

            closed = closed.Where(position => position.ClosedAt < closedAt || (position.ClosedAt == closedAt && position.Id.CompareTo(id) < 0));
        }

        var listed = await ListedTrades.OfAsync(context, userId, closed, skip: 0, IExportedRecords.ChunkSize, cancellationToken);
        var ids = listed.Select(trade => trade.Trade.PositionId).ToList();

        // The model that held each role when each position was opened, which the position keeps saying.
        var roles = (await context.Set<PositionRole>()
            .AsNoTracking()
            .OwnedBy(userId)
            .Where(role => ids.Contains(role.PositionId))
            .Select(role => new { role.PositionId, role.Role, role.ModelId })
            .ToListAsync(cancellationToken))
            .ToLookup(role => role.PositionId);

        // A model of the catalog is never deleted, so a position finds the ones of its team for good.
        foreach (var modelId in roles.SelectMany(ofPosition => ofPosition).Select(role => role.ModelId).Distinct())
        {
            if (!called.ContainsKey(modelId))
            {
                called[modelId] = await models.FindAsync(modelId, cancellationToken);
            }
        }

        await NameAsync(context, userId, listed.Select(trade => trade.AgentId), names, cancellationToken);

        return
        [
            .. listed.Select(trade => new ExportedTrade(
                trade.AgentId,
                names.GetValueOrDefault(trade.AgentId, string.Empty),
                trade.Timeframe,
                trade.StopLoss,
                trade.TakeProfit,
                trade.EntryCycleId,
                trade.Trade,
                [
                    .. roles[trade.Trade.PositionId]
                        .OrderBy(role => role.Role)
                        .Select(role => new ExportedRoleModel(
                            role.Role,
                            called[role.ModelId]?.ProviderName ?? CycleRecordViews.UnknownProvider,
                            called[role.ModelId]?.Identifier ?? CycleRecordViews.UnknownModel)),
                ])),
        ];
    }

    /// <summary>The next part of the decisions of a user: the latest that come after the last of the part before.</summary>
    private async Task<IReadOnlyList<ExportedDecision>> DecisionsBeforeAsync(
        Guid userId,
        (DateTimeOffset ClosedAt, DateTimeOffset StartedAt, Guid Id)? last,
        Dictionary<Guid, string> names,
        Dictionary<Guid, CatalogModel?> called,
        CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        // The cycles and the parts of their records as they were at one moment, as one record is read.
        await using var snapshot = await context.BeginSnapshotAsync(cancellationToken);

        // Asked for by owner: a cycle of another user's agent is not among them.
        var cycles = context.Set<DecisionCycle>().AsNoTracking().OwnedBy(userId);

        if (last is { } before)
        {
            // In the order that the cycles are listed in: an earlier close, then an earlier start.
            var (closedAt, startedAt, id) = (before.ClosedAt.ToUniversalTime(), before.StartedAt.ToUniversalTime(), before.Id);

            cycles = cycles.Where(cycle =>
                cycle.ClosedAt < closedAt
                || (cycle.ClosedAt == closedAt
                    && (cycle.StartedAt < startedAt || (cycle.StartedAt == startedAt && cycle.Id.CompareTo(id) < 0))));
        }

        var latest = await cycles
            .OrderByDescending(cycle => cycle.ClosedAt)
            .ThenByDescending(cycle => cycle.StartedAt)
            .ThenByDescending(cycle => cycle.Id)
            .Take(IExportedRecords.ChunkSize)
            .ToListAsync(cancellationToken);

        // By the one rule of what a record of a cycle holds, which its timeline goes by as well.
        var records = await CycleRecordViews.OfAsync(context, userId, latest, models, called, cancellationToken);

        await NameAsync(context, userId, latest.Select(cycle => cycle.AgentId), names, cancellationToken);

        return
        [
            .. latest.Select((cycle, index) => new ExportedDecision(
                cycle.AgentId, names.GetValueOrDefault(cycle.AgentId, string.Empty), records[index])),
        ];
    }

    /// <summary>
    /// Adds the names of the agents that are not named yet. An agent is asked for by owner, and
    /// what hangs on it is deleted with it, so each of them is found.
    /// </summary>
    private static async Task NameAsync(
        EcnanibDbContext context, Guid userId, IEnumerable<Guid> agentIds, Dictionary<Guid, string> names, CancellationToken cancellationToken)
    {
        var unnamed = agentIds.Where(id => !names.ContainsKey(id)).Distinct().ToList();

        if (unnamed.Count == 0)
        {
            return;
        }

        var found = await context.Set<Agent>()
            .AsNoTracking()
            .OwnedBy(userId)
            .Where(agent => unnamed.Contains(agent.Id))
            .Select(agent => new { agent.Id, agent.Name })
            .ToListAsync(cancellationToken);

        foreach (var agent in found)
        {
            names[agent.Id] = agent.Name;
        }
    }
}
