using System.Text.RegularExpressions;
using Ecnanib.Kernel;

namespace Ecnanib.Modules.ExchangeAdapter.Contracts;

/// <summary>
/// Places and cancels orders. Only Execution may use this, after a passing Risk Engine evaluation
/// (AC-01), and BannedApis enforces that. Read an order with <see cref="IExchangeAccount.GetOrderAsync"/>.
/// See Exchange adapter in docs/architecture.md.
/// </summary>
public interface IExchangeTrading
{
    /// <summary>
    /// Sends one order, once. Once the send has begun it ignores <paramref name="cancellationToken"/> and
    /// gives up after its own 10 seconds, so a shutdown can't cut it off half-recorded (NFR-REL-04).
    /// </summary>
    /// <exception cref="ArgumentException">The order doesn't suit the market, such as reduce-only on Spot.</exception>
    /// <exception cref="ExchangeException">
    /// It failed. When <see cref="ExchangeException.MayHaveExecuted"/> is true, query the order by its
    /// client order ID before sending it again (FR-EXE-05).
    /// </exception>
    Task<ExchangeOrder> PlaceOrderAsync(Market market, ExchangeKey key, OrderRequest order, CancellationToken cancellationToken);

    /// <summary>Cancels an open order, with the same timeout and cancellation rules as placing one.</summary>
    /// <exception cref="ExchangeException">It failed. -2011 means Binance has no such open order.</exception>
    Task CancelOrderAsync(
        Market market,
        ExchangeKey key,
        OrderReference order,
        RequestPriority priority,
        CancellationToken cancellationToken);
}

/// <summary>
/// Whether a request may use the request-weight headroom (FR-EXE-09). Placing and moving stops, and
/// closing positions, are protective. Everything else is normal, and is refused once usage reaches the
/// headroom.
/// </summary>
public enum RequestPriority
{
    Normal,
    Protective,
}

public enum OrderSide
{
    Buy,
    Sell,
}

/// <summary>
/// The order types an agent uses. Spot sends stops as STOP_LOSS and TAKE_PROFIT. Futures sends them as
/// STOP_MARKET and TAKE_PROFIT_MARKET through Binance's algo order service, which is where conditional
/// orders have gone since 2025-12-09.
/// </summary>
public enum OrderType
{
    Market,
    Limit,
    StopMarket,
    TakeProfitMarket,
}

public enum TimeInForce
{
    GoodTillCanceled,
    ImmediateOrCancel,
    FillOrKill,
}

/// <summary>
/// Which order to read or cancel. The type matters on Futures, where conditional orders live apart.
/// </summary>
public sealed record OrderReference(string Symbol, string ClientOrderId, OrderType Type);

/// <summary>
/// One order to send. It checks itself as it's built, so a bad value throws here rather than reaching
/// the exchange.
/// </summary>
public sealed partial class OrderRequest
{
    /// <param name="clientOrderId">
    /// Unique, derived from the agent, cycle and leg (FR-EXE-04). Binance allows up to 36 of letters, digits
    /// and <c>.:/_-</c>.
    /// </param>
    /// <param name="price">Limit orders only.</param>
    /// <param name="stopPrice">The trigger price of a stop or take-profit order.</param>
    /// <param name="timeInForce">Limit orders only. Good till canceled when left out.</param>
    /// <param name="reduceOnly">Futures only.</param>
    /// <exception cref="ArgumentException">A value is missing, out of range, or doesn't suit the type.</exception>
    public OrderRequest(
        string symbol,
        OrderSide side,
        OrderType type,
        decimal quantity,
        string clientOrderId,
        RequestPriority priority,
        decimal? price = null,
        decimal? stopPrice = null,
        TimeInForce? timeInForce = null,
        bool reduceOnly = false)
    {
        Require(symbol is not null && SymbolShape().IsMatch(symbol), nameof(symbol), "A symbol is upper-case letters and digits, such as BTCUSDT.");
        Require(clientOrderId is not null && ClientOrderIdShape().IsMatch(clientOrderId), nameof(clientOrderId),
            "A client order ID is 1 to 36 letters, digits, and . : / _ -.");
        Require(quantity > 0, nameof(quantity), "The quantity must be positive.");

        switch (type)
        {
            case OrderType.Limit:
                Require(price > 0, nameof(price), "A limit order needs a positive price.");
                Require(stopPrice is null, nameof(stopPrice), "A limit order has no stop price.");
                break;
            case OrderType.Market:
                Require(price is null && stopPrice is null && timeInForce is null, nameof(type), "A market order has no price, stop price or time in force.");
                break;
            case OrderType.StopMarket or OrderType.TakeProfitMarket:
                Require(stopPrice > 0, nameof(stopPrice), "A stop or take-profit order needs a positive stop price.");
                Require(price is null && timeInForce is null, nameof(type), "A stop or take-profit order fills at market, so it has no price or time in force.");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(type));
        }

        Symbol = symbol!;
        Side = side;
        Type = type;
        Quantity = quantity;
        ClientOrderId = clientOrderId!;
        Priority = priority;
        Price = price;
        StopPrice = stopPrice;
        TimeInForce = type == OrderType.Limit ? timeInForce ?? Contracts.TimeInForce.GoodTillCanceled : null;
        ReduceOnly = reduceOnly;
    }

    public string Symbol { get; }

    public OrderSide Side { get; }

    public OrderType Type { get; }

    public decimal Quantity { get; }

    public string ClientOrderId { get; }

    public RequestPriority Priority { get; }

    public decimal? Price { get; }

    public decimal? StopPrice { get; }

    public TimeInForce? TimeInForce { get; }

    public bool ReduceOnly { get; }

    public OrderReference Reference => new(Symbol, ClientOrderId, Type);

    public override string ToString() => $"{nameof(OrderRequest)} {ClientOrderId}";

    internal static bool IsSymbol(string? symbol) => symbol is not null && SymbolShape().IsMatch(symbol);

    internal static bool IsClientOrderId(string? clientOrderId) => clientOrderId is not null && ClientOrderIdShape().IsMatch(clientOrderId);

    private static void Require(bool condition, string parameter, string message)
    {
        if (!condition)
        {
            throw new ArgumentException(message, parameter);
        }
    }

    [GeneratedRegex("^[A-Z0-9]{2,20}$", RegexOptions.CultureInvariant)]
    private static partial Regex SymbolShape();

    [GeneratedRegex("^[.A-Z:/a-z0-9_-]{1,36}$", RegexOptions.CultureInvariant)]
    private static partial Regex ClientOrderIdShape();
}

/// <summary>An order as Binance last reported it.</summary>
/// <param name="ExchangeOrderId">Binance's order ID, or its algo ID for a Futures conditional order.</param>
/// <param name="Type">Null when it's a type Ecnanib doesn't place.</param>
/// <param name="Price">Null for a market order.</param>
/// <param name="AveragePrice">Null until something has filled.</param>
/// <param name="TriggeredOrderId">For a Futures conditional order once it has triggered: the order it became.</param>
public sealed record ExchangeOrder(
    string Symbol,
    string ClientOrderId,
    long ExchangeOrderId,
    OrderSide Side,
    OrderType? Type,
    ExchangeOrderStatus Status,
    decimal Quantity,
    decimal ExecutedQuantity,
    decimal? Price,
    decimal? StopPrice,
    decimal? AveragePrice,
    long? TriggeredOrderId,
    DateTimeOffset? UpdatedAt);

public enum ExchangeOrderStatus
{
    New,
    PartiallyFilled,
    Filled,
    Canceled,
    Rejected,
    Expired,

    /// <summary>A Futures conditional order whose trigger was met. <see cref="ExchangeOrder.TriggeredOrderId"/> is the order it became.</summary>
    Triggered,

    /// <summary>A Futures conditional order that triggered, and whose order then filled or was canceled.</summary>
    Finished,

    /// <summary>A status this version doesn't know. Treat the order as open until it's read again.</summary>
    Unrecognized,
}
