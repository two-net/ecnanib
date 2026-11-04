using System.Diagnostics;
using System.Net;
using Ecnanib.Kernel;
using Ecnanib.Modules.ExchangeAdapter.Contracts;
using Ecnanib.Tests.Persistence;

namespace Ecnanib.Tests.Exchange;

/// <summary>EXT-BIN-05, FR-EXE-05, NFR-REL-04.</summary>
public sealed class OutcomeTests(PostgresFixture postgres)
{
    private static CancellationToken Cancellation => ExchangeHarness.Cancellation;

    private static OrderRequest Order(string clientOrderId = "entry-1") =>
        new("BTCUSDT", OrderSide.Sell, OrderType.Market, 0.1m, clientOrderId, RequestPriority.Normal);

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, null)]
    [InlineData(HttpStatusCode.BadGateway, null)]
    [InlineData(HttpStatusCode.ServiceUnavailable, -1008)]
    [InlineData(HttpStatusCode.RequestTimeout, -1007)]
    [InlineData(HttpStatusCode.BadRequest, -1007)]
    [InlineData(HttpStatusCode.BadRequest, -1006)]
    [InlineData(HttpStatusCode.BadRequest, -1000)]
    [InlineData(HttpStatusCode.BadRequest, -4116)]
    public async Task A_server_error_or_a_timeout_at_Binance_leaves_the_order_unknown(HttpStatusCode status, int? code)
    {
        await using var exchange = new ExchangeHarness(postgres);
        exchange.Binance.On(
            HttpMethod.Post,
            "/fapi/v1/order",
            _ => code is null ? FakeBinance.Json("<html>Bad gateway</html>", status) : FakeBinance.Error(status, code.Value, "Unknown error, please check your request or try again later."));

        var exception = await Assert.ThrowsAsync<ExchangeException>(
            () => exchange.Trading.PlaceOrderAsync(Market.Futures, ExchangeHarness.HmacKey(), Order(), Cancellation));

        Assert.Equal(ExchangeFailure.Unknown, exception.Failure);
        Assert.True(exception.MayHaveExecuted);
        Assert.Single(exchange.Binance.To("/fapi/v1/order"));
    }

    [Fact]
    public async Task A_duplicate_client_order_ID_on_Spot_means_an_order_with_it_exists()
    {
        await using var exchange = new ExchangeHarness(postgres);
        exchange.Binance.On(HttpMethod.Post, "/api/v3/order", _ => FakeBinance.Error(HttpStatusCode.BadRequest, -2010, "Duplicate order sent."));

        var exception = await Assert.ThrowsAsync<ExchangeException>(
            () => exchange.Trading.PlaceOrderAsync(Market.Spot, ExchangeHarness.HmacKey(), Order(), Cancellation));

        Assert.Equal(ExchangeFailure.Unknown, exception.Failure);
    }

    [Fact]
    public async Task Anything_that_goes_wrong_once_an_order_is_sent_leaves_it_unknown()
    {
        await using var exchange = new ExchangeHarness(postgres);
        exchange.Binance.On(HttpMethod.Post, "/fapi/v1/order", (Func<RecordedRequest, HttpResponseMessage>)(_ => throw new InvalidDataException("The body couldn't be decompressed.")));

        var exception = await Assert.ThrowsAsync<ExchangeException>(
            () => exchange.Trading.PlaceOrderAsync(Market.Futures, ExchangeHarness.HmacKey(), Order(), Cancellation));

        Assert.Equal(ExchangeFailure.Unknown, exception.Failure);
        Assert.IsType<InvalidDataException>(exception.InnerException);
    }

    [Fact]
    public async Task An_order_with_an_unknown_outcome_is_not_said_to_be_missing_until_Binance_can_no_longer_accept_it()
    {
        await using var exchange = new ExchangeHarness(postgres);
        var key = ExchangeHarness.HmacKey();
        var order = Order();
        exchange.Binance.On(HttpMethod.Post, "/fapi/v1/order", _ => FakeBinance.Json("Service Unavailable.", HttpStatusCode.ServiceUnavailable));
        exchange.Binance.On(HttpMethod.Get, "/fapi/v1/order", _ => FakeBinance.Error(HttpStatusCode.BadRequest, -2013, "Order does not exist."));

        var unknown = await Assert.ThrowsAsync<ExchangeException>(() => exchange.Trading.PlaceOrderAsync(Market.Futures, key, order, Cancellation));

        // Its receive window, 5 seconds, and a margin of 5 more.
        var settlesAt = unknown.SettlesAt!.Value;
        Assert.InRange((settlesAt - exchange.Clock.GetUtcNow()).TotalSeconds, 9, 10);

        var notYet = await Assert.ThrowsAsync<ExchangeException>(
            () => exchange.Account.GetOrderAsync(Market.Futures, key, order.Reference, RequestPriority.Protective, Cancellation));
        Assert.Equal((ExchangeFailure.Unknown, settlesAt), (notYet.Failure, notYet.SettlesAt));

        // Another key's order with the same client order ID is another order.
        Assert.Null(await exchange.Account.GetOrderAsync(Market.Futures, ExchangeHarness.HmacKey(), order.Reference, RequestPriority.Protective, Cancellation));

        exchange.Clock.Advance(settlesAt - exchange.Clock.GetUtcNow() + TimeSpan.FromMilliseconds(1));
        Assert.Null(await exchange.Account.GetOrderAsync(Market.Futures, key, order.Reference, RequestPriority.Protective, Cancellation));
    }

    [Fact]
    public async Task A_rejection_carries_Binance_s_code_and_message_and_nothing_else()
    {
        await using var exchange = new ExchangeHarness(postgres);
        exchange.Binance.On(
            HttpMethod.Post,
            "/api/v3/order",
            _ => FakeBinance.Error(HttpStatusCode.BadRequest, -2010, "Account has insufficient balance for requested action."));

        var exception = await Assert.ThrowsAsync<ExchangeException>(
            () => exchange.Trading.PlaceOrderAsync(Market.Spot, ExchangeHarness.HmacKey(), Order(), Cancellation));

        Assert.Equal((ExchangeFailure.Rejected, -2010, false), (exception.Failure, exception.Code, exception.MayHaveExecuted));
        Assert.Contains("-2010 Account has insufficient balance", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("signature", exception.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ExchangeHarness.ExampleApiKey, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_order_whose_send_has_begun_ignores_the_caller_s_cancellation()
    {
        await using var exchange = new ExchangeHarness(postgres);
        var sent = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        exchange.Binance.On(HttpMethod.Post, "/fapi/v1/order", async (request, _) =>
        {
            sent.SetResult();
            await release.Task;
            return FakeBinance.Json(BinanceSamples.FuturesOrder(request.Parameter("newClientOrderId")!));
        });
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);

        var placing = exchange.Trading.PlaceOrderAsync(Market.Futures, ExchangeHarness.HmacKey(), Order(), caller.Token);
        await sent.Task.WaitAsync(Cancellation);
        await caller.CancelAsync();
        release.SetResult();

        var order = await placing;
        Assert.Equal(ExchangeOrderStatus.Filled, order.Status);
    }

    [Fact]
    public async Task An_order_cancelled_before_it_is_sent_is_not_sent()
    {
        await using var exchange = new ExchangeHarness(postgres);
        using var caller = new CancellationTokenSource();
        await caller.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => exchange.Trading.PlaceOrderAsync(Market.Futures, ExchangeHarness.HmacKey(), Order(), caller.Token));

        Assert.Empty(exchange.Binance.To("/fapi/v1/order"));
    }

    [Fact]
    public async Task An_order_that_gets_no_answer_is_unknown_after_ten_seconds()
    {
        await using var exchange = new ExchangeHarness(postgres);
        exchange.Binance.On(HttpMethod.Post, "/fapi/v1/order", async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new UnreachableException();
        });
        var started = Stopwatch.GetTimestamp();

        var exception = await Assert.ThrowsAsync<ExchangeException>(
            () => exchange.Trading.PlaceOrderAsync(Market.Futures, ExchangeHarness.HmacKey(), Order(), Cancellation));

        Assert.Equal(ExchangeFailure.Unknown, exception.Failure);
        Assert.InRange(Stopwatch.GetElapsedTime(started).TotalSeconds, 9.5, 20);
    }

    [Fact]
    public async Task A_read_follows_the_caller_s_cancellation()
    {
        await using var exchange = new ExchangeHarness(postgres);
        var sent = new TaskCompletionSource();
        exchange.Binance.On(HttpMethod.Get, "/api/v3/account", async (_, cancellationToken) =>
        {
            sent.SetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new UnreachableException();
        });
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);

        var reading = exchange.Account.GetAccountAsync(Market.Spot, ExchangeHarness.HmacKey(), caller.Token);
        await sent.Task.WaitAsync(Cancellation);
        await caller.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading);
    }

    [Fact]
    public async Task An_order_is_missing_only_when_Binance_says_so()
    {
        await using var exchange = new ExchangeHarness(postgres);
        var key = ExchangeHarness.HmacKey();
        var reference = new OrderReference("BTCUSDT", "entry-1", OrderType.Limit);
        exchange.Binance.OnSequence(
            HttpMethod.Get,
            "/fapi/v1/order",
            _ => FakeBinance.Error(HttpStatusCode.BadRequest, -2013, "Order does not exist."),
            _ => FakeBinance.Error(HttpStatusCode.ServiceUnavailable, -1008, "Server is currently overloaded with other requests."));

        Assert.Null(await exchange.Account.GetOrderAsync(Market.Futures, key, reference, RequestPriority.Protective, Cancellation));

        var unknown = await Assert.ThrowsAsync<ExchangeException>(
            () => exchange.Account.GetOrderAsync(Market.Futures, key, reference, RequestPriority.Protective, Cancellation));
        Assert.Equal(ExchangeFailure.Unknown, unknown.Failure);
    }

    [Fact]
    public async Task A_successful_answer_this_version_cannot_read_leaves_an_order_unknown()
    {
        await using var exchange = new ExchangeHarness(postgres);
        exchange.Binance.On(HttpMethod.Post, "/api/v3/order", _ => FakeBinance.Json("""{"orderId":"not a number"}"""));

        var exception = await Assert.ThrowsAsync<ExchangeException>(
            () => exchange.Trading.PlaceOrderAsync(Market.Spot, ExchangeHarness.HmacKey(), Order(), Cancellation));

        Assert.Equal(ExchangeFailure.Unknown, exception.Failure);
    }
}
