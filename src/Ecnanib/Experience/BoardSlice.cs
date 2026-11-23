using Ecnanib.Platform.Settings;

namespace Ecnanib.Experience;

/// <summary>
/// When a slice of the platform-wide board is shown: only when at least 5 users and 50 trades
/// contribute to it (FR-EXP-10), by the numbers of the configuration file. It is a rule only: it
/// reads nothing. See "The platform-wide board" in docs/experience.md.
/// </summary>
/// <remarks>
/// <para>
/// <b>A slice is one record of the board:</b> that of a provider, or that of a model, of the
/// trades that the filter of the board counts, in one mode. Each is held to the rule by itself.
/// </para>
/// <para>
/// <b>A trade contributes when it is counted:</b> when it has a result in R. One that is in no
/// measure of the record adds nothing to it, and neither does its user.
/// </para>
/// <para>
/// <b>A user is counted once,</b> however many of the trades are theirs. So one user with many
/// trades shows nothing, and neither do many users with a few.
/// </para>
/// </remarks>
public static class BoardSlice
{
    /// <summary>Whether the trades of a slice are enough for it to be shown.</summary>
    /// <param name="trades">The trades of the slice, each with whose it is.</param>
    /// <param name="board">The fewest users and trades that must contribute: <c>Experience:Board</c>.</param>
    public static bool IsShown(IEnumerable<ContributedTrade> trades, ExperienceBoardSettings board)
    {
        ArgumentNullException.ThrowIfNull(trades);
        ArgumentNullException.ThrowIfNull(board);

        var counted = trades.Where(trade => trade.Trade.Result?.ResultInR is not null).ToList();

        return counted.Count >= board.MinTrades
            && counted.Select(trade => trade.UserId).Distinct().Count() >= board.MinUsers;
    }
}
