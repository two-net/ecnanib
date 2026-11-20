using Ecnanib.ExchangeGateway;

namespace Ecnanib.Experience;

/// <summary>The side of a shadow trade: of the position that the vetoed plan would have opened.</summary>
public enum ShadowSide
{
    /// <summary>It would have bought, and gains when the price rises.</summary>
    Long,

    /// <summary>It would have sold, and gains when the price falls.</summary>
    Short,
}

/// <summary>How a shadow trade ended. A member is stored by its name.</summary>
public enum ShadowOutcome
{
    /// <summary>A later candle reached its stop-loss.</summary>
    StopLoss,

    /// <summary>A later candle reached its take-profit, and not its stop-loss.</summary>
    TakeProfit,

    /// <summary>
    /// Neither price was reached in the candles that a shadow trade is followed for: it is
    /// closed at the close of the last of them.
    /// </summary>
    TimeUp,

    /// <summary>
    /// Its entry at a price was not reached before the next candle closed, which is when such an
    /// entry is taken off the exchange. It has no result.
    /// </summary>
    NotEntered,

    /// <summary>
    /// Its stop-loss or its take-profit is on the wrong side of its entry, so the Risk Guard
    /// would have rejected the plan (FR-RSK-4). It has no result.
    /// </summary>
    Rejected,

    /// <summary>The candles after it can no longer be had from the exchange. It has no result.</summary>
    NoCandles,
}

/// <summary>How a shadow trade ended, as the rule says it.</summary>
/// <param name="Outcome">How it ended.</param>
/// <param name="ResultInR">
/// What it would have produced, as a multiple of what it risked, or null: one that was not
/// entered has none, and neither has one whose result is out of the range of a number.
/// </param>
/// <param name="ExitedAt">The close of the candle that ended it.</param>
public sealed record ShadowResult(ShadowOutcome Outcome, decimal? ResultInR, DateTimeOffset ExitedAt);

/// <summary>
/// What a vetoed plan would have produced: its shadow trade, against the candles that closed
/// after it, with its own entry, stop-loss, and take-profit and with standard fees (FR-EXP-5).
/// See "Shadow trades" in docs/experience.md.
/// </summary>
/// <remarks>
/// <para>
/// <b>It is a rule only.</b> It is handed prices and candles, and answers with a result. It
/// reads nothing and places nothing: a shadow trade never places an order.
/// </para>
/// <para>
/// <b>The stop-loss is taken to be hit first</b> when it and the take-profit fall inside one
/// candle: a candle does not say in which order it reached its prices, and the worse of the two
/// is assumed.
/// </para>
/// <para>
/// <b>An entry at the market is entered at the close of the candle that the plan was made
/// on,</b> which is the price that the plan was written on. So every later candle counts, whole.
/// </para>
/// <para>
/// <b>An entry at a price is entered at that price, once the first later candle reaches it,</b>
/// and not at all otherwise: an entry that rests is taken off when the next candle of its agent
/// has closed. In the candle that enters it only the stop-loss counts: the candle does not say
/// whether its other end came before the entry.
/// </para>
/// <para>
/// <b>It ends at its own prices.</b> A candle that opens beyond the stop-loss still ends the
/// trade at the stop-loss. It is charged the standard fee twice, on what its entry and its exit
/// are worth, and no funding.
/// </para>
/// </remarks>
public static class ShadowTradeRule
{
    /// <summary>
    /// How many later candles a shadow trade is followed for. Nothing manages it as a team
    /// manages a position, and the exchange gives the candles of the recent past alone, so one
    /// that has reached neither price by then is closed at the last close.
    /// </summary>
    public const int CandlesFollowed = 200;

    // The decimal places that a result in R is rounded to, as the result of a position is.
    private const int Places = 12;

    // From which side the market comes to a price that it is to reach.
    private const bool Above = true;
    private const bool Below = false;

    /// <summary>
    /// Whether the stop-loss and the take-profit of a trade are on the correct sides of its
    /// entry: for a long the stop-loss below and the take-profit above, and for a short the
    /// other way. The Risk Guard rejects a plan that is otherwise (FR-RSK-4).
    /// </summary>
    public static bool IsSound(ShadowSide side, decimal entryPrice, decimal stopLoss, decimal takeProfit) => side switch
    {
        ShadowSide.Long => stopLoss < entryPrice && entryPrice < takeProfit,
        ShadowSide.Short => takeProfit < entryPrice && entryPrice < stopLoss,
        _ => throw new ArgumentOutOfRangeException(nameof(side), side, "The side is none that a trade has."),
    };

    /// <summary>
    /// How a shadow trade ended, or null while it has not: its candles have not all closed yet.
    /// </summary>
    /// <param name="side">The side of the trade.</param>
    /// <param name="entryPrice">The price that it is entered at.</param>
    /// <param name="resting">Whether the entry rests at its price until the market reaches it, and is entered at once otherwise.</param>
    /// <param name="stopLoss">Its stop-loss.</param>
    /// <param name="takeProfit">Its take-profit.</param>
    /// <param name="later">The candles that closed after the candle of the plan, oldest first, with none left out.</param>
    /// <param name="feeRate">The standard fee of an order on its market, as a fraction of what the order is worth.</param>
    public static ShadowResult? Of(
        ShadowSide side,
        decimal entryPrice,
        bool resting,
        decimal stopLoss,
        decimal takeProfit,
        IReadOnlyList<ExchangeCandle> later,
        decimal feeRate)
    {
        ArgumentNullException.ThrowIfNull(later);

        if (!IsSound(side, entryPrice, stopLoss, takeProfit))
        {
            throw new ArgumentException("The stop-loss and the take-profit are not on the correct sides of the entry, so there is no trade to follow.");
        }

        var followed = Math.Min(later.Count, CandlesFollowed);

        for (var index = 0; index < followed; index++)
        {
            var candle = later[index];
            var entersHere = resting && index == 0;

            // An entry that rests is reached by the first later candle, or it is taken off.
            if (entersHere && !Reaches(candle, entryPrice, from: side == ShadowSide.Long ? Above : Below))
            {
                return new ShadowResult(ShadowOutcome.NotEntered, null, candle.ClosedAt);
            }

            // The stop-loss first: the worse of the two is assumed where a candle reached both.
            if (Reaches(candle, stopLoss, from: side == ShadowSide.Long ? Above : Below))
            {
                return Ended(ShadowOutcome.StopLoss, stopLoss, candle);
            }

            if (!entersHere && Reaches(candle, takeProfit, from: side == ShadowSide.Long ? Below : Above))
            {
                return Ended(ShadowOutcome.TakeProfit, takeProfit, candle);
            }
        }

        return followed == CandlesFollowed ? Ended(ShadowOutcome.TimeUp, later[followed - 1].Close, later[followed - 1]) : null;

        ShadowResult Ended(ShadowOutcome outcome, decimal exitPrice, ExchangeCandle candle) =>
            new(outcome, ResultInR(side, entryPrice, exitPrice, stopLoss, feeRate), candle.ClosedAt);
    }

    /// <summary>
    /// What a trade produced, as a multiple of what it risked: what its price moved by, less the
    /// fee on its entry and on its exit, over the distance from its entry to its stop-loss. The
    /// quantity is on both sides, and so in neither. Null where it is out of the range of a number.
    /// </summary>
    public static decimal? ResultInR(ShadowSide side, decimal entryPrice, decimal exitPrice, decimal stopLoss, decimal feeRate)
    {
        try
        {
            var moved = side == ShadowSide.Long ? exitPrice - entryPrice : entryPrice - exitPrice;
            var fees = feeRate * (entryPrice + exitPrice);
            var risked = Math.Abs(entryPrice - stopLoss);

            return risked > 0m ? Math.Round((moved - fees) / risked, Places, MidpointRounding.AwayFromZero) : null;
        }
        catch (OverflowException)
        {
            // A stop-loss next to the entry risks next to nothing, and the result is very many R.
            return null;
        }
    }

    /// <summary>Whether a candle traded at a price that the market comes to from above, or from below.</summary>
    private static bool Reaches(ExchangeCandle candle, decimal price, bool from) =>
        from == Above ? candle.Low <= price : candle.High >= price;
}
