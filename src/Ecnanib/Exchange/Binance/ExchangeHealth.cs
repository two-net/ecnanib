using Ecnanib.Exchange.Abstractions;
using Ecnanib.Exchange.Streaming;

namespace Ecnanib.Exchange.Binance;

internal sealed class ExchangeHealth : IExchangeHealth
{
    private readonly MarketStreamHub _marketStreams;
    private readonly UserDataStreamHub _userDataStreams;
    private readonly BinanceRateLimitMonitor _rateLimits;

    public ExchangeHealth(MarketStreamHub marketStreams, UserDataStreamHub userDataStreams, BinanceRateLimitMonitor rateLimits)
    {
        _marketStreams = marketStreams;
        _userDataStreams = userDataStreams;
        _rateLimits = rateLimits;
    }

    public IReadOnlyList<StreamStatus> MarketStreams => _marketStreams.GetStatuses();

    public IReadOnlyList<StreamStatus> UserDataStreams => _userDataStreams.GetStatuses();

    public RateLimitSnapshot RateLimits => _rateLimits.Snapshot();
}
