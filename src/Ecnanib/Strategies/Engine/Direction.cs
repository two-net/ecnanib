using Ecnanib.Agents.Abstractions;
using Ecnanib.Database.Entities;
using Candle = Ecnanib.Exchange.Abstractions.Candle;

namespace Ecnanib.Strategies.Engine;

/// <summary>
/// The sign that turns the long rules into the short ones (FR-STR-07): "a beyond b" is <c>Sign × (a − b) &gt; 0</c>, the
/// pullback extreme is the low of a long leg and the high of a short one, a stop sits <c>Sign × distance</c> below the entry.
/// </summary>
internal readonly record struct Direction(int Sign)
{
    public static readonly Direction Long = new(1);

    public static readonly Direction Short = new(-1);

    public bool IsLong => Sign > 0;

    public PositionSide Side => IsLong ? PositionSide.Long : PositionSide.Short;

    public static Direction? FromBias(BiasDirection bias) => bias switch
    {
        BiasDirection.Long => Long,
        BiasDirection.Short => Short,
        _ => null,
    };

    public bool Beyond(decimal a, decimal b) => Sign * (a - b) > 0;

    /// <summary>The far side of a candle in the pullback direction: the low for a long, the high for a short.</summary>
    public decimal Extreme(Candle candle) => IsLong ? candle.Low : candle.High;

    public decimal Opposite(Candle candle) => IsLong ? candle.High : candle.Low;

    /// <summary>A price <paramref name="distance"/> further in the trade direction (negative moves against it).</summary>
    public decimal Offset(decimal price, decimal distance) => price + Sign * distance;

    public string Word => IsLong ? "long" : "short";

    public string Capitalized => IsLong ? "Long" : "Short";

    /// <summary>"below" for a long, "above" for a short: the adverse side.</summary>
    public string Below => IsLong ? "below" : "above";

    public string Above => IsLong ? "above" : "below";

    public string High => IsLong ? "high" : "low";

    public string Low => IsLong ? "low" : "high";
}
