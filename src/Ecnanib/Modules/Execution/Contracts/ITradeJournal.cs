using Ecnanib.Kernel;

namespace Ecnanib.Modules.Execution.Contracts;

/// <summary>
/// An agent's results from its trade journal (FR-EXE-07), which the loss limits, the entries-per-day cap and the
/// cooldown after a loss count (FR-RISK-02, FR-RISK-08). A trade is one entry order, adds included, and closes once
/// every part of it has. Its P&amp;L is from the order rows the moment it closes, and from Binance's fills, fees and
/// funding once it's settled, usually within a minute. See The trade journal in docs/architecture.md.
/// </summary>
public interface ITradeJournal
{
    /// <summary>
    /// The agent's closed trades' realized P&amp;L, net of fees and funding once settled, counted on the day each
    /// closed. Losing trades in a row, newest first, among those closed since <paramref name="activeSince"/>, where a
    /// trade with nothing priced, such as one the user closed by transferring the asset, counts neither way. Entry
    /// orders created today that Binance may have taken, adds included. And when the newest losing trade closed. Only
    /// what the agent did in <paramref name="environment"/> counts, so an agent switched to Live today isn't held to, or
    /// let off by, its Demo results (FR-EXP-08).
    /// </summary>
    /// <param name="environment">The agent's environment now.</param>
    /// <param name="now">The caller's time, which <c>RiskPeriods</c> turns into the day and the week.</param>
    /// <param name="activeSince">When the agent last became Active, so a resumed agent's streak starts again.</param>
    Task<TradeResults> ReadResultsAsync(
        Guid userId, Guid agentId, TradingEnvironment environment, DateTimeOffset now, DateTimeOffset activeSince, CancellationToken cancellationToken);

    /// <summary>
    /// The agent's results to date in <paramref name="environment"/>, for its overview (FR-AGT-11): its closed trades'
    /// realized P&amp;L, net of fees and funding as far as they're known, and what its open trades have cost so far,
    /// which their unrealized P&amp;L is net of. One per quote asset, the agent's current pair's among them only once
    /// it has a trade. Only <paramref name="environment"/>'s trades count, so Demo and Live are never added together
    /// (FR-EXP-08). Scoped to the user (NFR-SEC-04).
    /// </summary>
    Task<IReadOnlyList<JournalTotals>> ReadTotalsAsync(Guid userId, Guid agentId, TradingEnvironment environment, CancellationToken cancellationToken);

    /// <summary>
    /// The agent's trades that <paramref name="query"/> picks, newest first: for its chart's entries and exits
    /// (FR-UI-03) and its list of trades. Scoped to the user (NFR-SEC-04).
    /// </summary>
    Task<IReadOnlyList<TradeRecord>> ListTradesAsync(Guid userId, Guid agentId, TradeQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// The user's settled trades, across all their agents, that closed in <paramref name="query"/>'s range, for the
    /// trading experience metrics (FR-EXP-02). Only settled trades, whose figures are final, and only those in the
    /// query's environment, from each trade's own row, so Demo and Live are never added together (FR-EXP-08). Each
    /// carries the cycle its entry order cites, which says which models it's attributed to (FR-EXP-01). Scoped to the
    /// user (NFR-SEC-04).
    /// </summary>
    Task<IReadOnlyList<SettledTrade>> ListSettledAsync(Guid userId, SettledTradeQuery query, CancellationToken cancellationToken);
}

/// <param name="RealizedToday">Since <c>RiskPeriods.DayStart</c>, in the quote asset. A loss is negative.</param>
/// <param name="RealizedThisWeek">Since <c>RiskPeriods.WeekStart</c>.</param>
/// <param name="ConsecutiveLosses">Losing trades in a row, newest first, closed since the agent last became Active.</param>
/// <param name="EntriesToday">Entry orders placed since <c>RiskPeriods.DayStart</c>, adds included.</param>
/// <param name="LastLossClosedAt">When the newest losing trade closed, or null when there's been none.</param>
public sealed record TradeResults(
    decimal RealizedToday,
    decimal RealizedThisWeek,
    int ConsecutiveLosses,
    int EntriesToday,
    DateTimeOffset? LastLossClosedAt);
