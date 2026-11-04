using System.Net;
using System.Net.Http;
using Ecnanib.Kernel;
using Ecnanib.Modules.ExchangeAdapter.Contracts;
using Ecnanib.Tests.Persistence;

namespace Ecnanib.Tests.Exchange;

/// <summary>FR-KEY-05: a Live key's permissions, and the account it belongs to.</summary>
public sealed class ApiRestrictionsTests(PostgresFixture postgres)
{
    private const string Path = "/sapi/v1/account/apiRestrictions";

    private static CancellationToken Cancellation => ExchangeHarness.Cancellation;

    [Fact]
    public async Task A_Live_key_s_restrictions_are_read_with_a_signed_request_to_Spot_Live()
    {
        await using var exchange = new ExchangeHarness(postgres);
        exchange.Binance.On(HttpMethod.Get, Path, _ => FakeBinance.Json(BinanceSamples.ApiRestrictions(ipRestrict: false, withdrawals: true, futures: false)));
        var key = ExchangeHarness.HmacKey(TradingEnvironment.Live);

        var restrictions = await exchange.Account.GetApiRestrictionsAsync(key, Cancellation);

        Assert.Equal(new ApiRestrictions(IpRestricted: false, Reading: true, Withdrawals: true, SpotAndMarginTrading: true, Futures: false), restrictions);

        var request = Assert.Single(exchange.Binance.To(Path));
        Assert.Equal(FakeBinance.SpotLive, request.Host);
        Assert.Equal(ExchangeHarness.ExampleApiKey, request.ApiKey);
        Assert.Equal(["timestamp", "recvWindow", "signature"], request.Parameters.Select(parameter => parameter.Name));
    }

    [Fact]
    public async Task A_Demo_key_is_refused_before_anything_is_sent()
    {
        await using var exchange = new ExchangeHarness(postgres);

        await Assert.ThrowsAsync<ArgumentException>(
            () => exchange.Account.GetApiRestrictionsAsync(ExchangeHarness.HmacKey(TradingEnvironment.Demo), Cancellation));

        Assert.Empty(exchange.Binance.Requests);
    }

    [Fact]
    public async Task A_key_Binance_refuses_is_rejected_with_its_code()
    {
        await using var exchange = new ExchangeHarness(postgres);
        exchange.Binance.On(
            HttpMethod.Get, Path, _ => FakeBinance.Error(HttpStatusCode.Unauthorized, -2015, "Invalid API-key, IP, or permissions for action."));

        var exception = await Assert.ThrowsAsync<ExchangeException>(
            () => exchange.Account.GetApiRestrictionsAsync(ExchangeHarness.HmacKey(TradingEnvironment.Live), Cancellation));

        Assert.Equal((ExchangeFailure.Rejected, -2015), (exception.Failure, exception.Code));
    }

    [Fact]
    public async Task The_Spot_account_says_which_account_it_is()
    {
        await using var exchange = new ExchangeHarness(postgres);
        exchange.Binance.On(HttpMethod.Get, "/api/v3/account", _ => FakeBinance.Json(BinanceSamples.SpotAccount));
        exchange.Binance.On(HttpMethod.Get, "/fapi/v3/account", _ => FakeBinance.Json(BinanceSamples.FuturesAccount));
        var key = ExchangeHarness.HmacKey();

        var spot = await exchange.Account.GetAccountAsync(Market.Spot, key, Cancellation);
        var futures = await exchange.Account.GetAccountAsync(Market.Futures, key, Cancellation);

        Assert.Equal(354937868, spot.Uid);
        Assert.Null(futures.Uid);
    }
}
