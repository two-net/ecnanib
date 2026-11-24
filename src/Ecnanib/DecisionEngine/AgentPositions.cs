using Ecnanib.Agents;
using Ecnanib.Platform.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.DecisionEngine;

/// <summary>
/// Answers Agents about what an agent holds on the exchange, from the positions and the orders
/// that the Decision Engine keeps, has the position of an agent closed at market for its user
/// (FR-AGT-8, FR-AGT-11, FR-AGT-12), and says what in the account of its user at the exchange
/// stands in the way of an agent (FR-BIN-F2). See <see cref="IAgentExposure"/> and docs/orders.md.
/// </summary>
/// <remarks>
/// <para>
/// An agent has an open position from the moment that its entry is written, and not only once
/// the entry is executed: an entry that is on its way, or whose outcome is not known, may open
/// one. So a user who stops the agent then is asked what to do with the position.
/// </para>
/// <para>
/// It places no order itself: the close is asked of what places the orders, and so is the
/// account, which is read with the key of the agent's user.
/// </para>
/// </remarks>
internal sealed class AgentExposure(IPlanOrders orders) : IAgentExposure
{
    public async Task<bool> HasOpenPositionOrOrdersAsync(EcnanibDbContext context, Guid agentId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        return await HoldsAsync(context, agentId, cancellationToken)
            || await context.Set<Order>()
                .AsNoTracking()
                .AnyAsync(order => order.AgentId == agentId && OrderStatuses.Live.Contains(order.Status), cancellationToken);
    }

    public async Task<bool> HasOpenPositionAsync(EcnanibDbContext context, Guid agentId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        return await HoldsAsync(context, agentId, cancellationToken)
            || await context.Set<Order>()
                .AsNoTracking()
                .AnyAsync(
                    order => order.AgentId == agentId
                        && order.Purpose == OrderPurpose.Entry
                        && OrderStatuses.Live.Contains(order.Status),
                    cancellationToken);
    }

    public Task<PositionCloseOutcome> ClosePositionAtMarketAsync(Guid agentId, CancellationToken cancellationToken) =>
        orders.CloseAtMarketAsync(agentId, cancellationToken);

    public Task<AccountConflict?> AccountConflictAsync(Agent agent, CancellationToken cancellationToken) =>
        orders.AccountConflictAsync(agent, cancellationToken);

    private static Task<bool> HoldsAsync(EcnanibDbContext context, Guid agentId, CancellationToken cancellationToken) =>
        context.Set<Position>()
            .AsNoTracking()
            .AnyAsync(position => position.AgentId == agentId && position.ClosedAt == null, cancellationToken);
}

/// <summary>
/// What an agent holds as its cycle starts, and as its plan is checked, from the positions and
/// the orders that the Decision Engine keeps (FR-CYC-2). See <see cref="IAgentHoldings"/> and
/// "What an agent holds" in docs/cycles.md.
/// </summary>
/// <remarks>
/// <para>
/// The stop-loss of the position is said when an order for it rests on the exchange, and not
/// otherwise: the price alone protects nothing. The same holds for its take-profit, for which an
/// order rests on both markets: as one pair with the stop-loss on the Spot market, and as an
/// order of its own on the Futures market.
/// </para>
/// <para>
/// <b>The equity of an agent is the capital that its user allocated to it, plus its net profit or
/// loss to date,</b> with what its open position would make now: <see cref="AgentResults"/> works
/// the result out from the rows. Its equity as the UTC day began and its peak equity are the
/// capital as it is now plus the marks that are stored for the agent, as what watches the loss
/// limits would write them now: see <see cref="EquityMarks.At"/>. It writes none of them.
/// </para>
/// </remarks>
internal sealed class StoredHoldings(IDbContextFactory<EcnanibDbContext> contexts, AgentResults results, TimeProvider clock)
    : IAgentHoldings
{
    public async Task<AgentHoldings> OfAsync(Agent agent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agent);

        var result = await results.OfAsync(agent, cancellationToken);
        var (_, atDayStart, peak) = EquityMarks.At(result.Marks, result.Net, agent.AllocatedCapital, agent.QuoteAsset, clock.GetUtcNow());

        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        var position = await StoredOrders.OpenPositionAsync(context, agent.Id, cancellationToken);

        // The orders that rest on the exchange: what is left of each. The page of the agent
        // says the protection of its position by the same rule: see IOpenPositions.
        var resting = await RestingOrders.OfAsync(context, agent.Id, cancellationToken);
        var (stopLoss, takeProfit) = RestingOrders.Protecting(resting, position);

        return new AgentHoldings(
            agent.AllocatedCapital + result.Net,
            position is null
                ? null
                : new PositionContext(position.Side, position.EntryPrice, position.Quantity, stopLoss?.Price, takeProfit?.Price),
            [
                .. resting.Select(order => new OpenOrder(
                    order.Purpose switch
                    {
                        OrderPurpose.StopLoss => OpenOrderKind.StopLoss,
                        OrderPurpose.TakeProfit => OpenOrderKind.TakeProfit,
                        _ => OpenOrderKind.Entry,
                    },
                    order.Side,
                    order.Quantity - order.ExecutedQuantity,
                    order.Price!.Value)),
            ],
            EquityAtDayStart: agent.AllocatedCapital + atDayStart,
            PeakEquity: agent.AllocatedCapital + peak,
            Valued: result.Valued);
    }
}

/// <summary>
/// Says whether the orders of an agent are settled with the exchange, which a cycle of the agent
/// waits for (FR-CYC-8, NFR-REL-3). See <see cref="IReconciliation"/> and "After a restart" in
/// docs/cycles.md.
/// </summary>
/// <remarks>
/// <para>
/// An agent is settled when its records hold no order of which it is not known whether the
/// exchange has it, and no open position without a stop-loss that rests. Every order is written
/// before it is sent, so an order that was under way when the application stopped is such an
/// order: the agent gets no cycle, and so places no order, until the exchange was asked about
/// it. That is what keeps a restart from placing an order twice.
/// </para>
/// <para>
/// It answers from the rows, at once. What settles the orders is a job of its own, and so is
/// what holds the records against what the account holds, which answers beside this: see
/// <see cref="ReconciledSinceStart"/>.
/// </para>
/// </remarks>
internal sealed class OrderReconciliation(StoredOrders stored) : IReconciliation
{
    public Task<bool> HasReconciledAsync(RunningAgent agent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agent);

        return stored.IsSettledAsync(agent.Agent.Id, cancellationToken);
    }
}
