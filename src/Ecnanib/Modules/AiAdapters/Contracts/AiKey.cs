using Ecnanib.Kernel;

namespace Ecnanib.Modules.AiAdapters.Contracts;

/// <summary>
/// A user's AI provider key, decrypted for one use (FR-KEY-09). The caller owns the secret and disposes
/// it once the call returns. An adapter reveals it only to set a request's header, and nothing keeps it.
/// A class, not a record, so that its ToString can't show the secret.
/// </summary>
/// <param name="provider">The provider's name, exactly as its adapter gives it, such as "OpenAI".</param>
/// <param name="credentialId">The credential the key belongs to, which concurrent calls are limited by (EXT-AI-03).</param>
public sealed class AiKey(string provider, Guid credentialId, SecretValue apiKey)
{
    public string Provider { get; } = !string.IsNullOrWhiteSpace(provider)
        ? provider
        : throw new ArgumentException("A provider is required.", nameof(provider));

    public Guid CredentialId { get; } = credentialId;

    public SecretValue ApiKey { get; } = apiKey ?? throw new ArgumentNullException(nameof(apiKey));

    public override string ToString() => $"{nameof(AiKey)} {Provider} {CredentialId}";
}
