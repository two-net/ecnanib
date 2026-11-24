using Ecnanib.ExchangeGateway;
using Ecnanib.Platform.Persistence;
using Ecnanib.Shared;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.DecisionEngine;

/// <summary>
/// The open position of an agent, for the page of the agent: positions update in the UI without
/// a manual refresh (FR-NTF-2). See "The position on a page" in docs/orders.md.
/// </summary>
/// <remarks>
/// <para>
/// It is asked for the user that a page hands over, which is the one who is signed in: the
/// Decision Engine may not use Accounts. The position of an agent of another user is not found.
/// </para>
/// <para>
/// It only reads the records, and asks the exchange nothing: what a position is worth now is
/// worked out by whoever asks, from a price that is followed. See <see cref="PositionValues"/>.
/// </para>
/// </remarks>
public interface IOpenPositions
{
    /// <summary>The open position of an agent of a user, or null when the agent holds none.</summary>
    /// <param name="userId">The user whose agent it is.</param>
    /// <param name="agentId">The agent.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<OpenPosition?> OfAgentAsync(Guid userId, Guid agentId, CancellationToken cancellationToken);
}

/// <summary>The open position of an agent, as a page says it.</summary>
/// <param name="Market">The market that it is on.</param>
/// <param name="Mode">The mode that it was opened in, which is that of its agent.</param>
/// <param name="Symbol">Its symbol.</param>
/// <param name="QuoteAsset">The asset that its symbol is priced in, which what it makes is in.</param>
/// <param name="Side">Long or short.</param>
/// <param name="Quantity">How much of the asset it holds.</param>
/// <param name="EntryPrice">The average price that it was entered at.</param>
/// <param name="StopLoss">The price of its stop-loss, when an order for it rests on the exchange, and null when none does: the price alone protects nothing.</param>
/// <param name="TakeProfit">The price of its take-profit, when an order for it rests on the exchange.</param>
/// <param name="OpenedAt">When it was opened.</param>
public sealed record OpenPosition(
    Market Market,
    TradingMode Mode,
    string Symbol,
    string QuoteAsset,
    PositionSide Side,
    decimal Quantity,
    decimal EntryPrice,
    decimal? StopLoss,
    decimal? TakeProfit,
    DateTimeOffset OpenedAt)
{
    /// <summary>
    /// What the position would make at a price, before fees and funding, in the asset that its
    /// symbol is priced in: above zero for a profit, below it for a loss.
    /// </summary>
    public decimal UnrealizedAt(decimal price) => PositionValues.Unrealized(Side, EntryPrice, Quantity, price);
}

/// <summary>
/// The one rule for what an open position is worth: which price it is valued at, and what it
/// would make at a price. The equity of an agent is worked out with it, and so is what a page
/// says of a position (FR-RSK-5, FR-RSK-8). See "The equity of an agent" in docs/orders.md.
/// </summary>
public static class PositionValues
{
    /// <summary>
    /// The price that a position of a market is valued at: the mark price on the Futures market,
    /// which is what the exchange values the position at and triggers its stop-loss on
    /// (FR-BIN-F3), and the price that the symbol last traded at on the Spot market.
    /// </summary>
    public static ExchangePriceKind PriceOf(Market market) =>
        market == Market.Futures ? ExchangePriceKind.Mark : ExchangePriceKind.LastTraded;

    /// <summary>What a position would make at a price: how far the price is from the price that it was entered at, in its direction, times what it holds.</summary>
    public static decimal Unrealized(PositionSide side, decimal entryPrice, decimal quantity, decimal price) =>
        (side == PositionSide.Long ? price - entryPrice : entryPrice - price) * quantity;
}

/// <summary>
/// The orders of an agent that rest on the exchange, as what an agent holds says them, and the
/// protection of its position among them. The stop-loss of a position is said when an order for
/// it rests, and not otherwise, and so is its take-profit: for a cycle, and for a page.
/// </summary>
internal static class RestingOrders
{
    /// <summary>The orders of an agent that rest on the exchange with a price: an entry that has not filled, a stop-loss, and a take-profit.</summary>
    public static async Task<IReadOnlyList<Order>> OfAsync(EcnanibDbContext context, Guid agentId, CancellationToken cancellationToken) =>
    [
        .. (await StoredOrders.LiveOrdersAsync(context, agentId, cancellationToken))
            .Where(order => OrderStatuses.IsResting(order.Status) && order.Purpose != OrderPurpose.Close && order.Price is not null),
    ];

    /// <summary>The stop-loss and the take-profit of a position among the orders that rest, each null when none rests for it.</summary>
    public static (Order? StopLoss, Order? TakeProfit) Protecting(IReadOnlyList<Order> resting, Position? position) =>
    (
        resting.FirstOrDefault(order => order.Purpose == OrderPurpose.StopLoss && order.PositionId == position?.Id),
        resting.FirstOrDefault(order => order.Purpose == OrderPurpose.TakeProfit && order.PositionId == position?.Id)
    );
}

/// <summary>Reads the open position of an agent from the rows that the Decision Engine keeps. See <see cref="IOpenPositions"/>.</summary>
internal sealed class OpenPositions(IDbContextFactory<EcnanibDbContext> contexts) : IOpenPositions
{
    public async Task<OpenPosition?> OfAgentAsync(Guid userId, Guid agentId, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        // The position and its orders as they were at one moment: a stop-loss that is executed
        // writes its order and its position at once.
        await using var snapshot = await context.BeginSnapshotAsync(cancellationToken);

        // Asked for by owner: the position of an agent of another user is not found.
        var position = await context.Set<Position>()
            .AsNoTracking()
            .OwnedBy(userId)
            .SingleOrDefaultAsync(held => held.AgentId == agentId && held.ClosedAt == null, cancellationToken);

        if (position is null)
        {
            return null;
        }

        var (stopLoss, takeProfit) = RestingOrders.Protecting(await RestingOrders.OfAsync(context, agentId, cancellationToken), position);

        return new OpenPosition(
            position.Market,
            position.Mode,
            position.Symbol,
            position.QuoteAsset,
            position.Side,
            position.Quantity,
            position.EntryPrice,
            stopLoss?.Price,
            takeProfit?.Price,
            position.OpenedAt);
    }
}
