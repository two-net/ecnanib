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
