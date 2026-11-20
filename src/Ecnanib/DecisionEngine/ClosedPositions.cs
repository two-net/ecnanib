using Ecnanib.Experience;
using Ecnanib.Platform.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.DecisionEngine;

/// <summary>
/// The closed positions of a user as the trades that a track record counts: what Experience
/// asks of whatever keeps positions, through <see cref="IClosedTrades"/> (FR-EXP-1, FR-EXP-2).
/// See "The results of a position" in docs/orders.md, and docs/experience.md.
/// </summary>
/// <remarks>
/// <para>
/// A trade is a position that is closed, with its result when that is recorded, and with the
/// model that held each role when it was opened. Nothing of it is said twice: the mode, the
/// market, and the timeframe are the position's own, the result is its row of
/// <see cref="PositionResult"/>, and the models are its rows of <see cref="PositionRole"/>.
/// </para>
/// <para>
/// Everything is asked for by owner, and read as it was at one moment. It reads no agent: a
/// position says what its agent was when it was opened, and is deleted with it.
/// </para>
/// </remarks>
internal sealed class ClosedPositions(IDbContextFactory<EcnanibDbContext> contexts) : IClosedTrades
{
    public async Task<IReadOnlyList<ClosedTrade>> OfUserAsync(Guid userId, DateTimeOffset? closedFrom, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        // The trades are rows of three tables, and a result may be written while they are read:
        // they are read as they were at one moment.
        await using var snapshot = await context.BeginSnapshotAsync(cancellationToken);

        // Asked for by owner: a position of another user is not among them.
        var closed = context.Set<Position>().AsNoTracking().OwnedBy(userId).Where(position => position.ClosedAt != null);

        if (closedFrom is { } asked)
        {
            // The database takes a moment in UTC alone.
            var from = asked.ToUniversalTime();

            closed = closed.Where(position => position.ClosedAt >= from);
        }

        var results = context.Set<PositionResult>().AsNoTracking().OwnedBy(userId);

        // A position that is closed has its result some time after: until then it has none,
        // and is a trade all the same.
        var positions = await (
            from position in closed
            join result in results on position.Id equals result.PositionId into recorded
            from result in recorded.DefaultIfEmpty()
            select new
            {
                position.Id,
                position.Mode,
                position.Market,
                position.Timeframe,
                position.ClosedAt,
                Recorded = result != null,
                ResultInR = (decimal?)result.ResultInR,
                HoldingSeconds = (long?)result.HoldingSeconds,
                Complete = (bool?)result.Complete,
            }).ToListAsync(cancellationToken);

        var roles = (await (
            from role in context.Set<PositionRole>().AsNoTracking().OwnedBy(userId)
            join position in closed on role.PositionId equals position.Id
            select new { role.PositionId, role.Role, role.ModelId }).ToListAsync(cancellationToken))
            .ToLookup(role => role.PositionId);

        return
        [
            .. positions
                .OrderBy(position => position.ClosedAt)
                .ThenBy(position => position.Id)
                .Select(position => new ClosedTrade(
                    position.Id,
                    position.Mode,
                    position.Market,
                    position.Timeframe,
                    position.ClosedAt!.Value,
                    position.Recorded
                        ? new TradeResult(position.ResultInR, position.HoldingSeconds ?? 0L, position.Complete ?? false)
                        : null,
                    [.. roles[position.Id].OrderBy(role => role.Role).Select(role => new TradeRole(role.Role, role.ModelId))])),
        ];
    }
}
