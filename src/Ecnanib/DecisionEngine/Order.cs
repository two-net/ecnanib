using Ecnanib.Agents;
using Ecnanib.Platform.Persistence;
using Ecnanib.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecnanib.DecisionEngine;

/// <summary>
/// An order of an agent on the exchange, as a row of the <c>orders</c> table: which agent and
/// which cycle placed it, the identifier that it carries, what it is for, and what has become of
/// it (FR-BIN-6). See "The record of an order" in docs/orders.md.
/// </summary>
/// <remarks>
/// <para>
/// <b>The row is written before the order is sent,</b> with the identifier that the order
/// carries. So whatever happens to the call, and to the application, there is a row that says
/// that an order may be with the exchange, and by which identifier it is asked about.
/// </para>
/// <para>
/// <b>The identifier is the id of the row,</b> as 32 hexadecimal digits: no two orders have the
/// same one, and the exchange takes it on both markets. A column of its own holds it, under a
/// unique index, so that it reads as what was sent.
/// </para>
/// <para>
/// It says the mode, the market, and the symbol that the agent had when the order was placed,
/// and keeps saying them: what an agent did in Demo is Demo for good (FR-BIN-11), and an order is
/// asked about where it was placed.
/// </para>
/// <para>
/// It carries its owner along to the agent that it hangs on, as a cycle does:
/// <c>(agent_id, user_id)</c> points at <c>agents (id, user_id)</c>. It names its cycle by the
/// id alone: a cycle is recorded when it ends, which is after its orders. See docs/ownership.md.
/// </para>
/// </remarks>
/// <param name="id">The id of the row, which is also the identifier of the order.</param>
/// <param name="agentId">The agent whose order it is.</param>
/// <param name="userId">The user whose agent it is.</param>
/// <param name="mode">The mode that the agent was in.</param>
/// <param name="market">The market that the order was placed on.</param>
/// <param name="symbol">The symbol that it was placed in.</param>
/// <param name="cycleId">The cycle that placed it, or null for an order that no cycle placed: its user closed the position, or the service settled it.</param>
/// <param name="positionId">The position that it opens, protects, or closes. Null for an entry until it opens one.</param>
/// <param name="clientOrderId">The identifier that the order carries at the exchange.</param>
/// <param name="exchangeOrderId">The id that the exchange knows it by, or null while the exchange has not said one.</param>
/// <param name="purpose">What it is for.</param>
/// <param name="type">The kind of order.</param>
/// <param name="side">Whether it buys or sells.</param>
/// <param name="price">The price that a stop is triggered at, or the price of an entry at a price. Null for an order at the market.</param>
/// <param name="stopLoss">
/// The price of the stop-loss of the position that an entry opens, as the Risk Guard accepted
/// it. It is kept with the entry, so that the position is protected at that price also when the
/// entry is found to be executed later, by what settles it. Null for every other order.
/// </param>
/// <param name="takeProfit">The price of the take-profit of that position, kept the same way. Null for every other order.</param>
/// <param name="quantity">How much of the asset that is traded it is for.</param>
/// <param name="status">What has become of it.</param>
/// <param name="executedQuantity">How much of it is executed, as far as the exchange has said.</param>
/// <param name="averagePrice">The average price of what is executed, or null when nothing is.</param>
/// <param name="exitReason">Why an order that closes a position closes it. Null for every other order.</param>
/// <param name="sends">How many times it was sent: once, or twice when the exchange did not have it after the first.</param>
/// <param name="fillsRecorded">Whether its executions are stored.</param>
/// <param name="createdAt">When the row was written, before the order was sent.</param>
/// <param name="updatedAt">When its status last changed.</param>
/// <param name="checkedAt">When the exchange last said what has become of it, or null when it never did.</param>
/// <param name="pairId">
/// The pair that it is one order of: the stop-loss and the take-profit of a position of the Spot
/// market, which the exchange holds as one list and of which it ends one when the other is
/// triggered (FR-BIN-S2). Both carry it, and it is the identifier that the pair carries at the
/// exchange. Null for every order that is of no pair.
/// </param>
/// <param name="cancelAfter">
/// When an entry at a price that still rests is taken off the exchange: the next close of the
/// candles of its agent, which is when the next cycle of the agent starts (FR-BIN-S2). Null for
/// every other order.
/// </param>
public sealed class Order(
    Guid id,
    Guid agentId,
    Guid userId,
    TradingMode mode,
    Market market,
    string symbol,
    Guid? cycleId,
    Guid? positionId,
    string clientOrderId,
    string? exchangeOrderId,
    OrderPurpose purpose,
    OrderType type,
    OrderSide side,
    decimal? price,
    decimal? stopLoss,
    decimal? takeProfit,
    decimal quantity,
    OrderStatus status,
    decimal executedQuantity,
    decimal? averagePrice,
    ExitReason? exitReason,
    int sends,
    bool fillsRecorded,
    DateTimeOffset createdAt,
    DateTimeOffset updatedAt,
    DateTimeOffset? checkedAt,
    Guid? pairId,
    DateTimeOffset? cancelAfter) : IUserOwned
{
    public Guid Id { get; } = id;

    public Guid AgentId { get; } = agentId;

    public Guid UserId { get; } = userId;

    public TradingMode Mode { get; } = mode;

    public Market Market { get; } = market;

    public string Symbol { get; } = symbol;

    public Guid? CycleId { get; } = cycleId;

    public Guid? PositionId { get; } = positionId;

    public string ClientOrderId { get; } = clientOrderId;

    public string? ExchangeOrderId { get; } = exchangeOrderId;

    public OrderPurpose Purpose { get; } = purpose;

    public OrderType Type { get; } = type;

    public OrderSide Side { get; } = side;

    public decimal? Price { get; } = price;

    public decimal? StopLoss { get; } = stopLoss;

    public decimal? TakeProfit { get; } = takeProfit;

    public decimal Quantity { get; } = quantity;

    public OrderStatus Status { get; } = status;

    public decimal ExecutedQuantity { get; } = executedQuantity;

    public decimal? AveragePrice { get; } = averagePrice;

    public ExitReason? ExitReason { get; } = exitReason;

    public int Sends { get; } = sends;

    public bool FillsRecorded { get; } = fillsRecorded;

    public DateTimeOffset CreatedAt { get; } = createdAt;

    public DateTimeOffset UpdatedAt { get; } = updatedAt;

    public DateTimeOffset? CheckedAt { get; } = checkedAt;

    public Guid? PairId { get; } = pairId;

    public DateTimeOffset? CancelAfter { get; } = cancelAfter;

    /// <summary>The identifier that the order of a row carries: the id of the row, as 32 hexadecimal digits.</summary>
    public static string ClientOrderIdOf(Guid id) => id.ToString("N");

    /// <summary>
    /// The identifier that a pair carries at the exchange: its id, written as the identifier of
    /// an order is. No order has it: the id of a pair is the id of no row.
    /// </summary>
    public static string PairIdOf(Guid pairId) => pairId.ToString("N");
}

/// <summary>What an order is for. A member is stored by its name, so renaming one needs a migration.</summary>
public enum OrderPurpose
{
    /// <summary>It opens a position.</summary>
    Entry,

    /// <summary>It rests on the exchange, and closes the position at a loss when the price reaches it (FR-RSK-7).</summary>
    StopLoss,

    /// <summary>It closes the position at the market.</summary>
    Close,

    /// <summary>
    /// It rests on the exchange, and closes the position at a profit when the price reaches it.
    /// On the Spot market it is one order of a pair with the stop-loss: the exchange ends the
    /// other when one of the two is triggered (FR-BIN-S2). On the Futures market it is an order
    /// of its own, which the exchange ends for nothing (FR-BIN-F3).
    /// </summary>
    TakeProfit,
}

/// <summary>The kind of an order. A member is stored by its name, so renaming one needs a migration.</summary>
public enum OrderType
{
    /// <summary>It is executed at once, at the best prices that there are.</summary>
    Market,

    /// <summary>It rests until the price reaches its trigger, and is then placed at the market.</summary>
    Stop,

    /// <summary>
    /// It is executed at its price or a better one, and what is not executed at once rests until
    /// it is, or until it is taken off the exchange (FR-BIN-S2).
    /// </summary>
    Limit,
}

/// <summary>
/// What has become of an order. A member is stored by its name, so renaming one needs a
/// migration. See "What becomes of an order" in docs/orders.md.
/// </summary>
public enum OrderStatus
{
    /// <summary>The row is written, and the order is being sent. A row that stays so was cut off: it is asked about as one whose outcome is unknown.</summary>
    Sending,

    /// <summary>The call that placed it ended without saying whether the exchange took it. It is asked about before anything else is done with it (FR-BIN-6).</summary>
    Unknown,

    /// <summary>It was not placed: it was not sent, or the exchange does not have it once it could no longer arrive.</summary>
    NotPlaced,

    /// <summary>The exchange refused it.</summary>
    Rejected,

    /// <summary>It rests on the exchange, and none of it is executed.</summary>
    Open,

    /// <summary>A part of it is executed, and the rest is still with the exchange.</summary>
    PartiallyFilled,

    /// <summary>All of it is executed.</summary>
    Filled,

    /// <summary>It was taken off the exchange.</summary>
    Canceled,

    /// <summary>The exchange ended it. A part of it may be executed.</summary>
    Expired,
}

/// <summary>What the status of an order says.</summary>
public static class OrderStatuses
{
    /// <summary>
    /// The statuses of an order that is not over: it is being sent, its outcome is unknown, or
    /// it is with the exchange. An agent has one such order of each purpose at most.
    /// </summary>
    public static readonly IReadOnlyList<OrderStatus> Live =
        [OrderStatus.Sending, OrderStatus.Unknown, OrderStatus.Open, OrderStatus.PartiallyFilled];

    /// <summary>The statuses of an order of which it is not known whether the exchange has it.</summary>
    public static readonly IReadOnlyList<OrderStatus> Unsettled = [OrderStatus.Sending, OrderStatus.Unknown];

    /// <summary>The statuses of an order that rests on the exchange.</summary>
    public static readonly IReadOnlyList<OrderStatus> Resting = [OrderStatus.Open, OrderStatus.PartiallyFilled];

    /// <summary>Whether an order in a status is not over.</summary>
    public static bool IsLive(OrderStatus status) => Live.Contains(status);

    /// <summary>Whether it is not known whether the exchange has an order in a status.</summary>
    public static bool IsUnsettled(OrderStatus status) => Unsettled.Contains(status);

    /// <summary>Whether an order in a status rests on the exchange.</summary>
    public static bool IsResting(OrderStatus status) => Resting.Contains(status);
}

internal sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> builder)
    {
        builder.ToTable("orders");
        builder.HasKey(order => order.Id);

        // The application assigns the id, before the order is sent: it is what the order carries.
        builder.Property(order => order.Id).ValueGeneratedNever();
        builder.Property(order => order.AgentId);
        builder.Property(order => order.UserId);
        builder.Property(order => order.Mode);
        builder.Property(order => order.Market);
        builder.Property(order => order.Symbol);
        builder.Property(order => order.CycleId);
        builder.Property(order => order.PositionId);
        builder.Property(order => order.ClientOrderId);
        builder.Property(order => order.ExchangeOrderId);
        builder.Property(order => order.Purpose);
        builder.Property(order => order.Type);
        builder.Property(order => order.Side);
        builder.Property(order => order.Price);
        builder.Property(order => order.StopLoss);
        builder.Property(order => order.TakeProfit);
        builder.Property(order => order.Quantity);
        builder.Property(order => order.Status);
        builder.Property(order => order.ExecutedQuantity);
        builder.Property(order => order.AveragePrice);
        builder.Property(order => order.ExitReason);
        builder.Property(order => order.Sends);
        builder.Property(order => order.FillsRecorded);
        builder.Property(order => order.CreatedAt);
        builder.Property(order => order.UpdatedAt);
        builder.Property(order => order.CheckedAt);
        builder.Property(order => order.PairId);
        builder.Property(order => order.CancelAfter);

        // Every order carries an identifier that no other order has (FR-BIN-6).
        builder.HasIndex(order => order.ClientOrderId).IsUnique();

        // An agent has one order of each purpose that is not over, at most: no second entry
        // beside one whose outcome is unknown or that rests, and one stop-loss that rests, with
        // one take-profit beside it. The statuses are those of OrderStatuses.Live. It is also
        // what finds the orders that are to be settled.
        builder.HasIndex(order => new { order.AgentId, order.Purpose })
            .IsUnique()
            .HasFilter("status IN ('Sending', 'Unknown', 'Open', 'PartiallyFilled')");

        // The orders of one agent of a user, in the order they were written in. It is also the
        // index of the key below.
        builder.HasIndex(order => new { order.AgentId, order.UserId, order.CreatedAt });

        // The executions of an order point at it with its owner.
        builder.HasAlternateKey(order => new { order.Id, order.UserId });

        // The orders of an agent go with it when it is deleted, and with the account when that
        // is. The key carries the owner along, so the order of an agent is its user's (NFR-DAT-9).
        builder.HasOne<Agent>()
            .WithMany()
            .HasForeignKey(order => new { order.AgentId, order.UserId })
            .HasPrincipalKey(agent => new { agent.Id, agent.UserId })
            .OnDelete(DeleteBehavior.Cascade);

        // The orders of a position go with it, which goes with its agent.
        builder.HasOne<Position>()
            .WithMany()
            .HasForeignKey(order => order.PositionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
