using System.Collections.Concurrent;
using Binance.Net;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Exchange.Configuration;
using Microsoft.Extensions.Options;

namespace Ecnanib.Exchange.Binance;

/// <summary>
/// Maps <see cref="ExchangeEnvironment"/> to a Binance.Net environment built from the configured endpoints (EX-04,
/// Appendix D). Names are prefixed so they never collide with the library's presets.
/// </summary>
internal sealed class BinanceEnvironmentFactory
{
    public const string ProductionName = "ecnanib-production";
    public const string TestnetName = "ecnanib-testnet";

    private readonly IOptions<ExchangeOptions> _options;
    private readonly ConcurrentDictionary<ExchangeEnvironment, BinanceEnvironment> _cache = new();

    public BinanceEnvironmentFactory(IOptions<ExchangeOptions> options)
    {
        _options = options;
    }

    public BinanceEnvironment Get(ExchangeEnvironment environment) => _cache.GetOrAdd(environment, Build);

    private BinanceEnvironment Build(ExchangeEnvironment environment)
    {
        var e = _options.Value.EndpointsFor(environment);
        return BinanceEnvironment.CreateCustom(
            environment == ExchangeEnvironment.Production ? ProductionName : TestnetName,
            e.SpotRest,
            e.SpotStreams,
            e.SpotWsApi,
            riskDataSocketAddress: null!,
            e.FuturesRest,
            e.FuturesStreams,
            string.IsNullOrWhiteSpace(e.FuturesWsApi) ? null! : e.FuturesWsApi,
            coinFuturesRestAddress: null!,
            coinFuturesSocketAddress: null!,
            coinFuturesSocketApiAddress: null!);
    }
}
