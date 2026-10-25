using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;

namespace Ecnanib.Agents.Abstractions;

/// <summary>Something the UI should reflect without a refresh (FR-AGT-10 "notify UI", FR-UI-04).</summary>
public abstract record AgentEvent(Guid AgentId, Guid UserId, DateTimeOffset At);

public sealed record AgentStateChanged(Guid AgentId, Guid UserId, DateTimeOffset At, AgentState State, string? Reason) : AgentEvent(AgentId, UserId, At);

public sealed record EvaluationFinished(Guid AgentId, Guid UserId, DateTimeOffset At, Guid EvaluationId, DateTimeOffset CandleOpenTime, EvaluationOutcome Outcome) : AgentEvent(AgentId, UserId, At);

/// <summary>The evaluation row is committed as InProgress; the UI shows "evaluating" until <see cref="EvaluationFinished"/> (FR-UI-03 a).</summary>
public sealed record EvaluationStarted(Guid AgentId, Guid UserId, DateTimeOffset At, Guid EvaluationId, DateTimeOffset CandleOpenTime) : AgentEvent(AgentId, UserId, At);

/// <summary>A council role's model is being called (FR-UI-03 b); roles that run no model publish only <see cref="CouncilRoleFinished"/>.</summary>
public sealed record CouncilRoleStarted(Guid AgentId, Guid UserId, DateTimeOffset At, Guid EvaluationId, CouncilRole Role, int Sequence) : AgentEvent(AgentId, UserId, At);

public sealed record CouncilRoleFinished(Guid AgentId, Guid UserId, DateTimeOffset At, Guid EvaluationId, CouncilRole Role, int Sequence, RoleOutputStatus Status) : AgentEvent(AgentId, UserId, At);

/// <summary>The decision row is committed, before execution; the verdict can be revealed (FR-UI-03 b).</summary>
public sealed record DecisionRecorded(Guid AgentId, Guid UserId, DateTimeOffset At, Guid EvaluationId, Guid DecisionId, DecisionAction Action) : AgentEvent(AgentId, UserId, At);

public sealed record OrderChanged(Guid AgentId, Guid UserId, DateTimeOffset At, Guid OrderId, OrderStatus Status) : AgentEvent(AgentId, UserId, At);

/// <summary>A trade opened, changed (a stop or target move keeps <paramref name="IsOpen"/> true) or closed.</summary>
public sealed record TradeChanged(Guid AgentId, Guid UserId, DateTimeOffset At, Guid TradeId, bool IsOpen) : AgentEvent(AgentId, UserId, At);

/// <summary>
/// In-process publication of agent events (C-10: no broker). Handlers are invoked after the fact and never block the
/// publisher; a failing handler is logged and dropped from that event only.
/// </summary>
public interface IAgentEvents
{
    /// <summary>Events of the user's agents only (NFR-SEC-03). Dispose to stop.</summary>
    IDisposable Subscribe(Guid userId, Func<AgentEvent, ValueTask> handler);

    void Publish(AgentEvent agentEvent);
}
