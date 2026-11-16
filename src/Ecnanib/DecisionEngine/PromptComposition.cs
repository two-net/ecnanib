using System.Globalization;
using System.Text;
using Ecnanib.RiskGuard;
using Ecnanib.Shared;

namespace Ecnanib.DecisionEngine;

/// <summary>
/// Composes the prompt of a role call (FR-PRM-1). The templates are read here, by the market
/// and the timeframe of the agent's context, and are given by nobody (FR-PRM-4).
/// </summary>
internal sealed class PromptComposition(IPromptTemplates templates) : IPromptComposition
{
    /// <summary>What a part says when the call was given nothing for it.</summary>
    private const string Nothing = "None.";

    // Every digit that a decimal has, and no zero after the last of them: 1.50 is written 1.5.
    private const string AsWritten = "0.############################";

    public async Task<ComposedPrompt> ComposeAsync(Role role, PromptParts parts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parts);
        ArgumentNullException.ThrowIfNull(parts.Agent);
        ArgumentNullException.ThrowIfNull(parts.Lessons);

        var agent = parts.Agent;

        // Asked here, and not where the context is made: a context is a record, and can be
        // copied with another market.
        if (agent is { Market: Market.Spot, Position.Side: PositionSide.Short })
        {
            throw new ArgumentException(
                "An agent on Spot is long-only, and has no short position (FR-BIN-S1). No prompt is composed for one.",
                nameof(parts));
        }

        // The modules of the agent's own market and timeframe, and of nothing else.
        var inUse = await templates.InUseAsync(agent.Market, agent.Timeframe, cancellationToken);
        var used = inUse.For(role);

        // The parts in the order of the specification (FR-PRM-1). A part that the call was given
        // nothing for is there all the same, and says so.
        var text = new StringBuilder();

        Part(text, "Role instructions", inUse.Roles[role].Text);
        Part(text, "Market", inUse.Market.Text);
        Part(text, "Timeframe", inUse.Timeframe.Text);
        Part(text, "Profile", inUse.Profile.Text);
        Part(text, "Agent context", ContextOf(agent));
        Part(text, "Data package", string.IsNullOrWhiteSpace(parts.DataPackage) ? Nothing : parts.DataPackage);
        Part(text, "Recalled lessons", LessonsOf(parts.Lessons));

        return new ComposedPrompt(text.ToString(), used);
    }

    /// <summary>
    /// One part of a prompt, under its heading. A line ends with a line feed, on every system:
    /// the same parts give the same prompt.
    /// </summary>
    private static void Part(StringBuilder text, string heading, string body)
    {
        if (text.Length > 0)
        {
            text.Append("\n\n");
        }

        text.Append("# ").Append(heading).Append('\n').Append(body);
    }

    /// <summary>The symbol, the equity, the position, and the limits of the agent, each on a line.</summary>
    private static string ContextOf(AgentContext agent)
    {
        var limits = agent.Limits;

        List<string> lines =
        [
            $"Symbol: {agent.Symbol}, priced in {agent.QuoteAsset}",
            $"Market: {agent.Market}",
            $"Timeframe: {agent.Timeframe.Code()}",
            $"Equity: {Number(agent.Equity)} {agent.QuoteAsset}",
            $"Position: {PositionOf(agent.Position)}",
            "Limits:",
            $"- Risk per trade: at most {Number(limits.RiskPerTradePercent)}% of equity",
            $"- Reward-to-risk ratio at entry: at least {Number(limits.MinRewardToRisk)}",
            $"- Distance of an entry from the current price: at most {Number(limits.MaxEntryDistancePercent)}%",
            $"- Daily loss limit: {Number(limits.DailyLossPercent)}% of equity",
            $"- Drawdown limit: {Number(limits.DrawdownPercent)}% from peak equity",
            $"- Open positions: at most {Number(FixedLimits.OpenPositionsPerAgent)}",
        ];

        // The leverage cap and the liquidation buffer count on Futures only, and the cap on the
        // value of a position on Spot only.
        if (agent.Market == Market.Futures)
        {
            lines.Add($"- Leverage: at most {Number(limits.LeverageCap)}x");
            lines.Add(
                $"- Liquidation buffer: the liquidation price at least {Number(limits.LiquidationBuffer)} times as far " +
                "from the entry as the stop-loss");
        }
        else
        {
            lines.Add($"- Position value: at most {Number(FixedLimits.SpotPositionPercentOfEquity)}% of equity");
        }

        return string.Join('\n', lines);
    }

    private static string PositionOf(PositionContext? position) => position is null
        ? "none"
        : $"{(position.Side == PositionSide.Long ? "long" : "short")}, quantity {Number(position.Quantity)}, " +
          $"entry price {Number(position.EntryPrice)}, stop-loss {Price(position.StopLoss)}, " +
          $"take-profit {Price(position.TakeProfit)}";

    /// <summary>The lessons, numbered in the order they were recalled in.</summary>
    private static string LessonsOf(IReadOnlyList<string> lessons) => lessons.Count == 0
        ? Nothing
        : string.Join('\n', lessons.Select((lesson, index) => $"{Number(index + 1)}. {lesson}"));

    private static string Price(decimal? price) => price is { } resting ? Number(resting) : "none";

    /// <summary>A number as a prompt writes it: the same in every country.</summary>
    private static string Number(decimal value) => value.ToString(AsWritten, CultureInfo.InvariantCulture);
}
