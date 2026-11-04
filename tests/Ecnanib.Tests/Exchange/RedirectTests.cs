using Ecnanib.Kernel;
using Ecnanib.Modules.ExchangeAdapter.Contracts;
using Ecnanib.Tests.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Exchange;

/// <summary>
/// The adapter's real HTTP handler doesn't follow a redirect, which could otherwise send a signed request
/// to a server the configuration never named (NFR-SEC-10). A fake handler can't show this, because
/// redirects are followed inside the real one, so this runs against a socket on loopback.
/// </summary>
public sealed class RedirectTests(PostgresFixture postgres)
{
    [Fact]
    public async Task A_redirect_is_not_followed()
    {
        var cancellation = TestContext.Current.CancellationToken;
        LoopbackServer? server = null;
        using var redirecting = server = new LoopbackServer(_ =>
            $"HTTP/1.1 302 Found\r\nLocation: {server!.BaseUrl}/elsewhere\r\nContent-Length: 0\r\n\r\n");
        await using var app = new AppFactory(
            postgres.NewConnectionString(),
            settings: new Dictionary<string, string?> { ["Binance:Spot:DemoBaseUrl"] = server.BaseUrl });
        app.Start();

        var exception = await Assert.ThrowsAsync<ExchangeException>(() => app.Services.GetRequiredService<IExchangeMarketData>()
            .GetExchangeInfoAsync(Market.Spot, TradingEnvironment.Demo, cancellation));

        Assert.Equal(ExchangeFailure.Unknown, exception.Failure);
        Assert.Contains("HTTP 302", exception.Message, StringComparison.Ordinal);
        Assert.StartsWith("GET /api/v3/exchangeInfo?showPermissionSets=false HTTP/1.1\n", Assert.Single(server.Requests), StringComparison.Ordinal);
    }
}
