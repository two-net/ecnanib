using Ecnanib.Kernel;
using Ecnanib.Modules.RiskEngine.Contracts;

namespace Ecnanib.Modules.Execution.Contracts;

/// <summary>
/// The orders a decision cycle placed, for its trace (FR-UI-04). Every call takes the user, so another user's cycle
/// has no orders (NFR-SEC-04). See Order execution in docs/architecture.md.
/// </summary>
public interface ICycleOrders
{
    /// <summary>
    /// Every order that cites the cycle's passing evaluation (AC-01), oldest first: its entry, exit or close, and the
    /// stops and targets it placed or moved. An approved order cites the cycle that proposed it. A stop or target cites
    /// the cycle that last changed its protection, so a later adjustment's legs are that cycle's, not the entry's.
    /// </summary>
    Task<IReadOnlyList<CycleOrder>> ListAsync(Guid userId, Guid agentId, Guid cycleId, CancellationToken cancellationToken);
}

/// <summary>
/// One order as the trace shows it (SRS 6.1's Order). Prices are in the quote asset and quantities in the base asset
/// of <paramref name="Symbol"/> (DATA-R-02).
/// </summary>
/// <param name="Environment">The order's own, which after a switch to Live may not be the agent's (FR-EXP-08).</param>
/// <param name="Price">A limit price: a limit entry's, or a Spot target's.</param>
/// <param name="StopPrice">A stop's or a Futures target's trigger price.</param>
/// <param name="AveragePrice">What it filled at on average, once anything has.</param>
/// <param name="ExchangeOrderId">Binance's ID, once Binance has it.</param>
/// <param name="ErrorCode">Binance's code when it rejected the order.</param>
/// <param name="FilledAt">When Binance last reported a fill of it.</param>
public sealed record CycleOrder(
    Guid Id,
    TradingEnvironment Environment,
    string Symbol,
    OrderLeg Leg,
    OrderKind Kind,
    OrderSide Side,
    decimal Quantity,
    decimal? Price,
    decimal? StopPrice,
    bool ReduceOnly,
    OrderStatus Status,
    decimal ExecutedQuantity,
    decimal? AveragePrice,
    string ClientOrderId,
    long? ExchangeOrderId,
    int? ErrorCode,
    DateTimeOffset CreatedAt,
    DateTimeOffset? SentAt,
    DateTimeOffset? FilledAt,
    DateTimeOffset? FinishedAt);

/// <summary>What an order is for. Stored by name, so a value is never renamed.</summary>
public enum OrderLeg
{
    Entry,
    Stop,
    Target,

    /// <summary>
    /// Ecnanib's own close at market, under an evaluation of its own: when a stop couldn't be placed (FR-EXE-02), or
    /// for the kill switch (FR-AGT-08). The protection's <c>ClosingFor</c> says which.
    /// </summary>
    EmergencyClose,

    /// <summary>The close at market of an exit the council proposed and the Risk Engine sized (SELL or CLOSE).</summary>
    Exit,
}

/// <summary>The order type sent to Binance. Stored by name, so a value is never renamed.</summary>
public enum OrderKind
{
    Market,
    Limit,

    /// <summary>Spot STOP_LOSS, or Futures STOP_MARKET through the algo order service.</summary>
    StopMarket,

    /// <summary>Futures TAKE_PROFIT_MARKET through the algo order service.</summary>
    TakeProfitMarket,

    /// <summary>A Spot OCO's target.</summary>
    LimitMaker,
}

/// <summary>Where an order is. Stored by name, so a value is never renamed.</summary>
public enum OrderStatus
{
    /// <summary>Written, and being sent. One left behind by a crash is settled like <see cref="Unknown"/>.</summary>
    Sending,

    /// <summary>Sent with no usable answer. Settled by its client order ID once a send could no longer be accepted.</summary>
    Unknown,

    /// <summary>Never reached Binance: not sent, refused before sending, or shown missing once it had settled.</summary>
    NotPlaced,

    Open,
    PartiallyFilled,
    Filled,

    /// <summary>A Futures conditional order whose trigger was met.</summary>
    Triggered,

    /// <summary>A Futures conditional order that triggered, and whose order then ended.</summary>
    Finished,

    Canceled,
    Expired,
    Rejected,
}
