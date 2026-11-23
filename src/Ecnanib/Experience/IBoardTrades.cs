namespace Ecnanib.Experience;

/// <summary>
/// The closed trades of every user who contributes to the platform-wide board, each with whose
/// it is, as whatever keeps positions recorded them (FR-EXP-10). The records of the board are
/// worked out from them. See "The platform-wide board" in docs/experience.md.
/// </summary>
/// <remarks>
/// <para>
/// <b>It answers across users, which <see cref="IClosedTrades"/> never does.</b> So it is a
/// question of its own, and what asks for the trades of one user cannot come by those of
/// another through it. Only the board asks it, and the board says of them what at least
/// <c>Experience:Board:MinUsers</c> users and <c>Experience:Board:MinTrades</c> trades add up
/// to, and nothing of one user or of one trade.
/// </para>
/// <para>
/// <b>Whoever answers leaves out the users who left the board:</b> those with a row of
/// <see cref="BoardOptOut"/>. It is left out where the trades are read, so that a trade of such
/// a user is handed to nothing.
/// </para>
/// <para>
/// Experience may not use the Decision Engine, which keeps the positions, so it declares here
/// what it needs of them, and the Decision Engine implements and registers it, as it does
/// <see cref="IClosedTrades"/>. It only reads.
/// </para>
/// </remarks>
public interface IBoardTrades
{
    /// <summary>
    /// The closed positions that have a result in R, in Demo and in Live, of every user who has
    /// not left the board. One whose result is not recorded yet, and one whose result says no R,
    /// is not among them: the board counts neither, and says nothing of them.
    /// </summary>
    /// <param name="closedFrom">The moment from which a position counts by when it was closed, itself included, or null for all of them.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<IReadOnlyList<ContributedTrade>> OfContributorsAsync(DateTimeOffset? closedFrom, CancellationToken cancellationToken);
}

/// <summary>One closed trade that counts on the board, with whose it is.</summary>
/// <param name="UserId">
/// The user whose agent made the trade. The board counts how many users a record is of, and
/// names none: an id tells nothing about the person, and it is in no answer of the board.
/// </param>
/// <param name="Trade">The trade, as a track record counts it.</param>
public sealed record ContributedTrade(Guid UserId, ClosedTrade Trade);

/// <summary>Asks everything that keeps closed trades. Where nothing does, the board has none.</summary>
internal static class BoardTradeSources
{
    /// <summary>The trades of every contributor from every source, in the order they were closed in.</summary>
    public static async Task<IReadOnlyList<ContributedTrade>> OfContributorsAsync(
        this IEnumerable<IBoardTrades> sources, DateTimeOffset? closedFrom, CancellationToken cancellationToken)
    {
        var trades = new List<ContributedTrade>();

        foreach (var source in sources)
        {
            trades.AddRange(await source.OfContributorsAsync(closedFrom, cancellationToken));
        }

        return [.. trades.OrderBy(trade => trade.Trade.ClosedAt).ThenBy(trade => trade.Trade.PositionId)];
    }
}
