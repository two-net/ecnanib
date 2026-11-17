using Ecnanib.Agents;
using Ecnanib.Platform.Persistence;
using Ecnanib.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecnanib.DecisionEngine;

/// <summary>
/// One position of an agent, from when it was opened to when it was closed, as a row of the
/// <c>positions</c> table: its side, what it was entered at, how much it holds, its stop-loss and
/// its take-profit, and how it ended. See "The record of a position" in docs/orders.md.
/// </summary>
/// <remarks>
/// <para>
/// <b>A position is open while it has no time of closing.</b> An agent has one open position at
/// most (FR-CYC-5), which an index holds it to.
/// </para>
/// <para>
/// <b>Its stop-loss is a price, and an order.</b> The price is here. The order that rests on the
/// exchange at that price is a row of the <c>orders</c> table that names the position
/// (FR-RSK-7). The take-profit is a price only: no order is placed for it yet.
/// </para>
/// <para>
/// It says the mode, the market, and the symbol that the agent had when the position was
/// opened, and keeps saying them (FR-BIN-11). It carries its owner along to the agent that it
/// hangs on: <c>(agent_id, user_id)</c> points at <c>agents (id, user_id)</c>. See
/// docs/ownership.md.
/// </para>
/// <para>
/// Its result, its fees, its funding, and its result in R are not stored yet.
/// </para>
/// </remarks>
/// <param name="id">Assigned by the application.</param>
/// <param name="agentId">The agent whose position it is.</param>
/// <param name="userId">The user whose agent it is.</param>
/// <param name="mode">The mode that the agent was in.</param>
/// <param name="market">The market that the position is on.</param>
/// <param name="symbol">The symbol that it is in.</param>
/// <param name="side">Long or short.</param>
/// <param name="entryPrice">The average price that it was entered at.</param>
/// <param name="quantity">How much of the asset it holds: what an order that protects or closes it is for.</param>
/// <param name="stopLoss">The price of its stop-loss.</param>
/// <param name="takeProfit">The price of its take-profit, or null when its plan named none.</param>
/// <param name="openedAt">When it was opened.</param>
/// <param name="closedAt">When it was closed, or null while it is open.</param>
/// <param name="exitReason">How it was closed, or null while it is open.</param>
/// <param name="alertedAt">
/// When its user was told that it is open with no stop-loss and could not be closed, or null
/// when that never happened. It is said once for each time that it comes to that.
/// </param>
public sealed class Position(
    Guid id,
    Guid agentId,
    Guid userId,
    TradingMode mode,
    Market market,
    string symbol,
    PositionSide side,
    decimal entryPrice,
    decimal quantity,
    decimal stopLoss,
    decimal? takeProfit,
    DateTimeOffset openedAt,
    DateTimeOffset? closedAt,
    ExitReason? exitReason,
    DateTimeOffset? alertedAt) : IUserOwned
{
    public Guid Id { get; } = id;

    public Guid AgentId { get; } = agentId;

    public Guid UserId { get; } = userId;

    public TradingMode Mode { get; } = mode;

    public Market Market { get; } = market;

    public string Symbol { get; } = symbol;

    public PositionSide Side { get; } = side;

    public decimal EntryPrice { get; } = entryPrice;

    public decimal Quantity { get; } = quantity;

    public decimal StopLoss { get; } = stopLoss;

    public decimal? TakeProfit { get; } = takeProfit;

    public DateTimeOffset OpenedAt { get; } = openedAt;

    public DateTimeOffset? ClosedAt { get; } = closedAt;

    public ExitReason? ExitReason { get; } = exitReason;

    public DateTimeOffset? AlertedAt { get; } = alertedAt;

    /// <summary>The side of an order that closes or protects the position: it sells what a long position holds, and buys back what a short one sold.</summary>
    public OrderSide ClosingSide => Side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;
}

/// <summary>How a position was closed. A member is stored by its name, so renaming one needs a migration.</summary>
public enum ExitReason
{
    /// <summary>Its stop-loss was executed.</summary>
    StopLoss,

    /// <summary>The plan of a cycle closed it: a plan to close, or the close of a plan to reverse.</summary>
    TeamClose,

    /// <summary>Its user closed it by hand: with "Close position now", or as the agent was stopped (FR-AGT-11, FR-AGT-12).</summary>
    UserClose,

    /// <summary>Its stop-loss could not be placed, so the service closed it at market (FR-RSK-7).</summary>
    ProtectionFailed,
}

internal sealed class PositionConfiguration : IEntityTypeConfiguration<Position>
{
    public void Configure(EntityTypeBuilder<Position> builder)
    {
        builder.ToTable("positions");
        builder.HasKey(position => position.Id);

        // The application assigns the id, so a row can be written without asking the database for one.
        builder.Property(position => position.Id).ValueGeneratedNever();
        builder.Property(position => position.AgentId);
        builder.Property(position => position.UserId);
        builder.Property(position => position.Mode);
        builder.Property(position => position.Market);
        builder.Property(position => position.Symbol);
        builder.Property(position => position.Side);
        builder.Property(position => position.EntryPrice);
        builder.Property(position => position.Quantity);
        builder.Property(position => position.StopLoss);
        builder.Property(position => position.TakeProfit);
        builder.Property(position => position.OpenedAt);
        builder.Property(position => position.ClosedAt);
        builder.Property(position => position.ExitReason);
        builder.Property(position => position.AlertedAt);

        // An agent holds one position at most (FR-CYC-5).
        builder.HasIndex(position => position.AgentId).IsUnique().HasFilter("closed_at IS NULL");

        // The positions of one agent of a user, in the order they were opened in. It is also
        // the index of the key below.
        builder.HasIndex(position => new { position.AgentId, position.UserId, position.OpenedAt });

        // The positions of an agent go with it when it is deleted, and with the account when
        // that is. The key carries the owner along (NFR-DAT-9).
        builder.HasOne<Agent>()
            .WithMany()
            .HasForeignKey(position => new { position.AgentId, position.UserId })
            .HasPrincipalKey(agent => new { agent.Id, agent.UserId })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
