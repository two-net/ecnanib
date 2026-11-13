namespace Ecnanib.Shared;

/// <summary>
/// What an AI provider or the exchange said about a key, when it was asked with a call that only
/// reads (FR-KEY-3). Both gateways answer with it, and neither may use the other.
/// </summary>
public enum KeyVerdict
{
    /// <summary>It took the key.</summary>
    Accepted,

    /// <summary>It refused the key: the key is wrong, was revoked, or may not be used from here.</summary>
    Refused,

    /// <summary>
    /// It said nothing about the key: it could not be reached, took too long, or answered with
    /// something that is not a verdict, such as a limit on requests. The key is neither good nor
    /// bad for it, and is asked about again later.
    /// </summary>
    Unreachable,
}
