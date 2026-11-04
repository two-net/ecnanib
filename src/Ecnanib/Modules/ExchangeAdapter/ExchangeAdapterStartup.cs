using Ecnanib.Modules.ExchangeAdapter.Contracts;

namespace Ecnanib.Modules.ExchangeAdapter;

/// <summary>
/// Says at startup which Binance servers are configured. The host builds every hosted service before it
/// starts any, and this one takes <see cref="BinanceSettings"/>, so an invalid setting stops startup
/// before any migration runs (CFG-03). It logs under its own name, because the Exchange category is for
/// exchange calls, each under the correlation ID of the work that made it.
/// </summary>
internal sealed class ExchangeAdapterStartup(BinanceSettings settings, ILogger<ExchangeAdapterStartup> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Binance servers: Spot Live {SpotLive}, Spot Demo {SpotDemo}, Futures Live {FuturesLive}, Futures Demo {FuturesDemo}, "
            + "market data {MarketData}. Receive window {RecvWindowMs} ms, weight headroom {WeightHeadroomPercent}%",
            settings.BaseUrls[ExchangeServer.SpotLive].Host,
            settings.BaseUrls[ExchangeServer.SpotDemo].Host,
            settings.BaseUrls[ExchangeServer.FuturesLive].Host,
            settings.BaseUrls[ExchangeServer.FuturesDemo].Host,
            settings.BaseUrls[ExchangeServer.SpotMarketData].Host,
            settings.RecvWindowMs,
            settings.WeightHeadroomPercent);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
