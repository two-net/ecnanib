using Ecnanib.Shared;

namespace Ecnanib.ExchangeGateway;

/// <summary>
/// The common interface the exchange sits behind, so the rest of the application does not depend
/// on Binance's request formats. Implementations live under <c>ExchangeGateway/Adapters</c>.
/// Operations that place orders belong in the <c>ExchangeGateway.Trading</c> namespace, which
/// only the Decision Engine may use.
/// </summary>
public interface IExchangeAdapter
{
    /// <summary>Reads the exchange's clock for one market and environment.</summary>
    Task<DateTimeOffset> GetServerTimeAsync(Market market, TradingMode mode, CancellationToken cancellationToken);
}
