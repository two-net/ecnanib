using System.Text.Json;
using System.Text.Json.Serialization;
using Ecnanib.Agents;
using Ecnanib.Shared;

namespace Ecnanib.DecisionEngine;

/// <summary>
/// The summary of the data package of a cycle, which the record of the cycle keeps (FR-CYC-10):
/// what the team of the agent decided on, without the candles. See "The record of a cycle" in
/// docs/team.md.
/// </summary>
/// <remarks>
/// <para>
/// <b>It keeps no candles.</b> Of each series it says how many candles the package held, the
/// latest of them, and the indicators at its close: the database is not a market-data warehouse
/// (NFR-DAT-6).
/// </para>
/// <para>
/// <b>It holds what a prompt holds, and nothing else:</b> market data, the settings of the agent,
/// and what it held. It has no name of the agent and no id of anything (FR-PRM-9).
/// </para>
/// <para>
/// It is stored as one line of JSON, which the service writes: a number with every digit that
/// it has, a time in UTC, and a member of a fixed set by its name.
/// </para>
/// </remarks>
/// <param name="BuiltAt">When the package was built, by the clock of the service.</param>
/// <param name="Market">The market of the agent then.</param>
/// <param name="Timeframe">The timeframe of the agent then.</param>
/// <param name="Symbol">The symbol that it traded.</param>
/// <param name="QuoteAsset">The asset that the symbol is priced in, which the equity is in.</param>
/// <param name="Equity">The equity of the agent: its allocated capital plus its net profit or loss to date.</param>
/// <param name="Position">Its open position, or null when it had none.</param>
/// <param name="Limits">Its risk settings, as they were stored when the turn of the cycle came.</param>
/// <param name="Series">The candles of the agent's own timeframe first, and then those of each higher timeframe, shortest first.</param>
/// <param name="Orders">The orders of the agent that rested on the exchange. Empty when it had none.</param>
/// <param name="Futures">The mark price, the funding, and the open interest, for an agent on Futures, and null for one on Spot.</param>
public sealed record DataPackageSummary(
    DateTimeOffset BuiltAt,
    Market Market,
    Timeframe Timeframe,
    string Symbol,
    string QuoteAsset,
    decimal Equity,
    PositionContext? Position,
    AgentRisk Limits,
    IReadOnlyList<SeriesSummary> Series,
    IReadOnlyList<OpenOrder> Orders,
    FuturesData? Futures)
{
    // One line, with the names of things and not their numbers: a row reads the same in the
    // database as in the code.
    private static readonly JsonSerializerOptions Stored = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>The summary of a data package.</summary>
    /// <param name="package">The package.</param>
    /// <param name="builtAt">When it was built.</param>
    public static DataPackageSummary Of(DataPackage package, DateTimeOffset builtAt)
    {
        ArgumentNullException.ThrowIfNull(package);

        var agent = package.Agent;

        return new DataPackageSummary(
            builtAt,
            agent.Market,
            agent.Timeframe,
            agent.Symbol,
            agent.QuoteAsset,
            agent.Equity,
            agent.Position,
            agent.Limits,
            [SeriesSummary.Of(package.Candles), .. package.Context.Select(SeriesSummary.Of)],
            [.. package.Orders],
            package.Futures);
    }

    /// <summary>The summary as it is stored: one line of JSON.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, Stored);

    /// <summary>
    /// The summary that a cycle stored, or null where it stored none: a cycle that ended before
    /// its package was built, and one that was recorded before a summary was kept. What does not
    /// read as a summary is none either: nothing of the application wrote it.
    /// </summary>
    /// <param name="stored">What the row of the cycle holds.</param>
    public static DataPackageSummary? Read(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<DataPackageSummary>(stored, Stored) is { Series: not null, Orders: not null, Limits: not null } read
                ? read
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>What the summary of a data package keeps of the candles of one length.</summary>
/// <param name="Timeframe">The length of a candle, as its code: <c>1H</c>, or <c>1W</c> for the week.</param>
/// <param name="Candles">How many closed candles the package held.</param>
/// <param name="ComputedFrom">How many closed candles the indicators were computed from.</param>
/// <param name="Latest">The latest candle of the series, or null for a series that had none.</param>
/// <param name="Indicators">The indicators at the close of that candle, computed by the system. One that had too few candles is null.</param>
public sealed record SeriesSummary(string Timeframe, int Candles, int ComputedFrom, CandleSummary? Latest, IndicatorValues Indicators)
{
    /// <summary>What is kept of a series: no candle but the latest.</summary>
    public static SeriesSummary Of(CandleSeries series)
    {
        ArgumentNullException.ThrowIfNull(series);

        return new SeriesSummary(
            TimeframeContext.Code(series.Interval),
            series.Candles.Count,
            series.ComputedFrom,
            series.Candles.Count == 0 ? null : CandleSummary.Of(series.Candles[^1]),
            series.Indicators);
    }
}

/// <summary>The latest candle of a series, as the summary of a data package keeps it.</summary>
/// <param name="ClosedAt">When it closed, which is through when the series reached.</param>
/// <param name="Open">The first price.</param>
/// <param name="High">The highest price.</param>
/// <param name="Low">The lowest price.</param>
/// <param name="Close">The last price.</param>
/// <param name="Volume">How much was traded, in the asset that is traded.</param>
public sealed record CandleSummary(DateTimeOffset ClosedAt, decimal Open, decimal High, decimal Low, decimal Close, decimal Volume)
{
    /// <summary>What is kept of a closed candle.</summary>
    public static CandleSummary Of(ExchangeGateway.ExchangeCandle candle)
    {
        ArgumentNullException.ThrowIfNull(candle);

        return new CandleSummary(candle.ClosedAt, candle.Open, candle.High, candle.Low, candle.Close, candle.Volume);
    }
}
