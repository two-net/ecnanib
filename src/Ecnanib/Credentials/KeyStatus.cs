namespace Ecnanib.Credentials;

/// <summary>
/// What the last check of a stored key found, when its provider or the exchange was asked about
/// it with a call that only reads (FR-KEY-3). A member is stored by its name, so renaming one
/// changes stored data and needs a migration. See docs/credentials.md.
/// </summary>
public enum KeyStatus
{
    /// <summary>
    /// The key was never checked: it was stored before keys were checked. The job that checks
    /// the keys takes it with its next run. A key that is stored now is checked first, and is
    /// not stored without an answer.
    /// </summary>
    Unchecked,

    /// <summary>The provider, or the exchange, took the key when it was last asked.</summary>
    Valid,

    /// <summary>
    /// The provider, or the exchange, refused the key when it was last asked: the key was
    /// revoked, or may no longer be used from here. An agent that depends on it moves to
    /// Attention required (FR-KEY-10). The key is asked about again daily, and is valid again
    /// when it is taken.
    /// </summary>
    Invalid,
}
