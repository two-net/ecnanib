using Ecnanib.Agents;
using Ecnanib.Shared;

namespace Ecnanib.DecisionEngine;

/// <summary>
/// Composes the prompt of a role call from its parts: the instructions of the role, the modules
/// of the market, the timeframe, and the profile, the context of the agent, the data package,
/// and recalled lessons (FR-PRM-1). See docs/prompts.md.
/// </summary>
/// <remarks>
/// It is given no template. The modules follow from the market and the timeframe of the agent,
/// which the context of the agent says once, so a module that does not go with the agent cannot
/// be chosen (FR-PRM-4). It calls no model, and stores nothing.
/// </remarks>
public interface IPromptComposition
{
    /// <summary>
    /// The prompt of one call of a role, from the templates that are in use for the agent's
    /// market and timeframe, and from what the call is given.
    /// </summary>
    /// <param name="role">The role that is called.</param>
    /// <param name="parts">What the prompt is composed from besides the templates.</param>
    /// <param name="cancellationToken">Cancels the question.</param>
    /// <exception cref="ArgumentException">The context names a short position of an agent on Spot, which is long-only.</exception>
    Task<ComposedPrompt> ComposeAsync(Role role, PromptParts parts, CancellationToken cancellationToken);
}

/// <summary>
/// What a prompt is composed from besides the templates (FR-PRM-1). Whatever runs a cycle hands
/// them over for each call of a role, and so decides what the role is given (FR-TEAM-6).
/// </summary>
/// <param name="Agent">The context of the agent: its symbol, its equity, its position, and its limits.</param>
/// <param name="DataPackage">
/// The data package of the cycle, as the text that the role reads (FR-CYC-2). What builds the
/// package writes it: it is placed as it is, and nothing is added to it. Empty when the call is
/// given none.
/// </param>
/// <param name="Lessons">
/// The lessons that were recalled for the call, most recent first (FR-EXP-7). What recalls them
/// says which and how many: they are placed as they are. Empty when none was recalled.
/// </param>
public sealed record PromptParts(AgentContext Agent, string DataPackage, IReadOnlyList<string> Lessons);

/// <summary>
/// The context of an agent in a prompt: its symbol, its equity, its position, and its limits
/// (FR-PRM-1), with the market and the timeframe that its modules follow from (FR-PRM-4).
/// </summary>
/// <remarks>
/// It has no name of the agent and no id of anything: a prompt holds no personal data and no
/// account identifier (FR-PRM-9).
/// </remarks>
/// <param name="Market">The market of the agent, which its market module follows from.</param>
/// <param name="Timeframe">The timeframe of the agent, which its timeframe module follows from.</param>
/// <param name="Symbol">The symbol it trades.</param>
/// <param name="QuoteAsset">The asset that the symbol is priced in, which the equity is in.</param>
/// <param name="Equity">The capital its user allocated to it, plus its net profit or loss to date.</param>
/// <param name="Position">Its open position, or null when it has none.</param>
/// <param name="Limits">Its risk settings. The limits that the specification fixes are stated with them.</param>
public sealed record AgentContext(
    Market Market,
    Timeframe Timeframe,
    string Symbol,
    string QuoteAsset,
    decimal Equity,
    PositionContext? Position,
    AgentRisk Limits)
{
    /// <summary>
    /// The context of a stored agent: its market, its timeframe, its symbol, and its limits are
    /// its own settings, so whatever runs a cycle names none of them itself (FR-PRM-4).
    /// </summary>
    /// <param name="agent">The agent, as it is stored.</param>
    /// <param name="equity">Its equity now.</param>
    /// <param name="position">Its open position, or null when it has none.</param>
    public static AgentContext Of(Agent agent, decimal equity, PositionContext? position)
    {
        ArgumentNullException.ThrowIfNull(agent);

        return new AgentContext(
            agent.Market,
            agent.Timeframe,
            agent.Symbol,
            agent.QuoteAsset,
            equity,
            position,
            new AgentRisk(
                agent.RiskPerTradePercent,
                agent.MinRewardToRisk,
                agent.LeverageCap,
                agent.LiquidationBuffer,
                agent.DailyLossPercent,
                agent.DrawdownPercent,
                agent.MaxEntryDistancePercent));
    }
}

/// <summary>The side of a position, as a prompt says it.</summary>
public enum PositionSide
{
    /// <summary>The agent bought, and gains when the price rises.</summary>
    Long,

    /// <summary>The agent sold, and gains when the price falls. On Futures only.</summary>
    Short,
}

/// <summary>The open position of an agent, as a prompt says it.</summary>
/// <param name="Side">Long or short.</param>
/// <param name="EntryPrice">The price it was entered at.</param>
/// <param name="Quantity">How much of the asset it holds.</param>
/// <param name="StopLoss">The price of its stop-loss, or null when none rests on the exchange.</param>
/// <param name="TakeProfit">The price of its take-profit, or null when none rests on the exchange.</param>
public sealed record PositionContext(
    PositionSide Side,
    decimal EntryPrice,
    decimal Quantity,
    decimal? StopLoss,
    decimal? TakeProfit);

/// <summary>The prompt of one call of a role.</summary>
/// <param name="Text">
/// The prompt, as it is sent. It is the same for every provider: only how the structured result
/// is asked for is a provider's own (FR-PRM-10).
/// </param>
/// <param name="Templates">
/// The templates that the prompt was composed from, each in the version that was in use, in the
/// order of the prompt. They are the template versions that the output of the call records
/// (FR-PRM-6).
/// </param>
public sealed record ComposedPrompt(string Text, IReadOnlyList<PromptTemplate> Templates);
