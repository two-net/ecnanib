using System.Text.Json;
using Ecnanib.Exchange.Abstractions;

namespace Ecnanib.Database.Entities;

/// <summary>
/// One candle-close evaluation of an agent (FR-AGT-09, FR-AGT-10). The unique (agent, candle open time) pair makes
/// repeated or restarted evaluations of the same candle idempotent.
/// </summary>
public sealed class Evaluation
{
    public Guid Id { get; set; }

    public Guid AgentId { get; set; }

    public Agent Agent { get; set; } = null!;

    /// <summary>Snapshots of the agent settings at evaluation time, so history survives a re-pointed agent (FR-AGT-07).</summary>
    public required string Symbol { get; set; }

    public MarketType MarketType { get; set; }

    public CandleInterval Timeframe { get; set; }

    /// <summary>Open time of the candle whose close triggered the evaluation, UTC-aligned.</summary>
    public DateTimeOffset CandleOpenTime { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>Strategy engine output (FR-STR-02); shape owned by the strategy engine.</summary>
    public JsonElement? StrategyOutput { get; set; }

    /// <summary>Whether the council ran (FR-AI-08).</summary>
    public bool CouncilConsulted { get; set; }

    public EvaluationOutcome Outcome { get; set; }

    public string? OutcomeDetail { get; set; }

    public Decision? Decision { get; set; }
}
