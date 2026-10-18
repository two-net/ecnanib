namespace Ecnanib.Database.Entities;

/// <summary>A user's acceptance of one version of the risk disclaimer (FR-ACC-08, NFR-LEGAL-01).</summary>
public sealed class DisclaimerAcceptance
{
    public long Id { get; set; }

    public Guid UserId { get; set; }

    public User User { get; set; } = null!;

    public required string Version { get; set; }

    public DateTimeOffset AcceptedAt { get; set; }
}
