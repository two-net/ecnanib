using Ecnanib.Shared;

namespace Ecnanib.ExchangeGateway;

/// <summary>
/// The length of a candle that the gateway reads: the four timeframes that an agent can work on,
/// and the week, which is the context of the longer ones (SRS 4.6). See docs/exchange.md.
/// </summary>
public enum CandleInterval
{
    /// <summary>15 minutes.</summary>
    FifteenMinutes,

    /// <summary>1 hour.</summary>
    OneHour,

    /// <summary>4 hours.</summary>
    FourHours,

    /// <summary>1 day, from midnight in UTC.</summary>
    OneDay,

    /// <summary>1 week, from Monday at midnight in UTC.</summary>
    OneWeek,
}

/// <summary>The lengths of a candle that the timeframes of an agent are.</summary>
public static class CandleIntervals
{
    /// <summary>
    /// The length of the candles that an agent on a timeframe works on: the member of the same
    /// name. A cycle asks for the candles of its timeframe with it.
    /// </summary>
    public static CandleInterval Of(Timeframe timeframe) => timeframe switch
    {
        Timeframe.FifteenMinutes => CandleInterval.FifteenMinutes,
        Timeframe.OneHour => CandleInterval.OneHour,
        Timeframe.FourHours => CandleInterval.FourHours,
        Timeframe.OneDay => CandleInterval.OneDay,
        _ => throw new ArgumentOutOfRangeException(nameof(timeframe), timeframe, "The timeframe has no candles."),
    };
}

/// <summary>What reading the candles of a symbol gave.</summary>
/// <param name="Outcome">How the read ended.</param>
/// <param name="Candles">
/// The newest closed candles, oldest first, when the read succeeded, and nothing otherwise. The
/// candle that is in progress is not among them. They are whole or they are none: a part of them
/// would look like a gap in the market.
/// </param>
/// <param name="ReadAt">
/// When the read that they are of was sent, by the clock of the service. It is there when the
/// read succeeded: what is shared was read a moment ago, and this says when.
/// </param>
public sealed record ExchangeCandles(ExchangeCallOutcome Outcome, IReadOnlyList<ExchangeCandle> Candles, DateTimeOffset? ReadAt)
{
    /// <summary>The most candles that a read gives. An asker takes the newest that it needs.</summary>
    public const int Most = 498;

    public IReadOnlyList<ExchangeCandle> Candles { get; } = Outcome == ExchangeCallOutcome.Succeeded || Candles.Count == 0
        ? Candles
        : throw new ArgumentException("A reading has candles only when it succeeded.", nameof(Candles));

    public DateTimeOffset? ReadAt { get; } = MarketReading.When(Outcome, ReadAt);
}

/// <summary>One closed candle.</summary>
/// <param name="OpenedAt">When it began.</param>
/// <param name="ClosedAt">When it ended, which is when the next one began: the close that a cycle starts at.</param>
/// <param name="Open">The first price.</param>
/// <param name="High">The highest price.</param>
/// <param name="Low">The lowest price.</param>
/// <param name="Close">The last price.</param>
/// <param name="Volume">How much was traded, in the base asset.</param>
public sealed record ExchangeCandle(
    DateTimeOffset OpenedAt, DateTimeOffset ClosedAt, decimal Open, decimal High, decimal Low, decimal Close, decimal Volume);

/// <summary>What reading the price of a symbol gave.</summary>
/// <param name="Outcome">How the read ended.</param>
/// <param name="Price">The price that the symbol last traded at. It is there when the read succeeded, and never otherwise.</param>
/// <param name="ReadAt">When the read was sent, by the clock of the service. It is there when the read succeeded.</param>
public sealed record ExchangePrice(ExchangeCallOutcome Outcome, decimal? Price, DateTimeOffset? ReadAt)
{
    public decimal? Price { get; } = MarketReading.Value(Outcome, Price, nameof(Price));

    public DateTimeOffset? ReadAt { get; } = MarketReading.When(Outcome, ReadAt);
}

/// <summary>What reading the symbols of a market gave.</summary>
/// <param name="Outcome">How the read ended.</param>
/// <param name="Symbols">
/// The symbols of the configuration file that the market has, in the order of the file, when the
/// read succeeded, and nothing otherwise. A symbol that is not among them is one that the market
/// does not have. A list is whole or it is none.
/// </param>
/// <param name="ReadAt">When the read was sent, by the clock of the service. It is there when the read succeeded.</param>
public sealed record ExchangeSymbols(ExchangeCallOutcome Outcome, IReadOnlyList<ExchangeSymbol> Symbols, DateTimeOffset? ReadAt)
{
    public IReadOnlyList<ExchangeSymbol> Symbols { get; } = Outcome == ExchangeCallOutcome.Succeeded || Symbols.Count == 0
        ? Symbols
        : throw new ArgumentException("A reading has symbols only when it succeeded.", nameof(Symbols));

    public DateTimeOffset? ReadAt { get; } = MarketReading.When(Outcome, ReadAt);
}

/// <summary>A symbol on one market, with the rules that an order in it must keep.</summary>
/// <param name="Symbol">The exchange's name of the symbol, such as <c>BTCUSDT</c>.</param>
/// <param name="BaseAsset">The asset that is traded, such as <c>BTC</c>.</param>
/// <param name="QuoteAsset">The asset that it is priced in, such as <c>USDT</c>.</param>
/// <param name="Trading">Whether the exchange takes orders in it now.</param>
/// <param name="PriceStep">What a price must be a multiple of.</param>
/// <param name="QuantityStep">What a quantity must be a multiple of.</param>
/// <param name="MinQuantity">The least quantity of an order.</param>
/// <param name="MaxQuantity">The most quantity of an order.</param>
/// <param name="MinValue">The least value of an order: its price times its quantity, in the quote asset.</param>
public sealed record ExchangeSymbol(
    string Symbol,
    string BaseAsset,
    string QuoteAsset,
    bool Trading,
    decimal PriceStep,
    decimal QuantityStep,
    decimal MinQuantity,
    decimal MaxQuantity,
    decimal MinValue);

/// <summary>What reading the mark price of a symbol on the Futures market gave.</summary>
/// <param name="Outcome">How the read ended.</param>
/// <param name="MarkPrice">The mark price, which protective orders are triggered on (FR-BIN-F3).</param>
/// <param name="FundingRate">
/// The funding rate now, as a fraction: <c>0.0001</c> is 0.01 percent. Longs pay shorts when it
/// is above zero, and shorts pay longs when it is below.
/// </param>
/// <param name="NextFundingAt">When funding is next paid.</param>
/// <param name="ReadAt">When the read was sent, by the clock of the service.</param>
/// <remarks>All four are there when the read succeeded, and never otherwise.</remarks>
public sealed record ExchangeMarkPrice(
    ExchangeCallOutcome Outcome, decimal? MarkPrice, decimal? FundingRate, DateTimeOffset? NextFundingAt, DateTimeOffset? ReadAt)
{
    public decimal? MarkPrice { get; } = MarketReading.Value(Outcome, MarkPrice, nameof(MarkPrice));

    public decimal? FundingRate { get; } = MarketReading.Value(Outcome, FundingRate, nameof(FundingRate));

    public DateTimeOffset? NextFundingAt { get; } = MarketReading.Value(Outcome, NextFundingAt, nameof(NextFundingAt));

    public DateTimeOffset? ReadAt { get; } = MarketReading.When(Outcome, ReadAt);
}

/// <summary>What reading the open interest of a symbol on the Futures market gave.</summary>
/// <param name="Outcome">How the read ended.</param>
/// <param name="OpenInterest">
/// The contracts that are open, in the base asset. It is there when the read succeeded, and
/// never otherwise.
/// </param>
/// <param name="ReadAt">When the read was sent, by the clock of the service. It is there when the read succeeded.</param>
public sealed record ExchangeOpenInterest(ExchangeCallOutcome Outcome, decimal? OpenInterest, DateTimeOffset? ReadAt)
{
    public decimal? OpenInterest { get; } = MarketReading.Value(Outcome, OpenInterest, nameof(OpenInterest));

    public DateTimeOffset? ReadAt { get; } = MarketReading.When(Outcome, ReadAt);
}

/// <summary>What every reading of market data keeps to: it holds what was read when it succeeded, and only then.</summary>
internal static class MarketReading
{
    public static T? Value<T>(ExchangeCallOutcome outcome, T? value, string name)
        where T : struct =>
        (outcome == ExchangeCallOutcome.Succeeded) == value.HasValue
            ? value
            : throw new ArgumentException("A reading has what was read when it succeeded, and only then.", name);

    public static DateTimeOffset? When(ExchangeCallOutcome outcome, DateTimeOffset? readAt) =>
        Value(outcome, readAt, "ReadAt");
}
