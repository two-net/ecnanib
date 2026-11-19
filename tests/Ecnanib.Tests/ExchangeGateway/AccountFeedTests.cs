using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ecnanib.ExchangeGateway;
using Ecnanib.Shared;
using Ecnanib.Tests.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Ecnanib.Tests.ExchangeGateway;

/// <summary>
/// Order and position changes are tracked through the exchange's account event feed where
/// available (FR-BIN-7). The gateway follows the account of a credential where the configuration
/// file names the address of the feed, and says of every event which symbol it is about, and
/// nothing else of it. It keeps no key (FR-KEY-9), and sends nothing on a feed but the one
/// request that subscribes (FR-BIN-9, NFR-SEC-6). See "The account event feed" in docs/exchange.md.
/// </summary>
public class AccountFeedTests
{
    private const string Area = "Ecnanib.ExchangeGateway.Adapters.Binance.BinanceFeed";

    private static readonly Guid Ada = Guid.Parse("11111111-1111-7111-8111-111111111111");
    private static readonly Guid Grace = Guid.Parse("22222222-2222-7222-8222-222222222222");

    [Fact]
    public async Task An_account_of_the_Futures_market_is_followed_with_a_stream_that_its_API_key_begins()
    {
        using var service = new FeedService();
        var (apiKey, secretKey) = BinanceCalls.CredentialOf(TradingMode.Demo);

        Assert.True(service.Feed.IsAvailable(Market.Futures, TradingMode.Demo));
        Assert.Equal(ExchangeFeedState.NotFollowed, service.Feed.StateOf(Ada, Market.Futures, TradingMode.Demo));

        Assert.Equal(ExchangeFeedOutcome.Following, await service.FollowAsync(Ada, Market.Futures));

        // One call begins the stream: it carries the API key in its header, and nothing else,
        // neither a signature nor a parameter, and no body.
        var began = Assert.Single(service.Binance.Calls);
        Assert.Equal(("POST", $"{BinanceCalls.AddressOf(Market.Futures, TradingMode.Demo)}/fapi/v1/listenKey"), (began.Method, began.Endpoint));
        Assert.Equal(apiKey, began.Headers["X-MBX-APIKEY"]);
        Assert.Empty(began.Query);
        Assert.Null(began.Body);

        // The stream is read at the address of the feed, under the key that Binance named it with.
        var session = await service.Server.NextAsync();
        Assert.Equal($"/private/ws/{BinanceCalls.StreamKey}", session.Path);

        // What happened in the account before was not heard: that is a change of everything in it.
        Assert.Equal([new ExchangeAccountChange(Ada, Market.Futures, TradingMode.Demo, Symbol: null)], await service.TakenAsync(1));
        Assert.Equal(ExchangeFeedState.Followed, service.Feed.StateOf(Ada, Market.Futures, TradingMode.Demo));

        // Nothing is sent on the feed of the Futures market, and the secret key went nowhere.
        Assert.Empty(session.Received);
        Assert.DoesNotContain(secretKey, service.Application.LogText());
    }

    [Theory]
    [InlineData("""{ "e": "ORDER_TRADE_UPDATE", "E": 1, "T": 1, "o": { "s": "BTCUSDT", "c": "abc", "S": "SELL", "X": "FILLED", "z": "0.02" } }""", "BTCUSDT")]
    [InlineData("""{ "e": "ALGO_UPDATE", "E": 1, "T": 1, "o": { "caid": "abc", "aid": 7, "s": "SOLUSDT", "X": "TRIGGERED" } }""", "SOLUSDT")]
    [InlineData("""{ "e": "TRADE_LITE", "E": 1, "T": 1, "s": "BTCUSDT", "q": "0.02", "p": "60000", "S": "SELL" }""", "BTCUSDT")]
    [InlineData("""{ "e": "ACCOUNT_UPDATE", "E": 1, "T": 1, "a": { "m": "ORDER", "B": [], "P": [ { "s": "BTCUSDT", "pa": "0" }, { "s": "SOLUSDT", "pa": "1" } ] } }""", "BTCUSDT,SOLUSDT")]
    [InlineData("""{ "e": "ACCOUNT_UPDATE", "E": 1, "T": 1, "a": { "m": "FUNDING_FEE", "S": "BTCUSDT", "B": [], "P": [] } }""", "BTCUSDT")]
    [InlineData("""{ "stream": "x", "data": { "e": "ORDER_TRADE_UPDATE", "o": { "s": "BTCUSDT" } } }""", "BTCUSDT")]
    public async Task An_event_of_the_Futures_market_says_which_symbol_something_changed_in_and_nothing_of_what(string said, string symbols)
    {
        using var service = new FeedService();
        var session = await service.FollowedAsync(Ada, Market.Futures);

        await session.SayAsync(said);

        Assert.Equal(
            symbols.Split(',').Select(symbol => new ExchangeAccountChange(Ada, Market.Futures, TradingMode.Demo, symbol)),
            (await service.TakenAsync(symbols.Split(',').Length)).OrderBy(change => change.Symbol));
    }

    [Theory]
    [InlineData("""{ "subscriptionId": 0, "event": { "e": "executionReport", "E": 1, "s": "BTCUSDT", "c": "abc", "X": "FILLED", "z": "0.01" } }""")]
    [InlineData("""{ "subscriptionId": 0, "event": { "e": "listStatus", "E": 1, "s": "BTCUSDT", "g": 2, "l": "ALL_DONE", "O": [] } }""")]
    public async Task An_event_of_the_Spot_market_says_which_symbol_something_changed_in(string said)
    {
        using var service = new FeedService();
        var session = await service.FollowedAsync(Ada, Market.Spot);

        await session.SayAsync(said);

        Assert.Equal([new ExchangeAccountChange(Ada, Market.Spot, TradingMode.Demo, "BTCUSDT")], await service.TakenAsync(1));
    }

    [Fact]
    public async Task What_is_said_often_before_it_is_taken_is_one_change()
    {
        using var service = new FeedService();
        var session = await service.FollowedAsync(Ada, Market.Futures);

        // One execution is several events at Binance: of the order, of the trade, and of the account.
        await session.SayAsync("""{ "e": "ORDER_TRADE_UPDATE", "o": { "s": "BTCUSDT", "X": "PARTIALLY_FILLED" } }""");
        await session.SayAsync("""{ "e": "TRADE_LITE", "s": "BTCUSDT" }""");
        await session.SayAsync("""{ "e": "ORDER_TRADE_UPDATE", "o": { "s": "BTCUSDT", "X": "FILLED" } }""");
        await session.SayAsync("""{ "e": "ACCOUNT_UPDATE", "a": { "m": "ORDER", "P": [ { "s": "BTCUSDT" } ] } }""");
        await session.SayAsync("""{ "e": "ORDER_TRADE_UPDATE", "o": { "s": "SOLUSDT" } }""");

        // The last of them has arrived when its change is there.
        var taken = await service.TakenAsync(2);

        Assert.Equal(["BTCUSDT", "SOLUSDT"], taken.Select(change => change.Symbol).Order());
        Assert.Empty(await service.Feed.TakeAsync(TimeSpan.Zero, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(Market.Futures, """{ "e": "MARGIN_CALL", "E": 1, "p": [ { "s": "BTCUSDT" } ] }""")]
    [InlineData(Market.Futures, """{ "e": "ACCOUNT_CONFIG_UPDATE", "E": 1, "ac": { "s": "BTCUSDT", "l": 5 } }""")]
    [InlineData(Market.Futures, """{ "e": "ACCOUNT_UPDATE", "a": { "m": "DEPOSIT", "B": [ { "a": "USDT" } ], "P": [] } }""")]
    [InlineData(Market.Futures, """{ "e": "SOMETHING_NEW", "s": "BTCUSDT" }""")]
    [InlineData(Market.Futures, """{ "result": null, "id": 1 }""")]
    [InlineData(Market.Futures, "[ 1, 2 ]")]
    [InlineData(Market.Spot, """{ "subscriptionId": 0, "event": { "e": "outboundAccountPosition", "E": 1, "B": [ { "a": "BTC", "f": "1", "l": "0" } ] } }""")]
    [InlineData(Market.Spot, """{ "subscriptionId": 0, "event": { "e": "balanceUpdate", "E": 1, "a": "BTC", "d": "1" } }""")]
    [InlineData(Market.Spot, """{ "event": { "e": "serverShutdown", "E": 1 } }""")]
    public async Task What_says_no_change_of_an_order_or_a_position_says_nothing(Market market, string said)
    {
        using var service = new FeedService();
        var session = await service.FollowedAsync(Ada, market);

        await session.SayAsync(said);

        // It has arrived when what was said after it has.
        await session.SayAsync(market == Market.Futures ? """{ "e": "TRADE_LITE", "s": "SOLUSDT" }""" : """{ "event": { "e": "executionReport", "s": "SOLUSDT" } }""");

        Assert.Equal("SOLUSDT", Assert.Single(await service.TakenAsync(1)).Symbol);
    }

    [Theory]
    [InlineData(Market.Futures, """{ "e": "ORDER_TRADE_UPDATE", "o": { "symbol": "BTCUSDT" } }""")]
    [InlineData(Market.Futures, """{ "e": "ORDER_TRADE_UPDATE", "o": { "s": "btc; DROP TABLE orders" } }""")]
    [InlineData(Market.Futures, """{ "e": "ACCOUNT_UPDATE", "a": { "P": [ { "pa": "0" } ] } }""")]
    [InlineData(Market.Futures, """{ "e": "ACCOUNT_UPDATE" }""")]
    [InlineData(Market.Futures, "this is no JSON")]
    [InlineData(Market.Spot, """{ "subscriptionId": 0, "event": { "e": "executionReport", "symbol": "BTCUSDT" } }""")]
    public async Task An_event_that_does_not_say_its_symbol_is_about_everything_of_the_account(Market market, string said)
    {
        using var service = new FeedService();
        var session = await service.FollowedAsync(Ada, market);

        await session.SayAsync(said);

        // Something changed, and it is not known where: everything of the account is asked about.
        Assert.Equal([new ExchangeAccountChange(Ada, market, TradingMode.Demo, Symbol: null)], await service.TakenAsync(1));
    }

    [Fact]
    public async Task The_stream_of_the_Futures_market_is_renewed_every_half_hour_with_the_API_key_alone()
    {
        using var service = new FeedService();
        var session = await service.FollowedAsync(Ada, Market.Futures);

        // Whoever follows the account says every minute that its feed is still wanted. It is not
        // due yet: following it again is no call.
        for (var minute = 0; minute < 29; minute++)
        {
            service.Clock.Advance(TimeSpan.FromMinutes(1));
            service.Feed.Keep(Ada, Market.Futures, TradingMode.Demo);
        }

        Assert.Equal(ExchangeFeedState.Followed, service.Feed.StateOf(Ada, Market.Futures, TradingMode.Demo));
        Assert.Equal(ExchangeFeedOutcome.Following, await service.FollowAsync(Ada, Market.Futures));
        Assert.Single(service.Binance.Calls);

        // Binance ends a stream that is not renewed within an hour: half of that has passed.
        service.Clock.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal(ExchangeFeedState.RenewalDue, service.Feed.StateOf(Ada, Market.Futures, TradingMode.Demo));
        Assert.Equal(ExchangeFeedOutcome.Following, await service.FollowAsync(Ada, Market.Futures));

        var renewed = service.Binance.Calls[1];
        Assert.Equal(("PUT", "/fapi/v1/listenKey"), (renewed.Method, renewed.Address.AbsolutePath));
        Assert.Equal(BinanceCalls.CredentialOf(TradingMode.Demo).ApiKey, renewed.Headers["X-MBX-APIKEY"]);
        Assert.Empty(renewed.Query);

        // The connection is the one that it was.
        Assert.Equal(ExchangeFeedState.Followed, service.Feed.StateOf(Ada, Market.Futures, TradingMode.Demo));
        Assert.Single(service.Server.Sessions);
        Assert.False(session.Ended.IsCompleted);
    }

    [Theory]
    [InlineData(Market.Futures, "expired")]
    [InlineData(Market.Futures, "closed")]
    [InlineData(Market.Futures, "broken")]
    [InlineData(Market.Spot, "expired")]
    [InlineData(Market.Spot, "closed")]
    [InlineData(Market.Spot, "broken")]
    public async Task A_feed_that_ends_is_no_longer_followed_and_that_is_a_change_of_everything_in_the_account(Market market, string how)
    {
        using var service = new FeedService();
        var session = await service.FollowedAsync(Ada, market);

        switch (how)
        {
            // Binance says that the stream of the account is over.
            case "expired":
                await session.SayAsync(market == Market.Futures
                    ? $$"""{ "e": "listenKeyExpired", "E": 1, "listenKey": "{{BinanceCalls.StreamKey}}" }"""
                    : """{ "subscriptionId": 0, "event": { "e": "eventStreamTerminated", "E": 1 } }""");
                break;

            // Binance closes every connection after a day.
            case "closed":
                await session.CloseAsync();
                break;

            default:
                session.Break();
                break;
        }

        await UntilAsync(() => service.Feed.StateOf(Ada, market, TradingMode.Demo) == ExchangeFeedState.NotFollowed);

        // What happens in the account from here on is not heard, and neither is what happened
        // while the feed ended: everything of it is asked about.
        Assert.Equal([new ExchangeAccountChange(Ada, market, TradingMode.Demo, Symbol: null)], await service.TakenAsync(1));

        var ended = Assert.Single(service.Application.Events(), written => written.Area == Area && written.Template.Contains("ended", StringComparison.Ordinal));
        Assert.Equal(how == "broken" ? "Warning" : "Information", ended.Level);
        Assert.Equal(how switch { "expired" => "Expired", "closed" => "Closed", _ => "Broken" }, ended.Text("FeedEnd"));

        // It is followed again when it is asked for, on a connection of its own.
        Assert.Equal(ExchangeFeedOutcome.Following, await service.FollowAsync(Ada, market));
        Assert.Equal(2, service.Server.Sessions.Count);
    }

    [Fact]
    public async Task A_feed_that_nobody_keeps_is_closed_and_one_that_is_kept_stays()
    {
        using var service = new FeedService();
        var adas = await service.FollowedAsync(Ada, Market.Futures);
        var graces = await service.FollowedAsync(Grace, Market.Futures);

        // Whoever follows the account of Grace says so every minute. Nobody asks for Ada's any more.
        for (var minute = 0; minute < 4; minute++)
        {
            service.Clock.Advance(TimeSpan.FromMinutes(1));
            service.Feed.Keep(Grace, Market.Futures, TradingMode.Demo);
        }

        await adas.Ended.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        await UntilAsync(() => service.Feed.StateOf(Ada, Market.Futures, TradingMode.Demo) == ExchangeFeedState.NotFollowed);

        Assert.Equal(ExchangeFeedState.Followed, service.Feed.StateOf(Grace, Market.Futures, TradingMode.Demo));
        Assert.False(graces.Ended.IsCompleted);

        // Nobody wants to know of that account: its end is no change that anyone is told of.
        Assert.DoesNotContain(await service.Feed.TakeAsync(TimeSpan.Zero, TestContext.Current.CancellationToken), change => change.UserId == Ada);
        Assert.Equal("NotKept", Assert.Single(service.Application.Events(), written => written.Area == Area && written.Template.Contains("ended", StringComparison.Ordinal)).Text("FeedEnd"));
    }

    [Fact]
    public async Task An_account_of_the_Spot_market_is_followed_with_one_signed_request_and_nothing_else_is_ever_sent()
    {
        using var service = new FeedService();
        var (apiKey, secretKey) = BinanceCalls.CredentialOf(TradingMode.Demo);

        Assert.Equal(ExchangeFeedOutcome.Following, await service.FollowAsync(Ada, Market.Spot));

        // The feed of the Spot market is Binance's WebSocket API: no call is made for it.
        Assert.Empty(service.Binance.Calls);

        var session = await service.Server.NextAsync();
        Assert.Equal("/ws-api/v3", session.Path);

        // One request, which subscribes to the events of the account of the API key. It is
        // signed as a call is: the parameters in the order of their names, hashed with the
        // secret key, which is not sent.
        using var request = JsonDocument.Parse(Assert.Single(session.Received));
        var root = request.RootElement;
        Assert.Equal(["id", "method", "params"], root.EnumerateObject().Select(property => property.Name));
        Assert.Equal("userDataStream.subscribe.signature", root.GetProperty("method").GetString());

        var parameters = root.GetProperty("params");
        Assert.Equal(["apiKey", "signature", "timestamp"], parameters.EnumerateObject().Select(property => property.Name));
        Assert.Equal(apiKey, parameters.GetProperty("apiKey").GetString());

        var timestamp = parameters.GetProperty("timestamp").GetInt64();
        Assert.Equal(service.Clock.GetUtcNow().ToUnixTimeMilliseconds(), timestamp);
        Assert.Equal(
            Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secretKey), Encoding.UTF8.GetBytes($"apiKey={apiKey}&timestamp={timestamp}"))),
            parameters.GetProperty("signature").GetString());
        Assert.DoesNotContain(secretKey, session.Received[0]);

        Assert.Equal([new ExchangeAccountChange(Ada, Market.Spot, TradingMode.Demo, Symbol: null)], await service.TakenAsync(1));

        // Whatever Binance says after that, and however long the account is followed, the
        // connection sends nothing more: the feed is no way to an order (NFR-SEC-6, FR-BIN-9).
        await session.SayAsync("""{ "subscriptionId": 0, "event": { "e": "executionReport", "s": "BTCUSDT" } }""");
        await service.TakenAsync(1);
        service.Feed.Keep(Ada, Market.Spot, TradingMode.Demo);
        Assert.Equal(ExchangeFeedOutcome.Following, await service.FollowAsync(Ada, Market.Spot));

        Assert.Single(session.Received);
        Assert.Single(service.Server.Sessions);

        // It needs no renewal.
        service.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(ExchangeFeedState.Followed, service.Feed.StateOf(Ada, Market.Spot, TradingMode.Demo));
    }

    [Fact]
    public async Task Where_the_file_names_no_address_there_is_no_feed_and_nothing_is_sent()
    {
        using var service = new FeedService();

        // The file of the tests names the feeds of the test environment alone.
        Assert.False(service.Feed.IsAvailable(Market.Spot, TradingMode.Live));
        Assert.False(service.Feed.IsAvailable(Market.Futures, TradingMode.Live));

        var (apiKey, secretKey) = BinanceCalls.CredentialOf(TradingMode.Live);

        Assert.Equal(
            ExchangeFeedOutcome.NotAvailable,
            await service.Feed.FollowAsync(Ada, Market.Futures, TradingMode.Live, apiKey, secretKey, TestContext.Current.CancellationToken));
        Assert.Equal(ExchangeFeedState.NotFollowed, service.Feed.StateOf(Ada, Market.Futures, TradingMode.Live));
        Assert.Empty(service.Binance.Calls);
        Assert.Empty(service.Server.Sessions);
    }

    [Fact]
    public async Task A_credential_that_Binance_refuses_follows_nothing()
    {
        using var service = new FeedService();

        // On the Futures market Binance refuses the call that begins the stream.
        service.Answer = _ => StandInSystem.Json(HttpStatusCode.Unauthorized, """{ "code": -2015, "msg": "Invalid API-key, IP, or permissions for action." }""");

        Assert.Equal(ExchangeFeedOutcome.KeyRefused, await service.FollowAsync(Ada, Market.Futures));
        Assert.Empty(service.Server.Sessions);

        // On the Spot market it refuses the request that subscribes, and the connection is let go.
        service.Server.Answers = FeedServer.Refused(401, -2015);

        Assert.Equal(ExchangeFeedOutcome.KeyRefused, await service.FollowAsync(Ada, Market.Spot));
        await (await service.Server.NextAsync()).Ended.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

        Assert.Equal(ExchangeFeedState.NotFollowed, service.Feed.StateOf(Ada, Market.Futures, TradingMode.Demo));
        Assert.Equal(ExchangeFeedState.NotFollowed, service.Feed.StateOf(Ada, Market.Spot, TradingMode.Demo));
        Assert.Empty(await service.Feed.TakeAsync(TimeSpan.Zero, TestContext.Current.CancellationToken));

        // Each is said, with the host, and with no key.
        Assert.Equal(2, service.Application.Events().Count(written => written.Area == Area && written.Level == "Warning"
            && written.Template.Contains("could not be followed", StringComparison.Ordinal)
            && written.Text("ExchangeFeedOutcome") == "KeyRefused"));
    }

    [Fact]
    public async Task A_subscription_that_Binance_refuses_for_its_time_pauses_new_entries_there()
    {
        using var service = new FeedService();
        var clock = service.Application.Services.GetRequiredService<IExchangeClock>();
        service.Server.Answers = FeedServer.Refused(400, -1021);

        Assert.Equal(ExchangeFeedOutcome.Failed, await service.FollowAsync(Ada, Market.Spot));

        // Binance's own word that the clock of the service is not near its own (FR-BIN-5).
        Assert.Equal(
            ExchangeClockState.Refused,
            Assert.Single(clock.Drift(), drift => drift is { Market: Market.Spot, Environment: TradingMode.Demo }).State);
    }

    [Theory]
    [InlineData(503, ExchangeFeedOutcome.Failed, false)]
    [InlineData(429, ExchangeFeedOutcome.NotNow, true)]
    [InlineData(418, ExchangeFeedOutcome.NotNow, true)]
    public async Task A_connection_that_Binance_does_not_take_follows_nothing_and_its_signal_of_too_many_holds_the_calls(
        int status, ExchangeFeedOutcome outcome, bool held)
    {
        using var service = new FeedService();
        var limits = service.Application.Services.GetRequiredService<IExchangeLimits>();
        service.Server.Refuses = status;

        Assert.Equal(outcome, await service.FollowAsync(Ada, Market.Spot));
        Assert.Equal(ExchangeFeedState.NotFollowed, service.Feed.StateOf(Ada, Market.Spot, TradingMode.Demo));

        // Binance counts its limits against the address of the service: the limiter hears of
        // the signal, and sends nothing on that limit for a while (FR-BIN-4).
        var spot = Assert.Single(limits.Usage(), usage => usage is { Name: "Spot", Environment: TradingMode.Demo });
        Assert.Equal(held, spot.HeldUntil is not null);
    }

    [Fact]
    public async Task No_more_than_a_few_feeds_are_opened_in_a_minute_for_all_users()
    {
        using var service = new FeedService();

        // As after a start of the application, when every account is to be followed at once.
        for (var user = 1; user <= 20; user++)
        {
            Assert.Equal(ExchangeFeedOutcome.Following, await service.FollowAsync(UserOf(user), Market.Futures));
        }

        Assert.Equal(ExchangeFeedOutcome.NotNow, await service.FollowAsync(UserOf(21), Market.Futures));
        Assert.Equal(20, service.Server.Sessions.Count);
        Assert.Equal(20, service.Binance.Calls.Count);

        // The rest are opened with the next minutes.
        service.Clock.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal(ExchangeFeedOutcome.Following, await service.FollowAsync(UserOf(21), Market.Futures));

        static Guid UserOf(int number) => Guid.Parse($"33333333-3333-7333-8333-{number:D12}");
    }

    [Fact]
    public async Task No_log_event_holds_a_key_the_key_of_a_stream_or_what_Binance_said_of_an_account()
    {
        using var service = new FeedService();
        var (apiKey, secretKey) = BinanceCalls.CredentialOf(TradingMode.Demo);

        var futures = await service.FollowedAsync(Ada, Market.Futures);
        var spot = await service.FollowedAsync(Ada, Market.Spot);
        await futures.SayAsync("""{ "e": "ORDER_TRADE_UPDATE", "o": { "s": "BTCUSDT", "c": "client-order-id-of-the-test", "z": "0.02", "ap": "60123.4" } }""");
        await spot.SayAsync("""{ "subscriptionId": 0, "event": { "e": "executionReport", "s": "BTCUSDT", "c": "client-order-id-of-the-test", "L": "60123.4" } }""");
        await service.TakenAsync(2);
        await futures.CloseAsync();
        await UntilAsync(() => service.Feed.StateOf(Ada, Market.Futures, TradingMode.Demo) == ExchangeFeedState.NotFollowed);

        // At the debug level, at which the log of outbound calls writes the bodies of calls too.
        var log = service.Application.LogText();

        Assert.DoesNotContain(apiKey, log);
        Assert.DoesNotContain(secretKey, log);
        Assert.DoesNotContain(BinanceCalls.StreamKey, log);
        Assert.DoesNotContain("client-order-id-of-the-test", log);
        Assert.DoesNotContain("60123.4", log);

        // An event of the feed names the user, the market, the environment, and the host.
        var followed = service.Application.Events().Where(written => written.Area == Area && written.Template.Contains("is followed", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, followed.Count);
        Assert.All(followed, written =>
        {
            Assert.Equal(Ada.ToString(), written.Text("UserId"));
            Assert.Equal("127.0.0.1", written.Text("Host"));
            Assert.Equal("Demo", written.Text("Environment"));
        });
    }

    [Fact]
    public async Task A_change_is_waited_for_and_given_as_it_arrives()
    {
        using var service = new FeedService();
        var session = await service.FollowedAsync(Ada, Market.Futures);

        var waiting = service.Feed.TakeAsync(TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);
        Assert.False(waiting.IsCompleted);

        await session.SayAsync("""{ "e": "TRADE_LITE", "s": "BTCUSDT" }""");

        Assert.Equal("BTCUSDT", Assert.Single(await waiting.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken)).Symbol);

        // With none in the time that it waits, it gives none.
        var none = service.Feed.TakeAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        service.Clock.Advance(TimeSpan.FromSeconds(30));

        Assert.Empty(await none.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken));
    }

    private static async Task UntilAsync(Func<bool> holds)
    {
        var began = System.Diagnostics.Stopwatch.GetTimestamp();

        while (!holds())
        {
            Assert.True(System.Diagnostics.Stopwatch.GetElapsedTime(began) < TimeSpan.FromSeconds(15), "What the test waits for did not come.");

            await Task.Delay(TimeSpan.FromMilliseconds(20), TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// The application with a Binance of the test's behind the client of the gateway, a server
    /// of the test's in place of Binance's account event feeds, in the test environment of both
    /// markets, and a clock that only the test moves.
    /// </summary>
    private sealed class FeedService : IDisposable
    {
        public FeedService()
        {
            Binance = new StandInSystem(request => Answer?.Invoke(request) ?? StandInSystem.Json(HttpStatusCode.OK, BinanceCalls.AnswerTo(request)));
            Application = BinanceCalls.Behind(
                Binance,
                debug: true,
                clock: Clock,
                file: file => file
                    .With("Binance:Futures:Demo:FeedUrl", Server.At("private"))
                    .With("Binance:Spot:Demo:FeedUrl", Server.At("ws-api/v3")));
            Feed = Application.Services.GetRequiredService<IExchangeAccountFeed>();
        }

        public FeedServer Server { get; } = new();

        public FakeTimeProvider Clock { get; } = new(BinanceCalls.Now);

        public StandInSystem Binance { get; }

        public LoggedApplication Application { get; }

        public IExchangeAccountFeed Feed { get; }

        /// <summary>What Binance answers a call with, when it is not what its documentation gives.</summary>
        public Func<HttpRequestMessage, HttpResponseMessage?>? Answer { get; set; }

        /// <summary>Follows the account of a user in the test environment, with the credential of that environment.</summary>
        public Task<ExchangeFeedOutcome> FollowAsync(Guid userId, Market market)
        {
            var (apiKey, secretKey) = BinanceCalls.CredentialOf(TradingMode.Demo);

            return Feed.FollowAsync(userId, market, TradingMode.Demo, apiKey, secretKey, TestContext.Current.CancellationToken);
        }

        /// <summary>The same, and gives the connection that the server took for it, once the change that says that the feed began is taken.</summary>
        public async Task<FeedSession> FollowedAsync(Guid userId, Market market)
        {
            Assert.Equal(ExchangeFeedOutcome.Following, await FollowAsync(userId, market));

            var session = await Server.NextAsync();
            Assert.Null(Assert.Single(await TakenAsync(1), change => change.UserId == userId && change.Market == market).Symbol);

            return session;
        }

        /// <summary>Takes changes until so many have come.</summary>
        public async Task<IReadOnlyList<ExchangeAccountChange>> TakenAsync(int count)
        {
            var taken = new List<ExchangeAccountChange>();
            var began = System.Diagnostics.Stopwatch.GetTimestamp();

            while (taken.Count < count)
            {
                Assert.True(System.Diagnostics.Stopwatch.GetElapsedTime(began) < TimeSpan.FromSeconds(15), "The changes that the test waits for did not come.");

                taken.AddRange(await Feed.TakeAsync(TimeSpan.Zero, TestContext.Current.CancellationToken));

                if (taken.Count < count)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(10), TestContext.Current.CancellationToken);
                }
            }

            return taken;
        }

        public void Dispose()
        {
            Application.Dispose();
            Server.Dispose();
        }
    }
}
