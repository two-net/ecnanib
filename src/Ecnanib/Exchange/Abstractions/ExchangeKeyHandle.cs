namespace Ecnanib.Exchange.Abstractions;

/// <summary>
/// Identifies a stored exchange key (FR-KEY-01). <paramref name="KeyId"/> is the stable identifier of the user's
/// ExchangeKey entity; the environment is fixed per key (EX-04).
/// </summary>
public sealed record ExchangeKeyHandle(string KeyId, ExchangeEnvironment Environment, ExchangeCredentials Credentials)
{
    public string KeyId { get; } = !string.IsNullOrWhiteSpace(KeyId)
        ? KeyId
        : throw new ArgumentException("Key id is required.", nameof(KeyId));

    public ExchangeCredentials Credentials { get; } = Credentials ?? throw new ArgumentNullException(nameof(Credentials));
}
