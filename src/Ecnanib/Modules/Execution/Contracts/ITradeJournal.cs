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
    /// orders created today that Binance may have taken, adds included. And when the newest losing trade closed.
    /// </summary>
    /// <param name="now">The caller's time, which <c>RiskPeriods</c> turns into the day and the week.</param>
    /// <param name="activeSince">When the agent last became Active, so a resumed agent's streak starts again.</param>
    Task<TradeResults> ReadResultsAsync(Guid userId, Guid agentId, DateTimeOffset now, DateTimeOffset activeSince, CancellationToken cancellationToken);
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
