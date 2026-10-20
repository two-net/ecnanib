using Ecnanib.Ai.Abstractions;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Keys.Abstractions;

namespace Ecnanib.Tests.Support;

/// <summary>Stands in for the keys module's resolver: provider keys are scripted per provider; exchange keys are not supported.</summary>
internal sealed class FakeKeyResolver : IKeyResolver
{
    public const string ApiKey = "sk-test-SECRET-ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

    public Dictionary<AiProvider, KeyResolution<AiKeyHandle>> Providers { get; } = [];

    public List<(Guid UserId, AiProvider Provider)> Resolutions { get; } = [];

    /// <summary>Every provider resolved with the same key value.</summary>
    public static FakeKeyResolver WithAllProviders(string apiKey = ApiKey)
    {
        var resolver = new FakeKeyResolver();
        foreach (var provider in Enum.GetValues<AiProvider>())
            resolver.Providers[provider] = new KeyResolution<AiKeyHandle>(KeyResolutionStatus.Resolved, new AiKeyHandle("key-" + provider.ToString().ToLowerInvariant(), provider, new AiCredentials(apiKey)));
        return resolver;
    }

    public Task<KeyResolution<ExchangeKeyHandle>> ResolveExchangeKeyAsync(Guid userId, Guid keyId, MarketType market, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<KeyResolution<AiKeyHandle>> ResolveProviderKeyAsync(Guid userId, AiProvider provider, CancellationToken cancellationToken)
    {
        lock (Resolutions)
            Resolutions.Add((userId, provider));
        return Task.FromResult(Providers.TryGetValue(provider, out var resolution)
            ? resolution
            : new KeyResolution<AiKeyHandle>(KeyResolutionStatus.Missing, null, $"No {ProviderNames.Display(provider)} key."));
    }
}
