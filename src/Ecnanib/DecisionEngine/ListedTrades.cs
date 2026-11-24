using Ecnanib.Platform.Persistence;
using Ecnanib.Shared;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.DecisionEngine;

/// <summary>A closed position as a list says it, with what the position says of itself beside.</summary>
/// <param name="AgentId">The agent whose position it was.</param>
/// <param name="Timeframe">The timeframe that the agent worked on when the position was opened.</param>
/// <param name="StopLoss">The price of its stop-loss, as it last was.</param>
/// <param name="TakeProfit">The price of its take-profit, or null when its plan named none.</param>
/// <param name="EntryCycleId">The cycle whose order opened it, or null where no entry of the agent is stored for it.</param>
/// <param name="Trade">The trade, as the list of the trades of its agent says it.</param>
internal sealed record ListedTrade(
    Guid AgentId, Timeframe Timeframe, decimal StopLoss, decimal? TakeProfit, Guid? EntryCycleId, AgentTrade Trade);

/// <summary>
/// Makes closed positions the trades that a list says, each with what its orders and its result
/// say of it: for the trade list of the screen of an agent (FR-RPT-2), and for the export of the
/// trades of a user (FR-RPT-4). The two say the same of a trade, because one rule makes both.
/// </summary>
internal static class ListedTrades
{
    /// <summary>Some of the closed positions that it is given, latest close first.</summary>
    /// <param name="context">The context that reads them, in the snapshot that it holds.</param>
    /// <param name="userId">The user whose positions they are: their results and their orders are asked for by owner as well.</param>
    /// <param name="closed">The closed positions to list from, asked for by owner.</param>
    /// <param name="skip">How many of the latest to leave out.</param>
    /// <param name="take">How many to list at the most.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    public static async Task<IReadOnlyList<ListedTrade>> OfAsync(
        EcnanibDbContext context,
        Guid userId,
        IQueryable<Position> closed,
        int skip,
        int take,
        CancellationToken cancellationToken)
    {
        var results = context.Set<PositionResult>().AsNoTracking().OwnedBy(userId);

        // A position that is closed has its result some time after: until then it has none, and
        // is a trade all the same.
        var listed = await (
            from position in closed
            join result in results on position.Id equals result.PositionId into written
            from result in written.DefaultIfEmpty()
            orderby position.ClosedAt descending, position.Id descending
            select new
            {
                position.Id,
                position.AgentId,
                position.Mode,
                position.Market,
                position.Symbol,
                position.QuoteAsset,
                position.Timeframe,
                position.Side,
                position.EntryPrice,
                position.StopLoss,
                position.TakeProfit,
                position.OpenedAt,
                position.ClosedAt,
                position.ExitReason,
                position.MadeAtExchange,
                Recorded = result != null,
                NetResult = (decimal?)result.NetResult,
                Fees = (decimal?)result.Fees,
                Funding = (decimal?)result.Funding,
                ResultInR = (decimal?)result.ResultInR,
                HoldingSeconds = (long?)result.HoldingSeconds,
                Complete = (bool?)result.Complete,
            })
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);

        var ids = listed.Select(trade => trade.Id).ToList();

        // What was executed of the orders of those positions: the entry of each says how much
        // it held and which cycle opened it, and the orders that closed it say at what price.
        var executed = (await context.Set<Order>()
            .AsNoTracking()
            .OwnedBy(userId)
            .Where(order => order.PositionId != null && ids.Contains(order.PositionId.Value) && order.ExecutedQuantity > 0m)
            .Select(order => new { PositionId = order.PositionId!.Value, order.Purpose, order.ExecutedQuantity, order.AveragePrice, order.CycleId })
            .ToListAsync(cancellationToken))
            .ToLookup(order => order.PositionId);

        return
        [
            .. listed.Select(trade =>
            {
                var orders = executed[trade.Id].ToList();
                var entry = orders.FirstOrDefault(order => order.Purpose == OrderPurpose.Entry);
                var exits = orders.Where(order => order.Purpose != OrderPurpose.Entry).Select(order => (order.ExecutedQuantity, order.AveragePrice)).ToList();

                return new ListedTrade(
                    trade.AgentId,
                    trade.Timeframe,
                    trade.StopLoss,
                    trade.TakeProfit,
                    entry?.CycleId,
                    new AgentTrade(
                        trade.Id,
                        trade.Mode,
                        trade.Market,
                        trade.Symbol,
                        trade.QuoteAsset,
                        trade.Side,

                        // A position has one entry. What it holds grows less as it is closed in
                        // parts, so the position itself does not say how much it held.
                        entry?.ExecutedQuantity,
                        trade.EntryPrice,
                        ExitPriceOf(exits),
                        trade.OpenedAt,
                        trade.ClosedAt!.Value,
                        trade.ExitReason,
                        trade.Recorded
                            ? new AgentTradeResult(
                                trade.NetResult ?? 0m,
                                trade.Fees ?? 0m,
                                trade.Funding ?? 0m,
                                trade.ResultInR,
                                TimeSpan.FromSeconds(trade.HoldingSeconds ?? 0L))
                            : null,
                        Estimate: trade.MadeAtExchange is not null || trade.Complete == false));
            }),
        ];
    }

    /// <summary>
    /// The average price that the orders which closed a position were executed at, by what each
    /// executed. Null where none closed it, and where one says no price: an average of the
    /// others would be a price that looks right, and is not.
    /// </summary>
    internal static decimal? ExitPriceOf(IReadOnlyList<(decimal Executed, decimal? AveragePrice)> exits)
    {
        if (exits.Count == 0 || exits.Any(exit => exit.AveragePrice is null))
        {
            return null;
        }

        try
        {
            var executed = exits.Sum(exit => exit.Executed);

            return executed > 0m
                ? AgentResults.Rounded(exits.Sum(exit => AgentResults.Rounded(exit.Executed * exit.AveragePrice!.Value)) / executed)
                : null;
        }
        catch (OverflowException)
        {
            // Beyond what can be worked out with: nothing is said then, and nothing fails.
            return null;
        }
    }
}
