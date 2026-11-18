using Ecnanib.Shared;

namespace Ecnanib.ExchangeGateway.Trading;

/// <summary>
/// The orders of the exchange, behind a common interface: placing one, asking what became of
/// one, taking one off the exchange, and reading its executions, the pair of a stop-loss and a
/// take-profit of the Spot market, where the execution of one ends the other (FR-BIN-S2), and
/// the margin and the leverage of a symbol of the Futures market, which are set before an entry
/// (FR-BIN-F2). Only the Decision Engine may use this namespace, after schema validation and the
/// Risk Guard (NFR-SEC-6). See docs/orders.md.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every order carries the identifier that its caller gave it</b> (FR-BIN-6), and is asked
/// about and taken off the exchange by that identifier. So an order can be asked about although
/// the call that placed it never said what the exchange made of it.
/// </para>
/// <para>
/// <b>A call is made once.</b> Nothing here places an order again. How a call ended says whether
/// there is an order: <see cref="ExchangeOrderOutcome.Unknown"/> is the end that does not say,
/// and after it the order is asked about before anything else is done with it.
/// </para>
/// <para>
/// <b>A call that changes something at the exchange cannot be cancelled once it is sent.</b>
/// Cancelling ends the wait at the limiter, before anything is sent, with an error. From then on
/// the call runs to its answer or to its time limit.
/// </para>
/// <para>
/// A credential is taken with each call and is kept nowhere, as with
/// <see cref="IExchangeAdapter"/>. No operation here moves funds between accounts or out of one
/// (FR-BIN-9): an order trades in the account that the credential is of, and the margin of a
/// symbol is a setting of that account.
/// </para>
/// </remarks>
public interface IExchangeOrders
{
    /// <summary>Places an order, once.</summary>
    /// <param name="userId">Whose call it is, for the limiter. It is not sent to the exchange.</param>
    /// <param name="market">The market.</param>
    /// <param name="mode">The environment the credential is for.</param>
    /// <param name="apiKey">The user's API key, passed on its own so that it is part of no value that can be printed.</param>
    /// <param name="secretKey">The secret key that belongs to it, which signs the call and is never sent.</param>
    /// <param name="order">The order.</param>
    /// <param name="cancellationToken">Cancels the wait before the call is sent, and nothing after that.</param>
    /// <returns>
    /// How the call ended, with the order as the exchange has it when it answered with one. An
    /// exchange that does not answer is an outcome: this does not fail for it.
    /// </returns>
    Task<ExchangeOrderResult> PlaceAsync(
        Guid userId,
        Market market,
        TradingMode mode,
        string apiKey,
        string secretKey,
        ExchangeOrderRequest order,
        CancellationToken cancellationToken);

    /// <summary>Asks what became of an order, by the identifier that its caller gave it. It only reads.</summary>
    /// <param name="userId">Whose call it is, for the limiter.</param>
    /// <param name="market">The market.</param>
    /// <param name="mode">The environment the credential is for.</param>
    /// <param name="apiKey">The user's API key.</param>
    /// <param name="secretKey">The secret key that belongs to it.</param>
    /// <param name="order">Which order.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>
    /// The order as the exchange has it, or <see cref="ExchangeOrderOutcome.NotFound"/> when it
    /// has none of that identifier. Every other end says nothing about the order.
    /// </returns>
    Task<ExchangeOrderResult> FindAsync(
        Guid userId,
        Market market,
        TradingMode mode,
        string apiKey,
        string secretKey,
        ExchangeOrderRef order,
        CancellationToken cancellationToken);

    /// <summary>Takes an order that rests on the exchange off it, once.</summary>
    /// <param name="userId">Whose call it is, for the limiter.</param>
    /// <param name="market">The market.</param>
    /// <param name="mode">The environment the credential is for.</param>
    /// <param name="apiKey">The user's API key.</param>
    /// <param name="secretKey">The secret key that belongs to it.</param>
    /// <param name="order">Which order.</param>
    /// <param name="cancellationToken">Cancels the wait before the call is sent, and nothing after that.</param>
    /// <returns>
    /// <see cref="ExchangeOrderOutcome.Answered"/> when the exchange took the order off, and
    /// <see cref="ExchangeOrderOutcome.NotFound"/> when no such order rests there: it was
    /// executed, was taken off before, or never was. Which of them, the order is asked about.
    /// </returns>
    Task<ExchangeOrderResult> CancelAsync(
        Guid userId,
        Market market,
        TradingMode mode,
        string apiKey,
        string secretKey,
        ExchangeOrderRef order,
        CancellationToken cancellationToken);

    /// <summary>Reads the executions of one order. It only reads.</summary>
    /// <param name="userId">Whose call it is, for the limiter.</param>
    /// <param name="market">The market.</param>
    /// <param name="mode">The environment the credential is for.</param>
    /// <param name="apiKey">The user's API key.</param>
    /// <param name="secretKey">The secret key that belongs to it.</param>
    /// <param name="symbol">The symbol of the order.</param>
    /// <param name="exchangeOrderId">The id that the exchange knows the order by: see <see cref="ExchangeOrder.ExchangeOrderId"/>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<ExchangeFills> FillsAsync(
        Guid userId,
        Market market,
        TradingMode mode,
        string apiKey,
        string secretKey,
        string symbol,
        string exchangeOrderId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Places the pair that protects what a position of the Spot market holds, once, as one
    /// call: a stop-loss and a take-profit that both sell the same quantity, of which the
    /// exchange ends one when the other is triggered (FR-BIN-S2). It takes no market: only the
    /// Spot market has such a pair.
    /// </summary>
    /// <param name="userId">Whose call it is, for the limiter. It is not sent to the exchange.</param>
    /// <param name="mode">The environment the credential is for.</param>
    /// <param name="apiKey">The user's API key.</param>
    /// <param name="secretKey">The secret key that belongs to it.</param>
    /// <param name="pair">The pair.</param>
    /// <param name="cancellationToken">Cancels the wait before the call is sent, and nothing after that.</param>
    /// <returns>
    /// How the call ended, with both orders as the exchange has them when it answered with them.
    /// The exchange takes both or neither. Each is asked about with <see cref="FindAsync"/>, by
    /// its own identifier.
    /// </returns>
    Task<ExchangePairResult> PlacePairAsync(
        Guid userId,
        TradingMode mode,
        string apiKey,
        string secretKey,
        ExchangePairRequest pair,
        CancellationToken cancellationToken);

    /// <summary>Takes a pair that rests on the Spot market off it, both of its orders with one call, once.</summary>
    /// <param name="userId">Whose call it is, for the limiter.</param>
    /// <param name="mode">The environment the credential is for.</param>
    /// <param name="apiKey">The user's API key.</param>
    /// <param name="secretKey">The secret key that belongs to it.</param>
    /// <param name="pair">Which pair.</param>
    /// <param name="cancellationToken">Cancels the wait before the call is sent, and nothing after that.</param>
    /// <returns>
    /// <see cref="ExchangeOrderOutcome.Answered"/> with both orders when the exchange took the
    /// pair off. Every other end says nothing about either order: each is asked about.
    /// </returns>
    Task<ExchangePairResult> CancelPairAsync(
        Guid userId,
        TradingMode mode,
        string apiKey,
        string secretKey,
        ExchangePairRef pair,
        CancellationToken cancellationToken);

    /// <summary>
    /// Has the positions of a symbol of the Futures market use isolated margin, at a leverage,
    /// in the account of the credential: the margin of a position is its own, and backs no
    /// other (FR-BIN-F2). Both are settings of the symbol, and neither is one of the whole
    /// account. It is asked before an entry, with the leverage that the Risk Guard computed. It
    /// takes no market: only the Futures market has margin.
    /// </summary>
    /// <param name="userId">Whose call it is, for the limiter. It is not sent to the exchange.</param>
    /// <param name="mode">The environment the credential is for.</param>
    /// <param name="apiKey">The user's API key.</param>
    /// <param name="secretKey">The secret key that belongs to it.</param>
    /// <param name="symbol">The symbol.</param>
    /// <param name="leverage">The leverage, as a whole number of 1 or more.</param>
    /// <param name="cancellationToken">Cancels the wait before a call is sent, and nothing after that.</param>
    /// <returns>
    /// <see cref="ExchangeOrderOutcome.Answered"/> when the symbol has isolated margin and that
    /// leverage: a symbol that had both already has them. <see cref="ExchangeOrderOutcome.Rejected"/>
    /// when the exchange refused one of the two, which it does while the account has orders or
    /// a position in the symbol that it would change the margin of. Every other end says that
    /// it is not known how the symbol is margined: no entry is placed after it.
    /// </returns>
    Task<ExchangeOrderOutcome> SetIsolatedMarginAsync(
        Guid userId,
        TradingMode mode,
        string apiKey,
        string secretKey,
        string symbol,
        int leverage,
        CancellationToken cancellationToken);
}

/// <summary>What is known of the time that the exchange takes an order in.</summary>
public static class ExchangeOrderTimes
{
    /// <summary>
    /// How long after a call that placed an order ended an answer that the exchange has no such
    /// order is final. The exchange takes a call for a few seconds after the time that the call
    /// carries, and no longer, so an order that is not there by then was not placed and will not
    /// be. Before that, the same answer says nothing: the order may still arrive.
    /// </summary>
    public static readonly TimeSpan FinalAfter = TimeSpan.FromSeconds(10);
}

/// <summary>The side of an order.</summary>
public enum ExchangeOrderSide
{
    /// <summary>It buys the asset that is traded.</summary>
    Buy,

    /// <summary>It sells the asset that is traded.</summary>
    Sell,
}

/// <summary>
/// The kinds of order that the gateway places. The pair of the Spot market is two stops that the
/// exchange holds together: see <see cref="IExchangeOrders.PlacePairAsync"/> and docs/orders.md.
/// </summary>
public enum ExchangeOrderType
{
    /// <summary>It is executed at once, at the best prices that there are.</summary>
    Market,

    /// <summary>It rests on the exchange until the price reaches its trigger, and is then placed at the market.</summary>
    Stop,

    /// <summary>
    /// It rests on the exchange until the price reaches its trigger on the side of a profit, and
    /// is then placed at the market: a sale at its trigger or above, and a purchase at its
    /// trigger or below, where a stop is triggered the other way. It is the take-profit of a
    /// position of the Futures market (FR-BIN-F3). On the Spot market the take-profit of a
    /// position is one order of a pair.
    /// </summary>
    TakeProfit,

    /// <summary>
    /// It is executed at its price or a better one, and what is not executed at once rests on
    /// the exchange until it is, or until it is taken off (FR-BIN-S2).
    /// </summary>
    Limit,
}

/// <summary>An order to place.</summary>
/// <param name="ClientOrderId">
/// The identifier that its caller gives the order, which no other order of the account has
/// (FR-BIN-6): 1 to 36 characters, of letters, digits, and <c>. : / _ -</c>.
/// </param>
/// <param name="Symbol">The exchange's name of the symbol, such as <c>BTCUSDT</c>.</param>
/// <param name="Side">Whether it buys or sells.</param>
/// <param name="Type">The kind of order.</param>
/// <param name="Quantity">How much of the asset that is traded it is for.</param>
/// <param name="Price">
/// The price that a stop or a take-profit is triggered at, or the price of a limit order. Null
/// for an order at the market. On the Futures market a trigger is held against the mark price
/// (FR-BIN-F3).
/// </param>
/// <param name="Closes">
/// Whether the order only closes a position: a stop-loss, a take-profit, and an order that
/// closes at the market. Where the exchange keeps positions, such an order opens nothing when
/// the position is gone already.
/// </param>
public sealed record ExchangeOrderRequest(
    string ClientOrderId,
    string Symbol,
    ExchangeOrderSide Side,
    ExchangeOrderType Type,
    decimal Quantity,
    decimal? Price,
    bool Closes);

/// <summary>Which order is asked about, or taken off the exchange.</summary>
/// <param name="ClientOrderId">The identifier that its caller gave the order.</param>
/// <param name="Symbol">The symbol of the order.</param>
/// <param name="Type">The kind of order, which says where the exchange keeps it.</param>
public sealed record ExchangeOrderRef(string ClientOrderId, string Symbol, ExchangeOrderType Type);

/// <summary>How a call about an order ended, in what it says about the order.</summary>
public enum ExchangeOrderOutcome
{
    /// <summary>The exchange answered with the order as it has it.</summary>
    Answered,

    /// <summary>
    /// The exchange refused the call. Of a call that placed an order: there is no order. Of any
    /// other call: it says nothing about the order.
    /// </summary>
    Rejected,

    /// <summary>
    /// The call did not reach the exchange's orders: the limit on requests held it back, or the
    /// exchange answered that it has had too many requests (FR-BIN-4). Of a call that placed an
    /// order: there is no order.
    /// </summary>
    NotSent,

    /// <summary>
    /// The exchange answered as it answers a wrong credential. Of a call that placed an order:
    /// there is no order. The credential should be checked again.
    /// </summary>
    KeyRefused,

    /// <summary>The exchange has no order of that identifier, or none that rests on it.</summary>
    NotFound,

    /// <summary>
    /// The call ended without saying what the exchange made of it: no answer in time, a
    /// connection that broke, a fault of the exchange, or an answer that does not read. Of a
    /// call that placed an order: there may be an order, and it is asked about before any other
    /// call is made for it (FR-BIN-6).
    /// </summary>
    Unknown,
}

/// <summary>What has become of an order at the exchange.</summary>
public enum ExchangeOrderStatus
{
    /// <summary>It rests on the exchange, and none of it is executed.</summary>
    Open,

    /// <summary>A part of it is executed, and the rest is still with the exchange.</summary>
    PartiallyFilled,

    /// <summary>All of it is executed.</summary>
    Filled,

    /// <summary>It was taken off the exchange. A part of it may have been executed before.</summary>
    Canceled,

    /// <summary>The exchange did not take it.</summary>
    Rejected,

    /// <summary>The exchange ended it: an order at the market that could not be executed in full ends so. A part of it may be executed.</summary>
    Expired,
}

/// <summary>An order as the exchange has it.</summary>
/// <param name="ExchangeOrderId">
/// The id that the exchange knows the order by now, which its executions are read with. A stop
/// that was triggered is known by the order that it became.
/// </param>
/// <param name="Status">What has become of it.</param>
/// <param name="ExecutedQuantity">How much of it is executed.</param>
/// <param name="AveragePrice">The average price of what is executed, or null when nothing is.</param>
/// <param name="Fills">
/// Its executions, where the answer says them: the answer to an order that was placed at the
/// market on the Spot market does. Empty otherwise, which does not say that there are none: see
/// <see cref="IExchangeOrders.FillsAsync"/>.
/// </param>
public sealed record ExchangeOrder(
    string ExchangeOrderId,
    ExchangeOrderStatus Status,
    decimal ExecutedQuantity,
    decimal? AveragePrice,
    IReadOnlyList<ExchangeFill> Fills)
{
    /// <summary>Whether nothing more becomes of the order: it is executed, taken off, refused, or ended.</summary>
    public bool IsOver => Status is not (ExchangeOrderStatus.Open or ExchangeOrderStatus.PartiallyFilled);
}

/// <summary>What a call about an order gave.</summary>
/// <param name="Outcome">How the call ended.</param>
/// <param name="Order">The order as the exchange has it. It is there when the exchange answered with it, and never otherwise.</param>
public sealed record ExchangeOrderResult(ExchangeOrderOutcome Outcome, ExchangeOrder? Order)
{
    public ExchangeOrder? Order { get; } = (Outcome == ExchangeOrderOutcome.Answered) == (Order is not null)
        ? Order
        : throw new ArgumentException("A result has an order when the exchange answered with it, and only then.", nameof(Order));

    /// <summary>A call that ended without an order to say.</summary>
    public static ExchangeOrderResult Without(ExchangeOrderOutcome outcome) => new(outcome, null);
}

/// <summary>One execution of an order.</summary>
/// <param name="TradeId">The number that the exchange gave the execution, which no other execution of the symbol has.</param>
/// <param name="Price">The price that it was executed at.</param>
/// <param name="Quantity">How much of the asset that is traded was executed.</param>
/// <param name="Fee">The fee that the exchange took for it.</param>
/// <param name="FeeAsset">The asset that the fee was taken in, which may be the asset that was bought.</param>
/// <param name="At">When it was executed.</param>
public sealed record ExchangeFill(string TradeId, decimal Price, decimal Quantity, decimal Fee, string FeeAsset, DateTimeOffset At);

/// <summary>
/// The pair to place: a stop-loss and a take-profit that sell the same quantity of what a
/// position of the Spot market holds. Each is a stop, which is placed at the market when the
/// price reaches its trigger, and the exchange ends the other then.
/// </summary>
/// <param name="PairId">
/// The identifier that its caller gives the pair, which no other pair of the account that rests
/// has, and which neither of its orders has: as an identifier of an order is written.
/// </param>
/// <param name="Symbol">The exchange's name of the symbol.</param>
/// <param name="Quantity">How much of the asset that is traded each of the two sells.</param>
/// <param name="StopLossId">The identifier of the stop-loss (FR-BIN-6).</param>
/// <param name="StopLossPrice">The price that the stop-loss is triggered at, below the price of the symbol.</param>
/// <param name="TakeProfitId">The identifier of the take-profit.</param>
/// <param name="TakeProfitPrice">The price that the take-profit is triggered at, above the price of the symbol.</param>
public sealed record ExchangePairRequest(
    string PairId,
    string Symbol,
    decimal Quantity,
    string StopLossId,
    decimal StopLossPrice,
    string TakeProfitId,
    decimal TakeProfitPrice);

/// <summary>Which pair is taken off the exchange, with the identifiers of its two orders, by which the answer is read.</summary>
/// <param name="PairId">The identifier that its caller gave the pair.</param>
/// <param name="Symbol">The symbol of the pair.</param>
/// <param name="StopLossId">The identifier of its stop-loss.</param>
/// <param name="TakeProfitId">The identifier of its take-profit.</param>
public sealed record ExchangePairRef(string PairId, string Symbol, string StopLossId, string TakeProfitId);

/// <summary>What a call about a pair gave.</summary>
/// <param name="Outcome">How the call ended, in what it says about the two orders: as for one order.</param>
/// <param name="StopLoss">The stop-loss as the exchange has it. It is there when the exchange answered with the pair, and never otherwise.</param>
/// <param name="TakeProfit">The take-profit as the exchange has it, the same way.</param>
public sealed record ExchangePairResult(ExchangeOrderOutcome Outcome, ExchangeOrder? StopLoss, ExchangeOrder? TakeProfit)
{
    public ExchangeOrder? StopLoss { get; } = (Outcome == ExchangeOrderOutcome.Answered) == (StopLoss is not null)
        ? StopLoss
        : throw new ArgumentException("A result has the orders of a pair when the exchange answered with them, and only then.", nameof(StopLoss));

    public ExchangeOrder? TakeProfit { get; } = (Outcome == ExchangeOrderOutcome.Answered) == (TakeProfit is not null)
        ? TakeProfit
        : throw new ArgumentException("A result has the orders of a pair when the exchange answered with them, and only then.", nameof(TakeProfit));

    /// <summary>A call that ended without the orders to say.</summary>
    public static ExchangePairResult Without(ExchangeOrderOutcome outcome) => new(outcome, null, null);
}

/// <summary>What reading the executions of an order gave.</summary>
/// <param name="Outcome">How the call ended.</param>
/// <param name="Fills">The executions, oldest first, when the call succeeded, and nothing otherwise. A list is whole or it is not a list.</param>
public sealed record ExchangeFills(ExchangeCallOutcome Outcome, IReadOnlyList<ExchangeFill> Fills)
{
    public IReadOnlyList<ExchangeFill> Fills { get; } = Outcome == ExchangeCallOutcome.Succeeded || Fills.Count == 0
        ? Fills
        : throw new ArgumentException("A reading has executions only when it succeeded.", nameof(Fills));
}
