using Ecnanib.Ai.Abstractions;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;

namespace Ecnanib.Experience.Abstractions;

/// <summary>The leaderboard filter (FR-EXP-07): a null dimension means every value of it, aggregated exactly from the stored counts and sums.</summary>
public sealed record ExperienceFilter(CouncilRole? Role, CandleInterval? Timeframe, MarketType? MarketType)
{
    public static readonly ExperienceFilter None = new(null, null, null);

    public bool Matches(ExperienceStat stat)
    {
        ArgumentNullException.ThrowIfNull(stat);
        return (Role is null || stat.Role == Role) && (Timeframe is null || stat.Timeframe == Timeframe) && (MarketType is null || stat.MarketType == MarketType);
    }
}

/// <summary>One provider and model on the leaderboard, its metrics aggregated over the filter.</summary>
public sealed record LeaderboardRow(AiProvider Provider, string Model, ExperienceMetrics Metrics);

/// <summary>
/// The leaderboard (FR-EXP-07): the user's own rows (every agent of theirs) and the system-wide rows (every user,
/// counts and ratios only, FR-EXP-05), best expectancy first. <see cref="ComputedAt"/> is null before the first refresh.
/// </summary>
public sealed record LeaderboardView(IReadOnlyList<LeaderboardRow> Mine, IReadOnlyList<LeaderboardRow> SystemWide, DateTimeOffset? ComputedAt);

/// <summary>One model in one role on the provider detail page, aggregated over the filter.</summary>
public sealed record ModelRoleRow(string Model, CouncilRole Role, ExperienceMetrics Metrics);

/// <summary>One of the user's agents' rows for the provider, at the agent's own timeframe and market.</summary>
public sealed record AgentExperienceRow(Guid AgentId, string AgentName, string Model, CouncilRole Role, CandleInterval Timeframe, MarketType MarketType, ExperienceMetrics Metrics);

/// <summary>The detail view of one provider (FR-EXP-07): per model and role for the user and system-wide, and per agent of the user.</summary>
public sealed record ProviderExperienceView(
    AiProvider Provider,
    IReadOnlyList<ModelRoleRow> Mine,
    IReadOnlyList<ModelRoleRow> SystemWide,
    IReadOnlyList<AgentExperienceRow> Agents,
    DateTimeOffset? ComputedAt);

/// <summary>An agent-scope row (FR-EXP-05); <see cref="IsCurrent"/> when the agent still assigns that provider and model to the role.</summary>
public sealed record RoleExperienceRow(CouncilRole Role, AiProvider Provider, string Model, CandleInterval Timeframe, MarketType MarketType, bool IsCurrent, ExperienceMetrics Metrics);

/// <summary>A closed trade with its outcome (FR-EXP-02), linked to the evaluation that opened it and counting the decisions that managed it.</summary>
public sealed record TradeOutcomeView(
    Guid TradeId,
    Guid EntryEvaluationId,
    PositionSide Side,
    decimal EntryPrice,
    decimal? ExitPrice,
    decimal Size,
    decimal? RMultiple,
    decimal? RealizedPnl,
    decimal Fees,
    decimal Funding,
    decimal? MaxAdverseExcursion,
    decimal? MaxFavorableExcursion,
    TradeExitReason? ExitReason,
    bool IsPaper,
    DateTimeOffset OpenedAt,
    DateTimeOffset ClosedAt,
    int ManagingDecisions)
{
    public TimeSpan Duration => ClosedAt - OpenedAt;
}

/// <summary>The experience section of an agent page: its rows per role and its most recent closed trades.</summary>
public sealed record AgentExperienceView(IReadOnlyList<RoleExperienceRow> Roles, IReadOnlyList<TradeOutcomeView> RecentTrades, DateTimeOffset? ComputedAt);

/// <summary>
/// The shadow outcome of a setup the council did not take (FR-EXP-03). <see cref="Reason"/> is why nothing was
/// opened (veto, limits, no-decision); <see cref="Resolution"/> how it ended (stop, target, not-filled, horizon,
/// stale, no-detail) once it is no longer pending.
/// </summary>
public sealed record ShadowOutcomeView(
    ShadowResult Result,
    string Reason,
    string? Resolution,
    decimal? RMultiple,
    int CandlesSeen,
    int EntryTimeoutCandles,
    int HorizonCandles,
    DateTimeOffset? FilledAt,
    DateTimeOffset? ResolvedAt);

/// <summary>
/// Reads experience for the pages (FR-EXP-07, FR-UI-01). Every read of a user's or an agent's rows is scoped to
/// the signed-in user (NFR-SEC-03): another user's agent and a missing one both answer null. System-wide rows carry
/// no user, agent or symbol and are shown to everyone (FR-EXP-05).
/// </summary>
public interface IExperienceReader
{
    Task<LeaderboardView> GetLeaderboardAsync(Guid userId, ExperienceFilter filter, CancellationToken cancellationToken);

    Task<ProviderExperienceView> GetProviderAsync(Guid userId, AiProvider provider, ExperienceFilter filter, CancellationToken cancellationToken);

    Task<AgentExperienceView?> GetAgentAsync(Guid userId, Guid agentId, CancellationToken cancellationToken);

    Task<ShadowOutcomeView?> GetShadowOutcomeAsync(Guid userId, Guid evaluationId, CancellationToken cancellationToken);
}
