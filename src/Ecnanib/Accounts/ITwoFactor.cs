namespace Ecnanib.Accounts;

/// <summary>
/// Two-factor authentication with an authenticator app: a user turns it on by enrolling the app,
/// and from then on signing in takes a code from the app after the password (FR-ACC-3). Every
/// change is recorded in the audit trail (NFR-LOG-7). See docs/accounts.md.
/// </summary>
/// <remarks>
/// Each method is for the user who is signed in on the request it is given, and fails for a
/// request that is not signed in.
/// </remarks>
public interface ITwoFactor
{
    /// <summary>Whether the user has two-factor authentication on, and what goes with it.</summary>
    Task<TwoFactorState> StateAsync(HttpContext request);

    /// <summary>
    /// A new key for the user's authenticator app, to show on the page that turns two-factor
    /// authentication on. Nothing is stored: the key is kept only when
    /// <see cref="TurnOnAsync"/> is given a code that the app made from it.
    /// </summary>
    TwoFactorSetup NewSetup(HttpContext request);

    /// <summary>
    /// The setup that a form carried back, to show the same key again after a wrong code.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="sealedKey">What <see cref="TwoFactorSetup.SealedKey"/> held when the page was made.</param>
    /// <returns>Null when the text is not a key that the application made for this user.</returns>
    TwoFactorSetup? SetupOf(HttpContext request, string sealedKey);

    /// <summary>
    /// Turns two-factor authentication on with the key of a setup, when the password is the
    /// user's and the code is one that an app made from the key. Every other session of the
    /// account ends, and the user is given recovery codes.
    /// </summary>
    /// <param name="request">The request that posted the form.</param>
    /// <param name="sealedKey">What <see cref="TwoFactorSetup.SealedKey"/> held when the page was made.</param>
    /// <param name="password">
    /// The password of the account. It is passed on its own, so that it is never part of a value
    /// that can be printed or logged.
    /// </param>
    /// <param name="code">The code that the app shows for the key.</param>
    Task<TwoFactorTurnOn> TurnOnAsync(HttpContext request, string sealedKey, string password, string code);

    /// <summary>
    /// Turns two-factor authentication off, when the code is right: the key and the recovery
    /// codes are deleted, and signing in takes the password alone again.
    /// </summary>
    /// <param name="request">The request that posted the form.</param>
    /// <param name="code">A code from the app, or a recovery code.</param>
    Task<TwoFactorChangeOutcome> TurnOffAsync(HttpContext request, string code);

    /// <summary>
    /// Replaces the recovery codes of the user by new ones, when the code is right. The earlier
    /// ones stop working.
    /// </summary>
    /// <param name="request">The request that posted the form.</param>
    /// <param name="code">A code from the app, or a recovery code.</param>
    Task<RecoveryCodesReplacement> ReplaceRecoveryCodesAsync(HttpContext request, string code);
}

/// <summary>Where a user stands with two-factor authentication.</summary>
/// <param name="IsOn">Whether signing in takes a code.</param>
/// <param name="Since">When it was turned on.</param>
/// <param name="RecoveryCodesLeft">How many recovery codes the user has not used.</param>
/// <param name="AddressVerified">
/// Whether the email address of the account is verified. Only then can two-factor authentication
/// be turned on.
/// </param>
public sealed record TwoFactorState(bool IsOn, DateTimeOffset? Since, int RecoveryCodesLeft, bool AddressVerified);

/// <summary>
/// A key for an authenticator app, in the forms the page that turns two-factor authentication on
/// needs. It is a secret, so it is not a record: nothing prints its members.
/// </summary>
/// <param name="key">The key as text for typing into an app, in groups of four.</param>
/// <param name="authenticatorUri">The address that the QR code carries to an app: the key, with the names of the service and the account.</param>
/// <param name="sealedKey">
/// The key, encrypted for this user. The form carries it back, so that the application keeps
/// nothing until the first code is accepted.
/// </param>
public sealed class TwoFactorSetup(string key, string authenticatorUri, string sealedKey)
{
    public string Key { get; } = key;

    public string AuthenticatorUri { get; } = authenticatorUri;

    public string SealedKey { get; } = sealedKey;
}

/// <summary>How turning two-factor authentication on ended.</summary>
public enum TwoFactorTurnOnOutcome
{
    /// <summary>It is on, and every other session of the account has ended.</summary>
    TurnedOn,

    /// <summary>It was on already. Nothing changed.</summary>
    AlreadyOn,

    /// <summary>The email address of the account is not verified, so it cannot be turned on.</summary>
    AddressNotVerified,

    /// <summary>The form did not carry a key that the application made for this user.</summary>
    KeyNotValid,

    /// <summary>The code is not one that an app makes from the key at this time.</summary>
    WrongCode,

    /// <summary>The password is not the password of the account.</summary>
    WrongPassword,
}

/// <summary>
/// The answer to turning two-factor authentication on. It holds the recovery codes, which are
/// secrets, so it is not a record: nothing prints its members.
/// </summary>
/// <param name="outcome">How it ended.</param>
/// <param name="recoveryCodes">The recovery codes of the account, to show once. Empty unless it was turned on.</param>
public sealed class TwoFactorTurnOn(TwoFactorTurnOnOutcome outcome, IReadOnlyList<string> recoveryCodes)
{
    public TwoFactorTurnOnOutcome Outcome { get; } = outcome;

    public IReadOnlyList<string> RecoveryCodes { get; } = recoveryCodes;
}

/// <summary>How a change that takes a code ended.</summary>
public enum TwoFactorChangeOutcome
{
    /// <summary>The change was made.</summary>
    Done,

    /// <summary>Two-factor authentication is not on, so there was nothing to change.</summary>
    NotOn,

    /// <summary>The code is not right. Nothing changed.</summary>
    WrongCode,

    /// <summary>
    /// Every code of the account is refused for <see cref="TwoFactorRules.RefusedFor"/>, after
    /// too many wrong ones. Nothing changed.
    /// </summary>
    CodesRefused,
}

/// <summary>
/// The answer to replacing the recovery codes. It holds the new codes, which are secrets, so it
/// is not a record: nothing prints its members.
/// </summary>
/// <param name="outcome">How it ended.</param>
/// <param name="recoveryCodes">The new recovery codes, to show once. Empty unless they were replaced.</param>
public sealed class RecoveryCodesReplacement(TwoFactorChangeOutcome outcome, IReadOnlyList<string> recoveryCodes)
{
    public TwoFactorChangeOutcome Outcome { get; } = outcome;

    public IReadOnlyList<string> RecoveryCodes { get; } = recoveryCodes;
}
