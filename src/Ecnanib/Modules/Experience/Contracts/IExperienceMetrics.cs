using Ecnanib.Kernel;

namespace Ecnanib.Modules.Experience.Contracts;

/// <summary>
/// A user's trading experience metrics per provider, model and role (FR-EXP-01, FR-EXP-02): how each model's calls
/// went, what it decided, and how the trades it took part in turned out. Every read takes one environment, so Demo and
/// Live are never combined in any view or aggregate (FR-EXP-08, AC-12). Scoped to the user (NFR-SEC-04). See Trading
/// experience in docs/architecture.md.
/// </summary>
public interface IExperienceMetrics
{
    /// <summary>The metrics <paramref name="query"/> picks, one per role, provider and model, in role order.</summary>
    /// <exception cref="ArgumentException">The range ends before it starts.</exception>
    Task<ExperienceReport> ReadAsync(Guid userId, ExperienceQuery query, CancellationToken cancellationToken);
}

/// <summary>
/// Which of a user's results to count: those in one environment, over whole UTC days. Calls and decisions count on the
/// day their cycle started, and trades on the day they closed.
/// </summary>
/// <param name="From">The first day.</param>
/// <param name="To">The last day, included.</param>
/// <param name="Market">Only this market. Null for both.</param>
/// <param name="Timeframe">Only this timeframe. Null for every one.</param>
/// <param name="Symbol">Only this pair, such as BTCUSDT. Null for every pair.</param>
public sealed record ExperienceQuery(
    TradingEnvironment Environment,
    DateOnly From,
    DateOnly To,
    Market? Market = null,
    Timeframe? Timeframe = null,
    string? Symbol = null);

/// <param name="Models">One per role, provider and model with anything to count, in role order, then provider and model.</param>
/// <param name="UnattributedTrades">
/// Settled trades in the range whose entry's models aren't known, such as one booked before its cycle's metrics were
/// built, which is at most an hour. They count for no model.
/// </param>
/// <param name="Provisional">
/// The range has days whose metrics may still change, because their analysts' calls can't all be scored yet.
/// </param>
public sealed record ExperienceReport(
    ExperienceQuery Query,
    IReadOnlyList<RoleModelMetrics> Models,
    int UnattributedTrades,
    bool Provisional);

/// <summary>
/// One model in one role. Every row has its calls and the trades whose entry it took part in. The role's own section
/// is filled for its role only: the Strategist's decisions, the Risk Critic's verdicts, or an analyst's calls.
/// </summary>
/// <param name="Provider">As the catalog names it, such as OpenAI.</param>
/// <param name="ModelId">The provider's ID for the model.</param>
/// <param name="Trades">
/// For the Strategist, the trades it proposed. For the Risk Critic, the outcomes of the trades it approved, as is or
/// modified, because no entry goes ahead without one of those.
/// </param>
public sealed record RoleModelMetrics(
    AiRole Role,
    string Provider,
    string ModelId,
    CallMetrics Calls,
    TradeMetrics Trades,
    StrategistMetrics? Strategist,
    CriticMetrics? Critic,
    AnalystMetrics? Analyst);

/// <summary>
/// How a model's calls in a role went (FR-EXP-02's operational metrics). A contract retry is a call of its own. Rates
/// are fractions of the calls sent, and null when none was.
/// </summary>
/// <param name="Cycles">Cycles in which the role called this model.</param>
/// <param name="AnsweredCycles">Cycles in which this model's answer filled the role.</param>
/// <param name="FallbackCycles">Of those, the ones in which it answered as the role's fallback.</param>
/// <param name="Sent">Calls that reached the provider.</param>
/// <param name="NotSent">Calls that couldn't be sent, such as with no usable key, which nothing billed.</param>
/// <param name="Answered">Calls whose answer conformed to the role's contract.</param>
/// <param name="ContractViolations">Calls whose answer broke the role's contract (FR-PRM-08).</param>
/// <param name="Timeouts">Calls the provider didn't answer in time, after its retries.</param>
/// <param name="Refusals">Calls the model refused.</param>
/// <param name="Errors">Every other call the provider failed, after its retries.</param>
/// <param name="Truncated">Answers cut off at the output limit, which are contract violations too.</param>
/// <param name="Cost">The estimated cost of the calls to a priced model, in US dollars.</param>
/// <param name="UnpricedTokens">Input and output tokens of calls to a model with no price, which have no cost.</param>
/// <param name="MedianLatency">
/// Of the calls sent, from the first send to the answer. It's the upper edge of a band about a quarter wide, so it's
/// approximate. Null when none was sent.
/// </param>
/// <param name="P95Latency">The 95th percentile, as approximate as the median.</param>
public sealed record CallMetrics(
    long Cycles,
    long AnsweredCycles,
    long FallbackCycles,
    long Sent,
    long NotSent,
    long Answered,
    long ContractViolations,
    long Timeouts,
    long Refusals,
    long Errors,
    long Truncated,
    long InputTokens,
    long OutputTokens,
    decimal Cost,
    long UnpricedTokens,
    TimeSpan? MedianLatency,
    TimeSpan? P95Latency)
{
    public decimal? ErrorRate => Rate(Errors);

    public decimal? TimeoutRate => Rate(Timeouts);

    public decimal? RefusalRate => Rate(Refusals);

    public decimal? ContractViolationRate => Rate(ContractViolations);

    private decimal? Rate(long count) => Sent == 0 ? null : (decimal)count / Sent;
}

/// <summary>
/// The settled trades a model took part in, through the cycle that proposed each one's entry. Only what was closed with
/// a known price counts for P&amp;L, and a trade with nothing priced counts neither as a win nor as a loss.
/// </summary>
/// <param name="Trades">Settled trades.</param>
/// <param name="Priced">Of those, the ones with anything closed at a known price.</param>
/// <param name="Wins">Priced trades whose P&amp;L, net of fees and funding, is above zero.</param>
/// <param name="Losses">And below zero.</param>
/// <param name="AverageR">The mean R-multiple of the priced trades that have one.</param>
/// <param name="AverageHolding">From the first fill to the last part's close.</param>
/// <param name="Results">One per quote asset, because P&amp;L in one can't be added to another.</param>
/// <param name="AiCostPerTrade">
/// The model's own cost in the role, in US dollars, over the trades: its HOLD cycles included. Null with no trades, or
/// when any of its calls had no price.
/// </param>
/// <param name="NetOfAiCostUsd">
/// The P&amp;L less the model's own cost in the role, in US dollars, when every trade's quote asset counts as US
/// dollars (<c>Experience:UsdQuoteAssets</c>) and every call had a price. Null otherwise.
/// </param>
public sealed record TradeMetrics(
    int Trades,
    int Priced,
    int Wins,
    int Losses,
    decimal? AverageR,
    TimeSpan? AverageHolding,
    IReadOnlyList<QuoteResults> Results,
    decimal? AiCostPerTrade,
    decimal? NetOfAiCostUsd)
{
    /// <summary>Wins over priced trades, or null with none.</summary>
    public decimal? WinRate => Priced == 0 ? null : (decimal)Wins / Priced;
}

/// <summary>Priced trades' results in one quote asset, in that asset.</summary>
/// <param name="Trades">Priced trades in this quote asset.</param>
/// <param name="TotalPnl">Net of fees and funding. A loss is negative.</param>
/// <param name="GrossProfit">The sum of the winning trades' P&amp;L.</param>
/// <param name="GrossLoss">The sum of the losing trades' P&amp;L, as a positive amount.</param>
/// <param name="MaxDrawdown">
/// The largest fall of the running total of P&amp;L from its highest point, starting from zero, with the trades in the
/// order they closed. A positive amount, or zero.
/// </param>
public sealed record QuoteResults(string QuoteAsset, int Trades, decimal TotalPnl, decimal GrossProfit, decimal GrossLoss, decimal MaxDrawdown)
{
    /// <summary>Gross profit over gross loss, or null with no loss.</summary>
    public decimal? ProfitFactor => GrossLoss == 0m ? null : GrossProfit / GrossLoss;

    /// <summary>The P&amp;L a trade made on average.</summary>
    public decimal Expectancy => Trades == 0 ? 0m : TotalPnl / Trades;
}

/// <summary>What the Strategist decided, from its answered calls.</summary>
/// <param name="Entries">BUY, OPEN_LONG and OPEN_SHORT, adds included.</param>
/// <param name="Exits">SELL and CLOSE.</param>
/// <param name="Adjustments">ADJUST_STOP, ADJUST_TARGET and CANCEL_PENDING.</param>
public sealed record StrategistMetrics(long Holds, long Entries, long Exits, long Adjustments)
{
    /// <summary>Every decision that wasn't HOLD.</summary>
    public long Proposals => Entries + Exits + Adjustments;
}

/// <summary>
/// The Risk Critic's verdicts, from its answered calls. A proposal the user rejected, or that expired or failed its
/// second look, isn't the Risk Critic's rejection, so it isn't counted here.
/// </summary>
public sealed record CriticMetrics(long Approvals, long Modifications, long Rejections);

/// <summary>
/// An analyst's directional calls, scored against the close <c>Experience:AnalystHorizonCandles</c> candles after the
/// cycle's candle. The Technical Analyst calls up or down with its trend, and makes no call in a range. The Sentiment
/// Analyst calls with a score of at least 0.2 either way. A price that didn't move is a miss.
/// </summary>
/// <param name="Hits">Calls the price followed.</param>
/// <param name="Misses">Calls it didn't.</param>
public sealed record AnalystMetrics(long BullishCalls, long BearishCalls, long Hits, long Misses)
{
    public long Calls => BullishCalls + BearishCalls;

    /// <summary>Calls not scored yet, or whose prices couldn't be read.</summary>
    public long Unresolved => Calls - Hits - Misses;

    /// <summary>Hits over scored calls, or null with none.</summary>
    public decimal? HitRate => Hits + Misses == 0 ? null : (decimal)Hits / (Hits + Misses);
}
