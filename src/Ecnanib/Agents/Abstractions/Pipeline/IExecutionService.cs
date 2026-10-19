using Ecnanib.Agents.Configuration;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;

namespace Ecnanib.Agents.Abstractions;

/// <summary>
/// Everything the "apply hard risk limits → execute" steps need (FR-AGT-10, FR-AI-11, FR-EXE-04). The decision is
/// already saved. <see cref="InterruptedPreviously"/> is true when an evaluation of this agent was cut short by a
/// restart, so the exchange state must be checked before any new action (NFR-REL-01). <see cref="ExchangeKey"/> is
/// the resolved handle of a live agent, null for paper.
/// </summary>
public sealed record ExecutionRequest(
    Agent Agent,
    Decision Decision,
    IReadOnlyList<Trade> OpenTrades,
    IReadOnlyList<Order> OpenOrders,
    RiskCapsOptions Caps,
    IOrderVenue Venue,
    bool InterruptedPreviously,
    ExchangeKeyHandle? ExchangeKey);

/// <summary><see cref="Orders"/> are the orders placed; <see cref="Note"/> is shown as the evaluation's outcome detail.</summary>
public sealed record ExecutionResult(IReadOnlyList<Order> Orders, string? Note);

/// <summary>
/// Sizing, limit enforcement and order placement for a decision (execution issue). Seeded by the agents issue with
/// a service that executes nothing and says so. Register an implementation before <c>AddAgents</c> to replace the default.
/// </summary>
public interface IExecutionService
{
    Task<ExecutionResult> ExecuteAsync(ExecutionRequest request, CancellationToken cancellationToken);
}
