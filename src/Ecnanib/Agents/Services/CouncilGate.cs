using Ecnanib.Agents.Abstractions;
using Ecnanib.Database.Entities;

namespace Ecnanib.Agents.Services;

/// <summary>
/// Whether an evaluation needs the council (FR-AI-08, NFR-COST-01). An open position always does: the council manages it
/// (hold, modify, close), also one flagged unmanaged, whose Close or Modify through the council is the only way left
/// to act on it. With nothing open the council could only open something, so it is asked only when an entry is
/// possible at all — entries are allowed, no no-entry window is active at the candle close, and the position cap
/// leaves room — and the strategy reports a setup or discretionary mode is on. Otherwise the evaluation ends after the
/// strategy engine with <see cref="CouncilNeed.Detail"/> as its "no action" record: the hard limits would refuse the
/// entry anyway (FR-AI-11), so the call is not worth its cost. The window and the cap are judged exactly as the hard
/// limits judge them (<see cref="EntryWindowSchedule"/>, <see cref="PositionRoom"/>); they are the opening limits that
/// depend on the context alone. Of the others, direction is settled by the engine (a spot agent gets no short setup)
/// and the daily-loss limit by the state (that pause makes the agent Paused, so entries are not allowed), and the rest
/// judge the proposal itself, which only a call can produce.
/// </summary>
internal static class CouncilGate
{
    internal static CouncilNeed Decide(
        Agent agent,
        StrategyOutput output,
        bool entriesAllowed,
        string? entriesBlocked,
        IReadOnlyList<Trade> openTrades,
        IReadOnlyList<Order> openOrders,
        int systemPositionCap,
        DateTimeOffset candleClose)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(openTrades);
        ArgumentNullException.ThrowIfNull(openOrders);

        if (openTrades.Count > 0)
            return CouncilNeed.Yes("a position is open");
        if (!entriesAllowed)
            return CouncilNeed.No(output.Note ?? (entriesBlocked is not null ? "No new entry this candle." : agent.State == AgentState.Running ? "No setup; the open entry is managed." : "Paused: no open position to manage."));
        if (output.Setup is null && !agent.Discretionary)
            return CouncilNeed.No(output.Note ?? "No setup and no open position.");
        if (EntryWindowSchedule.AnyActive(agent.EntryWindows, candleClose))
            return CouncilNeed.No(Join("No new entry: a no-entry window is active at the candle close.", output.Note));
        var open = PositionRoom.Open(openTrades, openOrders);
        var cap = PositionRoom.Cap(agent, systemPositionCap);
        if (open >= cap)
            return CouncilNeed.No(Join($"No new entry: {open} of {cap} already open (positions and pending entries).", output.Note));
        return CouncilNeed.Yes(output.Setup is not null ? "the strategy reports a setup" : "discretionary mode is on");
    }

    private static string Join(string first, string? second) => EvaluationPipeline.Join(first, second) ?? first;
}

/// <summary>The gate's answer: whether the council runs and, when it does not, the evaluation's "no action" detail (when it does, why).</summary>
internal sealed record CouncilNeed(bool Needed, string Detail)
{
    public static CouncilNeed Yes(string reason) => new(true, reason);

    public static CouncilNeed No(string detail) => new(false, detail);
}
