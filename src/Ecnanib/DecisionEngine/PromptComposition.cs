using System.Text;
using Ecnanib.RiskGuard;
using Ecnanib.Shared;

namespace Ecnanib.DecisionEngine;

/// <summary>
/// Composes the prompt of a role call (FR-PRM-1), and the prompt of the call that writes the
/// lesson of a closed position (FR-EXP-6). The templates are read here, by the market and the
/// timeframe of the agent's context, and are given by nobody (FR-PRM-4). No part is placed that
/// holds a secret, an email address, or an account identifier (FR-PRM-9).
/// </summary>
internal sealed class PromptComposition(IPromptTemplates templates, PromptScreen screen) : IPromptComposition
{
    /// <summary>What a part says when the call was given nothing for it.</summary>
    private const string Nothing = "None.";

    /// <summary>What stands in place of a plan or a review that is not recorded, as the lesson instructions name it.</summary>
    public const string NotRecorded = "not recorded";

    /// <summary>The heading of the first part of the prompt of a lesson, as its instructions name it.</summary>
    public const string LessonInstructionsHeading = "Lesson instructions";

    /// <summary>The heading of the second part of the prompt of a lesson, as its instructions name it.</summary>
    public const string ClosedPositionHeading = "Closed position";

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

    public async Task<ComposedPrompt> ComposeLessonAsync(LessonParts parts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parts);
        ArgumentNullException.ThrowIfNull(parts.Position);

        // The instructions are the same for every market and timeframe: they are read with the
        // templates of the position's own, which is what reads the templates in use.
        var inUse = await templates.InUseAsync(parts.Position.Market, parts.Position.Timeframe, cancellationToken);

        var text = new StringBuilder();

        Part(text, LessonInstructionsHeading, inUse.Lesson.Text);
        Part(text, ClosedPositionHeading, ClosedPositionOf(parts));

        return new ComposedPrompt(text.ToString(), [inUse.Lesson]);
    }

    /// <summary>
    /// One part of a prompt, under its heading. A line ends with a line feed, on every system:
    /// the same parts give the same prompt.
    /// </summary>
    /// <remarks>
    /// Every part passes the screen before it is placed, the templates among them: a prompt
    /// holds no secret, no email address, and no account identifier, whoever wrote the part
    /// (FR-PRM-9). The refusal names the part, and not what was found in it.
    /// </remarks>
    private void Part(StringBuilder text, string heading, string body)
    {
        if (screen.LeakIn(body) is { } leak)
        {
            throw new PromptRefusedException(heading, leak);
        }

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

    /// <summary>
    /// The closed position of a lesson, each fact on a line, and under them the plan that opened
    /// it and the review of that plan, each on the line under a heading of the service's own.
    /// </summary>
    private static string ClosedPositionOf(LessonParts parts)
    {
        var position = parts.Position;
        var asset = position.QuoteAsset;

        List<string> lines =
        [
            $"Symbol: {position.Symbol}, priced in {asset}",
            $"Market: {position.Market}",
            $"Timeframe: {position.Timeframe.Code()}",
            $"Position: {(position.Side == PositionSide.Long ? "long" : "short")}, entry price {Number(position.EntryPrice)}, " +
                $"stop-loss {Number(position.StopLoss)}, take-profit {Price(position.TakeProfit)}",
            $"Closed: {ClosedBy(position.ExitReason)}",
            $"Held: {HeldFor(position.HoldingSeconds)}",
            $"Result: {(position.ResultInR is { } inR ? $"{Signed(inR)} R" : "no result in R")}, " +
                $"net {Signed(position.NetResult)} {asset}, after fees of {Number(position.Fees)} {asset} " +
                $"and funding of {Signed(position.Funding)} {asset}",
        ];

        if (!position.Complete)
        {
            lines.Add("The result is an estimate: not everything of the position could be read from the exchange.");
        }

        // What models answered, each as one line under a line of the service's own: so neither
        // can pose as a part of the prompt, or as the line above the other.
        lines.Add("Trade plan:");
        lines.Add(string.IsNullOrWhiteSpace(parts.Plan) ? NotRecorded : parts.Plan);
        lines.Add("Review:");
        lines.Add(string.IsNullOrWhiteSpace(parts.Review) ? NotRecorded : parts.Review);

        return string.Join('\n', lines);
    }

    /// <summary>How a position was closed, in the words of the service.</summary>
    private static string ClosedBy(ExitReason reason) => reason switch
    {
        ExitReason.StopLoss => "its stop-loss was executed",
        ExitReason.TakeProfit => "its take-profit was executed",
        ExitReason.TeamClose => "a later plan of the team closed it",
        ExitReason.UserClose => "its user closed it by hand",
        ExitReason.ProtectionFailed => "its stop-loss could not be placed, so the service closed it at market",
        ExitReason.ClosedAtExchange => "it was closed at the exchange, by no order of the agent",
        ExitReason.LossLimit => "a loss limit of the agent was reached, so the service closed it at market",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "No such way to close a position."),
    };

    /// <summary>How long a position was held, in its two largest units.</summary>
    private static string HeldFor(long seconds)
    {
        var held = TimeSpan.FromSeconds(Math.Max(seconds, 0L));

        string[] units =
        [
            Unit(held.Days, "day"),
            Unit(held.Hours, "hour"),
            Unit(held.Minutes, "minute"),
        ];

        var said = units.SkipWhile(unit => unit.Length == 0).Take(2).Where(unit => unit.Length > 0).ToList();

        return said.Count == 0 ? "less than a minute" : string.Join(' ', said);

        static string Unit(int count, string name) => count == 0 ? string.Empty : $"{Number(count)} {name}{(count == 1 ? string.Empty : "s")}";
    }

    /// <summary>A number that says its sign, so that a gain is told from a loss: <c>+1.5</c>, <c>-1.04</c>, <c>0</c>.</summary>
    private static string Signed(decimal value) => value > 0m ? $"+{Number(value)}" : Number(value);

    /// <summary>The lessons, numbered in the order they were recalled in.</summary>
    private static string LessonsOf(IReadOnlyList<string> lessons) => lessons.Count == 0
        ? Nothing
        : string.Join('\n', lessons.Select((lesson, index) => $"{Number(index + 1)}. {lesson}"));

    private static string Price(decimal? price) => price is { } resting ? Number(resting) : "none";

    /// <summary>A number as a prompt writes it: the same in every country.</summary>
    private static string Number(decimal value) => Written.Number(value);
}
