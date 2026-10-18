namespace Ecnanib.Exchange.Abstractions;

/// <summary>
/// Entry point of the exchange module. Public data clients are shared per environment and market; account clients
/// are cached per key id and must be released when the key is deleted or rotated (FR-KEY-06).
/// </summary>
public interface IExchangeClientFactory
{
    IPublicMarketData GetPublicData(ExchangeEnvironment environment, MarketType market);

    ISpotAccountClient GetSpotClient(ExchangeKeyHandle key);

    IFuturesAccountClient GetFuturesClient(ExchangeKeyHandle key);

    /// <summary>Closes the key's user-data streams and drops its cached clients. Unknown ids are ignored.</summary>
    Task ReleaseAsync(string keyId, CancellationToken ct);
}
