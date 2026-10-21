using System.Text.RegularExpressions;

namespace Ecnanib.Exchange.Abstractions;

/// <summary>Reference to an order by exchange id or by client order id (FR-EXE-07).</summary>
public readonly record struct OrderRef(long? OrderId, string? ClientOrderId)
{
    public static OrderRef ById(long orderId) => new(orderId, null);

    public static OrderRef ByClientId(string clientOrderId) => new(null, clientOrderId);

    public bool IsEmpty => OrderId is null && string.IsNullOrEmpty(ClientOrderId);

    public override string ToString() => OrderId is { } id ? $"#{id}" : ClientOrderId ?? "(none)";
}

/// <summary>
/// A new order. The client order id is mandatory and validated against Binance's format so retries never duplicate
/// an order (FR-EXE-07, NFR-REL-02). Futures-only flags are ignored on spot.
/// </summary>
public sealed partial record OrderRequest(
    string Symbol,
    OrderSide Side,
    OrderType Type,
    string ClientOrderId,
    decimal? Quantity,
    decimal? Price = null,
    decimal? StopPrice = null,
    TimeInForce? TimeInForce = null,
    bool ReduceOnly = false,
    bool ClosePosition = false,
    WorkingType? WorkingType = null)
{
    public const int MaxClientOrderIdLength = 36;

    public string Symbol { get; } = !string.IsNullOrWhiteSpace(Symbol)
        ? Symbol.Trim().ToUpperInvariant()
        : throw new ArgumentException("Symbol is required.", nameof(Symbol));

    public string ClientOrderId { get; } = ClientOrderIdPattern().IsMatch(ClientOrderId ?? string.Empty)
        ? ClientOrderId!
        : throw new ArgumentException(
            $"Client order id must match ^[.A-Z:/a-z0-9_-]{{1,{MaxClientOrderIdLength}}}$.", nameof(ClientOrderId));

    public static bool IsValidClientOrderId(string? value) => ClientOrderIdPattern().IsMatch(value ?? string.Empty);

    [GeneratedRegex(@"^[\.A-Z\:/a-z0-9_-]{1,36}$")]
    private static partial Regex ClientOrderIdPattern();
}

public sealed record ExchangeOrder(
    string Symbol,
    long OrderId,
    string ClientOrderId,
    OrderSide Side,
    OrderType Type,
    OrderStatus Status,
    TimeInForce TimeInForce,
    decimal Price,
    decimal? StopPrice,
    decimal Quantity,
    decimal FilledQuantity,
    decimal FilledQuoteQuantity,
    decimal? AverageFillPrice,
    bool ReduceOnly,
    long? OrderListId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Spot OCO exit (FR-EXE-01): a take-profit limit-maker leg and a stop-loss-limit leg. For a long position the side
/// is Sell, the take-profit price is above market and the stop price below.
/// </summary>
public sealed record SpotOcoRequest(
    string Symbol,
    OrderSide Side,
    decimal Quantity,
    decimal TakeProfitPrice,
    decimal StopPrice,
    decimal StopLimitPrice,
    string ListClientOrderId,
    string TakeProfitClientOrderId,
    string StopClientOrderId)
{
    public string Symbol { get; } = !string.IsNullOrWhiteSpace(Symbol)
        ? Symbol.Trim().ToUpperInvariant()
        : throw new ArgumentException("Symbol is required.", nameof(Symbol));

    public string ListClientOrderId { get; } = Require(ListClientOrderId, nameof(ListClientOrderId));

    public string TakeProfitClientOrderId { get; } = Require(TakeProfitClientOrderId, nameof(TakeProfitClientOrderId));

    public string StopClientOrderId { get; } = Require(StopClientOrderId, nameof(StopClientOrderId));

    private static string Require(string? value, string name) => OrderRequest.IsValidClientOrderId(value)
        ? value!
        : throw new ArgumentException($"{name} must match ^[.A-Z:/a-z0-9_-]{{1,{OrderRequest.MaxClientOrderIdLength}}}$.", name);
}

public sealed record OcoOrderList(
    long OrderListId,
    string ListClientOrderId,
    string Symbol,
    OcoListStatus Status,
    IReadOnlyList<OrderRef> Orders,
    IReadOnlyList<ExchangeOrder>? OrderReports,
    DateTimeOffset TransactionTime);

/// <summary>A fill from the user's trade history; the source of fees and realized P&amp;L (FR-EXE-12).</summary>
public sealed record UserTrade(
    string Symbol,
    long TradeId,
    long OrderId,
    decimal Price,
    decimal Quantity,
    decimal QuoteQuantity,
    decimal Fee,
    string FeeAsset,
    bool IsBuyer,
    bool IsMaker,
    decimal? RealizedPnl,
    DateTimeOffset Timestamp);

/// <summary>
/// A USD-M futures conditional (algo) order: the stop-market, take-profit-market, stop-limit and take-profit-limit
/// types Binance moved to <c>POST /fapi/v1/algoOrder</c> (FR-EXE-02). <see cref="ClientAlgoId"/> is the deterministic
/// id (FR-EXE-07). <see cref="ClosePosition"/> closes the whole position on trigger and excludes a quantity and
/// reduce-only; a reduce-only order with a quantity reduces by at most that quantity.
/// </summary>
public sealed record ConditionalOrderRequest(
    string Symbol,
    OrderSide Side,
    OrderType Type,
    string ClientAlgoId,
    decimal TriggerPrice,
    decimal? Quantity,
    decimal? Price = null,
    bool ReduceOnly = false,
    bool ClosePosition = false,
    WorkingType? WorkingType = null,
    bool PriceProtect = false)
{
    public string Symbol { get; } = !string.IsNullOrWhiteSpace(Symbol)
        ? Symbol.Trim().ToUpperInvariant()
        : throw new ArgumentException("Symbol is required.", nameof(Symbol));

    public OrderType Type { get; } = Type is OrderType.StopMarket or OrderType.TakeProfitMarket or OrderType.StopLossLimit or OrderType.TakeProfitLimit
        ? Type
        : throw new ArgumentException("A conditional order is a stop-market, take-profit-market, stop-limit or take-profit-limit order.", nameof(Type));

    public string ClientAlgoId { get; } = OrderRequest.IsValidClientOrderId(ClientAlgoId)
        ? ClientAlgoId
        : throw new ArgumentException($"Client algo id must match ^[.A-Z:/a-z0-9_-]{{1,{OrderRequest.MaxClientOrderIdLength}}}$.", nameof(ClientAlgoId));

    public decimal TriggerPrice { get; } = TriggerPrice > 0
        ? TriggerPrice
        : throw new ArgumentException("The trigger price must be positive.", nameof(TriggerPrice));

    public decimal? Quantity { get; } = ClosePosition
        ? (Quantity is null ? null : throw new ArgumentException("A close-position order carries no quantity.", nameof(Quantity)))
        : (Quantity is > 0 ? Quantity : throw new ArgumentException("A conditional order needs a positive quantity unless it closes the position.", nameof(Quantity)));

    public bool ReduceOnly { get; } = ClosePosition && ReduceOnly
        ? throw new ArgumentException("A close-position order cannot also be reduce-only.", nameof(ReduceOnly))
        : ReduceOnly;
}

/// <summary>
/// A conditional order as the exchange reports it. <see cref="AlgoId"/> identifies it until it triggers;
/// <see cref="ActualOrderId"/> is the regular order it became, whose fills carry that id. <see cref="FilledQuantity"/>
/// and <see cref="AverageFillPrice"/> are known once the actual order traded.
/// </summary>
public sealed record ConditionalOrder(
    string Symbol,
    long AlgoId,
    string ClientAlgoId,
    OrderSide Side,
    OrderType Type,
    ConditionalOrderStatus Status,
    decimal TriggerPrice,
    decimal? Price,
    decimal? Quantity,
    bool ReduceOnly,
    bool ClosePosition,
    long? ActualOrderId,
    decimal? FilledQuantity,
    decimal? AverageFillPrice,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? TriggeredAt);

/// <summary>One funding payment of a USD-M futures position: negative when paid, positive when received (FR-EXE-12).</summary>
public sealed record FundingPayment(string Symbol, decimal Amount, string Asset, DateTimeOffset Timestamp, string? TransactionId);
