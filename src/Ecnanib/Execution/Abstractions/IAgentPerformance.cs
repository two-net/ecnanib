namespace Ecnanib.Execution.Abstractions;

/// <summary>
/// An agent's money figures for the dashboard (FR-UI-01). Realized P&amp;L is Σ <c>trades.realized_pnl</c>, net of fees and
/// funding, partial exits of open trades included; <see cref="RealizedToday"/> counts the current UTC day the way the
/// daily loss limit does (exit fills' realized P&amp;L less the entry fees paid today, FR-EXE-09). <see cref="PaperEquity"/>
/// is <c>Paper:StartingBalance</c> plus the paper realized P&amp;L and null for a live agent, whose equity is the account's,
/// not the agent's. Unrealized P&amp;L is the page's: the open positions against the last price (<c>PnlMath.Unrealized</c>).
/// </summary>
public sealed record AgentPnl(
    Guid AgentId,
    decimal RealizedAllTime,
    decimal RealizedToday,
    decimal Fees,
    decimal Funding,
    int ClosedTrades,
    int OpenPositions,
    decimal? PaperEquity);

/// <summary>Reads the money figures of the user's agents (NFR-SEC-03: another user's agent is absent from the answer).</summary>
public interface IAgentPerformance
{
    Task<IReadOnlyDictionary<Guid, AgentPnl>> GetAsync(Guid userId, IReadOnlyCollection<Guid> agentIds, CancellationToken cancellationToken);
}
