using Ecnanib.Exchange.Abstractions;

namespace Ecnanib.Tests.Exchange.Abstractions;

/// <summary>NFR-SEC-03: what the status page shows about a failure is its kind, never a message that could name a key, an agent or a symbol.</summary>
public class ErrorSummaryTests
{
    [Fact]
    public void NamesTheKindAndNeverTheMessage()
    {
        var agentId = Guid.NewGuid();

        Assert.Equal("TimeoutException", ErrorSummary.Describe(new TimeoutException($"agent {agentId} on BTCUSDT timed out")));
        Assert.Equal("Unauthorized (-2015) from the exchange", ErrorSummary.Describe(new ExchangeAuthException("Invalid API-key, IP, or permissions for action.", -2015)));
        Assert.Equal("RateLimited from the exchange", ErrorSummary.Describe(new ExchangeRateLimitedException("Too many requests; retry later", TimeSpan.FromSeconds(5), null, false)));
        Assert.Equal("InvalidOperationException", ErrorSummary.Describe(new AggregateException(new InvalidOperationException($"key {Guid.NewGuid()} is gone"))));
        Assert.Equal("AggregateException", ErrorSummary.Describe(new AggregateException(new TimeoutException("a"), new TimeoutException("b"))));
        Assert.DoesNotContain(agentId.ToString(), ErrorSummary.Describe(new TimeoutException($"agent {agentId}")));
    }
}
