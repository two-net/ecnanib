using Ecnanib.Modules.AiAdapters.Contracts;

namespace Ecnanib.Modules.AiAdapters;

/// <summary>Both AI contracts, over <see cref="AiTransport"/>, so every provider is called the same way (EXT-AI-05).</summary>
internal sealed class AiGateway(AiTransport transport) : IAiProviders, IAiCompletions
{
    /// <summary>Far beyond any provider's list today. A provider that keeps giving pages has gone wrong.</summary>
    private const int MaximumModelPages = 20;

    public IReadOnlyList<AiProviderInfo> All => transport.Providers;

    public async Task<IReadOnlyList<AiModel>> ListModelsAsync(AiKey key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);

        var call = new AiCall(key, AiOperation.ListModels, Model: null, Role: null, Price: null);
        var models = new List<AiModel>();
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        string? token = null;

        for (var page = 1; ; page++)
        {
            var pageToken = token;
            var sent = await transport.SendAsync(
                call,
                adapter => adapter.ListModels(key.ApiKey, pageToken),
                (adapter, body) => new Parsed<AiModelPage>(adapter.ReadModels(body), AiUsage.Zero, Refused: false),
                cancellationToken);

            models.AddRange(sent.Value.Models);
            token = string.IsNullOrEmpty(sent.Value.NextPageToken) ? null : sent.Value.NextPageToken;

            if (token is null)
            {
                return models;
            }

            // A partial list would make the catalog deprecate models that are still there (FR-CAT-04).
            if (!tokens.Add(token))
            {
                throw new AiException(key.Provider, AiFailure.Other, $"{key.Provider} repeated a page of its model list, so the list was abandoned.")
                {
                    Attempts = sent.Attempts,
                };
            }

            if (page == MaximumModelPages)
            {
                throw new AiException(key.Provider, AiFailure.Other, $"{key.Provider} listed more than {MaximumModelPages} pages of models, so the list was abandoned.")
                {
                    Attempts = sent.Attempts,
                };
            }
        }
    }

    public async Task<AiResponse> CompleteAsync(AiKey key, AiRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(request);

        var call = new AiCall(key, AiOperation.Complete, request.Model, request.Role, request.Price);
        var sent = await transport.SendAsync(
            call,
            adapter => adapter.Complete(key.ApiKey, request),
            (adapter, body) =>
            {
                var completion = adapter.ReadCompletion(body);

                return new Parsed<AiCompletion>(completion, completion.Usage, completion.Refused);
            },
            cancellationToken);

        return new AiResponse(sent.Value.Text, sent.Usage, sent.Cost, sent.Value.Truncated, sent.Attempts, sent.Latency);
    }
}
