using System.Text.Json;
using Ecnanib.Exchange.Abstractions;

namespace Ecnanib.Database.Entities;

/// <summary>
/// The council's final decision for one evaluation (Appendix C, FR-AI-10). Retained for the life of the account
/// (NFR-DATA-03); the purgeable transcript text lives in <see cref="DeliberationTranscript"/>.
/// </summary>
public sealed class Decision
{
    public Guid Id { get; set; }

    public Guid EvaluationId { get; set; }

    public Evaluation Evaluation { get; set; } = null!;

    /// <summary>
    /// The trade a managing decision (hold, modify, close) acted on (FR-EXP-02). Null for the opening decision,
    /// which the trade references through <see cref="Trade.EntryDecisionId"/>, and for no-trade decisions. Setting it
    /// on the opening decision in the same SaveChanges as the trade creates a cycle EF cannot order.
    /// </summary>
    public Guid? TradeId { get; set; }

    public Trade? Trade { get; set; }

    public DecisionAction FinalAction { get; set; }

    public OrderType? EntryType { get; set; }

    public decimal? EntryPrice { get; set; }

    public decimal? StopPrice { get; set; }

    /// <summary>Array of target prices.</summary>
    public JsonElement? Targets { get; set; }

    public decimal? Size { get; set; }

    /// <summary>Modify only: the fraction of the position to take off (FR-AI-10 "take partial"), 0 &lt; f ≤ 1.</summary>
    public decimal? PartialFraction { get; set; }

    public decimal? RiskPercent { get; set; }

    public decimal? Confidence { get; set; }

    public required string Rationale { get; set; }

    /// <summary>Per hard limit: pass, clamped or fail (FR-AI-11).</summary>
    public JsonElement? LimitsCheck { get; set; }

    public int TotalInputTokens { get; set; }

    public int TotalOutputTokens { get; set; }

    public int TotalLatencyMs { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public ICollection<DecisionRoleOutput> RoleOutputs { get; set; } = [];

    public ICollection<DeliberationTranscript> Transcripts { get; set; } = [];

    public ShadowOutcome? ShadowOutcome { get; set; }
}
