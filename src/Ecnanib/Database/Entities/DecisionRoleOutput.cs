using Ecnanib.Ai.Abstractions;

namespace Ecnanib.Database.Entities;

/// <summary>
/// The compact, permanent record of one role's turn: who produced it and at what cost (FR-EXP-01, FR-AI-12,
/// FR-AI-14). The role's text is in <see cref="DeliberationTranscript"/>.
/// </summary>
public sealed class DecisionRoleOutput
{
    public Guid DecisionId { get; set; }

    public Decision Decision { get; set; } = null!;

    public CouncilRole Role { get; set; }

    /// <summary>Execution order within the council (Appendix B).</summary>
    public int Sequence { get; set; }

    public AiProvider Provider { get; set; }

    public required string Model { get; set; }

    public RoleOutputStatus Status { get; set; }

    public DecisionAction? ProposedAction { get; set; }

    public decimal? Confidence { get; set; }

    public int InputTokens { get; set; }

    public int OutputTokens { get; set; }

    public int LatencyMs { get; set; }
}
