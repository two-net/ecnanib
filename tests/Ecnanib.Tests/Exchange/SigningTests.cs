using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Ecnanib.Kernel;
using Ecnanib.Modules.ExchangeAdapter.Contracts;
using Ecnanib.Tests.Persistence;
using NSec.Cryptography;

namespace Ecnanib.Tests.Exchange;

/// <summary>EXT-BIN-03, FR-KEY-06.</summary>
public sealed class SigningTests(PostgresFixture postgres)
{
    private static CancellationToken Cancellation => ExchangeHarness.Cancellation;

    [Fact]
    public async Task A_signed_request_carries_the_key_a_timestamp_and_an_HMAC_of_exactly_what_was_sent()
    {
        await using var exchange = new ExchangeHarness(postgres);
        exchange.Binance.On(HttpMethod.Get, "/api/v3/account", _ => FakeBinance.Json(BinanceSamples.SpotAccount));
        using var key = new KeyScope(ExchangeHarness.HmacKey());

        await exchange.Account.GetAccountAsync(Market.Spot, key.Key, Cancellation);

        var request = Assert.Single(exchange.Binance.To("/api/v3/account"));
        Assert.Equal(FakeBinance.SpotDemo, request.Host);
        Assert.Equal(ExchangeHarness.ExampleApiKey, request.ApiKey);
        Assert.Equal(
            ["omitZeroBalances", "timestamp", "recvWindow", "signature"],
            request.Parameters.Select(parameter => parameter.Name));
        Assert.Equal("5000", request.Parameter("recvWindow"));

        var expected = Convert.ToHexStringLower(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(ExchangeHarness.ExampleSecret), Encoding.ASCII.GetBytes(request.SignedPayload)));
        Assert.Equal(expected, request.Parameter("signature"));
    }

    [Fact]
    public async Task Values_are_percent_encoded_before_they_are_signed()
    {
        await using var exchange = new ExchangeHarness(postgres);
        const string clientOrderId = "agent:7/cycle-2";
        exchange.Binance.On(HttpMethod.Post, "/api/v3/order", _ => FakeBinance.Json(BinanceSamples.SpotOrder(clientOrderId)));
        using var key = new KeyScope(ExchangeHarness.HmacKey());

        await exchange.Trading.PlaceOrderAsync(
            Market.Spot,
            key.Key,
            new OrderRequest("BTCUSDT", OrderSide.Buy, OrderType.Limit, 0.002m, clientOrderId, RequestPriority.Normal, price: 60000m),
            Cancellation);

        var request = Assert.Single(exchange.Binance.To("/api/v3/order"));
        Assert.Contains("newClientOrderId=agent%3A7%2Fcycle-2&", request.SignedPayload, StringComparison.Ordinal);
        Assert.Equal(clientOrderId, request.Parameter("newClientOrderId"));
        Assert.Equal(
            Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(ExchangeHarness.ExampleSecret), Encoding.ASCII.GetBytes(request.SignedPayload))),
            request.Parameter("signature"));
    }

    [Fact]
    public async Task An_Ed25519_signature_verifies_with_the_public_key()
    {
        await using var exchange = new ExchangeHarness(postgres);
        exchange.Binance.On(HttpMethod.Get, "/fapi/v3/account", _ => FakeBinance.Json(BinanceSamples.FuturesAccount));

        using var privateKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
        var pem = Encoding.ASCII.GetString(privateKey.Export(KeyBlobFormat.PkixPrivateKeyText));
        using var key = new KeyScope(new ExchangeKey(
            Guid.NewGuid(), TradingEnvironment.Live, ExchangeKeyType.Ed25519, SecretValue.FromString("ed25519-api-key"), SecretValue.FromString(pem)));

        await exchange.Account.GetAccountAsync(Market.Futures, key.Key, Cancellation);

        var request = Assert.Single(exchange.Binance.To("/fapi/v3/account"));
        Assert.Equal(FakeBinance.FuturesLive, request.Host);
        var signature = Convert.FromBase64String(request.Parameter("signature")!);
        Assert.True(SignatureAlgorithm.Ed25519.Verify(privateKey.PublicKey, Encoding.ASCII.GetBytes(request.SignedPayload), signature));
    }

    [Fact]
    public async Task An_Ed25519_secret_that_is_not_a_private_key_is_refused_before_anything_is_sent()
    {
        await using var exchange = new ExchangeHarness(postgres);
        using var key = new KeyScope(new ExchangeKey(
            Guid.NewGuid(), TradingEnvironment.Demo, ExchangeKeyType.Ed25519, SecretValue.FromString("api-key"), SecretValue.FromString("not a key")));

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => exchange.Account.GetAccountAsync(Market.Spot, key.Key, Cancellation));

        Assert.DoesNotContain("not a key", exception.ToString(), StringComparison.Ordinal);
        Assert.Empty(exchange.Binance.To("/api/v3/account"));
    }

    [Fact]
    public async Task Public_requests_carry_no_key_timestamp_or_signature_and_Spot_Live_uses_the_market_data_server()
    {
        await using var exchange = new ExchangeHarness(postgres);
        exchange.Binance.On(HttpMethod.Get, "/api/v3/exchangeInfo", _ => FakeBinance.Json(BinanceSamples.SpotExchangeInfo()));

        await exchange.MarketData.GetExchangeInfoAsync(Market.Spot, TradingEnvironment.Live, Cancellation);
        await exchange.MarketData.GetExchangeInfoAsync(Market.Spot, TradingEnvironment.Demo, Cancellation);

        var requests = exchange.Binance.To("/api/v3/exchangeInfo");
        Assert.Equal([FakeBinance.MarketData, FakeBinance.SpotDemo], requests.Select(request => request.Host));
        Assert.All(requests, request =>
        {
            Assert.Null(request.ApiKey);
            Assert.Equal([("showPermissionSets", "false")], request.Parameters);
        });
        Assert.Empty(exchange.Binance.To("/api/v3/time"));
    }

    /// <summary>The caller owns a key's secrets and disposes them.</summary>
    private sealed class KeyScope(ExchangeKey key) : IDisposable
    {
        public ExchangeKey Key { get; } = key;

        public void Dispose()
        {
            Key.ApiKey.Dispose();
            Key.Secret.Dispose();
        }
    }
}
