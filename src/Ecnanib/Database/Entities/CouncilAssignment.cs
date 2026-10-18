using Ecnanib.Ai.Abstractions;

namespace Ecnanib.Database.Entities;

/// <summary>Provider and model chosen for one council role of an agent (FR-AI-03). The Risk Manager cannot be disabled (FR-AI-05).</summary>
public sealed class CouncilAssignment
{
    public Guid AgentId { get; set; }

    public Agent Agent { get; set; } = null!;

    public CouncilRole Role { get; set; }

    public AiProvider Provider { get; set; }

    /// <summary>A catalog model id or one entered manually (FR-AI-03); never hard-coded (FR-SYNC-05).</summary>
    public required string Model { get; set; }

    public bool Enabled { get; set; } = true;
}
