using System.Security.Claims;

namespace Ecnanib.Modules.Identity.Contracts;

/// <summary>
/// TOTP two-factor authentication (FR-AUTH-05). Once a user turns it on, signing in and each sensitive
/// action needs a code from their authenticator, or one of their recovery codes. Codes are secrets, so
/// a page that takes one to set up or turn off two-factor is a static page: see Forms that take a secret
/// and Two-factor in docs/architecture.md.
/// </summary>
public interface ITwoFactor
{
    Task<TwoFactorStatus> GetStatusAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>
    /// Whether the user has two-factor on, so a page knows to ask for a code before a sensitive action.
    /// Asking doesn't make the action safe: <see cref="CheckAsync"/> does.
    /// </summary>
    Task<bool> IsEnabledAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>
    /// Starts setting up an authenticator with a new key, replacing any setup that wasn't finished. Null
    /// when two-factor is already on: turn it off first to change authenticator.
    /// </summary>
    Task<TotpSetup?> BeginSetupAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>The setup in progress, to show again, or null when there isn't one.</summary>
    Task<TotpSetup?> FindSetupAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>
    /// Finishes setup. It needs the current password, which counts towards the sign-in lockout, so that
    /// someone holding only a session can't put their own authenticator on the account, and a code from
    /// the new authenticator, which proves it works. It ends the user's other sessions, because none of
    /// them passed two-factor, and returns the recovery codes, which are shown once and never again.
    /// </summary>
    Task<EnableTwoFactorResult> EnableAsync(ClaimsPrincipal user, string password, string code, CancellationToken cancellationToken);

    /// <summary>
    /// The check a sensitive action calls (FR-AUTH-05): adding or rotating exchange credentials, switching
    /// an agent to Live, the kill switch, and anything else that needs the same. Call it behind your
    /// module's contract, in the method that does the action, before changing anything, and go ahead only
    /// when <see cref="TwoFactorChecks.Allows"/> is true. It's safe on a circuit: wrong codes are limited
    /// per account, separately from the sign-in lockout. During a lock only recovery codes work, because
    /// they can't be guessed. A code works once, and a recovery code is spent and audited.
    /// </summary>
    /// <param name="code">What the user typed: a code from their authenticator, or a recovery code.</param>
    Task<TwoFactorCheck> CheckAsync(Guid userId, string? code, CancellationToken cancellationToken);

    /// <summary>
    /// Turns two-factor off, which is a sensitive action itself, so it needs a code. Its key and
    /// recovery codes are deleted. <see cref="TwoFactorCheck.NotRequired"/> when it was already off.
    /// </summary>
    Task<TwoFactorCheck> DisableAsync(Guid userId, string? code, CancellationToken cancellationToken);

    /// <summary>
    /// Replaces the recovery codes, which needs a code, and returns the new ones to show once. The old
    /// ones stop working. Without a pass, <see cref="RegenerateRecoveryCodesResult.Codes"/> is null.
    /// </summary>
    Task<RegenerateRecoveryCodesResult> RegenerateRecoveryCodesAsync(Guid userId, string? code, CancellationToken cancellationToken);
}

public enum TwoFactorState
{
    Off,

    /// <summary>A key was made and shown, and no code from it has confirmed it yet. Sign-in doesn't ask for one.</summary>
    SetupPending,

    On,
}

/// <param name="RecoveryCodesLeft">Unused recovery codes. Zero unless two-factor is on.</param>
public sealed record TwoFactorStatus(TwoFactorState State, int RecoveryCodesLeft);

public enum TwoFactorCheck
{
    /// <summary>The user doesn't have two-factor on, so the action needs no code.</summary>
    NotRequired,

    Passed,

    /// <summary>Wrong, already used, or not a code at all.</summary>
    Refused,

    /// <summary>Too many wrong authenticator codes. Recovery codes still work.</summary>
    Throttled,
}

public static class TwoFactorChecks
{
    /// <summary>Whether the action may go ahead.</summary>
    public static bool Allows(this TwoFactorCheck check) => check is TwoFactorCheck.Passed or TwoFactorCheck.NotRequired;
}

/// <summary>
/// What an authenticator app needs. Not a record, whose ToString would print the key, which is as good
/// as the authenticator itself.
/// </summary>
public sealed class TotpSetup(string key, string provisioningUri)
{
    /// <summary>The key in base32, for typing in by hand.</summary>
    public string Key { get; } = key;

    /// <summary>The otpauth:// URI that a QR code carries, which holds the key too.</summary>
    public string ProvisioningUri { get; } = provisioningUri;

    public override string ToString() => nameof(TotpSetup);
}

/// <summary>Recovery codes to show once. Not a record, whose ToString would print them.</summary>
public sealed class RecoveryCodeSet(IReadOnlyList<string> codes)
{
    public IReadOnlyList<string> Codes { get; } = codes;

    public override string ToString() => nameof(RecoveryCodeSet);
}

public enum EnableTwoFactorOutcome
{
    Enabled,

    /// <summary>Also the answer for a locked account, as at sign-in.</summary>
    PasswordWrong,

    CodeWrong,

    /// <summary>There's no setup in progress: it was never started, or two-factor is already on.</summary>
    NoSetup,
}

/// <param name="RecoveryCodes">Only when <see cref="Outcome"/> is <see cref="EnableTwoFactorOutcome.Enabled"/>.</param>
public sealed record EnableTwoFactorResult(EnableTwoFactorOutcome Outcome, RecoveryCodeSet? RecoveryCodes = null);

public sealed record RegenerateRecoveryCodesResult(TwoFactorCheck Check, RecoveryCodeSet? Codes = null);
