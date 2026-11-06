using System.Globalization;
using System.Text.Json;
using Ecnanib.Kernel;
using Ecnanib.Modules.ExchangeAdapter.Contracts;

namespace Ecnanib.Modules.ExchangeAdapter;

/// <summary>Turns orders into Binance's names and back.</summary>
internal static class BinanceOrders
{
    /// <summary>Futures conditional orders go through Binance's algo order service.</summary>
    public static bool IsAlgo(Market market, OrderType type) =>
        market == Market.Futures && type is OrderType.StopMarket or OrderType.TakeProfitMarket;

    public static string Side(OrderSide side) => side == OrderSide.Buy ? "BUY" : "SELL";

    public static string Type(Market market, OrderType type) => (market, type) switch
    {
        (_, OrderType.Market) => "MARKET",
        (_, OrderType.Limit) => "LIMIT",
        (Market.Spot, OrderType.StopMarket) => "STOP_LOSS",
        (Market.Spot, OrderType.TakeProfitMarket) => "TAKE_PROFIT",
        (Market.Futures, OrderType.StopMarket) => "STOP_MARKET",
        (Market.Futures, OrderType.TakeProfitMarket) => "TAKE_PROFIT_MARKET",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    public static string TimeInForce(TimeInForce timeInForce) => timeInForce switch
    {
        Contracts.TimeInForce.GoodTillCanceled => "GTC",
        Contracts.TimeInForce.ImmediateOrCancel => "IOC",
        Contracts.TimeInForce.FillOrKill => "FOK",
        _ => throw new ArgumentOutOfRangeException(nameof(timeInForce)),
    };

    public static string MarginType(FuturesMarginType marginType) => marginType switch
    {
        FuturesMarginType.Isolated => "ISOLATED",
        FuturesMarginType.Crossed => "CROSSED",
        _ => throw new ArgumentOutOfRangeException(nameof(marginType)),
    };

    /// <summary>Binance's name for a margin type, or null for one this version doesn't know.</summary>
    public static FuturesMarginType? MarginType(string marginType) => marginType.ToUpperInvariant() switch
    {
        "ISOLATED" => FuturesMarginType.Isolated,
        "CROSSED" or "CROSS" => FuturesMarginType.Crossed,
        _ => null,
    };

    /// <summary>Plain digits, never an exponent.</summary>
    public static string Number(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    /// <exception cref="ExchangeException">A side this version doesn't know, which means Binance changed its format.</exception>
    public static ExchangeOrder FromOrder(ExchangeServer server, OrderBody body)
    {
        var averagePrice = body.AvgPrice > 0
            ? body.AvgPrice
            : body.ExecutedQty > 0 && body.CummulativeQuoteQty > 0 ? body.CummulativeQuoteQty / body.ExecutedQty : null;

        return new ExchangeOrder(
            body.Symbol,
            body.ClientOrderId,
            body.OrderId,
            ParseSide(server, body.Side),
            ParseType(body.Type),
            ParseStatus(body.Status),
            body.OrigQty,
            body.ExecutedQty,
            body.Price > 0 ? body.Price : null,
            body.StopPrice > 0 ? body.StopPrice : null,
            averagePrice,
            TriggeredOrderId: null,
            Time(body.UpdateTime ?? body.TransactTime));
    }

    /// <exception cref="ExchangeException">A side this version doesn't know, which means Binance changed its format.</exception>
    public static ExchangeOrder FromAlgo(ExchangeServer server, AlgoOrderBody body) =>
        new(
            body.Symbol,
            body.ClientAlgoId,
            body.AlgoId,
            ParseSide(server, body.Side),
            ParseType(body.OrderType),
            ParseAlgoStatus(body.AlgoStatus),
            body.Quantity,
            body.ActualQty ?? 0,
            body.Price > 0 ? body.Price : null,
            body.TriggerPrice > 0 ? body.TriggerPrice : null,
            body.ActualPrice > 0 ? body.ActualPrice : null,
            OrderId(body.ActualOrderId),
            Time(body.UpdateTime ?? body.CreateTime));

    private static OrderSide ParseSide(ExchangeServer server, string side) => side switch
    {
        "BUY" => OrderSide.Buy,
        "SELL" => OrderSide.Sell,
        _ => throw new ExchangeException(server, ExchangeFailure.Unknown, $"{ExchangeServers.Describe(server)} reported an order side this version doesn't know."),
    };

    private static OrderType? ParseType(string type) => type switch
    {
        "MARKET" => OrderType.Market,
        "LIMIT" => OrderType.Limit,
        "STOP_LOSS" or "STOP_MARKET" => OrderType.StopMarket,
        "TAKE_PROFIT" or "TAKE_PROFIT_MARKET" => OrderType.TakeProfitMarket,
        _ => null,
    };

    private static ExchangeOrderStatus ParseStatus(string status) => status switch
    {
        "NEW" or "PENDING_NEW" => ExchangeOrderStatus.New,
        "PARTIALLY_FILLED" => ExchangeOrderStatus.PartiallyFilled,
        "FILLED" => ExchangeOrderStatus.Filled,
        "CANCELED" => ExchangeOrderStatus.Canceled,
        "REJECTED" => ExchangeOrderStatus.Rejected,
        "EXPIRED" or "EXPIRED_IN_MATCH" => ExchangeOrderStatus.Expired,
        _ => ExchangeOrderStatus.Unrecognized,
    };

    private static ExchangeOrderStatus ParseAlgoStatus(string status) => status switch
    {
        "NEW" => ExchangeOrderStatus.New,
        "CANCELED" => ExchangeOrderStatus.Canceled,
        "TRIGGERING" or "TRIGGERED" => ExchangeOrderStatus.Triggered,
        "FINISHED" => ExchangeOrderStatus.Finished,
        "REJECTED" => ExchangeOrderStatus.Rejected,
        "EXPIRED" => ExchangeOrderStatus.Expired,
        _ => ExchangeOrderStatus.Unrecognized,
    };

    private static long? OrderId(JsonElement? value) => value switch
    {
        { ValueKind: JsonValueKind.Number } number when number.TryGetInt64(out var id) && id > 0 => id,
        { ValueKind: JsonValueKind.String } text when long.TryParse(text.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 => id,
        _ => null,
    };

    private static DateTimeOffset? Time(long? milliseconds) =>
        milliseconds > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds.Value) : null;
}
