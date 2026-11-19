using Ecnanib.Agents;
using Ecnanib.ExchangeGateway;
using Ecnanib.Platform.Persistence;
using Ecnanib.Shared;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.DecisionEngine;

/// <summary>
/// The result of an agent: its net profit or loss to date, with unrealized profit and loss, from
/// the positions, the orders, the executions, and the funding that the Decision Engine keeps. The
/// equity of an agent is its allocated capital plus this, and its loss limits are counted from
/// it (FR-RSK-5, FR-RSK-8). See "The equity of an agent" in docs/orders.md.
/// </summary>
/// <remarks>
/// <para>
/// <b>What it adds up,</b> for the positions of the agent in the mode that the agent is in, in
/// the asset that its symbol is priced in:
/// </para>
/// <list type="bullet">
/// <item>
/// What its positions made: for each order that closed a position or a part of one, what was
/// executed times how far its average price is from the price that the position was entered at,
/// in the direction of the position.
/// </item>
/// <item>
/// Less the fees of the executions of its orders: as they are in that asset, and at the price
/// of the execution where the exchange took them in the asset that is traded.
/// </item>
/// <item>What its positions received as funding, less what they paid.</item>
/// <item>
/// What its open position would make at the price now: the mark price on the Futures market,
/// as the exchange values a position and triggers its stop-loss, and the price that the symbol
/// last traded at on the Spot market.
/// </item>
/// </list>
/// <para>
/// <b>The reads agree with each other.</b> A stop-loss that is executed writes its order and its
/// position at once, and may do so between two reads here: a sum that is read first and a
/// position that is read second would count it twice, or not at all, and an agent would be
/// halted for a loss that it does not have. So the rows are read in one snapshot. The price is
/// read after it, and only where the rows hold an open position.
/// </para>
/// <para>
/// <b>Each amount that is added up is rounded to 12 decimal places first.</b> An average price is
/// a quotient, which is stored with as many decimal places as a number can have, and the
/// product of two such numbers has more places than one can be read with. What is rounded away
/// is far below anything that is traded.
/// </para>
/// <para>
/// <b>What it does not know, it does not guess.</b> Where the price of an open position cannot
/// be read, the result says so: nothing is halted on it, and no position is opened on it. Not
/// counted, until the results of a position are kept: a fee in an asset that is neither of the
/// two of the symbol, a position in a symbol that is priced in another asset than the agent's,
/// and what was closed at the exchange by no order of the agent, by its user or by the exchange:
/// it has no execution, so it counts as closed at the price that it was entered at.
/// </para>
/// <para>
/// It only reads, and uses no key of a user: the prices are those of the market.
/// </para>
/// </remarks>
internal sealed class AgentResults(
    IDbContextFactory<EcnanibDbContext> contexts, IExchangeMarketData market, ILogger<AgentResults> logger)
{
    /// <summary>
    /// How many decimal places an amount is kept to as it is added up: more than any asset is
    /// traded with, and few enough that a sum of many of them is still a number that can be read.
    /// </summary>
    public const int Places = 12;

    /// <summary>The result of an agent now, with the marks that its loss limits are counted from as they are stored.</summary>
    /// <param name="agent">The agent, as it is stored: its mode and the asset that its symbol is priced in say what is counted.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    public async Task<AgentResult> OfAsync(Agent agent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agent);

        var id = agent.Id;
        var mode = agent.Mode;
        var quote = agent.QuoteAsset;

        decimal made;
        var priced = true;
        decimal funding;
        Position? open;
        EquityMark? marks;
        List<FeesOf> fees;

        await using (var context = await contexts.CreateDbContextAsync(cancellationToken))
        await using (await context.BeginSnapshotAsync(cancellationToken))
        {
            // The orders that closed a position of the agent, or a part of one, each with the
            // position that it closed.
            var exits =
                from order in context.Set<Order>().AsNoTracking()
                join position in context.Set<Position>().AsNoTracking() on order.PositionId equals (Guid?)position.Id
                where position.AgentId == id
                    && position.Mode == mode
                    && position.Symbol.EndsWith(quote)
                    && order.Purpose != OrderPurpose.Entry
                    && order.ExecutedQuantity > 0m
                select new { order.Id, order.ExecutedQuantity, order.AveragePrice, position.EntryPrice, position.Side };

            made = await exits
                .Where(exit => exit.AveragePrice != null)
                .SumAsync(
                    exit => exit.Side == PositionSide.Long
                        ? Math.Round(exit.ExecutedQuantity * (exit.AveragePrice!.Value - exit.EntryPrice), Places)
                        : Math.Round(exit.ExecutedQuantity * (exit.EntryPrice - exit.AveragePrice!.Value), Places),
                    cancellationToken);

            // An order of which the exchange said no average price is counted by its executions,
            // once they are stored and add up to what was executed of it. There are few such orders.
            var unpriced = await exits
                .Where(exit => exit.AveragePrice == null)
                .Select(exit => new { exit.Id, exit.ExecutedQuantity, exit.EntryPrice, exit.Side })
                .ToListAsync(cancellationToken);

            foreach (var exit in unpriced)
            {
                var executions = await context.Set<Fill>()
                    .AsNoTracking()
                    .Where(fill => fill.OrderId == exit.Id)
                    .Select(fill => new { fill.Quantity, fill.Price })
                    .ToListAsync(cancellationToken);

                if (executions.Sum(execution => execution.Quantity) != exit.ExecutedQuantity)
                {
                    priced = false;

                    continue;
                }

                var worth = executions.Sum(execution => execution.Quantity * execution.Price);
                var cost = exit.ExecutedQuantity * exit.EntryPrice;

                made += Math.Round(exit.Side == PositionSide.Long ? worth - cost : cost - worth, Places);
            }

            // The fees of every order of the agent, also of an entry that opened nothing, by
            // the asset that the exchange took them in.
            fees = await (
                from fill in context.Set<Fill>().AsNoTracking()
                join order in context.Set<Order>().AsNoTracking() on fill.OrderId equals order.Id
                where order.AgentId == id && order.Mode == mode && order.Symbol.EndsWith(quote)
                group new { Fee = Math.Round(fill.Fee, Places), AtItsPrice = Math.Round(fill.Fee * fill.Price, Places) }
                    by new { order.Symbol, fill.FeeAsset } into taken
                select new FeesOf(taken.Key.Symbol, taken.Key.FeeAsset, taken.Sum(fee => fee.Fee), taken.Sum(fee => fee.AtItsPrice)))
                .ToListAsync(cancellationToken);

            funding = await (
                from row in context.Set<Funding>().AsNoTracking()
                join position in context.Set<Position>().AsNoTracking() on row.PositionId equals position.Id
                where position.AgentId == id && position.Mode == mode && position.Symbol.EndsWith(quote) && row.Asset == quote
                select Math.Round(row.Amount, Places))
                .SumAsync(cancellationToken);

            open = await context.Set<Position>()
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    position => position.AgentId == id && position.ClosedAt == null && position.Mode == mode && position.Symbol.EndsWith(quote),
                    cancellationToken);

            marks = await context.Set<EquityMark>()
                .AsNoTracking()
                .SingleOrDefaultAsync(mark => mark.AgentId == id && mark.Mode == mode, cancellationToken);
        }

        // In the asset that the symbol is priced in, a fee is what it is. In the asset that is
        // traded it is worth what the execution paid for it. A fee in any other asset is not
        // counted: nothing here says what that asset is worth.
        var paid = fees.Sum(fee => fee.Asset == quote ? fee.Amount : fee.Symbol == fee.Asset + quote ? fee.AtItsPrice : 0m);

        if (!priced)
        {
            logger.LogWarning(
                "The result of agent {AgentId} is not known: an order that closed a part of a position of it says no price that it was " +
                "executed at, and its executions are not stored",
                id);
        }

        decimal? price = null;

        if (open is not null)
        {
            // Asked only for a position that is open: an agent that holds none is valued by its rows alone.
            (price, var outcome) = await PriceOfAsync(open, cancellationToken);

            if (price is null)
            {
                logger.LogWarning(
                    "The position of agent {AgentId} in {Symbol} could not be valued, so the equity of the agent is not known: the exchange did not give its price ({ExchangeCallOutcome})",
                    id,
                    open.Symbol,
                    outcome);
            }
        }

        return new AgentResult(made - paid + funding, open, price, priced, marks);
    }

    /// <summary>
    /// The price that an open position is valued at: the mark price on the Futures market, which
    /// is what the exchange values the position at and triggers its stop-loss on (FR-BIN-F3), and
    /// the price that the symbol last traded at on the Spot market. Null when the exchange did
    /// not say.
    /// </summary>
    private async Task<(decimal? Price, ExchangeCallOutcome Outcome)> PriceOfAsync(Position position, CancellationToken cancellationToken)
    {
        if (position.Market == Market.Futures)
        {
            var mark = await market.GetMarkPriceAsync(position.Mode, position.Symbol, cancellationToken);

            return (mark.Outcome == ExchangeCallOutcome.Succeeded ? mark.MarkPrice : null, mark.Outcome);
        }

        var last = await market.GetPriceAsync(position.Market, position.Mode, position.Symbol, cancellationToken);

        return (last.Outcome == ExchangeCallOutcome.Succeeded ? last.Price : null, last.Outcome);
    }

    /// <summary>The fees of the orders of an agent in a symbol that the exchange took in one asset.</summary>
    /// <param name="Symbol">The symbol.</param>
    /// <param name="Asset">The asset that they were taken in.</param>
    /// <param name="Amount">How much of it.</param>
    /// <param name="AtItsPrice">What that was worth in the asset that the symbol is priced in, each at the price of its execution, where the asset is the one that is traded.</param>
    private sealed record FeesOf(string Symbol, string Asset, decimal Amount, decimal AtItsPrice);
}

/// <summary>The result of an agent at a moment. See <see cref="AgentResults"/>.</summary>
/// <param name="Settled">
/// What is settled of it: what its positions made by the orders that closed them, less the fees
/// of its executions, with the funding that its positions received and paid.
/// </param>
/// <param name="Open">The open position of the agent, or null when it holds none.</param>
/// <param name="Price">The price that the open position is valued at, or null when it holds none, and when the exchange did not say.</param>
/// <param name="Priced">
/// Whether every order that closed a part of a position says the price that it was executed at:
/// its average price, or the prices of its executions where the exchange said no average.
/// </param>
/// <param name="Marks">What the loss limits of the agent are counted from, as it is stored, or null when nothing is stored yet.</param>
internal sealed record AgentResult(decimal Settled, Position? Open, decimal? Price, bool Priced, EquityMark? Marks)
{
    /// <summary>
    /// Whether the result is known: every part of it could be worked out. It is not where the
    /// price of the open position could not be read, or where an order that closed a part of a
    /// position says no price yet. Nothing is decided on a result that is not known: no agent is
    /// halted, and no position is opened.
    /// </summary>
    public bool Valued => Priced && (Open is null || Price is not null);

    /// <summary>What the open position would make at its price now, which is nothing where the agent holds none, and where the price is not known.</summary>
    public decimal Unrealized => Open is { } open && Price is { } price
        ? (open.Side == PositionSide.Long ? price - open.EntryPrice : open.EntryPrice - price) * open.Quantity
        : 0m;

    /// <summary>The net profit or loss of the agent to date, with unrealized profit and loss.</summary>
    public decimal Net => Settled + Unrealized;
}
