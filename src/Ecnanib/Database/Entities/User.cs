namespace Ecnanib.Database.Entities;

/// <summary>
/// A registered account (FR-ACC-01..04). Every other user-owned row cascades from here so account deletion removes
/// keys, agents, decisions and trades in one statement (FR-ACC-04); log entries are deleted explicitly because
/// <see cref="LogEntry"/> carries no foreign keys.
/// </summary>
public sealed class User
{
    public Guid Id { get; set; }

    /// <summary>The address as entered; <see cref="NormalizedEmail"/> is the unique lookup key.</summary>
    public required string Email { get; set; }

    public required string NormalizedEmail { get; set; }

    /// <summary>Modern password hash (NFR-SEC-01); never the password.</summary>
    public required string PasswordHash { get; set; }

    /// <summary>Changes whenever the password changes so existing sessions can be invalidated (FR-ACC-03).</summary>
    public required string SecurityStamp { get; set; }

    public UiTheme Theme { get; set; } = UiTheme.System;

    /// <summary>In-app toggle that disables non-essential animation (FR-UI-03, NFR-USE-02).</summary>
    public bool ReducedMotion { get; set; }

    /// <summary>Latest risk-disclaimer acceptance (FR-ACC-08); the history is in <see cref="DisclaimerAcceptance"/>.</summary>
    public DateTimeOffset? DisclaimerAcceptedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public ICollection<ExchangeKey> ExchangeKeys { get; set; } = [];

    public ICollection<ProviderKey> ProviderKeys { get; set; } = [];

    public ICollection<Agent> Agents { get; set; } = [];
}
