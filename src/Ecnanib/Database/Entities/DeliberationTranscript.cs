using System.Text.Json;

namespace Ecnanib.Database.Entities;

/// <summary>
/// The full text of one role's turn: its input summary and structured output (FR-AI-14, Appendix C). Swept after
/// the configured retention (NFR-DATA-03) while the owning <see cref="Decision"/> stays.
/// </summary>
public sealed class DeliberationTranscript
{
    public Guid DecisionId { get; set; }

    public Decision Decision { get; set; } = null!;

    public CouncilRole Role { get; set; }

    public required string InputSummary { get; set; }

    public JsonElement Output { get; set; } = JsonValues.EmptyObject;

    public DateTimeOffset CreatedAt { get; set; }
}
