using Ecnanib.Agents;

namespace Ecnanib.DecisionEngine;

/// <summary>
/// What evaluates the loss limits of an agent, at every cycle and every reconciliation: when its
/// loss for the UTC day reaches its daily loss limit, or its drawdown from its peak equity reaches
/// its drawdown limit, its position is closed and it is halted (FR-RSK-8). See "Halting an agent"
/// in docs/risk-guard.md.
/// </summary>
/// <remarks>
/// <para>
/// When a limit is reached is for the Risk Guard to say: <see cref="RiskGuard.LossLimits"/>,
/// which the rule that rejects an entry asks as well (FR-RSK-4). What the loss is counted from
/// is the result of the agent, with unrealized profit and loss, which whatever keeps the
/// positions works out.
/// </para>
/// <para>
/// It is public so that a test can put a stand-in behind it, for cycles of agents that need no
/// rows: the tests see no internal type.
/// </para>
/// </remarks>
public interface ILossLimits
{
    /// <summary>
    /// Evaluates the loss limits of an agent now. An agent that runs and has reached one is
    /// halted, with the limit as the cause and now as the time, its user is told, and its
    /// position is closed at market. An agent that does not run is left as it is: its position
    /// stays open with its protective orders (SRS 3.3).
    /// </summary>
    /// <param name="agent">The agent, as it is stored: its limits and its capital are those that it has then.</param>
    /// <param name="cancellationToken">Canceled when what asked is stopped. An agent that was halted by then stays halted, and its position is still closed.</param>
    /// <returns>What was found.</returns>
    Task<LossLimitVerdict> EvaluateAsync(Agent agent, CancellationToken cancellationToken);
}

/// <summary>What evaluating the loss limits of an agent found.</summary>
public enum LossLimitVerdict
{
    /// <summary>The agent has reached no limit, or does not run, and is as it was.</summary>
    Within,

    /// <summary>
    /// Its equity is not known, because the exchange did not say the price that its open position
    /// is valued at. Nothing is halted on a guess: it is evaluated again the next time.
    /// </summary>
    NotKnown,

    /// <summary>
    /// The agent is halted for a loss limit: by this evaluation, or by one before it. Its
    /// position is closed, or is still being closed. It runs no cycle and opens no position.
    /// </summary>
    Halted,
}
