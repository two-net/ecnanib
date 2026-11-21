using Ecnanib.Agents;
using Ecnanib.Shared;

namespace Ecnanib.DecisionEngine;

/// <summary>
/// Composes the prompt of a role call from its parts: the instructions of the role, the modules
/// of the market, the timeframe, and the profile, the context of the agent, the data package,
/// and recalled lessons (FR-PRM-1). And the prompt of the call that writes the lesson of a
/// closed position (FR-EXP-6). See docs/prompts.md.
/// </summary>
/// <remarks>
/// <para>
/// It is given no template. The modules follow from the market and the timeframe of the agent,
/// which the context of the agent says once, so a module that does not go with the agent cannot
/// be chosen (FR-PRM-4). It calls no model, and stores nothing.
/// </para>
/// <para>
/// What it is given is what a provider is sent, and is limited to market data, the settings of
/// the agent, the state of its position, and lessons (NFR-SEC-8). A prompt that would hold a
/// secret, an email address, or an account identifier is not composed (FR-PRM-9).
/// </para>
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
    /// <exception cref="PromptRefusedException">
    /// A part of the prompt would hold a secret, an email address, or an account identifier,
    /// which a prompt never holds (FR-PRM-9).
    /// </exception>
    Task<ComposedPrompt> ComposeAsync(Role role, PromptParts parts, CancellationToken cancellationToken);

    /// <summary>
    /// The prompt of the call that writes the lesson of a closed position (FR-EXP-6): the lesson
    /// instructions that are in use, and the closed position as the call is given it. It has no
    /// module and no context of an agent: the call decides nothing about a trade.
    /// </summary>
    /// <param name="parts">What the prompt is composed from besides the template.</param>
    /// <param name="cancellationToken">Cancels the question.</param>
    /// <exception cref="PromptRefusedException">
    /// A part of the prompt would hold a secret, an email address, or an account identifier,
    /// which a prompt never holds (FR-PRM-9).
    /// </exception>
    Task<ComposedPrompt> ComposeLessonAsync(LessonParts parts, CancellationToken cancellationToken);
}

/// <summary>
/// No prompt was composed: one of its parts would hold what a prompt never holds, a secret, an
/// email address, or an account identifier (FR-PRM-9). So nothing of the kind is sent to a
/// provider (NFR-SEC-8), however a part came to hold it.
/// </summary>
/// <remarks>
/// It says which part and what kind of thing, and never what was found: a message is logged.
/// Whatever runs a cycle makes no call with such a prompt, and the cycle ends with no decision.
/// </remarks>
/// <param name="part">The heading of the part of the prompt that would hold it.</param>
/// <param name="holds">What kind of thing the part would hold.</param>
public sealed class PromptRefusedException(string part, PromptLeak holds) : Exception(Describe(part, holds))
{
    /// <summary>The heading of the part of the prompt that would hold it, such as <c>Data package</c>.</summary>
    public string Part { get; } = part;

    /// <summary>What kind of thing the part would hold.</summary>
    public PromptLeak Holds { get; } = holds;

    private static string Describe(string part, PromptLeak holds)
    {
        var what = holds switch
        {
            PromptLeak.Secret => "a secret",
            PromptLeak.EmailAddress => "an email address",
            PromptLeak.Identifier => "an account identifier",
            _ => throw new ArgumentOutOfRangeException(nameof(holds), holds, "No such kind of leak."),
        };

        return $"No prompt is composed: its part \"{part}\" would hold {what}. A prompt never holds secrets, email " +
            "addresses, account identifiers, or other personal data (FR-PRM-9). See docs/prompts.md.";
    }
}

/// <summary>
/// What a prompt is composed from besides the templates (FR-PRM-1). Whatever runs a cycle hands
/// them over for each call of a role, and so decides what the role is given (FR-TEAM-6).
/// </summary>
/// <param name="Agent">The context of the agent: its symbol, its equity, its position, and its limits.</param>
/// <param name="DataPackage">
/// The data package of the cycle, as the text that the role reads (FR-CYC-2). What builds the
/// package writes it: it is placed as it is, and nothing is added to it. Empty when the call is
/// given none. It holds market data, and nothing that says whose agent it is for (NFR-SEC-8).
/// </param>
/// <param name="Lessons">
/// The lessons that were recalled for the call, most recent first (FR-EXP-7). What recalls them
/// says which and how many: they are placed as they are. Empty when none was recalled. A lesson
/// is about a trade, and names no user and no agent.
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

/// <summary>
/// What the prompt of a lesson is composed from besides its template (FR-EXP-6): the closed
/// position, and what the team answered in the cycle that opened it. It is what the Risk Manager
/// of a cycle is given of a plan, with what became of it: no report of an analyst is among it.
/// </summary>
/// <param name="Position">The closed position, with its result.</param>
/// <param name="Plan">
/// The trade plan that opened the position, as the Strategist answered it and as its record
/// keeps it: one line of JSON, which was validated and screened. Null where none is recorded.
/// </param>
/// <param name="Review">
/// The review of that plan, as the Risk Manager answered it and as its record keeps it: one line
/// of JSON. Null where none is recorded.
/// </param>
public sealed record LessonParts(ClosedPositionContext Position, string? Plan, string? Review);

/// <summary>
/// A closed position in the prompt of its lesson: what it was, how it ended, and its result. It
/// has no id and no name of anything, and no time: a prompt holds no account identifier
/// (FR-PRM-9).
/// </summary>
/// <param name="Market">The market that it was traded on.</param>
/// <param name="Timeframe">The timeframe of its agent when it was opened.</param>
/// <param name="Symbol">Its symbol.</param>
/// <param name="QuoteAsset">The asset that the symbol is priced in, which its result is in.</param>
/// <param name="Side">Long or short.</param>
/// <param name="EntryPrice">The price that it was entered at.</param>
/// <param name="StopLoss">The stop-loss that it was opened with.</param>
/// <param name="TakeProfit">The take-profit that it was opened with, or null when it had none.</param>
/// <param name="ExitReason">How it was closed.</param>
/// <param name="HoldingSeconds">How long it was held.</param>
/// <param name="ResultInR">Its net result as a multiple of what was risked, or null where that cannot be said.</param>
/// <param name="NetResult">Its net result, after fees and funding.</param>
/// <param name="Fees">Its fees.</param>
/// <param name="Funding">What it received as funding, less what it paid.</param>
/// <param name="Complete">Whether its result is all what the exchange said. One that is not is an estimate.</param>
public sealed record ClosedPositionContext(
    Market Market,
    Timeframe Timeframe,
    string Symbol,
    string QuoteAsset,
    PositionSide Side,
    decimal EntryPrice,
    decimal StopLoss,
    decimal? TakeProfit,
    ExitReason ExitReason,
    long HoldingSeconds,
    decimal? ResultInR,
    decimal NetResult,
    decimal Fees,
    decimal Funding,
    bool Complete);

/// <summary>The prompt of one call of a role.</summary>
/// <param name="Text">
/// The prompt, as it is sent. It is the same for every provider: only how the structured result
/// is asked for is a provider's own (FR-PRM-10).
/// </param>
/// <param name="Templates">
/// The templates that the prompt was composed from, each in the version that was in use, in the
/// order of the prompt. They are the template versions that the output of the call records
/// (FR-PRM-6). The prompt of a lesson has one: its instructions.
/// </param>
public sealed record ComposedPrompt(string Text, IReadOnlyList<PromptTemplate> Templates);
