using Ecnanib.Shared;

namespace Ecnanib.Experience;

/// <summary>
/// The closed trades of a user, as whatever keeps positions recorded them: each with its result
/// in R, how long it was held, and the model that held each role when it was opened (FR-EXP-1).
/// A track record is worked out from them (FR-EXP-2). See docs/experience.md.
/// </summary>
/// <remarks>
/// <para>
/// Experience may not use the Decision Engine, which keeps the positions, so it declares here
/// what it needs of them, and the Decision Engine implements and registers it. The reference
/// still points downward. See "When a lower component must trigger a higher one" in
/// docs/architecture.md.
/// </para>
/// <para>
/// It takes the user by id, and not from a request: neither component may use Accounts.
/// Whatever serves the request hands over the user who is signed in, and a trade of anyone else
/// is not among the answer (NFR-SEC-3). It only reads.
/// </para>
/// </remarks>
public interface IClosedTrades
{
    /// <summary>
    /// The closed positions of a user, in Demo and in Live, each as a trade. One whose result is
    /// not recorded yet is among them, and says so.
    /// </summary>
    /// <param name="userId">The user who is signed in.</param>
    /// <param name="closedFrom">The moment from which a position counts by when it was closed, itself included, or null for all of them.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<IReadOnlyList<ClosedTrade>> OfUserAsync(Guid userId, DateTimeOffset? closedFrom, CancellationToken cancellationToken);
}

/// <summary>One closed position of a user, as a track record counts it.</summary>
/// <param name="PositionId">The id of the position, which tells two trades of one moment apart.</param>
/// <param name="Mode">The mode that the agent was in when the position was opened, which stays as it was when the agent is switched (FR-BIN-11).</param>
/// <param name="Market">The market that it was traded on.</param>
/// <param name="Timeframe">The timeframe that its agent worked on.</param>
/// <param name="ClosedAt">When it was closed.</param>
/// <param name="Result">Its recorded result, or null while that is not recorded yet: a result is recorded some time after the close.</param>
/// <param name="Roles">The model that held each role when it was opened. A role that had no model is not among them.</param>
public sealed record ClosedTrade(
    Guid PositionId,
    TradingMode Mode,
    Market Market,
    Timeframe Timeframe,
    DateTimeOffset ClosedAt,
    TradeResult? Result,
    IReadOnlyList<TradeRole> Roles);

/// <summary>The recorded result of a closed trade.</summary>
/// <param name="ResultInR">Its net result as a multiple of R, the amount that was risked on it, or null where R is not known.</param>
/// <param name="HoldingSeconds">How long it was held, in whole seconds.</param>
/// <param name="Complete">Whether all of the result is what the exchange said. One that is not is an estimate.</param>
public sealed record TradeResult(decimal? ResultInR, long HoldingSeconds, bool Complete);

/// <summary>The model that held one role when a trade was opened.</summary>
/// <param name="Role">The role.</param>
/// <param name="ModelId">The model of the catalog that held it, which says its provider.</param>
public sealed record TradeRole(Role Role, Guid ModelId);

/// <summary>Asks everything that keeps closed trades. Where nothing does, no user has any.</summary>
internal static class ClosedTradeSources
{
    /// <summary>The closed trades of a user from every source, in the order they were closed in.</summary>
    public static async Task<IReadOnlyList<ClosedTrade>> OfUserAsync(
        this IEnumerable<IClosedTrades> sources, Guid userId, DateTimeOffset? closedFrom, CancellationToken cancellationToken)
    {
        var trades = new List<ClosedTrade>();

        foreach (var source in sources)
        {
            trades.AddRange(await source.OfUserAsync(userId, closedFrom, cancellationToken));
        }

        return [.. trades.OrderBy(trade => trade.ClosedAt).ThenBy(trade => trade.PositionId)];
    }
}
