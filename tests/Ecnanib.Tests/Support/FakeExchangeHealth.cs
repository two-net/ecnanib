using Ecnanib.Exchange.Abstractions;

namespace Ecnanib.Tests.Support;

/// <summary>Stands in for the exchange module's health view with whatever streams and limits a test sets.</summary>
internal sealed class FakeExchangeHealth : IExchangeHealth
{
    public IReadOnlyList<StreamStatus> MarketStreams { get; set; } = [];

    public IReadOnlyList<StreamStatus> UserDataStreams { get; set; } = [];

    public RateLimitSnapshot RateLimits { get; set; } = RateLimitSnapshot.Empty;
}
