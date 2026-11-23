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
/// <para>
/// <b>The trades of the platform-wide board are the one thing that is not asked for by owner</b>
/// (FR-EXP-10): <see cref="IBoardTrades"/> answers with the counted trades of every user who has
/// not left the board, each with whose it is, for Experience to add up. A user who left it has a
/// row of <see cref="BoardOptOut"/>, and is left out where the positions are read. See "What is
/// not asked for by owner" in docs/ownership.md.
/// </para>
/// </remarks>
internal sealed class ClosedPositions(IDbContextFactory<EcnanibDbContext> contexts) : IClosedTrades, IBoardTrades
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
                    RolesOf(roles[position.Id].Select(role => new TradeRole(role.Role, role.ModelId))))),
        ];
    }

    public async Task<IReadOnlyList<ContributedTrade>> OfContributorsAsync(DateTimeOffset? closedFrom, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        // Read as they were at one moment, as the trades of one user are. Who left the board is
        // read at that moment too: a user who leaves it while this reads is in all of the
        // answer, or in none of it.
        await using var snapshot = await context.BeginSnapshotAsync(cancellationToken);

        // Of every user but those who left the board. This is not asked for by owner: it is the
        // trades of the board, which says what they add up to and nothing of one user. A user
        // who left it is left out here, where the positions are read, so that a trade of theirs
        // is handed to nothing.
        var left = context.Set<BoardOptOut>();
        var closed = context.Set<Position>()
            .AsNoTracking()
            .Where(position => position.ClosedAt != null && !left.Any(optOut => optOut.UserId == position.UserId));

        if (closedFrom is { } asked)
        {
            // The database takes a moment in UTC alone.
            var from = asked.ToUniversalTime();

            closed = closed.Where(position => position.ClosedAt >= from);
        }

        // Only a position that has a result in R: the board counts no other, and says nothing
        // of one, so no other is read. A result carries the owner of its position along, so the
        // two are of one user.
        var counted =
            from position in closed
            join result in context.Set<PositionResult>().AsNoTracking() on position.Id equals result.PositionId
            where result.ResultInR != null
            select new
            {
                position.Id,
                position.UserId,
                position.Mode,
                position.Market,
                position.Timeframe,
                position.ClosedAt,
                result.ResultInR,
                result.HoldingSeconds,
                result.Complete,
            };

        var positions = await counted.ToListAsync(cancellationToken);

        var roles = (await (
            from role in context.Set<PositionRole>().AsNoTracking()
            join position in counted on role.PositionId equals position.Id
            select new { role.PositionId, role.Role, role.ModelId }).ToListAsync(cancellationToken))
            .ToLookup(role => role.PositionId);

        return
        [
            .. positions
                .OrderBy(position => position.ClosedAt)
                .ThenBy(position => position.Id)
                .Select(position => new ContributedTrade(
                    position.UserId,
                    new ClosedTrade(
                        position.Id,
                        position.Mode,
                        position.Market,
                        position.Timeframe,
                        position.ClosedAt!.Value,
                        new TradeResult(position.ResultInR, position.HoldingSeconds, position.Complete),
                        RolesOf(roles[position.Id].Select(role => new TradeRole(role.Role, role.ModelId)))))),
        ];
    }

    /// <summary>The roles of a trade in the order of the roles, whichever way they were read.</summary>
    private static IReadOnlyList<TradeRole> RolesOf(IEnumerable<TradeRole> roles) => [.. roles.OrderBy(role => role.Role)];
}
