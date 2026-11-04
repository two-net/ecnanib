using Ecnanib.Modules.Credentials.Contracts;

namespace Ecnanib.Modules.Credentials;

/// <summary>What a provider said about a key, or that it couldn't be asked.</summary>
/// <param name="Reached">False when the provider couldn't be asked, so nothing is known.</param>
/// <param name="Uid">Binance's ID for the account, when the Spot account said.</param>
internal sealed record Verdict(
    bool Reached,
    CredentialStatus Status,
    CredentialProblem? Problem = null,
    bool? SpotEnabled = null,
    bool? FuturesEnabled = null,
    bool? IpRestricted = null,
    long? Uid = null)
{
    public static Verdict Unreachable { get; } = new(Reached: false, CredentialStatus.Unverified);

    public static Verdict Refused { get; } = new(Reached: true, CredentialStatus.Invalid, CredentialProblem.Refused);

    /// <summary>Whether it must never be stored: the provider refused it, or it can withdraw funds (AC-04).</summary>
    public bool Unacceptable => Reached && Status == CredentialStatus.Invalid;
}
