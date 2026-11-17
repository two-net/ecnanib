using Ecnanib.ExchangeGateway;

namespace Ecnanib.DecisionEngine;

/// <summary>
/// Builds the data package of a cycle: what the team of the agent decides on (FR-CYC-2, SRS 3.2,
/// step 2). See "The data package" in docs/cycles.md.
/// </summary>
/// <remarks>
/// <para>
/// A package contains the most recent closed candles of the agent's timeframe, the candles of
/// the higher timeframes that are its context (SRS 4.6), indicators that the system computes and
/// no model does, the position, the orders, and the equity of the agent, its limits, and on
/// Futures the mark price, the funding, and the open interest.
/// </para>
/// <para>
/// It reads market data through the gateway, which shares every read among the agents that need
/// it (FR-BIN-3), and it stores nothing. It calls no model.
/// </para>
/// </remarks>
public interface IDataPackages
{
    /// <summary>The data package of one cycle of an agent.</summary>
    /// <param name="cycle">
    /// The cycle, with the closed candles of the agent's own timeframe through its close: they
    /// are not read again.
    /// </param>
    /// <param name="cancellationToken">Canceled when the cycle is stopped.</param>
    /// <exception cref="DataPackageException">
    /// The exchange did not give a part of the market data. No package is built, and the cycle
    /// makes no decision.
    /// </exception>
    Task<DataPackage> BuildAsync(CycleStart cycle, CancellationToken cancellationToken);
}

/// <summary>The data package of one cycle (FR-CYC-2).</summary>
/// <param name="ClosedAt">The close of the candle that the cycle is for. Nothing in the package closed after it.</param>
/// <param name="Agent">
/// The context of the agent: its symbol, its equity, its open position, and its limits. It is
/// what a prompt states under its own heading, from the same values (FR-PRM-1).
/// </param>
/// <param name="Orders">The orders of the agent that rest on the exchange. Empty when it has none.</param>
/// <param name="Candles">The most recent closed candles of the agent's own timeframe, with their indicators.</param>
/// <param name="Context">The most recent closed candles of each higher timeframe of SRS 4.6, shortest first, with their indicators.</param>
/// <param name="Futures">The mark price, the funding, and the open interest, for an agent on Futures, and null for one on Spot.</param>
/// <param name="Text">
/// The package as a role reads it, under the heading of the data package of a prompt: the
/// candles, the indicators, the open orders, and what Futures adds. The equity, the position, and
/// the limits are not in it: the same prompt states them as the context of the agent.
/// </param>
public sealed record DataPackage(
    DateTimeOffset ClosedAt,
    AgentContext Agent,
    IReadOnlyList<OpenOrder> Orders,
    CandleSeries Candles,
    IReadOnlyList<CandleSeries> Context,
    FuturesData? Futures,
    string Text)
{
    /// <summary>
    /// What the prompt of a role call is composed from besides the templates: the context of the
    /// agent and the text of this package, with the lessons that were recalled for the call.
    /// </summary>
    /// <param name="lessons">The lessons, most recent first. Empty when the call is given none.</param>
    public PromptParts PartsWith(IReadOnlyList<string> lessons) => new(Agent, Text, lessons);

    /// <summary>
    /// The same for a role that reads the report of the Sentiment Analyst, as the Strategist
    /// does: the report stands under the text of this package, with the mark that says whether it
    /// rests on live data (FR-CYC-3). It is data there, like the rest of the package.
    /// </summary>
    /// <param name="lessons">The lessons, most recent first. Empty when the call is given none.</param>
    /// <param name="sentiment">The report of the Sentiment Analyst of this cycle.</param>
    public PromptParts PartsWith(IReadOnlyList<string> lessons, SentimentReport sentiment)
    {
        ArgumentNullException.ThrowIfNull(sentiment);

        return new PromptParts(Agent, $"{Text}\n\n{sentiment.Text}", lessons);
    }

    /// <summary>What the line of the close begins with, in the data package of the Sentiment Analyst.</summary>
    public const string CloseHeading = "Candle close: ";

    /// <summary>The line above the report of the Market Analyst, in the data package of the Strategist.</summary>
    public const string MarketReportHeading = "Market report:";

    /// <summary>The line above the plan of the Strategist, in the data package of the Risk Manager.</summary>
    public const string TradePlanHeading = "Trade plan:";

    /// <summary>
    /// What the Market Analyst is given: the context of the agent and the text of this package,
    /// which is the price, the volume, and the indicators, and on Futures the funding and the
    /// open interest (SRS 4.5). It is given no lesson (FR-EXP-7).
    /// </summary>
    public PromptParts ForMarketAnalyst() => PartsWith([]);

    /// <summary>
    /// What the Sentiment Analyst is given: the context of the agent, which names the symbol,
    /// and the close that the cycle is for. It reads news and social context (SRS 4.5), and no
    /// candle: so its read is its own, and not a second read of the price.
    /// </summary>
    public PromptParts ForSentimentAnalyst() => new(Agent, $"{CloseHeading}{Written.Time(ClosedAt)}", []);

    /// <summary>
    /// What the Strategist is given: the text of this package, and under it the two reports
    /// (SRS 4.5). The report of the Market Analyst is one line of JSON under its heading, and
    /// the report of the Sentiment Analyst stands as <see cref="SentimentReport.Text"/> writes
    /// it, with its mark (FR-CYC-3). Both are data there, like the rest of the package.
    /// </summary>
    /// <param name="lessons">The lessons that were recalled for the call, most recent first (FR-EXP-7). Empty when there are none.</param>
    /// <param name="market">The report of the Market Analyst of this cycle, which was validated.</param>
    /// <param name="sentiment">The report of the Sentiment Analyst of this cycle, or the one that says that there is none.</param>
    /// <exception cref="ArgumentException">The report of the Market Analyst is not a valid result.</exception>
    public PromptParts ForStrategist(IReadOnlyList<string> lessons, RoleResult<MarketReport> market, SentimentReport sentiment)
    {
        ArgumentNullException.ThrowIfNull(sentiment);

        return new PromptParts(Agent, $"{Text}\n\n{MarketReportHeading}\n{Placed(market)}\n\n{sentiment.Text}", lessons);
    }

    /// <summary>
    /// What the Risk Manager is given: the text of this package, and under it the plan of the
    /// Strategist, as one line of JSON under its heading. It reviews the plan independently
    /// (SRS 4.5): it is given neither report, so what a search found reaches it only as the
    /// Strategist's own words.
    /// </summary>
    /// <param name="lessons">The lessons that were recalled for the call, most recent first (FR-EXP-7). Empty when there are none.</param>
    /// <param name="plan">The plan of the Strategist of this cycle, which was validated.</param>
    /// <exception cref="ArgumentException">The plan is not a valid result.</exception>
    public PromptParts ForRiskManager(IReadOnlyList<string> lessons, RoleResult<TradePlan> plan) =>
        new(Agent, $"{Text}\n\n{TradePlanHeading}\n{Placed(plan)}", lessons);

    /// <summary>
    /// A result as a later role reads it: one line of JSON, whatever the model wrote, so that no
    /// line of it begins as a heading of a prompt does. Only a result that was validated is
    /// placed (NFR-SEC-7).
    /// </summary>
    private static string Placed<T>(RoleResult<T> result)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(result);

        return result is { IsValid: true, Json: { } placed }
            ? placed
            : throw new ArgumentException("Only a result that was validated is placed in the prompt of a later role.", nameof(result));
    }
}

/// <summary>The closed candles of one length that a data package holds, with their indicators.</summary>
/// <param name="Interval">The length of a candle.</param>
/// <param name="Candles">
/// The most recent closed candles, oldest first: as many as the configuration file says, or all
/// that the exchange has when those are fewer.
/// </param>
/// <param name="ComputedFrom">
/// How many closed candles the indicators are computed from: every one that the exchange gave,
/// which is more than the package holds.
/// </param>
/// <param name="Indicators">The indicators at the close of the latest candle, computed by the system.</param>
public sealed record CandleSeries(
    CandleInterval Interval,
    IReadOnlyList<ExchangeCandle> Candles,
    int ComputedFrom,
    IndicatorValues Indicators);

/// <summary>What a data package holds for an agent on Futures alone.</summary>
/// <param name="MarkPrice">The mark price, which protective orders are triggered on (FR-BIN-F3).</param>
/// <param name="FundingRate">The funding rate now, as a fraction: <c>0.0001</c> is 0.01 percent (FR-BIN-F4).</param>
/// <param name="NextFundingAt">When funding is next paid.</param>
/// <param name="OpenInterest">The contracts that are open, in the asset that is traded.</param>
public sealed record FuturesData(decimal MarkPrice, decimal FundingRate, DateTimeOffset NextFundingAt, decimal OpenInterest);

/// <summary>
/// No data package was built: the exchange did not give a part of the market data. The cycle
/// makes no decision, and an open position stays as it is, with its protective orders. Nothing is
/// asked again: the next close starts the next cycle.
/// </summary>
/// <remarks>It says which part and how the read ended, and nothing of the agent: a message is logged.</remarks>
/// <param name="missing">What the exchange did not give, such as <c>the 4H candles</c>.</param>
/// <param name="outcome">How the read ended.</param>
public sealed class DataPackageException(string missing, ExchangeCallOutcome outcome)
    : Exception(
        $"No data package is built: the exchange did not give {missing} ({outcome}). The cycle makes no decision. " +
        "See docs/cycles.md.")
{
    /// <summary>What the exchange did not give.</summary>
    public string Missing { get; } = missing;

    /// <summary>How the read ended.</summary>
    public ExchangeCallOutcome Outcome { get; } = outcome;
}
