namespace Ecnanib.Ai.Abstractions;

/// <summary>
/// Identifies a stored provider key (FR-KEY-03). <paramref name="KeyId"/> is the stable identifier of the user's
/// ProviderKey entity; a key belongs to exactly one provider and is only ever sent there (FR-KEY-05).
/// </summary>
public sealed record AiKeyHandle(string KeyId, AiProvider Provider, AiCredentials Credentials)
{
    public string KeyId { get; } = !string.IsNullOrWhiteSpace(KeyId)
        ? KeyId
        : throw new ArgumentException("Key id is required.", nameof(KeyId));

    public AiCredentials Credentials { get; } = Credentials ?? throw new ArgumentNullException(nameof(Credentials));
}
