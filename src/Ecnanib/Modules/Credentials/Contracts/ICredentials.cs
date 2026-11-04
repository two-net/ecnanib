using Ecnanib.Kernel;
using Ecnanib.Modules.Identity.Contracts;

namespace Ecnanib.Modules.Credentials.Contracts;

/// <summary>
/// A user's own AI provider and Binance keys (CON-11, FR-KEY-01 to FR-KEY-05). Secrets go in and never
/// come out: a <see cref="CredentialRecord"/> has no secret, and listing never decrypts anything
/// (FR-KEY-03). Every call is scoped to the user, so another user's credential behaves like one that
/// doesn't exist (NFR-SEC-04). Using a key is <see cref="ICredentialKeys"/>. See Credentials in
/// docs/architecture.md.
/// </summary>
public interface ICredentials
{
    /// <summary>The AI providers a key can be added for: those with an adapter that's enabled.</summary>
    IReadOnlyList<string> AiProviders { get; }

    /// <summary>
    /// The server's addresses, from <c>App:EgressIpAddresses</c>, for users to put on a Binance key's IP
    /// restriction (FR-KEY-05). Empty when the operator hasn't listed them.
    /// </summary>
    IReadOnlyList<string> EgressIpAddresses { get; }

    /// <summary>The user's credentials, oldest first.</summary>
    Task<IReadOnlyList<CredentialRecord>> ListAsync(Guid userId, CancellationToken cancellationToken);

    Task<CredentialRecord?> FindAsync(Guid userId, Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// Validates an AI key by listing its models (FR-KEY-04), and saves it unless the provider refused it.
    /// When the provider can't be reached, it's saved as <see cref="CredentialStatus.Unverified"/>.
    /// </summary>
    /// <param name="apiKey">The caller owns it, and disposes it.</param>
    Task<SaveResult> AddAiKeyAsync(Guid userId, string provider, string label, SecretValue apiKey, CancellationToken cancellationToken);

    /// <summary>
    /// Checks the two-factor code first, because this is a sensitive action (FR-AUTH-05), then validates
    /// the key with Binance and saves it. A key with withdrawals enabled is never saved (AC-04), and
    /// neither is one Binance couldn't be asked about.
    /// </summary>
    /// <param name="apiKey">The caller owns it, and disposes it.</param>
    /// <param name="secret">The HMAC secret, or the Ed25519 private key in PKCS#8 PEM. The caller owns it, and disposes it.</param>
    /// <param name="twoFactorCode">Ignored when the user doesn't have two-factor on.</param>
    Task<SaveResult> AddBinanceKeyAsync(
        Guid userId,
        TradingEnvironment environment,
        string label,
        BinanceKeyType keyType,
        SecretValue apiKey,
        SecretValue secret,
        string? twoFactorCode,
        CancellationToken cancellationToken);

    /// <summary>Replaces an AI credential's key, validated like a new one. Its ID and label stay.</summary>
    Task<SaveResult> RotateAiKeyAsync(Guid userId, Guid id, SecretValue apiKey, CancellationToken cancellationToken);

    /// <summary>
    /// Replaces a Binance credential's key and secret, validated like a new one, in the same environment.
    /// The new key must belong to the same Binance account, because agents' positions live there.
    /// </summary>
    Task<SaveResult> RotateBinanceKeyAsync(
        Guid userId,
        Guid id,
        BinanceKeyType keyType,
        SecretValue apiKey,
        SecretValue secret,
        string? twoFactorCode,
        CancellationToken cancellationToken);

    Task<RenameOutcome> RenameAsync(Guid userId, Guid id, string label, CancellationToken cancellationToken);

    /// <summary>
    /// Validates a saved credential again, on demand (FR-KEY-04). At most once a minute per credential,
    /// because a circuit never reaches the HTTP limiter and Binance's weight is shared by every user. When
    /// the provider can't be reached, the status stays as it was.
    /// </summary>
    Task<ValidationResult> ValidateAsync(Guid userId, Guid id, CancellationToken cancellationToken);

    /// <summary>Deletes the credential and its secrets. False when there was nothing to delete.</summary>
    Task<bool> DeleteAsync(Guid userId, Guid id, CancellationToken cancellationToken);
}

public enum CredentialKind
{
    Ai,

    /// <summary>Binance, Live or Demo.</summary>
    Exchange,
}

/// <summary>FR-KEY-04's status values. Rows store the name, so a value is never renamed.</summary>
public enum CredentialStatus
{
    /// <summary>Usable: an AI key that lists models, or a Binance key enabled for at least one market.</summary>
    Valid,

    /// <summary>The provider refused it, or a Binance key now has withdrawals enabled.</summary>
    Invalid,

    InsufficientPermissions,

    /// <summary>An AI key saved while its provider couldn't be reached. It isn't used until it's validated.</summary>
    Unverified,
}

/// <summary>Why a credential isn't <see cref="CredentialStatus.Valid"/>. Rows store the name, so a value is never renamed.</summary>
public enum CredentialProblem
{
    /// <summary>The provider refused the key: wrong, revoked, or from an address its IP restriction leaves out.</summary>
    Refused,

    /// <summary>The AI provider recognised the key but refused it access (HTTP 403).</summary>
    PermissionDenied,

    /// <summary>The AI key can't use any model that writes text.</summary>
    NoModels,

    /// <summary>The Binance key can withdraw funds (FR-KEY-05, AC-04).</summary>
    WithdrawalsEnabled,

    /// <summary>The Binance key can trade neither Spot nor Futures.</summary>
    NoTradingPermission,
}

/// <summary>How a Binance key signs (FR-KEY-06). Rows store the name, so a value is never renamed.</summary>
public enum BinanceKeyType
{
    Hmac,
    Ed25519,
}

/// <summary>
/// What FR-KEY-03 lets the browser see: never a secret, only the last four characters of the API key.
/// </summary>
/// <param name="Provider">An AI provider's name, such as <c>OpenAI</c>, or <see cref="CredentialProviders.Binance"/>.</param>
/// <param name="Environment">Binance only.</param>
/// <param name="KeyType">Binance only.</param>
/// <param name="Label">The user's name for it. Anyone with an account chooses this text, so only show it as text.</param>
/// <param name="Problem">Why it isn't valid, when the provider said.</param>
/// <param name="SpotEnabled">Binance only (FR-KEY-02). Null until Binance has said.</param>
/// <param name="FuturesEnabled">Binance only (FR-KEY-02). Null until Binance has said.</param>
/// <param name="IpRestricted">Binance Live only (FR-KEY-05). Binance doesn't document it for Demo, so it stays null there.</param>
/// <param name="ValidatedAt">When a check last reached the provider and got an answer.</param>
public sealed record CredentialRecord(
    Guid Id,
    CredentialKind Kind,
    string Provider,
    TradingEnvironment? Environment,
    BinanceKeyType? KeyType,
    string Label,
    string LastFour,
    CredentialStatus Status,
    CredentialProblem? Problem,
    bool? SpotEnabled,
    bool? FuturesEnabled,
    bool? IpRestricted,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ValidatedAt,
    DateTimeOffset? LastUsedAt);

public static class CredentialProviders
{
    /// <summary>The provider name of every exchange credential.</summary>
    public const string Binance = "Binance";
}

public static class CredentialLimits
{
    /// <summary>The longest label, after control characters are removed.</summary>
    public const int LabelMaxLength = 60;

    /// <summary>The shortest API key or HMAC secret. Every provider's is far longer, and the last four must not be most of it.</summary>
    public const int KeyMinLength = 16;

    public const int KeyMaxLength = 512;

    /// <summary>An Ed25519 private key in PEM is about 120 characters.</summary>
    public const int PrivateKeyMaxLength = 4096;
}

public enum SaveOutcome
{
    /// <summary>Stored. <see cref="SaveResult.Credential"/> says its status, which isn't always Valid.</summary>
    Saved,

    LabelInvalid,

    /// <summary>The key or secret isn't shaped like one, so nothing was sent.</summary>
    KeyMalformed,

    UnknownProvider,

    /// <summary><see cref="SaveResult.TwoFactor"/> says whether the code was wrong or checks are paused.</summary>
    TwoFactorRefused,

    /// <summary>The provider refused the key. Nothing was stored.</summary>
    Refused,

    /// <summary>The Binance key can withdraw funds, which is never accepted (AC-04). Nothing was stored.</summary>
    WithdrawalsEnabled,

    /// <summary>Binance couldn't be asked, so nothing was stored. Try again later.</summary>
    Unreachable,

    /// <summary>
    /// The new key belongs to another Binance account, or Binance didn't say which. To trade on another
    /// account, add it as a credential of its own.
    /// </summary>
    DifferentAccount,

    /// <summary>The credential to rotate doesn't exist, or isn't this user's, or isn't of this kind.</summary>
    NotFound,

    /// <summary>The credential was rotated or deleted meanwhile, so this change wasn't saved.</summary>
    Changed,
}

/// <param name="Credential">The stored credential, when <see cref="Outcome"/> is <see cref="SaveOutcome.Saved"/>.</param>
/// <param name="TwoFactor">The check's result, when <see cref="Outcome"/> is <see cref="SaveOutcome.TwoFactorRefused"/>.</param>
public sealed record SaveResult(SaveOutcome Outcome, CredentialRecord? Credential = null, TwoFactorCheck? TwoFactor = null);

public enum RenameOutcome
{
    Renamed,
    LabelInvalid,
    NotFound,
}

public enum ValidationOutcome
{
    /// <summary>The provider answered, and <see cref="ValidationResult.Credential"/> has the new status.</summary>
    Checked,

    /// <summary>It was checked less than a minute ago.</summary>
    TooSoon,

    /// <summary>The provider couldn't be asked, so nothing changed. Try again later.</summary>
    Unreachable,

    NotFound,
}

/// <param name="Credential">The credential as it is now, unless <see cref="Outcome"/> is <see cref="ValidationOutcome.NotFound"/>.</param>
public sealed record ValidationResult(ValidationOutcome Outcome, CredentialRecord? Credential = null);
