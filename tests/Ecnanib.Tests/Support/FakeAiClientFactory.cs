using System.Text.Json;
using Ecnanib.Ai.Abstractions;

namespace Ecnanib.Tests.Support;

/// <summary>Stands in for the AI module: model listings are scripted; a handle for another provider is rejected like the real clients do.</summary>
internal sealed class FakeAiClientFactory : IAiClientFactory
{
    private static readonly JsonElement Empty = JsonDocument.Parse("{}").RootElement.Clone();

    /// <summary>Called for every listing; throw to script a failure. Defaults to two models.</summary>
    public Func<AiKeyHandle, CancellationToken, Task<IReadOnlyList<AiModelInfo>>>? OnListModels { get; set; }

    public List<AiKeyHandle> Listings { get; } = [];

    /// <summary>Called for every completion; throw to script a failure. Without it a completion is not supported.</summary>
    public Func<AiKeyHandle, AiRequest, CancellationToken, Task<AiCompletion>>? OnComplete { get; set; }

    /// <summary>Every completion request in order, with the key it was made under.</summary>
    public List<(AiKeyHandle Key, AiRequest Request)> Completions { get; } = [];

    public IAiProviderClient GetClient(AiProvider provider) => new FakeAiProviderClient(this, provider);

    public static AiModelInfo Model(AiProvider provider, string id, string? displayName = null, DateTimeOffset? createdAt = null) =>
        new(provider, id, displayName ?? id, createdAt, 128_000, 8192, true, Empty);

    public static IReadOnlyList<AiModelInfo> DefaultModels(AiProvider provider) => [Model(provider, provider.ToString().ToLowerInvariant() + "-large"), Model(provider, provider.ToString().ToLowerInvariant() + "-small")];

    /// <summary>A canned structured completion of <paramref name="text"/>, complete unless another finish reason is given.</summary>
    public static AiCompletion Completion(AiProvider provider, string model, string text, AiFinishReason finish = AiFinishReason.Stop, int inputTokens = 10, int outputTokens = 5, TimeSpan? latency = null, int? reasoningTokens = null) =>
        new(provider, model, text, finish == AiFinishReason.Stop, finish, finish.ToString().ToLowerInvariant(), new AiTokenUsage(inputTokens, outputTokens, null, reasoningTokens), latency ?? TimeSpan.FromMilliseconds(100), null);

    private Task<AiCompletion> CompleteAsync(AiKeyHandle key, AiRequest request, CancellationToken ct)
    {
        lock (Completions)
            Completions.Add((key, request));
        return OnComplete?.Invoke(key, request, ct) ?? throw new NotSupportedException("Script OnComplete to answer completions.");
    }

    private Task<IReadOnlyList<AiModelInfo>> ListAsync(AiKeyHandle key, CancellationToken ct)
    {
        lock (Listings)
            Listings.Add(key);
        return OnListModels?.Invoke(key, ct) ?? Task.FromResult(DefaultModels(key.Provider));
    }

    private sealed class FakeAiProviderClient(FakeAiClientFactory owner, AiProvider provider) : IAiProviderClient
    {
        public AiProvider Provider => provider;

        public Task<IReadOnlyList<AiModelInfo>> ListModelsAsync(AiKeyHandle key, CancellationToken ct)
        {
            if (key.Provider != provider)
                throw new ArgumentException($"Key {key.KeyId} belongs to {key.Provider} but was passed to the {provider} client.", nameof(key));
            return owner.ListAsync(key, ct);
        }

        public Task<AiCompletion> CompleteAsync(AiKeyHandle key, AiRequest request, CancellationToken ct)
        {
            if (key.Provider != provider)
                throw new ArgumentException($"Key {key.KeyId} belongs to {key.Provider} but was passed to the {provider} client.", nameof(key));
            return owner.CompleteAsync(key, request, ct);
        }
    }
}
