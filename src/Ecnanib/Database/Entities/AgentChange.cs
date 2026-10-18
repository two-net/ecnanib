using System.Text.Json;

namespace Ecnanib.Database.Entities;

/// <summary>One edit in an agent's change history (FR-AGT-07).</summary>
public sealed class AgentChange
{
    public long Id { get; set; }

    public Guid AgentId { get; set; }

    public Agent Agent { get; set; } = null!;

    public DateTimeOffset Timestamp { get; set; }

    public required string Field { get; set; }

    public JsonElement? OldValue { get; set; }

    public JsonElement? NewValue { get; set; }
}
