using System.ComponentModel.DataAnnotations;

namespace Ecnanib.Accounts.Configuration;

/// <summary>
/// The <c>Accounts</c> section of the system configuration file: how long a signed-in session lasts (FR-ACC-02), how
/// often a session is re-checked against the account, the sign-in rate limit (NFR-SEC-01) and the password length
/// rule. Appendix D lists no such section; FR-CFG-08 makes the appendix a minimum, not a ceiling.
/// </summary>
public sealed class AccountsOptions
{
    public const string SectionName = "Accounts";

    /// <summary>Lifetime of the persistent session cookie; every request slides it forward.</summary>
    [Range(1, 3650)]
    public int SessionDays { get; set; } = 30;

    /// <summary>How long a session ticket is trusted before the account's security stamp is read again.</summary>
    [Range(1, 1440)]
    public int SessionRevalidationMinutes { get; set; } = 5;

    /// <summary>Failed sign-ins allowed per (email, client address) pair within one window.</summary>
    [Range(1, 1000)]
    public int SignInAttemptsPerWindow { get; set; } = 10;

    /// <summary>Failed sign-ins and registrations allowed per client address within one window, whatever the email.</summary>
    [Range(1, 100_000)]
    public int SignInAttemptsPerAddress { get; set; } = 100;

    [Range(1, 1440)]
    public int SignInWindowMinutes { get; set; } = 15;

    /// <summary>Length is the only password rule (NIST SP 800-63B); the maximum is fixed at 128.</summary>
    [Range(8, 64)]
    public int MinPasswordLength { get; set; } = 8;

    public TimeSpan SessionLifetime => TimeSpan.FromDays(SessionDays);

    public TimeSpan SessionRevalidation => TimeSpan.FromMinutes(SessionRevalidationMinutes);

    public TimeSpan SignInWindow => TimeSpan.FromMinutes(SignInWindowMinutes);
}
