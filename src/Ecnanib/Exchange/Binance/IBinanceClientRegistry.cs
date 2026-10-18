using Binance.Net.Interfaces.Clients;
using Ecnanib.Exchange.Abstractions;

namespace Ecnanib.Exchange.Binance;

/// <summary>
/// Owns every Binance.Net client: one keyless REST and socket client per environment for public data (EX-06), and one
/// authenticated pair per exchange key (EX-04). The only component that ever reads an API secret.
/// </summary>
internal interface IBinanceClientRegistry
{
    IBinanceRestClient GetPublicRest(ExchangeEnvironment environment);

    IBinanceSocketClient GetPublicSocket(ExchangeEnvironment environment);

    IBinanceRestClient GetKeyedRest(ExchangeKeyHandle key);

    IBinanceSocketClient GetKeyedSocket(ExchangeKeyHandle key);

    bool HasKey(string keyId);

    /// <summary>Unsubscribes and disposes the key's clients. Unknown ids are ignored.</summary>
    Task ReleaseKeyAsync(string keyId, CancellationToken ct);
}
