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
/// (FR-RSK-7). The take-profit is an order as well: on the Spot market of one pair with the
/// stop-loss (FR-BIN-S2), and on the Futures market an order of its own, which rests beside the
/// stop-loss (FR-BIN-F3). A position whose take-profit could neither be placed nor be made up
/// for by closing it says none.
/// </para>
/// <para>
/// It says the mode, the market, the symbol, the asset that the symbol is priced in, and the
/// timeframe that the agent had when the position was opened, and keeps saying them (FR-BIN-11,
/// FR-EXP-1). The models that held the roles of the agent then are rows of their own that name
/// it: see <see cref="PositionRole"/>. It carries its owner along to the agent that it hangs
/// on: <c>(agent_id, user_id)</c> points at <c>agents (id, user_id)</c>. See docs/ownership.md.
/// </para>
/// <para>
/// <b>What it received and paid as funding</b> on the Futures market is rows of the
/// <c>fundings</c> table that name it (FR-BIN-F4), as its fees are those of the executions of
/// its orders. The position says when its funding is read next, and says nothing there once all
/// of it is read. See "Funding" in docs/orders.md.
/// </para>
/// <para>
/// <b>What left it by no order of the agent is an amount here.</b> A position that its user, or
/// the exchange, closed or reduced at the exchange has no execution for that part, so what the
/// part made is counted at the price when the records were corrected, and kept here: see
/// <see cref="StoredOrders.ClosedAtExchangeAsync"/> and "What is corrected" in docs/orders.md.
/// A position that says an amount here, also one of nothing, is one whose result is an estimate.
/// </para>
/// <para>
/// Its result, the sums of its fees and of its funding, and its result in R are a row of their
/// own, which is written once the position is closed and all of it is stored: see
/// <see cref="PositionResult"/>. Until then, and for the equity of its agent, they are worked out
/// from its orders, their executions, and its funding (<see cref="AgentResults"/>).
/// </para>
/// </remarks>
/// <param name="id">Assigned by the application.</param>
/// <param name="agentId">The agent whose position it is.</param>
/// <param name="userId">The user whose agent it is.</param>
/// <param name="mode">The mode that the agent was in.</param>
/// <param name="market">The market that the position is on.</param>
/// <param name="symbol">The symbol that it is in.</param>
/// <param name="quoteAsset">The asset that the symbol is priced in, which its result is in.</param>
/// <param name="timeframe">The timeframe that the agent worked on.</param>
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
/// <param name="fundingDueAt">
/// When its funding is read next: when it was opened, until its funding is read for the first
/// time, and then the next funding time that the exchange said. It is null when there is
/// nothing more to read: for a position of the Spot market, which has no funding, and for a
/// closed position whose funding was read after it was closed. So a closed position that says
/// a time here is one whose funding is not all stored.
/// </param>
/// <param name="madeAtExchange">
/// What the parts of it made that left it by no order of the agent, in the asset that its symbol
/// is priced in, each at the price when the records were corrected: above zero for a profit,
/// below it for a loss. It also holds what keeps the orders that closed a part of it earlier at
/// the value that they had, when the exchange said another entry price. Null for a position
/// whose records were never corrected against the account.
/// </param>
public sealed class Position(
    Guid id,
    Guid agentId,
    Guid userId,
    TradingMode mode,
    Market market,
    string symbol,
    string quoteAsset,
    Timeframe timeframe,
    PositionSide side,
    decimal entryPrice,
    decimal quantity,
    decimal stopLoss,
    decimal? takeProfit,
    DateTimeOffset openedAt,
    DateTimeOffset? closedAt,
    ExitReason? exitReason,
    DateTimeOffset? alertedAt,
    DateTimeOffset? fundingDueAt,
    decimal? madeAtExchange = null) : IUserOwned
{
    public Guid Id { get; } = id;

    public Guid AgentId { get; } = agentId;

    public Guid UserId { get; } = userId;

    public TradingMode Mode { get; } = mode;

    public Market Market { get; } = market;

    public string Symbol { get; } = symbol;

    public string QuoteAsset { get; } = quoteAsset;

    public Timeframe Timeframe { get; } = timeframe;

    public PositionSide Side { get; } = side;

    public decimal EntryPrice { get; } = entryPrice;

    public decimal Quantity { get; } = quantity;

    public decimal StopLoss { get; } = stopLoss;

    public decimal? TakeProfit { get; } = takeProfit;

    public DateTimeOffset OpenedAt { get; } = openedAt;

    public DateTimeOffset? ClosedAt { get; } = closedAt;

    public ExitReason? ExitReason { get; } = exitReason;

    public DateTimeOffset? AlertedAt { get; } = alertedAt;

    public DateTimeOffset? FundingDueAt { get; } = fundingDueAt;

    public decimal? MadeAtExchange { get; } = madeAtExchange;

    /// <summary>The side of an order that closes or protects the position: it sells what a long position holds, and buys back what a short one sold.</summary>
    public OrderSide ClosingSide => Side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;

    /// <summary>
    /// What a quantity of the position makes between two prices, in the direction of the
    /// position: a long position gains as the price rises, and a short one as it falls. It is
    /// rounded as every amount that is added up is: see <see cref="AgentResults.Rounded"/>.
    /// </summary>
    /// <param name="side">The side of the position.</param>
    /// <param name="quantity">How much of the asset that is traded.</param>
    /// <param name="from">The price that it is counted from.</param>
    /// <param name="to">The price that it is counted to.</param>
    public static decimal Made(PositionSide side, decimal quantity, decimal from, decimal to) =>
        AgentResults.Rounded(quantity * (side == PositionSide.Long ? to - from : from - to));
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

    /// <summary>Its take-profit was executed (FR-BIN-S2).</summary>
    TakeProfit,

    /// <summary>
    /// The account of its user no longer held it, as the exchange said when it was asked: it
    /// was closed at the exchange, by its user or by the exchange, and by no order of the agent
    /// (FR-BIN-7, FR-BIN-8). An order of the agent that is found executed later says the reason
    /// in its place.
    /// </summary>
    ClosedAtExchange,

    /// <summary>
    /// Its agent reached a loss limit, its daily loss limit or its drawdown limit, so the
    /// service closed it at market, and halted the agent (FR-RSK-8).
    /// </summary>
    LossLimit,
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
        builder.Property(position => position.QuoteAsset);
        builder.Property(position => position.Timeframe);
        builder.Property(position => position.Side);
        builder.Property(position => position.EntryPrice);
        builder.Property(position => position.Quantity);
        builder.Property(position => position.StopLoss);
        builder.Property(position => position.TakeProfit);
        builder.Property(position => position.OpenedAt);
        builder.Property(position => position.ClosedAt);
        builder.Property(position => position.ExitReason);
        builder.Property(position => position.AlertedAt);
        builder.Property(position => position.FundingDueAt);
        builder.Property(position => position.MadeAtExchange);

        // An agent holds one position at most (FR-CYC-5).
        builder.HasIndex(position => position.AgentId).IsUnique().HasFilter("closed_at IS NULL");

        // The positions whose funding is still to be read, which what watches the positions
        // asks for with every run: few of all the positions that there ever were.
        builder.HasIndex(position => position.FundingDueAt).HasFilter("funding_due_at IS NOT NULL");

        // A position is found by its id together with its owner as well. What it received and
        // paid as funding hangs on it by both, and so carries its owner along. See
        // docs/ownership.md.
        builder.HasAlternateKey(position => new { position.Id, position.UserId });

        // The positions of one agent of a user, in the order they were opened in. It is also
        // the index of the key below.
        builder.HasIndex(position => new { position.AgentId, position.UserId, position.OpenedAt });

        // The closed positions of one user, by when they were closed: what a track record is
        // worked out from, for all of them or for a period (FR-EXP-2). No other index starts
        // with the owner, so without it that read would go through the positions of everyone.
        builder.HasIndex(position => new { position.UserId, position.ClosedAt });

        // The positions of an agent go with it when it is deleted, and with the account when
        // that is. The key carries the owner along (NFR-DAT-9).
        builder.HasOne<Agent>()
            .WithMany()
            .HasForeignKey(position => new { position.AgentId, position.UserId })
            .HasPrincipalKey(agent => new { agent.Id, agent.UserId })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
