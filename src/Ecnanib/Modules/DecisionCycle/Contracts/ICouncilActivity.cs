using Ecnanib.Kernel;
using Ecnanib.Modules.AiCouncil.Contracts;

namespace Ecnanib.Modules.DecisionCycle.Contracts;

/// <summary>
/// What every user's councils did, call by call, for the trading experience metrics (FR-EXP-01, FR-EXP-02), which
/// Experience keeps after retention purges the traces (#50). It isn't scoped to a user, because Experience's job looks
/// after everyone's, so <c>BannedApis</c> keeps it to DecisionCycle and Experience. It never reads a prompt, an answer
/// or a provider's message: only each call's facts, and the few fields of a checked output that the metrics score. See
/// Trading experience in docs/architecture.md.
/// </summary>
public interface ICouncilActivity
{
    /// <summary>
    /// Each user and timeframe with a cycle that started at or after <paramref name="startedFrom"/>, with the earliest
    /// such start.
    /// </summary>
    Task<IReadOnlyList<CouncilActivityKey>> ListAsync(DateTimeOffset startedFrom, CancellationToken cancellationToken);

    /// <summary>
    /// Every AI call of the user's cycles in <paramref name="timeframe"/> that started at or after
    /// <paramref name="from"/> and before <paramref name="to"/>, in the order they were made. Each carries its cycle's
    /// environment, market and pair from the cycle's own row, never the agent's (FR-EXP-08).
    /// </summary>
    Task<IReadOnlyList<CouncilCallRecord>> ListCallsAsync(
        Guid userId, Timeframe timeframe, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken);
}

/// <param name="FirstStartedAt">The earliest start among the cycles the list was asked for.</param>
public sealed record CouncilActivityKey(Guid UserId, Timeframe Timeframe, DateTimeOffset FirstStartedAt);

/// <summary>
/// One call to a model in a cycle, or one that couldn't be sent, with its cycle's dimensions. The scored fields are
/// read from the checked output of an answered call, so they're null for any other: what the Strategist proposed,
/// the Risk Critic's verdict, the Technical Analyst's trend and the Sentiment Analyst's score.
/// </summary>
/// <param name="Id">The call's, which orders the calls of a cycle.</param>
/// <param name="CandleClose">When the cycle's candle closed.</param>
/// <param name="CycleStartedAt">When the cycle started, which says which day it counts in.</param>
/// <param name="Fallback">Whether the call was to the role's fallback model.</param>
/// <param name="Attempt">1, or 2 for the retry an answer that broke its contract gets (FR-CYC-05).</param>
/// <param name="Latency">From the first send to the answer, the adapter's retries included. Zero when nothing was sent.</param>
/// <param name="Cost">In US dollars, or null when the model has no price or nothing was billed.</param>
public sealed record CouncilCallRecord(
    long Id,
    Guid CycleId,
    Guid AgentId,
    Market Market,
    TradingEnvironment Environment,
    string Symbol,
    Timeframe Timeframe,
    DateTimeOffset CandleClose,
    DateTimeOffset CycleStartedAt,
    AiRole Role,
    string Provider,
    string ModelId,
    bool Fallback,
    int Attempt,
    CouncilCallStatus Status,
    CouncilFailure? Failure,
    bool Truncated,
    TimeSpan Latency,
    long InputTokens,
    long OutputTokens,
    decimal? Cost)
{
    /// <summary>The Strategist's action.</summary>
    public TradeAction? Action { get; init; }

    /// <summary>The Risk Critic's verdict.</summary>
    public VerdictKind? Verdict { get; init; }

    /// <summary>The Technical Analyst's trend on the agent's timeframe.</summary>
    public Trend? Trend { get; init; }

    /// <summary>The Sentiment Analyst's score, from −1, bearish, to +1, bullish.</summary>
    public decimal? Score { get; init; }
}
