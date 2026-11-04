using System.Net;
using System.Text.Json;
using Ecnanib.Kernel;
using Ecnanib.Modules.ExchangeAdapter.Contracts;
using Ecnanib.Tests.Identity;
using Ecnanib.Tests.Logging;
using Ecnanib.Tests.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Exchange;

/// <summary>FR-LOG-08, FR-KEY-09.</summary>
public sealed class ExchangeLoggingTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Each_call_is_logged_with_its_endpoint_weight_status_latency_and_client_order_and_never_its_key_or_signature()
    {
        var cancellation = TestContext.Current.CancellationToken;

        // Shorter than 64 characters, so masking by shape can't hide a leak.
        var apiKey = CanaryCases.NewCanary();
        var secret = CanaryCases.NewCanary();
        var clock = new TestClock();
        var binance = new FakeBinance(clock);
        binance.On(HttpMethod.Post, "/api/v3/order", request => FakeBinance.Json(
            BinanceSamples.SpotOrder(request.Parameter("newClientOrderId")!),
            headers: ("X-MBX-USED-WEIGHT-1M", "42")));
        binance.On(HttpMethod.Get, "/api/v3/account", _ => FakeBinance.Error(
            HttpStatusCode.Unauthorized,
            -2015,
            "Invalid API-key, IP, or permissions for action."));

        await using var app = new LoggedApp(postgres.NewConnectionString(), configureServices: services => ExchangeHarness.Use(services, binance, clock));
        using var apiKeyValue = SecretValue.FromString(apiKey);
        using var secretValue = SecretValue.FromString(secret);
        var key = new ExchangeKey(Guid.NewGuid(), TradingEnvironment.Demo, ExchangeKeyType.Hmac, apiKeyValue, secretValue);

        await app.App.Services.GetRequiredService<IExchangeTrading>().PlaceOrderAsync(
            Market.Spot,
            key,
            new OrderRequest("BTCUSDT", OrderSide.Buy, OrderType.Limit, 0.002m, "a1-c2-entry", RequestPriority.Normal, price: 60000m),
            cancellation);
        await Assert.ThrowsAsync<ExchangeException>(
            () => app.App.Services.GetRequiredService<IExchangeAccount>().GetAccountAsync(Market.Spot, key, cancellation));
        var signatures = binance.Requests.Select(request => request.Parameter("signature")).OfType<string>().ToList();
        await app.StopAsync();

        var events = app.FileEvents().Where(entry => Property(entry, "SourceContext") == "Exchange").ToList();
        var order = Assert.Single(events, entry => Property(entry, "Endpoint") == "/api/v3/order");
        Assert.Equal(
            ("Information", "SpotDemo", "POST", "200", "42", "6000", "a1-c2-entry"),
            (order.GetProperty("Level").GetString(), Property(order, "Server"), Property(order, "Method"), Property(order, "StatusCode"),
                Property(order, "UsedWeight"), Property(order, "WeightLimit"), Property(order, "ClientOrderId")));
        Assert.True(order.GetProperty("Properties").GetProperty("LatencyMs").GetDouble() >= 0);

        var rejected = Assert.Single(events, entry => Property(entry, "Endpoint") == "/api/v3/account");
        Assert.Equal(("Warning", "401", "-2015"), (rejected.GetProperty("Level").GetString(), Property(rejected, "StatusCode"), Property(rejected, "ErrorCode")));
        Assert.Single(events, entry => Property(entry, "Endpoint") == "/api/v3/time");

        var everything = app.FileText() + await app.TableTextAsync(cancellation);
        Assert.Equal(2, signatures.Count);
        Assert.All(new[] { apiKey, secret }.Concat(signatures), value => Assert.DoesNotContain(value, everything, StringComparison.Ordinal));
        Assert.DoesNotContain("recvWindow", everything, StringComparison.Ordinal);
    }

    private static string? Property(JsonElement entry, string name) =>
        entry.GetProperty("Properties").TryGetProperty(name, out var value)
            ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText()
            : null;
}
