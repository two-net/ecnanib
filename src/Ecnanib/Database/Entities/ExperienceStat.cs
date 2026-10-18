using Ecnanib.Ai.Abstractions;
using Ecnanib.Exchange.Abstractions;

namespace Ecnanib.Database.Entities;

/// <summary>
/// Experience statistics for one provider/model/role/timeframe/market combination at one scope (FR-EXP-04,
/// FR-EXP-05). Agent-scope rows also carry the user so every read stays user-scoped (NFR-SEC-03); system-scope
/// rows carry neither and hold counts and ratios only.
/// </summary>
public sealed class ExperienceStat
{
    public long Id { get; set; }

    public StatScope Scope { get; set; }

    public Guid? AgentId { get; set; }

    public Agent? Agent { get; set; }

    public Guid? UserId { get; set; }

    public User? User { get; set; }

    public AiProvider Provider { get; set; }

    public required string Model { get; set; }

    public CouncilRole Role { get; set; }

    public CandleInterval Timeframe { get; set; }

    public MarketType MarketType { get; set; }

    public ExperienceMetrics Metrics { get; set; } = new();

    public DateTimeOffset ComputedAt { get; set; }
}

/// <summary>The FR-EXP-04 metrics. Ratios are 0..1; R and expectancy are in risk units.</summary>
public sealed class ExperienceMetrics
{
    public int Decisions { get; set; }

    public int Approvals { get; set; }

    public int Rejections { get; set; }

    public decimal? WinRate { get; set; }

    public decimal? AverageR { get; set; }

    public decimal? Expectancy { get; set; }

    public decimal? ProfitFactor { get; set; }

    public decimal? DrawdownContribution { get; set; }

    /// <summary>How often the role's output needed clamping to a hard limit (FR-AI-11).</summary>
    public decimal? ClampRate { get; set; }

    public decimal? FailureRate { get; set; }

    public int? AverageLatencyMs { get; set; }
}
