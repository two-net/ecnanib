using System.Text.Json;

namespace Ecnanib.Database.Entities;

/// <summary>Hypothetical result of a setup the council rejected, using the strategy's stop and target (FR-EXP-03).</summary>
public sealed class ShadowOutcome
{
    public Guid DecisionId { get; set; }

    public Decision Decision { get; set; } = null!;

    public ShadowResult Result { get; set; } = ShadowResult.Pending;

    public decimal? RMultiple { get; set; }

    public DateTimeOffset? ResolvedAt { get; set; }

    public JsonElement? Detail { get; set; }
}
