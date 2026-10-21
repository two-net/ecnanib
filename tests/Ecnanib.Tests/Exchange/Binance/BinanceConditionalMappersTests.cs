using Binance.Net.Enums;
using Binance.Net.Objects.Models.Futures;
using Binance.Net.Objects.Models.Futures.Socket;
using Binance.Net.Objects.Models.Spot;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Exchange.Binance;
using OrderType = Ecnanib.Exchange.Abstractions.OrderType;
using OrderSide = Ecnanib.Exchange.Abstractions.OrderSide;
using BinanceOrderSide = Binance.Net.Enums.OrderSide;

namespace Ecnanib.Tests.Exchange.Binance;

/// <summary>The USD-M conditional (algo) order surface Binance moved stops and take-profits to (FR-EXE-02), the funding income (FR-EXE-12) and the order-count filters.</summary>
public class BinanceConditionalMappersTests
{
    private static readonly DateTime Utc = new(2026, 10, 18, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void AConditionalOrderMapsItsIdsStatusAndTriggerAndAnUntriggeredOneHasNoActualOrder()
    {
        var order = new BinanceFuturesConditionalOrder
        {
            Symbol = "BTCUSDT", Id = 2146760, ClientOrderId = "e1234", Side = BinanceOrderSide.Sell, Type = FuturesOrderType.StopMarket, Status = AlgoOrderStatus.New,
            TriggerPrice = 59_000m, Quantity = 0.5m, ReduceOnly = true, ClosePosition = false, ActualOrderId = 0, ActualQuantity = 0, ActualPrice = 0, CreateTime = Utc, UpdateTime = Utc, TriggerTime = new DateTime(1970, 1, 1),
        };

        var mapped = BinanceMappers.ToConditionalOrder(order);

        Assert.Equal(("BTCUSDT", 2146760L, "e1234", OrderSide.Sell, OrderType.StopMarket, ConditionalOrderStatus.New, 59_000m, 0.5m, true, false), (mapped.Symbol, mapped.AlgoId, mapped.ClientAlgoId, mapped.Side, mapped.Type, mapped.Status, mapped.TriggerPrice, mapped.Quantity, mapped.ReduceOnly, mapped.ClosePosition));
        Assert.Null(mapped.ActualOrderId);
        Assert.Null(mapped.FilledQuantity);
        Assert.Null(mapped.AverageFillPrice);
        Assert.Null(mapped.TriggeredAt);
        Assert.True(mapped.Status.IsOpen());
        Assert.Equal(new DateTimeOffset(Utc), mapped.CreatedAt);
    }

    [Fact]
    public void AFinishedUpdateCarriesTheActualOrderAndItsFill()
    {
        var update = new BinanceAlgoOrderUpdate
        {
            Timestamp = Utc, EventTime = Utc,
            Order = new BinanceAlgoOrderUpdateOrder
            {
                Symbol = "ETHUSDT", Id = 77, ClientOrderId = "e-sl", Side = BinanceOrderSide.Sell, Type = FuturesOrderType.TakeProfitMarket, Status = AlgoOrderStatus.Finished,
                TriggerPrice = 3100m, Quantity = 2m, ReduceOnly = true, ActualOrderId = 9001, QuantityFilled = 2m, AverageFillPrice = 3101.5m, TriggerTime = Utc.AddSeconds(1),
            },
        };

        var mapped = BinanceMappers.ToConditionalOrder(update);

        Assert.Equal((77L, "e-sl", OrderType.TakeProfitMarket, ConditionalOrderStatus.Finished, 9001L, 2m, 3101.5m), (mapped.AlgoId, mapped.ClientAlgoId, mapped.Type, mapped.Status, mapped.ActualOrderId, mapped.FilledQuantity, mapped.AverageFillPrice));
        Assert.Equal(new DateTimeOffset(Utc.AddSeconds(1)), mapped.TriggeredAt);
        Assert.True(mapped.Status.IsTerminal());
    }

    [Theory]
    [InlineData(AlgoOrderStatus.Triggering, ConditionalOrderStatus.Triggering)]
    [InlineData(AlgoOrderStatus.Triggered, ConditionalOrderStatus.Triggered)]
    [InlineData(AlgoOrderStatus.Canceled, ConditionalOrderStatus.Canceled)]
    [InlineData(AlgoOrderStatus.Rejected, ConditionalOrderStatus.Rejected)]
    [InlineData(AlgoOrderStatus.Expired, ConditionalOrderStatus.Expired)]
    public void StatusesMap(AlgoOrderStatus binance, ConditionalOrderStatus expected)
    {
        Assert.Equal(expected, BinanceMappers.ToConditionalOrderStatus(binance));
    }

    [Theory]
    [InlineData(OrderType.StopMarket, ConditionalOrderType.StopMarket)]
    [InlineData(OrderType.TakeProfitMarket, ConditionalOrderType.TakeProfitMarket)]
    [InlineData(OrderType.StopLossLimit, ConditionalOrderType.Stop)]
    [InlineData(OrderType.TakeProfitLimit, ConditionalOrderType.TakeProfit)]
    public void ConditionalTypesMapAndOthersAreRefused(OrderType ours, ConditionalOrderType binance)
    {
        Assert.Equal(binance, BinanceMappers.ToConditionalOrderType(ours));
        Assert.Throws<ArgumentOutOfRangeException>(() => BinanceMappers.ToConditionalOrderType(OrderType.Limit));
    }

    [Fact]
    public void FundingIncomeMapsToAPayment()
    {
        var income = new BinanceFuturesIncomeHistory { Symbol = "BTCUSDT", IncomeType = IncomeType.FundingFee, Income = -1.25m, Asset = "USDT", Timestamp = Utc, TransactionId = "tx-1" };

        var payment = BinanceMappers.ToFundingPayment(income);

        Assert.Equal(("BTCUSDT", -1.25m, "USDT", new DateTimeOffset(Utc), "tx-1"), (payment.Symbol, payment.Amount, payment.Asset, payment.Timestamp, payment.TransactionId));
    }

    [Fact]
    public void TheOrderCountFiltersAreMappedOnBothMarkets()
    {
        var spot = new BinanceSymbol
        {
            Name = "BTCUSDT", BaseAsset = "BTC", QuoteAsset = "USDT", Status = SymbolStatus.Trading, BaseAssetPrecision = 8, QuoteAssetPrecision = 8,
            Filters = [new BinanceSymbolMaxOrdersFilter { MaxNumberOrders = 200 }, new BinanceSymbolMaxAlgorithmicOrdersFilter { MaxNumberAlgorithmicOrders = 5 }],
        };
        var futures = new BinanceFuturesUsdtSymbol
        {
            Name = "BTCUSDT", BaseAsset = "BTC", QuoteAsset = "USDT", Status = SymbolStatus.Trading, PricePrecision = 2, QuantityPrecision = 3,
            Filters = [new BinanceSymbolMaxOrdersFilter { MaxNumberOrders = 200 }, new BinanceSymbolMaxAlgorithmicOrdersFilter { MaxNumberAlgorithmicOrders = 10 }],
        };

        Assert.Equal((200, 5), (BinanceMappers.ToSymbolInfo(spot).Filters.MaxNumOrders, BinanceMappers.ToSymbolInfo(spot).Filters.MaxNumAlgoOrders));
        Assert.Equal((200, 10), (BinanceMappers.ToSymbolInfo(futures).Filters.MaxNumOrders, BinanceMappers.ToSymbolInfo(futures).Filters.MaxNumAlgoOrders));
        Assert.Null(BinanceMappers.ToSymbolInfo(new BinanceSymbol { Name = "X", BaseAsset = "X", QuoteAsset = "USDT", Filters = [] }).Filters.MaxNumAlgoOrders);
    }
}
