using Ecnanib.Ai.Abstractions;
using Ecnanib.Exchange.Abstractions;

namespace Ecnanib.Keys.Abstractions;

/// <summary>
/// Turns a user's stored key into the handle the exchange or AI module needs (AI-03, FR-KEY-05). Only a Valid key
/// resolves; a missing or invalid one is a status with a reason, so a feature that needs it can say why it is
/// unavailable (FR-KEY-08). Handles are the only objects that carry key values out of this module.
/// </summary>
public interface IKeyResolver
{
    Task<KeyResolution<ExchangeKeyHandle>> ResolveExchangeKeyAsync(Guid userId, Guid keyId, MarketType market, CancellationToken cancellationToken);

    Task<KeyResolution<AiKeyHandle>> ResolveProviderKeyAsync(Guid userId, AiProvider provider, CancellationToken cancellationToken);
}
