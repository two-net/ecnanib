using Ecnanib.Platform.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Experience;

/// <summary>
/// Whether the trades of a user are counted on the platform-wide board, which is the user's own
/// choice: a user can opt out of contributing (FR-EXP-10). See "The platform-wide board" in
/// docs/experience.md.
/// </summary>
/// <remarks>
/// It takes the user by id, and not from a request: Experience may not use Accounts. Whatever
/// serves the request hands over the user who is signed in, and the choice of anyone else is
/// neither read nor changed (NFR-SEC-3).
/// </remarks>
public interface IBoardContribution
{
    /// <summary>Whether the trades of the user are counted on the board. They are until the user leaves it.</summary>
    /// <param name="userId">The user who is signed in.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<bool> OfUserAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>
    /// Leaves the board, or comes back to it. The board is worked out from the trades each time
    /// it is asked for, so the choice holds from the next time on. A choice that is made twice
    /// changes nothing the second time.
    /// </summary>
    /// <param name="userId">The user who is signed in.</param>
    /// <param name="contributes">Whether the trades of the user are to be counted on the board.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    Task SetAsync(Guid userId, bool contributes, CancellationToken cancellationToken);
}

/// <summary>Reads and stores who left the board.</summary>
internal sealed class BoardContribution(
    IDbContextFactory<EcnanibDbContext> contexts,
    TimeProvider clock,
    ILogger<BoardContribution> logger) : IBoardContribution
{
    public async Task<bool> OfUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        // Asked for by owner: the row of another user says nothing here.
        return !await context.Set<BoardOptOut>().AsNoTracking().OwnedBy(userId).AnyAsync(cancellationToken);
    }

    public async Task SetAsync(Guid userId, bool contributes, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        if (contributes)
        {
            // By owner: the row of the user, and no other.
            if (await context.Set<BoardOptOut>().OwnedBy(userId).ExecuteDeleteAsync(cancellationToken) > 0)
            {
                logger.LogInformation("User {UserId} contributes to the platform-wide board again", userId);
            }

            return;
        }

        var now = clock.GetUtcNow();

        // Of two forms that are sent at the same time, the second finds the row of the first,
        // and writes nothing: a user has one row at the most. The statement names the owner
        // itself.
        var written = await context.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO board_opt_outs (user_id, opted_out_at)
            VALUES ({userId}, {now})
            ON CONFLICT (user_id) DO NOTHING
            """,
            cancellationToken);

        if (written > 0)
        {
            logger.LogInformation("User {UserId} left the platform-wide board", userId);
        }
    }
}
