namespace Ecnanib.DecisionEngine;

/// <summary>Which way a role reads the market.</summary>
public enum Bias
{
    Bullish,
    Bearish,
    Neutral,
}

/// <summary>What a key price level is to the price.</summary>
public enum LevelKind
{
    /// <summary>A price that the market is expected to hold above.</summary>
    Support,

    /// <summary>A price that the market is expected to stay below.</summary>
    Resistance,
}

/// <summary>How much the price moves, as the Market Analyst reads it.</summary>
public enum Volatility
{
    Low,
    Normal,
    High,
}

/// <summary>What a trade plan does (FR-CYC-4). Which of them a market allows is not held to here.</summary>
public enum PlanAction
{
    /// <summary>Buy, to gain when the price rises.</summary>
    OpenLong,

    /// <summary>Sell, to gain when the price falls.</summary>
    OpenShort,

    /// <summary>Change nothing.</summary>
    Hold,

    /// <summary>Close the open position.</summary>
    Close,

    /// <summary>Close the open position, then open one on the opposite side.</summary>
    Reverse,

    /// <summary>Move the stop-loss or the take-profit of the open position.</summary>
    AdjustProtection,
}

/// <summary>How a plan enters a position (FR-BIN-S2).</summary>
public enum EntryType
{
    /// <summary>At the price of the market, at once.</summary>
    Market,

    /// <summary>At a price that the plan names, or better.</summary>
    Limit,
}

/// <summary>What the Risk Manager makes of a plan (FR-TEAM-7).</summary>
public enum ReviewVerdict
{
    /// <summary>The plan goes on as it was planned.</summary>
    Approve,

    /// <summary>The plan goes on with a lower risk fraction.</summary>
    Reduce,

    /// <summary>The plan does not go on.</summary>
    Veto,
}

/// <summary>A key price level of a market report.</summary>
/// <remarks>Only what validates the answer of a role makes one: see <see cref="IRoleResults"/>.</remarks>
public sealed record KeyLevel
{
    internal KeyLevel(decimal price, LevelKind kind)
    {
        Price = price;
        Kind = kind;
    }

    /// <summary>The price of the level, in the asset that the symbol is priced in.</summary>
    public decimal Price { get; }

    /// <summary>Whether the level is a support or a resistance.</summary>
    public LevelKind Kind { get; }
}

/// <summary>
/// The report of the Market Analyst: its bias, the key price levels, and its read of the
/// volatility, with a short rationale (SRS 4.5, FR-TEAM-6). See docs/team.md.
/// </summary>
/// <remarks>Only what validates the answer of a role makes one: see <see cref="IRoleResults"/>.</remarks>
public sealed record MarketReport
{
    internal MarketReport(Bias bias, IReadOnlyList<KeyLevel> keyLevels, Volatility volatility, string rationale)
    {
        Bias = bias;
        KeyLevels = keyLevels;
        Volatility = volatility;
        Rationale = rationale;
    }

    public Bias Bias { get; }

    /// <summary>The key price levels. Empty when the role names none.</summary>
    public IReadOnlyList<KeyLevel> KeyLevels { get; }

    public Volatility Volatility { get; }

    /// <summary>Why, in the role's words.</summary>
    public string Rationale { get; }
}

/// <summary>
/// What the Sentiment Analyst answered: its bias, the notable events, its confidence, and
/// whether it says that live data was available, with a short rationale (SRS 4.5, FR-TEAM-6).
/// </summary>
/// <remarks>
/// <para>
/// Whether the report rests on live data is the service's to say, and not the role's:
/// <see cref="SentimentReport"/> marks it, from what the call was and what the provider did.
/// <see cref="LiveDataAvailable"/> is only what the model says of itself.
/// </para>
/// <para>Only what validates the answer of a role makes one: see <see cref="IRoleResults"/>.</para>
/// </remarks>
public sealed record SentimentRead
{
    internal SentimentRead(
        Bias bias, IReadOnlyList<string> notableEvents, decimal confidence, bool liveDataAvailable, string rationale)
    {
        Bias = bias;
        NotableEvents = notableEvents;
        Confidence = confidence;
        LiveDataAvailable = liveDataAvailable;
        Rationale = rationale;
    }

    public Bias Bias { get; }

    /// <summary>The events that the role found notable. Empty when it names none.</summary>
    public IReadOnlyList<string> NotableEvents { get; }

    /// <summary>How sure the role is, from 0 to 1.</summary>
    public decimal Confidence { get; }

    /// <summary>Whether the model says that live data was available to it. It decides nothing.</summary>
    public bool LiveDataAvailable { get; }

    /// <summary>Why, in the role's words.</summary>
    public string Rationale { get; }
}

/// <summary>
/// The trade plan of the Strategist: an action, and for a plan that opens a position how it
/// enters, its stop-loss, its take-profit, and its risk fraction, with a rationale (SRS 4.5,
/// FR-TEAM-6). See docs/team.md.
/// </summary>
/// <remarks>
/// <para>
/// It has no quantity and no leverage: models choose neither (FR-RSK-5). It is a plan, and no
/// order: nothing places an order from it but what comes after the Risk Guard (NFR-SEC-6).
/// </para>
/// <para>Only what validates the answer of a role makes one: see <see cref="IRoleResults"/>.</para>
/// </remarks>
public sealed record TradePlan
{
    internal TradePlan(
        PlanAction action,
        EntryType? entryType,
        decimal? entryPrice,
        decimal? stopLoss,
        decimal? takeProfit,
        decimal? riskFraction,
        string rationale)
    {
        Action = action;
        EntryType = entryType;
        EntryPrice = entryPrice;
        StopLoss = stopLoss;
        TakeProfit = takeProfit;
        RiskFraction = riskFraction;
        Rationale = rationale;
    }

    public PlanAction Action { get; }

    /// <summary>How a plan that opens a position enters it, and null for every other plan.</summary>
    public EntryType? EntryType { get; }

    /// <summary>The price of a limit entry, and null for a market entry and for a plan that opens nothing.</summary>
    public decimal? EntryPrice { get; }

    /// <summary>The price of the stop-loss: of the position that the plan opens, or the one that it moves the stop-loss to.</summary>
    public decimal? StopLoss { get; }

    /// <summary>The price of the take-profit: of the position that the plan opens, or the one that it moves the take-profit to.</summary>
    public decimal? TakeProfit { get; }

    /// <summary>
    /// The share of the agent's equity that a plan which opens a position risks, as a fraction:
    /// <c>0.02</c> is 2 percent. Null for a plan that opens nothing.
    /// </summary>
    public decimal? RiskFraction { get; }

    /// <summary>Why, in the role's words.</summary>
    public string Rationale { get; }

    /// <summary>
    /// Whether the plan opens a position: open long, open short, and reverse do. Such a plan is
    /// what the Risk Manager reviews (FR-TEAM-7).
    /// </summary>
    public bool OpensPosition => Action is PlanAction.OpenLong or PlanAction.OpenShort or PlanAction.Reverse;

    /// <summary>The same plan with a lower risk fraction, and nothing else of it changed.</summary>
    internal TradePlan WithRisk(decimal riskFraction) =>
        new(Action, EntryType, EntryPrice, StopLoss, TakeProfit, riskFraction, Rationale);
}

/// <summary>
/// The review of the Risk Manager: approve, reduce, or veto, with its reasons (SRS 4.5,
/// FR-TEAM-7). See docs/team.md.
/// </summary>
/// <remarks>
/// <para>
/// It has a verdict, a lower risk fraction, and reasons, and nothing else: no direction, no
/// entry, and no stop-loss. So a review cannot change the direction of a plan or widen its
/// stop-loss, whatever a model answers.
/// </para>
/// <para>Only what validates the answer of a role makes one: see <see cref="IRoleResults"/>.</para>
/// </remarks>
public sealed record RiskReview
{
    internal RiskReview(ReviewVerdict verdict, decimal? riskFraction, string rationale)
    {
        Verdict = verdict;
        RiskFraction = riskFraction;
        Rationale = rationale;
    }

    public ReviewVerdict Verdict { get; }

    /// <summary>The lower risk fraction of a plan that is reduced, and null for every other verdict.</summary>
    public decimal? RiskFraction { get; }

    /// <summary>The reasons, in the role's words.</summary>
    public string Rationale { get; }
}
