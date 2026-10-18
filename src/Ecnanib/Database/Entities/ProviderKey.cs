using Ecnanib.Ai.Abstractions;

namespace Ecnanib.Database.Entities;

/// <summary>One AI provider key per user and provider (FR-KEY-03, FR-KEY-04). Stored plain per A-02.</summary>
public sealed class ProviderKey
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public User User { get; set; } = null!;

    public AiProvider Provider { get; set; }

    public required string ApiKey { get; set; }

    public KeyStatus Status { get; set; } = KeyStatus.Unvalidated;

    public DateTimeOffset? LastValidatedAt { get; set; }

    public string? LastValidationError { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
