using Ecnanib.Ai.Abstractions;
using Ecnanib.Keys.Abstractions;
using Ecnanib.Keys.Configuration;
using Microsoft.Extensions.Options;

namespace Ecnanib.Keys.Services;

/// <summary>
/// Proves a provider key by listing the provider's models (FR-KEY-03); the list feeds the catalog (FR-SYNC-02).
/// Bounded by <see cref="KeysOptions.ValidationTimeout"/>. AI clients keep no per-key state, so nothing is released.
/// </summary>
internal sealed class ProviderKeyValidator
{
    private readonly IAiClientFactory _ai;
    private readonly IOptions<KeysOptions> _options;
    private readonly TimeProvider _time;

    public ProviderKeyValidator(IAiClientFactory ai, IOptions<KeysOptions> options, TimeProvider time)
    {
        _ai = ai;
        _options = options;
        _time = time;
    }

    public async Task<ProviderKeyValidation> ValidateAsync(AiProvider provider, AiCredentials credentials, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credentials);

        var handle = new AiKeyHandle(ExchangeKeyValidator.ScratchPrefix + Guid.CreateVersion7().ToString("N"), provider, credentials);
        var timeout = _options.Value.ValidationTimeout;
        using var limit = new CancellationTokenSource(timeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, limit.Token);
        try
        {
            var models = await _ai.GetClient(provider).ListModelsAsync(handle, linked.Token).ConfigureAwait(false);
            return new ProviderKeyValidation(ValidationVerdict.Valid, models);
        }
        catch (AiAuthException ex)
        {
            return new ProviderKeyValidation(ValidationVerdict.Invalid, Reason: ex.Message);
        }
        catch (AiException ex)
        {
            return new ProviderKeyValidation(ValidationVerdict.Failed, Reason: ex.Message);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ProviderKeyValidation(ValidationVerdict.Failed, Reason: KeyReasons.Timeout(ProviderNames.Display(provider), timeout));
        }
    }
}
