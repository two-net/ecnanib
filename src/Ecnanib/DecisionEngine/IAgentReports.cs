using Ecnanib.Agents;
using Ecnanib.ExchangeGateway;
using Ecnanib.Experience;
using Ecnanib.Platform.Persistence;
using Ecnanib.Platform.Settings;
using Ecnanib.RiskGuard;
using Ecnanib.Shared;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.DecisionEngine;

/// <summary>
/// How one agent of a user has performed, for the screen of the agent: its performance figures,
/// its equity over time, and its closed trades (FR-RPT-2). See docs/agent-screen.md.
/// </summary>
/// <remarks>
/// <para>
/// <b>The figures and the equity are of the mode that the agent is in now, in the asset that
/// its symbol is priced in.</b> What an agent did in Demo is never combined with what it does in
/// Live (FR-BIN-11). The trades are all that the agent closed, and each says the mode it was in.
/// </para>
/// <para>
/// <b>Everything is read at one moment,</b> in one snapshot: a position that is closed between
/// two reads is in all of the answer, or in none of it.
/// </para>
/// <para>
/// <b>It asks the exchange nothing.</b> The equity of the agent is worked out as the dashboard
/// works it out, with its open position at the price that the watch has: see
/// <see cref="IAgentEquities"/>. Where the watch has none, it is not known, and neither is the
/// return.
/// </para>
/// <para>
/// It is asked for the user that a page hands over, which is the one who is signed in: the
/// Decision Engine may not use Accounts. An agent of another user is not found. It only reads.
/// </para>
/// </remarks>
public interface IAgentReports
{
    /// <summary>The report of an agent of a user, or null when that user has no such agent.</summary>
    /// <param name="userId">The user who is signed in.</param>
    /// <param name="agentId">The agent.</param>
    /// <param name="tradesPage">Which page of its closed trades, from 1. A page after the last holds no trade.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    Task<AgentReport?> OfAgentAsync(Guid userId, Guid agentId, int tradesPage, CancellationToken cancellationToken);
}

/// <summary>How an agent has performed.</summary>
/// <param name="Equity">Its equity now, with what it holds, by the rule of the dashboard.</param>
/// <param name="Figures">Its performance figures, in the mode that it is in.</param>
/// <param name="Curve">Its equity over time as it was recorded, in the mode that it is in.</param>
/// <param name="Trades">One page of its closed trades.</param>
public sealed record AgentReport(AgentEquity Equity, AgentFigures Figures, EquityCurve Curve, AgentTrades Trades);

/// <summary>The performance figures of an agent in one mode (FR-RPT-2).</summary>
/// <param name="NetResult">Its net profit or loss to date, with unrealized profit and loss, or null where its equity is not known.</param>
/// <param name="ReturnPercent">
/// Its return: the net result in percent of the capital that it is allocated now. Null where
/// its equity is not known.
/// </param>
/// <param name="Record">
/// What its closed trades came to, in R: the win rate, the average R, and the profit factor are
/// those of a track record, by the one rule of the experience screens (FR-EXP-2).
/// </param>
/// <param name="Closed">How many positions it closed in the mode. Those that have no result in R yet, or none at all, are in no measure of the record.</param>
/// <param name="Fees">The fees of the executions of its orders, as far as they are valued: a sum of costs, above zero.</param>
/// <param name="FeesValued">Whether every execution says what its fee is worth.</param>
/// <param name="Funding">What its positions received as funding, less what they paid: above zero when it received more.</param>
public sealed record AgentFigures(
    decimal? NetResult, decimal? ReturnPercent, TrackRecord Record, int Closed, decimal Fees, bool FeesValued, decimal Funding);

/// <summary>The equity of an agent at a moment, as it was recorded.</summary>
/// <param name="At">The moment.</param>
/// <param name="Equity">Its equity then: its capital, plus its net profit or loss to date.</param>
/// <param name="Capital">The capital that it was allocated then.</param>
public sealed record EquityPoint(DateTimeOffset At, decimal Equity, decimal Capital);

/// <summary>The largest drawdown of an agent: the deepest fall of its equity from its peak.</summary>
/// <param name="Percent">The fall, in percent of the peak equity: above zero.</param>
/// <param name="Amount">The fall as an amount, under the capital that the agent was allocated when it was at its lowest.</param>
/// <param name="PeakAt">When the equity was at the peak that it fell from, or null where that peak is the capital that the agent began with.</param>
/// <param name="LowAt">When the equity was at its lowest under that peak.</param>
public sealed record EquityDrawdown(decimal Percent, decimal Amount, DateTimeOffset? PeakAt, DateTimeOffset LowAt);

/// <summary>The equity of an agent over time, as a page draws it and says it.</summary>
/// <param name="Recorded">How many times its equity was recorded.</param>
/// <param name="Points">
/// What is drawn, in the order of time: every recording of a short series, and of a long one
/// the first, the lowest, the highest, and the last of each of the equal times that it is cut
/// into. The equity holds each value until the next.
/// </param>
/// <param name="First">The first recording, or null when there is none.</param>
/// <param name="Latest">The latest recording.</param>
/// <param name="Lowest">The recording of the lowest equity: the earliest of them.</param>
/// <param name="Highest">The recording of the highest equity: the earliest of them.</param>
/// <param name="LargestDrawdown">The largest drawdown over every recording, or null where the equity never fell from a peak.</param>
public sealed record EquityCurve(
    int Recorded,
    IReadOnlyList<EquityPoint> Points,
    EquityPoint? First,
    EquityPoint? Latest,
    EquityPoint? Lowest,
    EquityPoint? Highest,
    EquityDrawdown? LargestDrawdown)
{
    /// <summary>The curve of an agent whose equity was never recorded.</summary>
    public static EquityCurve None { get; } = new(0, [], First: null, Latest: null, Lowest: null, Highest: null, LargestDrawdown: null);
}

/// <summary>One page of the closed trades of an agent, latest close first.</summary>
/// <param name="Total">How many positions the agent closed in all, in either mode.</param>
/// <param name="Page">Which page it is, from 1.</param>
/// <param name="Pages">How many pages there are: 1 at the least.</param>
/// <param name="Trades">The trades of the page.</param>
public sealed record AgentTrades(int Total, int Page, int Pages, IReadOnlyList<AgentTrade> Trades)
{
    /// <summary>How many trades a page holds.</summary>
    public const int PageSize = 25;
}

/// <summary>One closed position of an agent, as the list of its trades says it.</summary>
/// <param name="PositionId">The id of the position.</param>
/// <param name="Mode">The mode that the agent was in when it was opened, which stays as it was when the agent is switched (FR-BIN-11).</param>
/// <param name="Market">The market that it was on.</param>
/// <param name="Symbol">Its symbol.</param>
/// <param name="Asset">The asset that its symbol is priced in, which its amounts are in.</param>
/// <param name="Side">Long or short.</param>
/// <param name="Quantity">
/// How much it held when it was opened: what was executed of its entry. Null where no entry of
/// the agent is stored for it.
/// </param>
/// <param name="EntryPrice">The average price that it was entered at.</param>
/// <param name="ExitPrice">
/// The average price that the orders of the agent closed it at, by what each executed. Null
/// where no order of the agent closed it, and where one that did says no price.
/// </param>
/// <param name="OpenedAt">When it was opened.</param>
/// <param name="ClosedAt">When it was closed.</param>
/// <param name="ExitReason">How it was closed.</param>
/// <param name="Result">Its recorded result, or null while that is not recorded yet: a result is recorded some time after the close.</param>
/// <param name="Estimate">
/// Whether its result is an estimate: a part of it left the agent by no order of the agent, or
/// not all of its result is what the exchange said.
/// </param>
public sealed record AgentTrade(
    Guid PositionId,
    TradingMode Mode,
    Market Market,
    string Symbol,
    string Asset,
    PositionSide Side,
    decimal? Quantity,
    decimal EntryPrice,
    decimal? ExitPrice,
    DateTimeOffset OpenedAt,
    DateTimeOffset ClosedAt,
    ExitReason? ExitReason,
    AgentTradeResult? Result,
    bool Estimate);

/// <summary>The recorded result of a closed trade of an agent, in the asset that its symbol is priced in.</summary>
/// <param name="NetResult">Its net profit or loss: what it made, less the fees, with the funding.</param>
/// <param name="Fees">The fees of the executions of its orders: above zero.</param>
/// <param name="Funding">What it received as funding, less what it paid.</param>
/// <param name="ResultInR">The net result as a multiple of R, or null where R is not known.</param>
/// <param name="Held">How long it was held.</param>
public sealed record AgentTradeResult(decimal NetResult, decimal Fees, decimal Funding, decimal? ResultInR, TimeSpan Held);

/// <summary>
/// The one rule for what the recorded equity of an agent says: its largest drawdown, and what
/// of it is drawn. It reads nothing: it is handed the recordings, in the order of time.
/// </summary>
/// <remarks>
/// <para>
/// <b>The drawdown is counted as the drawdown limit counts it</b> (FR-RSK-8): the fall of the
/// equity from its peak, in percent of the peak, by <see cref="LossLimits.LossPercent"/>. The
/// peak is the highest result that was seen, and starts at the capital, which is a result of
/// nothing. A capital that its user changed makes no fall, and takes none away: the peak is
/// worked out anew for the capital, by <see cref="EquityMarks.Under"/>, as what watches the loss
/// limits does it.
/// </para>
/// <para>
/// <b>It differs from the limit in two things.</b> The peak that the limit is counted from starts
/// anew when the limit halts the agent, and this one never does: it is the largest fall that
/// there was. And it is counted over the recordings, of which an agent that holds a position has
/// one in a quarter of an hour at the most: a fall between two of them is not in it.
/// </para>
/// <para>
/// <b>The largest is the largest in percent.</b> Two amounts under two capitals are not told
/// which is the larger.
/// </para>
/// </remarks>
public static class EquityCurves
{
    /// <summary>The most points of a curve that are drawn: a line of more is no finer on a screen.</summary>
    public const int MostPoints = 240;

    /// <summary>What a series of recordings says, in the order of time.</summary>
    /// <param name="recorded">The recordings of one agent in one mode and one asset, earliest first.</param>
    /// <param name="mostPoints">The most points that are drawn.</param>
    public static EquityCurve Of(IReadOnlyList<EquityPoint> recorded, int mostPoints = MostPoints)
    {
        ArgumentNullException.ThrowIfNull(recorded);
        ArgumentOutOfRangeException.ThrowIfLessThan(mostPoints, 4);

        if (recorded.Count == 0)
        {
            return EquityCurve.None;
        }

        var lowest = recorded[0];
        var highest = recorded[0];

        foreach (var point in recorded)
        {
            if (point.Equity < lowest.Equity)
            {
                lowest = point;
            }

            if (point.Equity > highest.Equity)
            {
                highest = point;
            }
        }

        return new EquityCurve(recorded.Count, Drawn(recorded, mostPoints), recorded[0], recorded[^1], lowest, highest, LargestDrawdown(recorded));
    }

    /// <summary>The largest drawdown over the recordings, or null where the equity never fell from a peak.</summary>
    public static EquityDrawdown? LargestDrawdown(IReadOnlyList<EquityPoint> recorded)
    {
        ArgumentNullException.ThrowIfNull(recorded);

        EquityDrawdown? largest = null;

        // The peak, as a result under the capital of the recording before: it starts at the
        // capital, which is a result of nothing, at no moment that was recorded.
        var peak = 0m;
        DateTimeOffset? peakAt = null;
        decimal? before = null;

        foreach (var point in recorded)
        {
            try
            {
                var result = point.Equity - point.Capital;

                if (before is { } capital && capital != point.Capital)
                {
                    // Its user changed the capital: the peak is the same share above the equity as before.
                    peak = EquityMarks.Under(point.Capital, peak, capital, result);
                }

                before = point.Capital;

                if (result >= peak)
                {
                    peak = result;
                    peakAt = point.At;

                    continue;
                }

                var from = point.Capital + peak;

                // A loss that is counted from nothing is not known: see LossLimits.IsKnown.
                if (!LossLimits.IsKnown(from))
                {
                    continue;
                }

                var percent = LossLimits.LossPercent(point.Equity, from);

                if (largest is null || percent > largest.Percent)
                {
                    largest = new EquityDrawdown(percent, from - point.Equity, peakAt, point.At);
                }
            }
            catch (OverflowException)
            {
                // Numbers that cannot be worked out with are no numbers of a market: the recording says nothing.
            }
        }

        return largest;
    }

    /// <summary>
    /// What is drawn of the recordings: all of a short series, and of a long one the first, the
    /// lowest, the highest, and the last of each of the equal times that it is cut into, so that
    /// no peak and no fall is lost from the line.
    /// </summary>
    private static IReadOnlyList<EquityPoint> Drawn(IReadOnlyList<EquityPoint> recorded, int mostPoints)
    {
        if (recorded.Count <= mostPoints)
        {
            return recorded;
        }

        var times = mostPoints / 4;
        var span = recorded[^1].At.UtcTicks - recorded[0].At.UtcTicks;

        // Each time is as long as the next, and the last recording is in the last of them.
        var length = (span / times) + 1;
        var from = recorded[0].At.UtcTicks;

        var drawn = new List<EquityPoint>(mostPoints);
        var start = 0;

        while (start < recorded.Count)
        {
            var time = (recorded[start].At.UtcTicks - from) / length;
            var end = start;
            var (low, high) = (start, start);

            while (end + 1 < recorded.Count && (recorded[end + 1].At.UtcTicks - from) / length == time)
            {
                end++;

                if (recorded[end].Equity < recorded[low].Equity)
                {
                    low = end;
                }

                if (recorded[end].Equity > recorded[high].Equity)
                {
                    high = end;
                }
            }

            // In the order of time, each once.
            foreach (var index in new SortedSet<int> { start, low, high, end })
            {
                drawn.Add(recorded[index]);
            }

            start = end + 1;
        }

        return drawn;
    }
}

/// <summary>Reads how an agent has performed from the rows that the Decision Engine keeps. See <see cref="IAgentReports"/>.</summary>
internal sealed class AgentReports(
    IDbContextFactory<EcnanibDbContext> contexts, IExchangePriceWatch prices, ExperienceSettings experience, TimeProvider clock)
    : IAgentReports
{
    public async Task<AgentReport?> OfAgentAsync(Guid userId, Guid agentId, int tradesPage, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(tradesPage, 1);

        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        // The figures, the curve, and the trades are rows of several tables, and a position may
        // be closed while they are read: they are read as they were at one moment.
        await using var snapshot = await context.BeginSnapshotAsync(cancellationToken);

        // Asked for by owner: an agent of another user is not found, and neither is anything of it.
        var agent = await context.Set<Agent>()
            .AsNoTracking()
            .OwnedBy(userId)
            .SingleOrDefaultAsync(stored => stored.Id == agentId, cancellationToken);

        if (agent is null)
        {
            return null;
        }

        var mode = agent.Mode;
        var asset = agent.QuoteAsset;

        var rows = await AgentResults.RowsAsync(context, agent, cancellationToken);

        // The protection of the position as it rests on the exchange, by the rule of the page
        // of the agent: the price alone protects nothing.
        var held = rows.Open is { } open
            ? OpenPositions.Of(open, await RestingOrders.OfAsync(context, agent.Id, cancellationToken))
            : null;

        // The equity of the agent over time, in its mode and its asset: the index of the key
        // gives the rows in the order of time.
        var recorded = await context.Set<EquitySnapshot>()
            .AsNoTracking()
            .OwnedBy(userId)
            .Where(row => row.AgentId == agentId && row.Mode == mode && row.Asset == asset)
            .OrderBy(row => row.At)
            .Select(row => new EquityPoint(row.At, row.Equity, row.Capital))
            .ToListAsync(cancellationToken);

        var closed = context.Set<Position>()
            .AsNoTracking()
            .OwnedBy(userId)
            .Where(position => position.AgentId == agentId && position.ClosedAt != null);

        var results = context.Set<PositionResult>().AsNoTracking().OwnedBy(userId);

        // What the track record counts: the positions that the agent closed in the mode that it
        // is in, each with its result in R once that is recorded.
        var counted = await (
            from position in closed
            where position.Mode == mode
            join result in results on position.Id equals result.PositionId into written
            from result in written.DefaultIfEmpty()
            select new
            {
                position.Id,
                position.Mode,
                position.Market,
                position.Timeframe,
                position.ClosedAt,
                Recorded = result != null,
                ResultInR = (decimal?)result.ResultInR,
                HoldingSeconds = (long?)result.HoldingSeconds,
                Complete = (bool?)result.Complete,
            }).ToListAsync(cancellationToken);

        var record = TrackRecord.Of(
            counted.Select(trade => new ClosedTrade(
                trade.Id,
                trade.Mode,
                trade.Market,
                trade.Timeframe,
                trade.ClosedAt!.Value,
                trade.Recorded ? new TradeResult(trade.ResultInR, trade.HoldingSeconds ?? 0L, trade.Complete ?? false) : null,
                Roles: [])),
            experience.MinSamples);

        var trades = await TradesAsync(context, userId, closed, results, tradesPage, cancellationToken);

        var equity = AgentEquities.Valued(agent, rows, held, prices, clock.GetUtcNow());
        var net = equity.Equity - equity.Capital;

        return new AgentReport(
            equity,
            new AgentFigures(
                net,
                net is { } made && equity.Capital > 0m ? Worked(() => made * 100m / equity.Capital) : null,
                record,
                counted.Count,
                rows.Fees,
                rows.FeesValued,
                rows.Funding),
            EquityCurves.Of(recorded),
            trades);
    }

    /// <summary>One page of the closed positions of an agent, latest close first, each with what its orders and its result say of it.</summary>
    private static async Task<AgentTrades> TradesAsync(
        EcnanibDbContext context,
        Guid userId,
        IQueryable<Position> closed,
        IQueryable<PositionResult> results,
        int page,
        CancellationToken cancellationToken)
    {
        var total = await closed.CountAsync(cancellationToken);
        var pages = Math.Max((total + AgentTrades.PageSize - 1) / AgentTrades.PageSize, 1);

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
                position.Mode,
                position.Market,
                position.Symbol,
                position.QuoteAsset,
                position.Side,
                position.EntryPrice,
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
            .Skip((page - 1) * AgentTrades.PageSize)
            .Take(AgentTrades.PageSize)
            .ToListAsync(cancellationToken);

        var ids = listed.Select(trade => trade.Id).ToList();

        // What was executed of the orders of those positions: the entry of each says how much
        // it held, and the orders that closed it say at what price.
        var executed = (await context.Set<Order>()
            .AsNoTracking()
            .OwnedBy(userId)
            .Where(order => order.PositionId != null && ids.Contains(order.PositionId.Value) && order.ExecutedQuantity > 0m)
            .Select(order => new { PositionId = order.PositionId!.Value, order.Purpose, order.ExecutedQuantity, order.AveragePrice })
            .ToListAsync(cancellationToken))
            .ToLookup(order => order.PositionId);

        return new AgentTrades(
            total,
            page,
            pages,
            [
                .. listed.Select(trade =>
                {
                    var orders = executed[trade.Id].ToList();
                    var exits = orders.Where(order => order.Purpose != OrderPurpose.Entry).Select(order => (order.ExecutedQuantity, order.AveragePrice)).ToList();

                    return new AgentTrade(
                        trade.Id,
                        trade.Mode,
                        trade.Market,
                        trade.Symbol,
                        trade.QuoteAsset,
                        trade.Side,

                        // A position has one entry. What it holds grows less as it is closed in
                        // parts, so the position itself does not say how much it held.
                        orders.Where(order => order.Purpose == OrderPurpose.Entry).Select(order => (decimal?)order.ExecutedQuantity).FirstOrDefault(),
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
                        Estimate: trade.MadeAtExchange is not null || trade.Complete == false);
                }),
            ]);
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

        return Worked(() =>
        {
            var executed = exits.Sum(exit => exit.Executed);

            return executed > 0m
                ? AgentResults.Rounded(exits.Sum(exit => AgentResults.Rounded(exit.Executed * exit.AveragePrice!.Value)) / executed)
                : (decimal?)null;
        });
    }

    /// <summary>What a sum works out to, or null where it is beyond what can be worked out with: nothing is said then, and nothing fails.</summary>
    private static decimal? Worked(Func<decimal?> sum)
    {
        try
        {
            return sum();
        }
        catch (OverflowException)
        {
            return null;
        }
    }
}
