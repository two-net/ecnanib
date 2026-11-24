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
/// in the direction of the position. And what left a position by no order of the agent, at the
/// price when that was found: see <see cref="Position.MadeAtExchange"/>.
/// </item>
/// <item>
/// Less the fees of the executions of its orders, each by what it is worth in that asset, which
/// its execution says: see <see cref="FeeValues"/>.
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
/// counted: a fee that has no value, which is one in an asset that the configuration file names
/// no symbol for, and a position in a symbol that is priced in another asset than the agent's.
/// </para>
/// <para>
/// <b>The sums are those of <see cref="ResultSums"/>,</b> which the result of a closed position
/// is worked out with as well: what an agent made is what its positions made, by one rule.
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

    /// <summary>
    /// An amount as it is added up: rounded to <see cref="Places"/>, a half away from zero, which
    /// is how the database rounds the amounts that it adds up itself.
    /// </summary>
    public static decimal Rounded(decimal amount) => Math.Round(amount, Places, MidpointRounding.AwayFromZero);

    /// <summary>The result of an agent now, with the marks that its loss limits are counted from as they are stored.</summary>
    /// <param name="agent">The agent, as it is stored: its mode and the asset that its symbol is priced in say what is counted.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    public async Task<AgentResult> OfAsync(Agent agent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agent);

        var id = agent.Id;

        AgentRows rows;

        await using (var context = await contexts.CreateDbContextAsync(cancellationToken))
        await using (await context.BeginSnapshotAsync(cancellationToken))
        {
            rows = await RowsAsync(context, agent, cancellationToken);
        }

        if (!rows.Priced)
        {
            logger.LogWarning(
                "The result of agent {AgentId} is not known: an order that closed a part of a position of it says no price that it was " +
                "executed at, and its executions are not stored",
                id);
        }

        decimal? price = null;

        if (rows.Open is { } open)
        {
            // Asked only for a position that is open: an agent that holds none is valued by its rows alone.
            (price, var outcome) = await PriceOfAsync(market, open, cancellationToken);

            if (price is null)
            {
                logger.LogWarning(
                    "The position of agent {AgentId} in {Symbol} could not be valued, so the equity of the agent is not known: the exchange did not give its price ({ExchangeCallOutcome})",
                    id,
                    open.Symbol,
                    outcome);
            }
        }

        return rows.At(price);
    }

    /// <summary>
    /// What the rows say of the result of an agent: what is settled of it, its open position,
    /// and the marks that its loss limits are counted from. It asks the exchange nothing: what
    /// the open position is worth is for whoever asks to say, with a price. See
    /// <see cref="AgentRows.At"/>.
    /// </summary>
    /// <remarks>
    /// It reads with the context that it is given, in the transaction that the context holds:
    /// the reads agree with each other in a snapshot, which whoever asks begins. So what asks
    /// for the results of several agents at one moment asks for them in one.
    /// </remarks>
    /// <param name="context">The context, which holds a snapshot.</param>
    /// <param name="agent">The agent, as it is stored: its mode and the asset that its symbol is priced in say what is counted.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    public static async Task<AgentRows> RowsAsync(EcnanibDbContext context, Agent agent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(agent);

        var id = agent.Id;
        var mode = agent.Mode;
        var quote = agent.QuoteAsset;

        // The positions of the agent in its mode, in the asset that its symbol is priced in.
        var positions = context.Set<Position>()
            .AsNoTracking()
            .Where(position => position.AgentId == id && position.Mode == mode && position.QuoteAsset == quote);

        // Every order of the agent there, also an entry that opened nothing: its fee is paid.
        var orders = context.Set<Order>()
            .AsNoTracking()
            .Where(order => order.AgentId == id && order.Mode == mode && order.Symbol.EndsWith(quote));

        var (made, priced) = await ResultSums.MadeAsync(context, positions, cancellationToken);
        var (paid, valued) = await ResultSums.FeesAsync(context, orders, cancellationToken);
        var funding = await ResultSums.FundingAsync(context, positions, cancellationToken);

        var open = await positions.SingleOrDefaultAsync(position => position.ClosedAt == null, cancellationToken);

        var marks = await context.Set<EquityMark>()
            .AsNoTracking()
            .SingleOrDefaultAsync(mark => mark.AgentId == id && mark.Mode == mode, cancellationToken);

        return new AgentRows(made - paid + funding, open, priced, marks, paid, valued, funding);
    }

    /// <summary>
    /// The price that an open position is valued at: the mark price on the Futures market, which
    /// is what the exchange values the position at and triggers its stop-loss on (FR-BIN-F3), and
    /// the price that the symbol last traded at on the Spot market. Null when the exchange did
    /// not say. What left a position by no order of the agent is counted at it as well: see
    /// <see cref="StoredOrders.ClosedAtExchangeAsync"/>.
    /// </summary>
    public static async Task<(decimal? Price, ExchangeCallOutcome Outcome)> PriceOfAsync(
        IExchangeMarketData market, Position position, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(market);
        ArgumentNullException.ThrowIfNull(position);

        if (PositionValues.PriceOf(position.Market) == ExchangePriceKind.Mark)
        {
            var mark = await market.GetMarkPriceAsync(position.Mode, position.Symbol, cancellationToken);

            return (mark.Outcome == ExchangeCallOutcome.Succeeded ? mark.MarkPrice : null, mark.Outcome);
        }

        var last = await market.GetPriceAsync(position.Market, position.Mode, position.Symbol, cancellationToken);

        return (last.Outcome == ExchangeCallOutcome.Succeeded ? last.Price : null, last.Outcome);
    }
}

/// <summary>
/// The sums that a result is worked out from, of the positions and the orders that it is asked
/// about: the one rule for what positions made, what their fees were, and what they received and
/// paid as funding (FR-BIN-10). The result of an agent asks it about all that the agent did in a
/// mode, and the result of a closed position about that position alone: see
/// <see cref="AgentResults"/> and <see cref="PositionResults"/>.
/// </summary>
/// <remarks>
/// It reads with the context that it is given, in the transaction that the context holds: what
/// asks for sums that agree with each other asks in a snapshot.
/// </remarks>
internal static class ResultSums
{
    /// <summary>
    /// What positions made, before fees and funding: for each order that closed one or a part of
    /// one, what was executed of it times how far its average price is from the price that the
    /// position was entered at, in the direction of the position, with what left the positions
    /// by no order of the agent.
    /// </summary>
    /// <returns>
    /// The sum, and whether every such order says the price that it was executed at: its average
    /// price, or the prices of its executions where the exchange said no average. One that says
    /// neither is not in the sum.
    /// </returns>
    public static async Task<(decimal Made, bool Priced)> MadeAsync(
        EcnanibDbContext context, IQueryable<Position> positions, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(positions);

        // The orders that closed a position, or a part of one, each with the position that it closed.
        var exits =
            from order in context.Set<Order>().AsNoTracking()
            join position in positions on order.PositionId equals (Guid?)position.Id
            where order.Purpose != OrderPurpose.Entry && order.ExecutedQuantity > 0m
            select new { order.Id, order.ExecutedQuantity, order.AveragePrice, position.EntryPrice, position.Side };

        var made = await exits
            .Where(exit => exit.AveragePrice != null)
            .SumAsync(
                exit => exit.Side == PositionSide.Long
                    ? Math.Round(exit.ExecutedQuantity * (exit.AveragePrice!.Value - exit.EntryPrice), AgentResults.Places)
                    : Math.Round(exit.ExecutedQuantity * (exit.EntryPrice - exit.AveragePrice!.Value), AgentResults.Places),
                cancellationToken);

        var priced = true;

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

            made += AgentResults.Rounded(exit.Side == PositionSide.Long ? worth - cost : cost - worth);
        }

        // What left the positions by no order of the agent, each part at the price when it was
        // found: it has no execution, and is kept with its position.
        made += await positions.SumAsync(position => position.MadeAtExchange ?? 0m, cancellationToken);

        return (made, priced);
    }

    /// <summary>
    /// The fees of the executions of orders, each by what it is worth in the asset that the
    /// symbol of its order is priced in, which its execution says.
    /// </summary>
    /// <returns>The sum, and whether every execution says what its fee is worth. A fee that has no value is not in the sum.</returns>
    public static async Task<(decimal Fees, bool Valued)> FeesAsync(
        EcnanibDbContext context, IQueryable<Order> orders, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(orders);

        var values =
            from fill in context.Set<Fill>().AsNoTracking()
            join order in orders on fill.OrderId equals order.Id
            select fill.FeeValue;

        var fees = await values.SumAsync(value => value ?? 0m, cancellationToken);
        var valued = !await values.AnyAsync(value => value == null, cancellationToken);

        return (fees, valued);
    }

    /// <summary>
    /// What positions received as funding, less what they paid, in the asset that the symbol of
    /// each is priced in. Funding in another asset is not in the sum.
    /// </summary>
    public static Task<decimal> FundingAsync(EcnanibDbContext context, IQueryable<Position> positions, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(positions);

        return (
            from row in context.Set<Funding>().AsNoTracking()
            join position in positions on row.PositionId equals position.Id
            where row.Asset == position.QuoteAsset
            select Math.Round(row.Amount, AgentResults.Places))
            .SumAsync(cancellationToken);
    }
}

/// <summary>
/// What the rows say of the result of an agent at a moment, before its open position is valued.
/// See <see cref="AgentResults.RowsAsync"/>.
/// </summary>
/// <param name="Settled">
/// What is settled of it: what its positions made by the orders that closed them, less the fees
/// of its executions, with the funding that its positions received and paid.
/// </param>
/// <param name="Open">The open position of the agent, or null when it holds none.</param>
/// <param name="Priced">Whether every order that closed a part of a position says the price that it was executed at.</param>
/// <param name="Marks">What the loss limits of the agent are counted from, as it is stored, or null when nothing is stored yet.</param>
/// <param name="Fees">
/// The fees of the executions of its orders, which are in <paramref name="Settled"/>: a sum of
/// costs, above zero. The screen of the agent says it on its own (FR-RPT-2).
/// </param>
/// <param name="FeesValued">Whether every execution says what its fee is worth. A fee that has no value is not in the sum.</param>
/// <param name="Funding">What its positions received as funding, less what they paid, which is in <paramref name="Settled"/> as well.</param>
internal sealed record AgentRows(
    decimal Settled, Position? Open, bool Priced, EquityMark? Marks, decimal Fees = 0m, bool FeesValued = true, decimal Funding = 0m)
{
    /// <summary>
    /// The result of the agent with its open position valued at a price: the one that the
    /// exchange said, or the one that a page follows. Null where the price is not known, which
    /// is a result that is not known while the agent holds a position.
    /// </summary>
    public AgentResult At(decimal? price) => new(Settled, Open, Open is null ? null : price, Priced, Marks);
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
        ? PositionValues.Unrealized(open.Side, open.EntryPrice, open.Quantity, price)
        : 0m;

    /// <summary>The net profit or loss of the agent to date, with unrealized profit and loss.</summary>
    public decimal Net => Settled + Unrealized;
}
