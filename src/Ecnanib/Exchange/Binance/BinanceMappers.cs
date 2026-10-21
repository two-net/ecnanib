using Binance.Net.Enums;
using Binance.Net.Interfaces;
using Binance.Net.Objects.Models;
using Binance.Net.Objects.Models.Futures;
using Binance.Net.Objects.Models.Futures.Socket;
using Binance.Net.Objects.Models.Spot;
using Binance.Net.Objects.Models.Spot.Socket;
using Ecnanib.Exchange.Abstractions;
using OrderSide = Ecnanib.Exchange.Abstractions.OrderSide;
using OrderStatus = Ecnanib.Exchange.Abstractions.OrderStatus;
using ExecutionType = Ecnanib.Exchange.Abstractions.ExecutionType;
using TimeInForce = Ecnanib.Exchange.Abstractions.TimeInForce;
using WorkingType = Ecnanib.Exchange.Abstractions.WorkingType;
using BinanceOrderSide = Binance.Net.Enums.OrderSide;
using BinanceOrderStatus = Binance.Net.Enums.OrderStatus;
using BinanceExecutionType = Binance.Net.Enums.ExecutionType;
using BinanceTimeInForce = Binance.Net.Enums.TimeInForce;
using BinanceWorkingType = Binance.Net.Enums.WorkingType;

namespace Ecnanib.Exchange.Binance;

/// <summary>Pure mappings from Binance.Net models and enums to the module's abstractions.</summary>
internal static class BinanceMappers
{
    // ---- time ----

    public static DateTimeOffset ToUtc(DateTime value) =>
        new(DateTime.SpecifyKind(value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : value, DateTimeKind.Utc));

    public static DateTimeOffset? ToUtc(DateTime? value) => value is { } v ? ToUtc(v) : null;

    // ---- intervals ----

    public static KlineInterval ToKlineInterval(CandleInterval interval) => interval switch
    {
        CandleInterval.FifteenMinutes => KlineInterval.FifteenMinutes,
        CandleInterval.OneHour => KlineInterval.OneHour,
        CandleInterval.FourHours => KlineInterval.FourHour,
        CandleInterval.OneDay => KlineInterval.OneDay,
        CandleInterval.OneWeek => KlineInterval.OneWeek,
        _ => throw new ArgumentOutOfRangeException(nameof(interval), interval, "Unsupported candle interval."),
    };

    // ---- market data ----

    public static Candle ToCandle(IBinanceKline kline, string symbol, CandleInterval interval, DateTimeOffset now)
    {
        var closeTime = ToUtc(kline.CloseTime);
        return new Candle(
            symbol, interval, ToUtc(kline.OpenTime), closeTime,
            kline.OpenPrice, kline.HighPrice, kline.LowPrice, kline.ClosePrice,
            kline.Volume, kline.QuoteVolume, kline.TradeCount,
            IsClosed: closeTime <= now);
    }

    public static Candle ToCandle(IBinanceStreamKlineData data, CandleInterval interval)
    {
        var k = data.Data;
        return new Candle(
            data.Symbol, interval, ToUtc(k.OpenTime), ToUtc(k.CloseTime),
            k.OpenPrice, k.HighPrice, k.LowPrice, k.ClosePrice,
            k.Volume, k.QuoteVolume, k.TradeCount,
            IsClosed: k.Final);
    }

    public static PriceTick ToPriceTick(BinancePrice price, DateTimeOffset now) =>
        new(price.Symbol, price.Price, ToUtc(price.Timestamp) ?? now);

    public static PriceTick ToPriceTick(IBinanceMiniTick tick, DateTimeOffset now) =>
        new(tick.Symbol, tick.LastPrice, now);

    public static MarkPrice ToMarkPrice(BinanceFuturesMarkPrice mark) =>
        new(mark.Symbol, mark.MarkPrice, mark.IndexPrice, mark.FundingRate ?? 0m, ToUtc(mark.NextFundingTime), ToUtc(mark.Timestamp));

    public static MarkPrice ToMarkPrice(BinanceFuturesUsdtStreamMarkPrice mark, DateTimeOffset now) =>
        new(mark.Symbol, mark.MarkPrice, mark.IndexPrice, mark.FundingRate ?? 0m, ToUtc(mark.NextFundingTime),
            mark.EventTime == default ? now : ToUtc(mark.EventTime));

    public static RateLimitRule ToRateLimitRule(BinanceRateLimit limit)
    {
        var kind = limit.Type switch
        {
            RateLimitType.RequestWeight => RateLimitKind.RequestWeight,
            RateLimitType.Orders => RateLimitKind.Orders,
            RateLimitType.RawRequests => RateLimitKind.RawRequests,
            _ => RateLimitKind.Other,
        };
        var unit = limit.Interval switch
        {
            RateLimitInterval.Second => TimeSpan.FromSeconds(1),
            RateLimitInterval.Minute => TimeSpan.FromMinutes(1),
            RateLimitInterval.Day => TimeSpan.FromDays(1),
            _ => TimeSpan.FromMinutes(1),
        };
        return new RateLimitRule(kind, unit * Math.Max(1, limit.IntervalNumber), limit.Limit);
    }

    public static SymbolInfo ToSymbolInfo(BinanceSymbol s)
    {
        var price = s.PriceFilter;
        var lot = s.LotSizeFilter;
        var marketLot = s.MarketLotSizeFilter;
        var notional = s.NotionalFilter;
        var minNotional = s.MinNotionalFilter;
        var filters = new SymbolFilters(
            TickSize: SymbolFilters.Normalize(price?.TickSize ?? 0m),
            MinPrice: price?.MinPrice ?? 0m,
            MaxPrice: price?.MaxPrice ?? 0m,
            StepSize: SymbolFilters.Normalize(lot?.StepSize ?? 0m),
            MinQuantity: lot?.MinQuantity ?? 0m,
            MaxQuantity: lot?.MaxQuantity ?? 0m,
            MarketStepSize: marketLot is null ? null : SymbolFilters.Normalize(marketLot.StepSize),
            MarketMinQuantity: marketLot?.MinQuantity,
            MarketMaxQuantity: marketLot?.MaxQuantity,
            MinNotional: notional?.MinNotional ?? minNotional?.MinNotional ?? 0m,
            MaxNotional: notional?.MaxNotional,
            MinNotionalAppliesToMarketOrders: notional?.ApplyMinToMarketOrders ?? minNotional?.ApplyToMarketOrders ?? true,
            MaxNumOrders: s.MaxOrdersFilter?.MaxNumberOrders,
            MaxNumAlgoOrders: s.MaxAlgorithmicOrdersFilter?.MaxNumberAlgorithmicOrders);
        return new SymbolInfo(
            s.Name, s.BaseAsset, s.QuoteAsset,
            IsTrading: s.Status == SymbolStatus.Trading,
            Status: s.Status.ToString(),
            PricePrecision: Scale(filters.TickSize),
            QuantityPrecision: s.BaseAssetPrecision,
            OcoAllowed: s.OCOAllowed,
            filters);
    }

    public static SymbolInfo ToSymbolInfo(BinanceFuturesUsdtSymbol s)
    {
        var price = s.PriceFilter;
        var lot = s.LotSizeFilter;
        var marketLot = s.MarketLotSizeFilter;
        var filters = new SymbolFilters(
            TickSize: SymbolFilters.Normalize(price?.TickSize ?? 0m),
            MinPrice: price?.MinPrice ?? 0m,
            MaxPrice: price?.MaxPrice ?? 0m,
            StepSize: SymbolFilters.Normalize(lot?.StepSize ?? 0m),
            MinQuantity: lot?.MinQuantity ?? 0m,
            MaxQuantity: lot?.MaxQuantity ?? 0m,
            MarketStepSize: marketLot is null ? null : SymbolFilters.Normalize(marketLot.StepSize),
            MarketMinQuantity: marketLot?.MinQuantity,
            MarketMaxQuantity: marketLot?.MaxQuantity,
            MinNotional: s.MinNotionalFilter?.MinNotional ?? 0m,
            MaxNotional: null,
            MinNotionalAppliesToMarketOrders: true,
            MaxNumOrders: s.MaxOrdersFilter?.MaxNumberOrders,
            MaxNumAlgoOrders: s.MaxAlgoOrdersFilter?.MaxNumberAlgorithmicOrders);
        return new SymbolInfo(
            s.Name, s.BaseAsset, s.QuoteAsset,
            IsTrading: s.Status == SymbolStatus.Trading,
            Status: s.Status.ToString(),
            PricePrecision: s.PricePrecision,
            QuantityPrecision: s.QuantityPrecision,
            OcoAllowed: false,
            filters);
    }

    /// <summary>Number of decimal places of a normalised decimal (0.01 → 2).</summary>
    public static int Scale(decimal value) => (decimal.GetBits(SymbolFilters.Normalize(value))[3] >> 16) & 0xFF;

    // ---- account ----

    public static ApiKeyPermissions ToPermissions(BinanceAPIKeyPermissions p) => new(
        Verified: true,
        EnableReading: p.EnableReading,
        EnableSpotTrading: p.EnableSpotAndMarginTrading,
        EnableFutures: p.EnableFutures,
        EnableWithdrawals: p.EnableWithdrawals,
        IpRestricted: p.IpRestrict,
        CreatedAt: ToUtc(p.CreateTime));

    public static AssetBalance ToBalance(BinanceBalance b) => new(b.Asset, b.Available, b.Locked, b.Total, null);

    public static AssetBalance ToBalance(BinanceStreamBalance b) => new(b.Asset, b.Available, b.Locked, b.Total, null);

    public static AssetBalance ToBalance(BinanceFuturesAccountBalance b) =>
        new(b.Asset, b.AvailableBalance, Math.Max(0m, b.WalletBalance - b.AvailableBalance), b.WalletBalance, b.CrossUnrealizedPnl);

    public static AssetBalance ToBalance(BinanceFuturesStreamBalance b) =>
        new(b.Asset, b.CrossWalletBalance, Math.Max(0m, b.WalletBalance - b.CrossWalletBalance), b.WalletBalance, null);

    public static FuturesAccountSummary ToAccountSummary(BinanceFuturesAccountInfoV3 a, DateTimeOffset now) =>
        new(a.TotalWalletBalance, a.TotalMarginBalance, a.TotalUnrealizedProfit, a.AvailableBalance, now);

    public static FuturesPosition ToPosition(BinancePositionV3 p, DateTimeOffset now) => new(
        p.Symbol,
        Quantity: p.PositionAmt,
        EntryPrice: p.EntryPrice,
        MarkPrice: p.MarkPrice,
        UnrealizedPnl: p.UnrealizedProfit,
        LiquidationPrice: p.LiquidationPrice > 0 ? p.LiquidationPrice : null,
        Leverage: (int)(p.Leverage ?? 0m),
        MarginType: ToMarginType(p.MarginType ?? FuturesMarginType.Cross),
        IsolatedMargin: p.MarginType == FuturesMarginType.Isolated ? p.IsolatedMargin : null,
        Notional: p.Notional,
        UpdatedAt: ToUtc(p.UpdateTime) ?? now);

    public static FuturesPosition ToPosition(BinanceFuturesStreamPosition p, DateTimeOffset timestamp) => new(
        p.Symbol,
        Quantity: p.Quantity,
        EntryPrice: p.EntryPrice,
        MarkPrice: 0m,
        UnrealizedPnl: p.UnrealizedPnl,
        LiquidationPrice: null,
        Leverage: 0,
        MarginType: ToMarginType(p.MarginType),
        IsolatedMargin: p.MarginType == FuturesMarginType.Isolated ? p.IsolatedMargin : null,
        Notional: Math.Abs(p.Quantity * p.EntryPrice),
        UpdatedAt: timestamp);

    // ---- orders ----

    public static ExchangeOrder ToOrder(BinanceOrderBase o) => new(
        o.Symbol, o.Id, o.ClientOrderId, ToOrderSide(o.Side), ToOrderType(o.Type), ToOrderStatus(o.Status), ToTimeInForce(o.TimeInForce),
        Price: o.Price,
        StopPrice: o.StopPrice is > 0 ? o.StopPrice : null,
        Quantity: o.Quantity,
        FilledQuantity: o.QuantityFilled,
        FilledQuoteQuantity: o.QuoteQuantityFilled,
        AverageFillPrice: o.AverageFillPrice is > 0 ? o.AverageFillPrice : (o.QuantityFilled > 0 ? o.QuoteQuantityFilled / o.QuantityFilled : null),
        ReduceOnly: false,
        OrderListId: o.OrderListId is >= 0 ? o.OrderListId : null,
        CreatedAt: ToUtc(o.CreateTime),
        UpdatedAt: ToUtc(o.UpdateTime) ?? ToUtc(o.CreateTime));

    public static ExchangeOrder ToOrder(BinanceFuturesOrder o) => new(
        o.Symbol, o.Id, o.ClientOrderId, ToOrderSide(o.Side), ToOrderType(o.Type), ToOrderStatus(o.Status), ToTimeInForce(o.TimeInForce),
        Price: o.Price,
        StopPrice: o.StopPrice is > 0 ? o.StopPrice : null,
        Quantity: o.Quantity,
        FilledQuantity: o.QuantityFilled,
        FilledQuoteQuantity: o.QuoteQuantityFilled ?? 0m,
        AverageFillPrice: o.AveragePrice > 0 ? o.AveragePrice : null,
        ReduceOnly: o.ReduceOnly,
        OrderListId: null,
        CreatedAt: ToUtc(o.CreateTime),
        UpdatedAt: ToUtc(o.UpdateTime));

    public static OcoOrderList ToOcoList(BinanceOrderOcoList list) => new(
        list.Id, list.ListClientOrderId, list.Symbol, ToOcoListStatus(list.ListOrderStatus),
        list.Orders.Select(o => new OrderRef(o.OrderId, o.ClientOrderId)).ToArray(),
        list.OrderReports?.Select(o => ToOrder(o)).ToArray(),
        ToUtc(list.TransactionTime));

    public static UserTrade ToTrade(BinanceTrade t) => new(
        t.Symbol, t.Id, t.OrderId, t.Price, t.Quantity, t.QuoteQuantity, t.Fee, t.FeeAsset ?? string.Empty,
        t.IsBuyer, t.IsMaker, null, ToUtc(t.Timestamp));

    public static UserTrade ToTrade(BinanceFuturesUsdtTrade t) => new(
        t.Symbol, t.Id, t.OrderId, t.Price, t.Quantity, t.QuoteQuantity, t.Fee, t.FeeAsset ?? string.Empty,
        t.Buyer, t.Maker, t.RealizedPnl, ToUtc(t.Timestamp));

    // ---- conditional (algo) orders and funding ----

    public static ConditionalOrder ToConditionalOrder(BinanceFuturesConditionalOrder o) => new(
        o.Symbol, o.Id, o.ClientOrderId ?? string.Empty, ToOrderSide(o.Side), ToOrderType(o.Type), ToConditionalOrderStatus(o.Status),
        TriggerPrice: o.TriggerPrice ?? 0m,
        Price: o.Price > 0 ? o.Price : null,
        Quantity: o.Quantity > 0 ? o.Quantity : null,
        ReduceOnly: o.ReduceOnly,
        ClosePosition: o.ClosePosition,
        ActualOrderId: o.ActualOrderId is > 0 ? o.ActualOrderId : null,
        FilledQuantity: o.ActualQuantity is > 0 ? o.ActualQuantity : null,
        AverageFillPrice: o.ActualPrice is > 0 ? o.ActualPrice : null,
        CreatedAt: ToUtc(o.CreateTime),
        UpdatedAt: ToUtc(o.UpdateTime),
        TriggeredAt: Stamp(o.TriggerTime));

    public static ConditionalOrder ToConditionalOrder(BinanceAlgoOrderUpdate update)
    {
        var o = update.Order;
        var at = ToUtc(update.Timestamp == default ? update.EventTime : update.Timestamp);
        return new ConditionalOrder(
            o.Symbol, o.Id, o.ClientOrderId ?? string.Empty, ToOrderSide(o.Side), ToOrderType(o.Type), ToConditionalOrderStatus(o.Status),
            TriggerPrice: o.TriggerPrice ?? 0m,
            Price: o.Price > 0 ? o.Price : null,
            Quantity: o.Quantity > 0 ? o.Quantity : null,
            ReduceOnly: o.ReduceOnly,
            ClosePosition: o.ClosePosition,
            ActualOrderId: o.ActualOrderId is > 0 ? o.ActualOrderId : null,
            FilledQuantity: o.QuantityFilled is > 0 ? o.QuantityFilled : null,
            AverageFillPrice: o.AverageFillPrice is > 0 ? o.AverageFillPrice : null,
            CreatedAt: at,
            UpdatedAt: at,
            TriggeredAt: Stamp(o.TriggerTime));
    }

    public static FundingPayment ToFundingPayment(BinanceFuturesIncomeHistory income) =>
        new(income.Symbol ?? string.Empty, income.Income, income.Asset ?? string.Empty, ToUtc(income.Timestamp),
            string.IsNullOrEmpty(income.TransactionId) ? null : income.TransactionId);

    /// <summary>Binance sends 0 (the epoch) for a trigger time that has not happened.</summary>
    private static DateTimeOffset? Stamp(DateTime? value) => value is { Year: > 2000 } v ? ToUtc(v) : null;

    // ---- user-data streams ----

    public static (ExchangeOrder Order, ExecutionType Execution, UserTrade? Fill) ToOrderUpdate(BinanceStreamOrderUpdate u)
    {
        var order = new ExchangeOrder(
            u.Symbol, u.Id, u.ClientOrderId, ToOrderSide(u.Side), ToOrderType(u.Type), ToOrderStatus(u.Status), ToTimeInForce(u.TimeInForce),
            Price: u.Price,
            StopPrice: u.StopPrice > 0 ? u.StopPrice : null,
            Quantity: u.Quantity,
            FilledQuantity: u.QuantityFilled,
            FilledQuoteQuantity: u.QuoteQuantityFilled,
            AverageFillPrice: u.QuantityFilled > 0 ? u.QuoteQuantityFilled / u.QuantityFilled : null,
            ReduceOnly: false,
            OrderListId: u.OrderListId >= 0 ? u.OrderListId : null,
            CreatedAt: ToUtc(u.CreateTime),
            UpdatedAt: ToUtc(u.UpdateTime));
        var execution = ToExecutionType(u.ExecutionType);
        UserTrade? fill = execution == ExecutionType.Trade && u.LastQuantityFilled > 0
            ? new UserTrade(u.Symbol, u.TradeId, u.Id, u.LastPriceFilled, u.LastQuantityFilled, u.LastQuoteQuantity,
                u.Fee, u.FeeAsset ?? string.Empty, u.Side == BinanceOrderSide.Buy, IsMaker(u.Side, u.BuyerIsMaker), null, ToUtc(u.UpdateTime))
            : null;
        return (order, execution, fill);
    }

    public static OcoOrderList ToOcoList(BinanceStreamOrderList list) => new(
        list.Id, list.ListClientOrderId, list.Symbol, ToOcoListStatus(list.ListOrderStatus),
        list.Orders.Select(o => new OrderRef(o.OrderId, o.ClientOrderId)).ToArray(),
        null,
        ToUtc(list.TransactionTime));

    public static (ExchangeOrder Order, ExecutionType Execution, UserTrade? Fill) ToOrderUpdate(BinanceFuturesStreamOrderUpdate update)
    {
        var u = update.UpdateData;
        var timestamp = ToUtc(update.TransactionTime);
        var order = new ExchangeOrder(
            u.Symbol, u.OrderId, u.ClientOrderId, ToOrderSide(u.Side), ToOrderType(u.Type), ToOrderStatus(u.Status), ToTimeInForce(u.TimeInForce),
            Price: u.Price,
            StopPrice: u.StopPrice > 0 ? u.StopPrice : null,
            Quantity: u.Quantity,
            FilledQuantity: u.AccumulatedQuantityOfFilledTrades,
            FilledQuoteQuantity: u.AccumulatedQuantityOfFilledTrades * u.AveragePrice,
            AverageFillPrice: u.AveragePrice > 0 ? u.AveragePrice : null,
            ReduceOnly: u.IsReduce,
            OrderListId: null,
            CreatedAt: timestamp,
            UpdatedAt: ToUtc(u.UpdateTime));
        var execution = ToExecutionType(u.ExecutionType);
        UserTrade? fill = execution == ExecutionType.Trade && u.QuantityOfLastFilledTrade > 0
            ? new UserTrade(u.Symbol, u.TradeId, u.OrderId, u.PriceLastFilledTrade, u.QuantityOfLastFilledTrade,
                u.PriceLastFilledTrade * u.QuantityOfLastFilledTrade, u.Fee, u.FeeAsset ?? string.Empty,
                u.Side == BinanceOrderSide.Buy, IsMaker(u.Side, u.BuyerIsMaker), u.RealizedProfit, ToUtc(u.UpdateTime))
            : null;
        return (order, execution, fill);
    }

    /// <summary>Binance reports whether the buyer was the maker; our side tells whether we were the buyer.</summary>
    public static bool IsMaker(BinanceOrderSide ourSide, bool buyerIsMaker) => ourSide == BinanceOrderSide.Buy ? buyerIsMaker : !buyerIsMaker;

    // ---- enums ----

    public static OrderSide ToOrderSide(BinanceOrderSide side) => side == BinanceOrderSide.Buy ? OrderSide.Buy : OrderSide.Sell;

    public static BinanceOrderSide FromOrderSide(OrderSide side) => side == OrderSide.Buy ? BinanceOrderSide.Buy : BinanceOrderSide.Sell;

    public static OrderType ToOrderType(SpotOrderType type) => type switch
    {
        SpotOrderType.Limit => OrderType.Limit,
        SpotOrderType.Market => OrderType.Market,
        SpotOrderType.StopLoss => OrderType.StopMarket,
        SpotOrderType.StopLossLimit => OrderType.StopLossLimit,
        SpotOrderType.TakeProfit => OrderType.TakeProfitMarket,
        SpotOrderType.TakeProfitLimit => OrderType.TakeProfitLimit,
        SpotOrderType.LimitMaker => OrderType.LimitMaker,
        _ => OrderType.Other,
    };

    public static SpotOrderType ToSpotOrderType(OrderType type) => type switch
    {
        OrderType.Limit => SpotOrderType.Limit,
        OrderType.Market => SpotOrderType.Market,
        OrderType.StopMarket => SpotOrderType.StopLoss,
        OrderType.StopLossLimit => SpotOrderType.StopLossLimit,
        OrderType.TakeProfitMarket => SpotOrderType.TakeProfit,
        OrderType.TakeProfitLimit => SpotOrderType.TakeProfitLimit,
        OrderType.LimitMaker => SpotOrderType.LimitMaker,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Order type is not supported on spot."),
    };

    public static OrderType ToOrderType(FuturesOrderType type) => type switch
    {
        FuturesOrderType.Limit => OrderType.Limit,
        FuturesOrderType.Market => OrderType.Market,
        FuturesOrderType.Stop => OrderType.StopLossLimit,
        FuturesOrderType.StopMarket => OrderType.StopMarket,
        FuturesOrderType.TakeProfit => OrderType.TakeProfitLimit,
        FuturesOrderType.TakeProfitMarket => OrderType.TakeProfitMarket,
        _ => OrderType.Other,
    };

    public static FuturesOrderType ToFuturesOrderType(OrderType type) => type switch
    {
        OrderType.Limit => FuturesOrderType.Limit,
        OrderType.Market => FuturesOrderType.Market,
        OrderType.StopLossLimit => FuturesOrderType.Stop,
        OrderType.StopMarket => FuturesOrderType.StopMarket,
        OrderType.TakeProfitLimit => FuturesOrderType.TakeProfit,
        OrderType.TakeProfitMarket => FuturesOrderType.TakeProfitMarket,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Order type is not supported on USD-M futures."),
    };

    public static ConditionalOrderType ToConditionalOrderType(OrderType type) => type switch
    {
        OrderType.StopMarket => ConditionalOrderType.StopMarket,
        OrderType.TakeProfitMarket => ConditionalOrderType.TakeProfitMarket,
        OrderType.StopLossLimit => ConditionalOrderType.Stop,
        OrderType.TakeProfitLimit => ConditionalOrderType.TakeProfit,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Order type is not a conditional order type."),
    };

    public static ConditionalOrderStatus ToConditionalOrderStatus(AlgoOrderStatus status) => status switch
    {
        AlgoOrderStatus.New => ConditionalOrderStatus.New,
        AlgoOrderStatus.Triggering => ConditionalOrderStatus.Triggering,
        AlgoOrderStatus.Triggered => ConditionalOrderStatus.Triggered,
        AlgoOrderStatus.Finished => ConditionalOrderStatus.Finished,
        AlgoOrderStatus.Canceled => ConditionalOrderStatus.Canceled,
        AlgoOrderStatus.Rejected => ConditionalOrderStatus.Rejected,
        AlgoOrderStatus.Expired => ConditionalOrderStatus.Expired,
        _ => ConditionalOrderStatus.Other,
    };

    public static OrderStatus ToOrderStatus(BinanceOrderStatus status) => status switch
    {
        BinanceOrderStatus.New or BinanceOrderStatus.PendingNew => OrderStatus.New,
        BinanceOrderStatus.PartiallyFilled => OrderStatus.PartiallyFilled,
        BinanceOrderStatus.Filled => OrderStatus.Filled,
        BinanceOrderStatus.Canceled => OrderStatus.Canceled,
        BinanceOrderStatus.PendingCancel => OrderStatus.PendingCancel,
        BinanceOrderStatus.Rejected => OrderStatus.Rejected,
        BinanceOrderStatus.Expired or BinanceOrderStatus.ExpiredInMatch => OrderStatus.Expired,
        _ => OrderStatus.Other,
    };

    public static ExecutionType ToExecutionType(BinanceExecutionType type) => type switch
    {
        BinanceExecutionType.New => ExecutionType.New,
        BinanceExecutionType.Canceled => ExecutionType.Canceled,
        BinanceExecutionType.Trade => ExecutionType.Trade,
        BinanceExecutionType.Expired => ExecutionType.Expired,
        BinanceExecutionType.Rejected => ExecutionType.Rejected,
        _ => ExecutionType.Other,
    };

    public static TimeInForce ToTimeInForce(BinanceTimeInForce tif) => tif switch
    {
        BinanceTimeInForce.GoodTillCanceled => TimeInForce.GoodTillCanceled,
        BinanceTimeInForce.ImmediateOrCancel => TimeInForce.ImmediateOrCancel,
        BinanceTimeInForce.FillOrKill => TimeInForce.FillOrKill,
        _ => TimeInForce.Other,
    };

    public static BinanceTimeInForce FromTimeInForce(TimeInForce tif) => tif switch
    {
        TimeInForce.GoodTillCanceled => BinanceTimeInForce.GoodTillCanceled,
        TimeInForce.ImmediateOrCancel => BinanceTimeInForce.ImmediateOrCancel,
        TimeInForce.FillOrKill => BinanceTimeInForce.FillOrKill,
        _ => throw new ArgumentOutOfRangeException(nameof(tif), tif, "Unsupported time in force."),
    };

    public static MarginType ToMarginType(FuturesMarginType type) => type == FuturesMarginType.Isolated ? MarginType.Isolated : MarginType.Cross;

    public static FuturesMarginType FromMarginType(MarginType type) => type == MarginType.Isolated ? FuturesMarginType.Isolated : FuturesMarginType.Cross;

    public static BinanceWorkingType FromWorkingType(WorkingType type) => type == WorkingType.MarkPrice ? BinanceWorkingType.Mark : BinanceWorkingType.Contract;

    public static OcoListStatus ToOcoListStatus(ListOrderStatus status) => status switch
    {
        ListOrderStatus.Executing => OcoListStatus.Executing,
        ListOrderStatus.Done => OcoListStatus.AllDone,
        ListOrderStatus.Rejected => OcoListStatus.Rejected,
        _ => OcoListStatus.Other,
    };
}
