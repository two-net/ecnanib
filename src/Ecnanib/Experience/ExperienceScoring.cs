using Ecnanib.ExchangeGateway;
using Ecnanib.Platform.Persistence;
using Ecnanib.Platform.Settings;
using Ecnanib.Shared;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Experience;

/// <summary>
/// Scores the calls of the analysts and follows the shadow trades, against the candles that
/// closed after them (FR-EXP-3, FR-EXP-5). See "The job" in docs/experience.md.
/// </summary>
/// <remarks>
/// <para>
/// <b>It reads candles, and places nothing.</b> The candles are those of the Exchange Gateway,
/// which are read once and shared. Experience cannot reach the orders of the gateway, so a
/// shadow trade never places one.
/// </para>
/// <para>
/// <b>It works across users.</b> A job has no user who is signed in: each row says whose it is,
/// and what is written of a row is written to that row alone. See "What is not asked for by
/// owner" in docs/ownership.md.
/// </para>
/// <para>
/// <b>A row is completed once.</b> An update changes a row only while it has no outcome, so a
/// row that went with its agent meanwhile fails nothing, and a row that has its outcome keeps it.
/// </para>
/// <para>
/// <b>The exchange gives the candles of the recent past alone,</b> the newest
/// <see cref="ExchangeCandles.Most"/> of a length. A row whose candles have not closed yet
/// waits for the next run, and so does one whose candles could not be read. A row whose candles
/// are no longer given, because the service was down for longer than they reach back, or
/// because the configuration file no longer lists its symbol, ends with no result.
/// </para>
/// </remarks>
internal sealed class ExperienceScoring(
    IDbContextFactory<EcnanibDbContext> contexts,
    IExchangeMarketData marketData,
    ExperienceSettings settings,
    TimeProvider clock,
    ILogger<ExperienceScoring> logger)
{
    /// <summary>The most rows of each kind that one run takes: the ones that are due the longest.</summary>
    public const int Most = 200;

    /// <summary>Scores the calls that are due, and follows the shadow trades that are, and says how many of each it completed.</summary>
    /// <param name="cancellationToken">Stops the run. What it completed by then stays.</param>
    public async Task<ScoringRun> ScoreDueAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();

        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        // The rows of every user that are due: a job works across users. The longest due
        // first, and by their keys beside it, so that a run takes the same rows in the same order.
        var calls = await context.Set<AnalystCall>()
            .AsNoTracking()
            .Where(call => call.Outcome == null && call.DueAt <= now)
            .OrderBy(call => call.DueAt)
            .ThenBy(call => call.CycleId)
            .ThenBy(call => call.Role)
            .Take(Most)
            .ToListAsync(cancellationToken);

        var trades = await context.Set<ShadowTrade>()
            .AsNoTracking()
            .Where(trade => trade.Outcome == null && trade.DueAt <= now)
            .OrderBy(trade => trade.DueAt)
            .ThenBy(trade => trade.CycleId)
            .Take(Most)
            .ToListAsync(cancellationToken);

        var (scored, ended) = (0, 0);

        // The candles of a symbol are read once for everything that needs them.
        var series = calls.Select(SeriesOf).Concat(trades.Select(SeriesOf)).Distinct().ToList();

        foreach (var asked in series)
        {
            var read = await ReadAsync(asked, cancellationToken);

            if (read is { Gone: false, Candles: null })
            {
                // The read failed. Everything of this symbol waits for the next run.
                continue;
            }

            foreach (var call in calls.Where(call => SeriesOf(call) == asked))
            {
                scored += await ScoreAsync(context, call, read, now, cancellationToken);
            }

            foreach (var trade in trades.Where(trade => SeriesOf(trade) == asked))
            {
                ended += await FollowAsync(context, trade, read, now, cancellationToken);
            }
        }

        return new ScoringRun(scored, ended);
    }

    /// <summary>Gives a call its outcome where the candles say it, and says whether it did.</summary>
    private static async Task<int> ScoreAsync(
        EcnanibDbContext context, AnalystCall call, CandleRead read, DateTimeOffset now, CancellationToken cancellationToken)
    {
        CallOutcome? outcome = null;
        decimal? laterPrice = null;

        if (LaterThan(call.ClosedAt, call.Timeframe, read, now) is not { } later)
        {
            outcome = CallOutcome.NoCandles;
        }
        else if (later.Count >= CallScore.CandlesLater)
        {
            laterPrice = later[CallScore.CandlesLater - 1].Close;
            outcome = CallScore.Of(call.Direction, call.Price, laterPrice.Value);
        }

        if (outcome is null)
        {
            // Its 4th later candle is not given yet: the exchange may not have begun the next one.
            return 0;
        }

        return await context.Set<AnalystCall>()
            .Where(row => row.CycleId == call.CycleId && row.Role == call.Role && row.Outcome == null)
            .ExecuteUpdateAsync(
                row => row
                    .SetProperty(scored => scored.Outcome, outcome)
                    .SetProperty(scored => scored.LaterPrice, laterPrice)
                    .SetProperty(scored => scored.ScoredAt, now),
                cancellationToken);
    }

    /// <summary>Ends a shadow trade where the candles say how it ended, or says when to look at it next, and says whether it ended.</summary>
    private async Task<int> FollowAsync(
        EcnanibDbContext context, ShadowTrade trade, CandleRead read, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ShadowResult? result;

        if (LaterThan(trade.ClosedAt, trade.Timeframe, read, now) is not { } later)
        {
            result = new ShadowResult(ShadowOutcome.NoCandles, null, now);
        }
        else if (!ShadowTradeRule.IsSound(trade.Side, trade.EntryPrice, trade.StopLoss, trade.TakeProfit))
        {
            // No such row is written as one to follow. One that is there all the same is not followed.
            result = new ShadowResult(ShadowOutcome.Rejected, null, now);
        }
        else
        {
            result = ShadowTradeRule.Of(
                trade.Side,
                trade.EntryPrice,
                trade.Resting,
                trade.StopLoss,
                trade.TakeProfit,
                later,
                settings.ShadowFeePercent.RateOf(trade.Market));

            if (result is null)
            {
                // It is open still. It is looked at again once the next candle has closed that
                // it was not followed over, so that the rows that wait behind it have their turn.
                var followedThrough = later.Count > 0 ? later[^1].ClosedAt : trade.ClosedAt;
                var next = followedThrough + trade.Timeframe.Length();

                if (next != trade.DueAt)
                {
                    await context.Set<ShadowTrade>()
                        .Where(row => row.CycleId == trade.CycleId && row.Outcome == null)
                        .ExecuteUpdateAsync(row => row.SetProperty(open => open.DueAt, next), cancellationToken);
                }

                return 0;
            }
        }

        ShadowOutcome? outcome = result.Outcome;
        DateTimeOffset? exitedAt = result.Outcome is ShadowOutcome.NoCandles or ShadowOutcome.Rejected ? null : result.ExitedAt;

        return await context.Set<ShadowTrade>()
            .Where(row => row.CycleId == trade.CycleId && row.Outcome == null)
            .ExecuteUpdateAsync(
                row => row
                    .SetProperty(ended => ended.Outcome, outcome)
                    .SetProperty(ended => ended.ResultInR, result.ResultInR)
                    .SetProperty(ended => ended.ExitedAt, exitedAt)
                    .SetProperty(ended => ended.EvaluatedAt, now),
                cancellationToken);
    }

    /// <summary>
    /// The candles that closed after a close, oldest first, or null where they are no longer
    /// given: the first of them is older than what the exchange gives, or nothing of the symbol
    /// is given at all.
    /// </summary>
    private static IReadOnlyList<ExchangeCandle>? LaterThan(DateTimeOffset closedAt, Timeframe timeframe, CandleRead read, DateTimeOffset now)
    {
        if (read.Gone || read.Candles is not { } candles)
        {
            return null;
        }

        // The candle that began at the close is the first of the later ones. Where the oldest
        // that is given began after it, the first is gone, and what is left is not all of them.
        if (candles.Count > 0 && candles[0].OpenedAt > closedAt)
        {
            return null;
        }

        // A read that gives no candle at all does not say how far back it reaches. The clock
        // does: the exchange gives so many candles of a length and no more, so a close that is
        // longer ago than they last is one whose later candles will not be given any more.
        if (candles.Count == 0 && closedAt + (ExchangeCandles.Most * timeframe.Length()) < now)
        {
            return null;
        }

        return [.. candles.Where(candle => candle.ClosedAt > closedAt)];
    }

    /// <summary>Reads the candles of a symbol, and says so when they could not be read, or are not to be had.</summary>
    private async Task<CandleRead> ReadAsync(Series asked, CancellationToken cancellationToken)
    {
        ExchangeCandles read;

        try
        {
            read = await marketData.GetCandlesAsync(
                asked.Market, asked.Mode, asked.Symbol, CandleIntervals.Of(asked.Timeframe), cancellationToken);
        }
        catch (ArgumentException)
        {
            // The gateway serves the symbols of the configuration file alone, and says so with
            // an error: what was asked for is no call, and never will be one.
            logger.LogWarning(
                "The candles of {Symbol} on {Market} in {TradingMode} are not to be had: the configuration file does not list the symbol. " +
                "What Experience was to score of it ends with no result",
                asked.Symbol,
                asked.Market,
                asked.Mode);

            return new CandleRead(null, Gone: true);
        }

        if (read.Outcome != ExchangeCallOutcome.Succeeded)
        {
            logger.LogWarning(
                "The candles of {Symbol} on {Market} in {TradingMode} could not be read for Experience ({ExchangeCallOutcome}). What it was to score of it waits for the next run",
                asked.Symbol,
                asked.Market,
                asked.Mode,
                read.Outcome);

            return new CandleRead(null, Gone: false);
        }

        return new CandleRead(read.Candles, Gone: false);
    }

    private static Series SeriesOf(AnalystCall call) => new(call.Market, call.Mode, call.Symbol, call.Timeframe);

    private static Series SeriesOf(ShadowTrade trade) => new(trade.Market, trade.Mode, trade.Symbol, trade.Timeframe);

    /// <summary>The candles that a row is scored against: those of its symbol, on its market, in its environment, of the length of its timeframe.</summary>
    private readonly record struct Series(Market Market, TradingMode Mode, string Symbol, Timeframe Timeframe);

    /// <summary>What reading the candles of a symbol gave.</summary>
    /// <param name="Candles">The candles, or null where they were not read.</param>
    /// <param name="Gone">Whether they are not to be had at all, now or later.</param>
    private sealed record CandleRead(IReadOnlyList<ExchangeCandle>? Candles, bool Gone);
}

/// <summary>What a run of the scoring completed.</summary>
/// <param name="Calls">How many calls of analysts were given their outcome.</param>
/// <param name="ShadowTrades">How many shadow trades ended.</param>
internal sealed record ScoringRun(int Calls, int ShadowTrades);
