using Ecnanib.Kernel;

namespace Ecnanib.Modules.ExchangeAdapter.Contracts;

/// <summary>How a Binance key signs requests (FR-KEY-06).</summary>
public enum ExchangeKeyType
{
    /// <summary>The secret is the key's HMAC secret.</summary>
    Hmac,

    /// <summary>
    /// The secret is the Ed25519 private key in PKCS#8 PEM, as Binance's key generator writes it. Binance
    /// documents Ed25519 for Spot. Its Futures documentation names only HMAC and RSA.
    /// </summary>
    Ed25519,
}

/// <summary>
/// A user's Binance key, decrypted for one use (FR-KEY-09). The caller owns the secrets and disposes
/// them once the call returns. The adapter reveals them only to sign a request and set its header, and
/// never keeps them. A class, not a record, so that its ToString can't show a secret.
/// </summary>
/// <param name="credentialId">The credential the key belongs to, which order counts are tracked by (EXT-BIN-04).</param>
/// <param name="environment">Which servers the key belongs to (FR-KEY-02).</param>
public sealed class ExchangeKey(
    Guid credentialId,
    TradingEnvironment environment,
    ExchangeKeyType type,
    SecretValue apiKey,
    SecretValue secret)
{
    public Guid CredentialId { get; } = credentialId;

    public TradingEnvironment Environment { get; } = environment;

    public ExchangeKeyType Type { get; } = type;

    public SecretValue ApiKey { get; } = apiKey ?? throw new ArgumentNullException(nameof(apiKey));

    public SecretValue Secret { get; } = secret ?? throw new ArgumentNullException(nameof(secret));

    public override string ToString() => $"{nameof(ExchangeKey)} {CredentialId}";
}
