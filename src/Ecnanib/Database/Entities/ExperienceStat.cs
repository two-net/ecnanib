using Ecnanib.Ai.Abstractions;
using Ecnanib.Exchange.Abstractions;

namespace Ecnanib.Database.Entities;

/// <summary>
/// Experience statistics for one provider/model/role/timeframe/market combination at one scope (FR-EXP-04,
/// FR-EXP-05). Agent-scope rows also carry the user so every read stays user-scoped (NFR-SEC-03); system-scope
/// rows carry neither and hold counts and ratios only. Written by the experience module's refresher alone.
/// </summary>
public sealed class ExperienceStat
{
    public long Id { get; set; }

    public StatScope Scope { get; set; }

    public Guid? AgentId { get; set; }

    public Agent? Agent { get; set; }

    public Guid? UserId { get; set; }

    public User? User { get; set; }

    public AiProvider Provider { get; set; }

    public required string Model { get; set; }

    public CouncilRole Role { get; set; }

    public CandleInterval Timeframe { get; set; }

    public MarketType MarketType { get; set; }

    public ExperienceMetrics Metrics { get; set; } = new();

    public DateTimeOffset ComputedAt { get; set; }
}

/// <summary>
/// The FR-EXP-04 metrics as the counts and sums they are computed from, with the ratios derived by
/// <see cref="Derive"/>, so rows roll up exactly across agents, users and leaderboard filters (<see cref="Add"/>).
/// Ratios are 0..1; R, expectancy and the sums are in risk units (R), never money, so a system-wide row exposes
/// nothing about any account (FR-EXP-05). The experience module's README defines each figure.
/// </summary>
public sealed class ExperienceMetrics
{
    private const int RatioDecimals = 4;

    // Turns: the role was called (Succeeded, Failed or TimedOut).

    /// <summary>Turns of the role: every time it was called, whatever the outcome.</summary>
    public int Decisions { get; set; }

    /// <summary>Turns that proposed open_long or open_short.</summary>
    public int Approvals { get; set; }

    /// <summary>Turns that proposed no_trade while an entry was on the table (the decision opened one, or a shadow outcome exists).</summary>
    public int Rejections { get; set; }

    /// <summary>Turns that failed or timed out (FR-AI-12).</summary>
    public int Failures { get; set; }

    public long TotalLatencyMs { get; set; }

    // Risk Manager only: final documents and how many of them the hard limits clamped (FR-AI-11).

    public int RiskManagerTurns { get; set; }

    public int Clamped { get; set; }

    // Closed trades whose entry the role proposed; R is gross (price move over the initial risk), net R is after fees and funding.

    public int Trades { get; set; }

    /// <summary>Trades with a positive gross R; a breakeven trade is neither a win nor a loss.</summary>
    public int Wins { get; set; }

    public int Losses { get; set; }

    public decimal SumR { get; set; }

    public decimal GrossProfitR { get; set; }

    /// <summary>Sum of the losing trades' |R|, a positive number.</summary>
    public decimal GrossLossR { get; set; }

    /// <summary>Trades whose net R is known (an initial stop was recorded).</summary>
    public int NetTrades { get; set; }

    public decimal SumNetR { get; set; }

    /// <summary>
    /// Gross loss (R) of every closed trade of the owner (agent, user or everyone) at this timeframe and market: the
    /// denominator of <see cref="DrawdownContribution"/>. The same figure on every row of one owner, so
    /// <see cref="Add"/> never sums it; the aggregator sets it.
    /// </summary>
    public decimal ScopeGrossLossR { get; set; }

    // Rejections with a known outcome: resolved shadow outcomes (FR-EXP-03), and trades opened over the role's objection.

    public int RejectionsResolved { get; set; }

    /// <summary>Resolved rejections that would have hit the target, or did win once opened anyway.</summary>
    public int RejectionsMissedWins { get; set; }

    public decimal RejectionsSumR { get; set; }

    // Derived by Derive(); kept in the row so the stored JSON reads on its own.

    public decimal? WinRate { get; set; }

    public decimal? AverageR { get; set; }

    /// <summary>Mean net R per trade: what the account earned per unit risked, fees and funding included.</summary>
    public decimal? Expectancy { get; set; }

    public decimal? ProfitFactor { get; set; }

    public decimal? DrawdownContribution { get; set; }

    /// <summary>How often the Risk Manager's output needed clamping to a hard limit (FR-AI-11); null for other roles.</summary>
    public decimal? ClampRate { get; set; }

    public decimal? FailureRate { get; set; }

    public int? AverageLatencyMs { get; set; }

    /// <summary>Share of the resolved rejections that avoided a loss.</summary>
    public decimal? RejectionAccuracy { get; set; }

    /// <summary>Adds the other row's counts and sums; the ratios are stale until <see cref="Derive"/> runs again. <see cref="ScopeGrossLossR"/> is left alone.</summary>
    public ExperienceMetrics Add(ExperienceMetrics other)
    {
        ArgumentNullException.ThrowIfNull(other);
        Decisions += other.Decisions;
        Approvals += other.Approvals;
        Rejections += other.Rejections;
        Failures += other.Failures;
        TotalLatencyMs += other.TotalLatencyMs;
        RiskManagerTurns += other.RiskManagerTurns;
        Clamped += other.Clamped;
        Trades += other.Trades;
        Wins += other.Wins;
        Losses += other.Losses;
        SumR += other.SumR;
        GrossProfitR += other.GrossProfitR;
        GrossLossR += other.GrossLossR;
        NetTrades += other.NetTrades;
        SumNetR += other.SumNetR;
        RejectionsResolved += other.RejectionsResolved;
        RejectionsMissedWins += other.RejectionsMissedWins;
        RejectionsSumR += other.RejectionsSumR;
        return this;
    }

    /// <summary>Fills the ratios from the counts and sums; a ratio without a denominator is null.</summary>
    public ExperienceMetrics Derive()
    {
        WinRate = Ratio(Wins, Trades);
        AverageR = Trades > 0 ? Round(SumR / Trades) : null;
        Expectancy = NetTrades > 0 ? Round(SumNetR / NetTrades) : null;
        ProfitFactor = Trades > 0 && GrossLossR > 0 ? Round(GrossProfitR / GrossLossR) : null;
        DrawdownContribution = ScopeGrossLossR > 0 ? Round(GrossLossR / ScopeGrossLossR) : null;
        ClampRate = Ratio(Clamped, RiskManagerTurns);
        FailureRate = Ratio(Failures, Decisions);
        AverageLatencyMs = Decisions > 0 ? (int)Math.Min(int.MaxValue, TotalLatencyMs / Decisions) : null;
        RejectionAccuracy = Ratio(RejectionsResolved - RejectionsMissedWins, RejectionsResolved);
        return this;
    }

    /// <summary>A copy of the counts and sums (ratios included as they stand).</summary>
    public ExperienceMetrics Clone() => (ExperienceMetrics)MemberwiseClone();

    private static decimal? Ratio(int numerator, int denominator) => denominator > 0 ? Round((decimal)numerator / denominator) : null;

    private static decimal Round(decimal value) => Math.Round(value, RatioDecimals, MidpointRounding.AwayFromZero);
}
