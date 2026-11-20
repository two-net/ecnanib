namespace Ecnanib.Experience;

/// <summary>The direction that an analyst called: its bias, where that is not neutral.</summary>
public enum CallDirection
{
    /// <summary>The analyst said that the price would rise.</summary>
    Bullish,

    /// <summary>The analyst said that it would fall.</summary>
    Bearish,
}

/// <summary>What became of a call of an analyst. A member is stored by its name.</summary>
public enum CallOutcome
{
    /// <summary>The price changed in the direction that was called.</summary>
    Matched,

    /// <summary>It changed in the other direction, or not at all.</summary>
    Missed,

    /// <summary>
    /// The candles after the call can no longer be had from the exchange, so the call is not
    /// scored, and is no sample.
    /// </summary>
    NoCandles,
}

/// <summary>
/// Whether a call of an analyst matched: whether the price changed in the direction that was
/// called, over the next 4 closed candles (FR-EXP-3). See "The calls of the analysts" in
/// docs/experience.md.
/// </summary>
public static class CallScore
{
    /// <summary>How many closed candles after the candle of a call its price change is taken over.</summary>
    public const int CandlesLater = 4;

    /// <summary>What became of a call.</summary>
    /// <remarks>
    /// <b>A price that did not change matches no direction.</b> The call said that it would
    /// change, so it is a sample, and one that was not right.
    /// </remarks>
    /// <param name="direction">The direction that was called.</param>
    /// <param name="price">The close of the candle that the call was made on.</param>
    /// <param name="laterPrice">The close of the 4th candle after it.</param>
    public static CallOutcome Of(CallDirection direction, decimal price, decimal laterPrice) => direction switch
    {
        CallDirection.Bullish => laterPrice > price ? CallOutcome.Matched : CallOutcome.Missed,
        CallDirection.Bearish => laterPrice < price ? CallOutcome.Matched : CallOutcome.Missed,
        _ => throw new ArgumentOutOfRangeException(nameof(direction), direction, "The direction is none that a call has."),
    };
}
