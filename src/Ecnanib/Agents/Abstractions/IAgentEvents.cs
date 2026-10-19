using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;

namespace Ecnanib.Agents.Abstractions;

/// <summary>Something the UI should reflect without a refresh (FR-AGT-10 "notify UI", FR-UI-04).</summary>
public abstract record AgentEvent(Guid AgentId, Guid UserId, DateTimeOffset At);

public sealed record AgentStateChanged(Guid AgentId, Guid UserId, DateTimeOffset At, AgentState State, string? Reason) : AgentEvent(AgentId, UserId, At);

public sealed record EvaluationFinished(Guid AgentId, Guid UserId, DateTimeOffset At, Guid EvaluationId, DateTimeOffset CandleOpenTime, EvaluationOutcome Outcome) : AgentEvent(AgentId, UserId, At);

public sealed record OrderChanged(Guid AgentId, Guid UserId, DateTimeOffset At, Guid OrderId, OrderStatus Status) : AgentEvent(AgentId, UserId, At);

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
