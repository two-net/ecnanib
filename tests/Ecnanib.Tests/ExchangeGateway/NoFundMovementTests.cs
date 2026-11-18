using Ecnanib.ExchangeGateway;
using Ecnanib.ExchangeGateway.Trading;
using Ecnanib.Shared;
using Ecnanib.Tests.Architecture;
using Ecnanib.Tests.DecisionEngine;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.ExchangeGateway;

/// <summary>
/// Ecnanib never withdraws, deposits, or transfers funds, between the Spot and the Futures wallet
/// of a user included (FR-BIN-9). The gateway has no operation that does: everything it can send
/// to Binance is a short list of calls that read, of calls about orders, which trade in the
/// account that the credential is of, and of two calls that set how a symbol of the Futures
/// market is margined in that account, and every call it makes passes one place. Binance is the
/// only exchange behind it (FR-BIN-1).
/// </summary>
public class NoFundMovementTests
{
    // Everything that the gateway sends to Binance, whatever it is asked, in any market and any
    // environment. Binance keeps the calls that move funds in its wallet API, under sapi:
    // withdrawals, the addresses of deposits, and transfers between wallets. The one call that
    // the gateway makes there reads what a key may do. The calls of the market data are of
    // Binance's public API, which takes no credential and so can move nothing.
    private static readonly string[] Reads =
    [
        "GET /api/v3/account",
        "GET /api/v3/exchangeInfo",
        "GET /api/v3/klines",
        "GET /api/v3/ticker/price",
        "GET /api/v3/time",
        "GET /fapi/v1/accountConfig",
        "GET /fapi/v1/exchangeInfo",
        "GET /fapi/v1/klines",
        "GET /fapi/v1/openInterest",
        "GET /fapi/v1/premiumIndex",
        "GET /fapi/v1/time",
        "GET /fapi/v2/ticker/price",
        "GET /fapi/v3/balance",
        "GET /fapi/v3/positionRisk",
        "GET /sapi/v1/account/apiRestrictions",
    ];

    // Everything that the gateway sends to Binance about an order: it places one, asks what
    // became of it, takes it off the exchange, and reads its executions, on each market, and
    // on the Futures market the same for a stop, which Binance keeps apart there. On the Spot
    // market it places the pair of a stop-loss and a take-profit as one list, and takes the
    // list off. An order buys or sells in the account of the credential. None of these calls
    // moves funds out of that account, into it, or between its wallets: Binance has those
    // under sapi.
    private static readonly string[] Orders =
    [
        "DELETE /api/v3/order",
        "DELETE /api/v3/orderList",
        "DELETE /fapi/v1/algoOrder",
        "DELETE /fapi/v1/order",
        "GET /api/v3/myTrades",
        "GET /api/v3/order",
        "GET /fapi/v1/algoOrder",
        "GET /fapi/v1/order",
        "GET /fapi/v1/userTrades",
        "POST /api/v3/order",
        "POST /api/v3/orderList/oco",
        "POST /fapi/v1/algoOrder",
        "POST /fapi/v1/order",
    ];

    // What the gateway sends to Binance to set how a symbol of the Futures market is margined
    // in the account of a credential: with margin of its own for a position, and at a leverage
    // (FR-BIN-F2). Each is a setting of the symbol. Neither names an amount of an asset, or
    // moves any: the margin of a position is taken from the wallet of the market by the order
    // that opens it, and the gateway has no call that adds margin to a position.
    private static readonly string[] Settings =
    [
        "POST /fapi/v1/leverage",
        "POST /fapi/v1/marginType",
    ];

    // The operations of the gateway, which the first test asks one by one. An operation that is
    // added is asked there too, so that what it sends is held to the list.
    private static readonly string[] Operations =
    [
        nameof(IExchangeAdapter.CheckKeyAsync),
        nameof(IExchangeAdapter.GetBalancesAsync),
        nameof(IExchangeAdapter.GetFuturesAccountAsync),
        nameof(IExchangeAdapter.GetServerTimeAsync),
    ];

    // The same of the market data, which the gateway reads for everyone.
    private static readonly string[] MarketDataOperations =
    [
        nameof(IExchangeMarketData.GetCandlesAsync),
        nameof(IExchangeMarketData.GetMarkPriceAsync),
        nameof(IExchangeMarketData.GetOpenInterestAsync),
        nameof(IExchangeMarketData.GetPriceAsync),
        nameof(IExchangeMarketData.GetSymbolsAsync),
    ];

    // The same of the orders, which only the Decision Engine may use.
    private static readonly string[] OrderOperations =
    [
        nameof(IExchangeOrders.CancelAsync),
        nameof(IExchangeOrders.CancelPairAsync),
        nameof(IExchangeOrders.FillsAsync),
        nameof(IExchangeOrders.FindAsync),
        nameof(IExchangeOrders.PlaceAsync),
        nameof(IExchangeOrders.PlacePairAsync),
        nameof(IExchangeOrders.SetIsolatedMarginAsync),
    ];

    [Fact]
    public async Task Whatever_the_gateway_is_asked_it_sends_binance_calls_that_read_calls_about_orders_and_the_margin_of_a_symbol_and_nothing_else()
    {
        Assert.Equal(Operations, typeof(IExchangeAdapter).GetMembers().Select(member => member.Name).Order(StringComparer.Ordinal));
        Assert.Equal(
            MarketDataOperations, typeof(IExchangeMarketData).GetMembers().Select(member => member.Name).Order(StringComparer.Ordinal));
        Assert.Equal(OrderOperations, typeof(IExchangeOrders).GetMembers().Select(member => member.Name).Order(StringComparer.Ordinal));

        // A Binance that keeps orders, and answers every other call with what its
        // documentation gives for it.
        var trading = new TradingBinance();
        var binance = trading.System;
        using var application = BinanceCalls.Behind(binance);
        var exchange = application.Services.GetRequiredService<IExchangeAdapter>();
        var marketData = application.Services.GetRequiredService<IExchangeMarketData>();
        var orders = application.Services.GetRequiredService<IExchangeOrders>();

        foreach (var mode in new[] { TradingMode.Live, TradingMode.Demo })
        {
            var (apiKey, secretKey) = BinanceCalls.CredentialOf(mode);
            await exchange.CheckKeyAsync(BinanceCalls.User, mode, apiKey, secretKey, TestContext.Current.CancellationToken);

            foreach (var market in new[] { Market.Spot, Market.Futures })
            {
                await exchange.GetServerTimeAsync(market, mode, TestContext.Current.CancellationToken);
                await exchange.GetBalancesAsync(BinanceCalls.User, market, mode, apiKey, secretKey, TestContext.Current.CancellationToken);

                await marketData.GetCandlesAsync(
                    market, mode, MarketService.Symbol, CandleInterval.OneHour, TestContext.Current.CancellationToken);
                await marketData.GetPriceAsync(market, mode, MarketService.Symbol, TestContext.Current.CancellationToken);
                await marketData.GetSymbolsAsync(market, mode, TestContext.Current.CancellationToken);

                // Every operation of the orders, for each kind of order that the gateway places.
                foreach (var type in Enum.GetValues<ExchangeOrderType>())
                {
                    var id = $"{market}-{mode}-{type}";
                    var which = new ExchangeOrderRef(id, MarketService.Symbol, type);

                    // Below the price of the symbol, which is 60000, but for a take-profit,
                    // which sells above it.
                    decimal? price = type switch
                    {
                        ExchangeOrderType.Market => null,
                        ExchangeOrderType.TakeProfit => 62000m,
                        _ => 59000m,
                    };

                    var order = new ExchangeOrderRequest(id, MarketService.Symbol, ExchangeOrderSide.Sell, type, 0.01m, price, Closes: true);

                    var placed = await orders.PlaceAsync(BinanceCalls.User, market, mode, apiKey, secretKey, order, TestContext.Current.CancellationToken);
                    await orders.FindAsync(BinanceCalls.User, market, mode, apiKey, secretKey, which, TestContext.Current.CancellationToken);
                    await orders.CancelAsync(BinanceCalls.User, market, mode, apiKey, secretKey, which, TestContext.Current.CancellationToken);
                    await orders.FillsAsync(
                        BinanceCalls.User, market, mode, apiKey, secretKey, MarketService.Symbol, placed.Order!.ExchangeOrderId,
                        TestContext.Current.CancellationToken);
                }
            }

            // The pair of the Spot market: placed as one list, and taken off as one.
            var pair = new ExchangePairRequest($"{mode}-Pair", MarketService.Symbol, 0.01m, $"{mode}-StopLoss", 59000m, $"{mode}-TakeProfit", 62000m);

            await orders.PlacePairAsync(BinanceCalls.User, mode, apiKey, secretKey, pair, TestContext.Current.CancellationToken);
            await orders.CancelPairAsync(
                BinanceCalls.User, mode, apiKey, secretKey, new ExchangePairRef(pair.PairId, pair.Symbol, pair.StopLossId, pair.TakeProfitId),
                TestContext.Current.CancellationToken);

            await marketData.GetMarkPriceAsync(mode, MarketService.Symbol, TestContext.Current.CancellationToken);
            await marketData.GetOpenInterestAsync(mode, MarketService.Symbol, TestContext.Current.CancellationToken);

            // The Futures market alone: how the account stands, and the margin of a symbol.
            await exchange.GetFuturesAccountAsync(BinanceCalls.User, mode, apiKey, secretKey, MarketService.Symbol, TestContext.Current.CancellationToken);
            await orders.SetIsolatedMarginAsync(BinanceCalls.User, mode, apiKey, secretKey, MarketService.Symbol, 3, TestContext.Current.CancellationToken);
        }

        // All of the three lists, and nothing that is not on them.
        Assert.Equal(
            Reads.Concat(Orders).Concat(Settings).Order(StringComparer.Ordinal),
            binance.Calls.Select(call => $"{call.Method} {call.Address.AbsolutePath}").Distinct().Order(StringComparer.Ordinal));

        // A call that is no GET is one about an order, or one of the two that set the margin of
        // a symbol. Each call about an order names one order, or one pair of them, by its
        // identifier: it carries no amount of an asset to move, and no address to move it to.
        Assert.All(
            binance.Calls.Where(call => call.Method != "GET" && !Settings.Contains($"{call.Method} {call.Address.AbsolutePath}")),
            call => Assert.Contains(
                call.Query,
                parameter => parameter.Name is "newClientOrderId" or "origClientOrderId" or "clientAlgoId" or "listClientOrderId"));

        // Each of the two settings names the symbol and what the symbol is to have, and nothing
        // else: no asset, no amount, and no address.
        Assert.All(
            binance.Calls.Where(call => Settings.Contains($"{call.Method} {call.Address.AbsolutePath}")),
            call => Assert.Equal(
                call.Address.AbsolutePath == "/fapi/v1/marginType"
                    ? ["marginType", "symbol", "recvWindow", "timestamp", "signature"]
                    : ["symbol", "leverage", "recvWindow", "timestamp", "signature"],
                call.Query.Select(parameter => parameter.Name)));

        // Every call sends nothing but its address: a call about an order as well.
        Assert.All(binance.Calls, call => Assert.Null(call.Body));

        // The wallet API is asked on the real exchange only, at the address of its Spot market.
        Assert.All(
            binance.Calls.Where(call => call.Address.AbsolutePath.StartsWith("/sapi/", StringComparison.Ordinal)),
            call => Assert.Equal(BinanceCalls.HostOf(Market.Spot, TradingMode.Live), call.Address.Host));
    }

    [Fact]
    public async Task Binance_is_the_only_exchange_and_is_reached_at_the_addresses_of_the_configuration_file_only()
    {
        var binance = BinanceCalls.ThatAnswers();
        using var application = BinanceCalls.Behind(binance);

        // One adapter is behind the common interface, and the market data has one source.
        var exchange = Assert.Single(application.Services.GetServices<IExchangeAdapter>());
        var marketData = Assert.Single(application.Services.GetServices<IExchangeMarketData>());

        foreach (var (market, mode) in BinanceCalls.Pairs)
        {
            var (apiKey, secretKey) = BinanceCalls.CredentialOf(mode);
            await exchange.CheckKeyAsync(BinanceCalls.User, mode, apiKey, secretKey, TestContext.Current.CancellationToken);
            await exchange.GetServerTimeAsync(market, mode, TestContext.Current.CancellationToken);
            await exchange.GetBalancesAsync(BinanceCalls.User, market, mode, apiKey, secretKey, TestContext.Current.CancellationToken);

            await marketData.GetCandlesAsync(
                market, mode, MarketService.Symbol, CandleInterval.OneDay, TestContext.Current.CancellationToken);
            await marketData.GetPriceAsync(market, mode, MarketService.Symbol, TestContext.Current.CancellationToken);
            await marketData.GetSymbolsAsync(market, mode, TestContext.Current.CancellationToken);
            await marketData.GetMarkPriceAsync(mode, MarketService.Symbol, TestContext.Current.CancellationToken);
            await marketData.GetOpenInterestAsync(mode, MarketService.Symbol, TestContext.Current.CancellationToken);
        }

        // Each of the four addresses is reached, and no other.
        Assert.Equal(
            BinanceCalls.Pairs.Select(pair => BinanceCalls.HostOf(pair.Market, pair.Mode)).Order(StringComparer.Ordinal),
            binance.Calls.Select(call => call.Address.Host).Distinct().Order(StringComparer.Ordinal));
        Assert.All(binance.Calls, call => Assert.Equal("https", call.Address.Scheme));
    }

    [Fact]
    public void Every_call_to_binance_passes_the_one_place_that_takes_only_a_call_of_the_list()
    {
        const string Gateway = "Ecnanib.ExchangeGateway";
        const string TheCall = "Ecnanib.ExchangeGateway.Adapters.Binance.BinanceCall";

        // What takes a client from the host's factory, and what sends with one. A type that
        // did either beside the call could send Binance anything.
        var users = new[] { "System.Net.Http.IHttpClientFactory::", "System.Net.Http.HttpClient::", "System.Net.Http.HttpMessageInvoker::" }
            .SelectMany(method => DependencyChecker.FindCallersOf(typeof(Program).Assembly, method))
            .Where(caller => caller.StartsWith(Gateway + ".", StringComparison.Ordinal))
            .Distinct()
            .ToList();

        // The rule finds the call itself, so it would find another.
        Assert.NotEmpty(users);
        Assert.All(users, caller => Assert.StartsWith(TheCall, caller));
    }
}
