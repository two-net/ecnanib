using Ecnanib.Agents.Abstractions;
using System.Text.Json;
using Ecnanib.Ai.Abstractions;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;

namespace Ecnanib.Council.Abstractions;

/// <summary>One role's turn as the user sees it (FR-AI-14). <see cref="Document"/> and <see cref="InputSummary"/> are null once the transcript was swept.</summary>
public sealed record RoleTurnView(
    CouncilRole Role,
    int Sequence,
    AiProvider Provider,
    string Model,
    RoleOutputStatus Status,
    DecisionAction? ProposedAction,
    decimal? Confidence,
    int InputTokens,
    int OutputTokens,
    int LatencyMs,
    int Attempts,
    string? Error,
    JsonElement? Document,
    string? InputSummary);

/// <summary>The decision as stored, with its limits check (Appendix C) and the execution check appended at order time.</summary>
public sealed record FinalDecisionView(
    DecisionAction Action,
    OrderType? EntryType,
    decimal? EntryPrice,
    decimal? StopPrice,
    IReadOnlyList<decimal> Targets,
    decimal? PartialFraction,
    decimal? RiskPercent,
    decimal? Confidence,
    string Rationale,
    IReadOnlyList<LimitCheck> Limits,
    int TotalInputTokens,
    int TotalOutputTokens,
    int TotalLatencyMs,
    DateTimeOffset CreatedAt,
    /// <summary>The execution module's own checks at order time (sizing, notional, balance, daily loss; FR-EXE-04), empty until it ran.</summary>
    IReadOnlyList<LimitCheck>? ExecutionChecks = null)
{
    public IReadOnlyList<LimitCheck> ExecutionChecks { get; init; } = ExecutionChecks ?? [];
}

/// <summary>One evaluation with its deliberation, when the council ran (FR-AI-14).</summary>
public sealed record DeliberationView(
    Guid EvaluationId,
    Guid AgentId,
    string AgentName,
    string Symbol,
    MarketType MarketType,
    CandleInterval Timeframe,
    DateTimeOffset CandleOpenTime,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    EvaluationOutcome Outcome,
    string? OutcomeDetail,
    bool CouncilConsulted,
    JsonElement? StrategyOutput,
    FinalDecisionView? Decision,
    IReadOnlyList<RoleTurnView> Turns);

/// <summary>
/// One role's share of an agent's council calls (NFR-COST-01). <see cref="Calls"/> counts the council's attempts (one per
/// turn, two after a rejected answer, FR-AI-07), not the AI module's transient retries, which only the log has;
/// <see cref="ReasoningTokens"/> is the part of <see cref="OutputTokens"/> the models spent reasoning. Roles that were
/// disabled or skipped made none.
/// </summary>
public sealed record RoleTokenUsage(CouncilRole Role, int Calls, int InputTokens, int OutputTokens, int ReasoningTokens = 0);

/// <summary>
/// Token usage of an agent's council (NFR-COST-01, AI-04): the totals of every stored decision, today's UTC share, the
/// split per role and the output-token cap new calls are made under (<c>Council:MaxOutputTokens</c>, or
/// <c>Providers:MaxOutputTokens</c> when the council has none of its own; the configuration's current value, not the
/// cap a past decision was made under).
/// </summary>
public sealed record AgentTokenUsage(
    int Decisions,
    int InputTokens,
    int OutputTokens,
    int DecisionsToday,
    int InputTokensToday,
    int OutputTokensToday,
    DateTimeOffset? Since,
    IReadOnlyList<RoleTokenUsage> ByRole,
    int OutputTokenCap);

/// <summary>Reads one evaluation's deliberation, or an agent's token usage, for its owner (NFR-SEC-03): another user's evaluation or agent and a missing one both answer null.</summary>
public interface IDeliberationReader
{
    Task<DeliberationView?> GetAsync(Guid userId, Guid evaluationId, CancellationToken cancellationToken);

    Task<AgentTokenUsage?> GetAgentUsageAsync(Guid userId, Guid agentId, CancellationToken cancellationToken);
}
