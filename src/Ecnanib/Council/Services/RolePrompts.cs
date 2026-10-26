using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ecnanib.Agents.Abstractions;
using Ecnanib.Council.Abstractions;
using Ecnanib.Council.Configuration;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;
using Candle = Ecnanib.Exchange.Abstractions.Candle;

namespace Ecnanib.Council.Services;

/// <summary>What an earlier role contributed, as handed to the roles after it (FR-AI-06, FR-AI-12).</summary>
internal sealed record PrecedingTurn(CouncilRole Role, RoleOutputStatus Status, string? Reason, RoleOutputDocument? Document);

/// <summary>
/// The role instructions (system prompt) and the per-evaluation message (user prompt) of FR-AI-06: the agent
/// configuration, the strategy engine output, a compact candle summary, the position and open orders, the
/// experience summary and the preceding roles' outputs, in a fixed order. Nothing here can reach a key value: the
/// builder sees entities, the snapshot and documents only (FR-KEY-05).
/// </summary>
internal static class RolePrompts
{
    /// <summary>Bumped when the instructions or the message layout change, so stored transcripts can be told apart.</summary>
    public const int PromptVersion = 1;

    /// <summary>The pipeline's serializer for the strategy output: web defaults and string enums, so the prompt matches <c>evaluations.strategy_output</c>.</summary>
    public static readonly JsonSerializerOptions StrategyJson = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    /// <summary>Documents on the wire and in transcripts: snake_case names and values, as the schema names them.</summary>
    public static readonly JsonSerializerOptions DocumentJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    public static string System(CouncilRole role, CouncilOptions options)
    {
        var text = new StringBuilder();
        text.AppendLine($"You are the {CouncilRoles.Display(role)} of Ecnanib's trading council: four AI roles that evaluate one cryptocurrency trading agent in sequence at each candle close on Binance. Each role sees the same market data and the outputs of the roles before it. The roles run in this order: 1. Market Analyst, 2. Sentiment & Flow, 3. Cross-Market Context, 4. Risk Manager, the final gate and the only role whose decision is executed.");
        text.AppendLine();
        text.AppendLine($"Your responsibility: {CouncilRoles.Responsibility(role)}");
        text.AppendLine();
        text.AppendLine("Rules:");
        text.AppendLine("- The message is the complete picture of the agent. Use it and your general knowledge; never invent prices, positions, orders or news.");
        text.AppendLine("- Answer with exactly one JSON document matching the schema you were given, and nothing else.");
        text.AppendLine($"- assessment: your reasoning in plain prose, at most {options.MaxAssessmentLength} characters. confidence: 0 (none) to 1 (certain). flags: short snake_case tags.");
        text.AppendLine("- proposed_action is one of open_long, open_short, hold, modify, close, no_trade. Prices are in the quote asset.");
        text.AppendLine("- An open_long or open_short needs entry_type (limit or market), entry_price for a limit, and stop_price on the losing side of the entry. With an open position propose hold, modify (a tighter stop_price and/or new targets) or close. Stops are only ever tightened, never widened.");
        text.AppendLine("- A Spot agent is long-only: never propose open_short on Spot. Entries are only taken on the strategy's setup unless the message says discretionary mode is on.");
        text.AppendLine("- When nothing should be done, say so with no_trade (no position) or hold (position open); do not force a trade.");
        text.AppendLine();
        text.AppendLine(role switch
        {
            CouncilRole.MarketAnalyst => "Focus: judge the technical structure and the quality of the setup the strategy engine reports (or why there is none): trend and bias, the key levels, the invalidation. Suggest the entry, stop and targets you would use. Suggested flags: setup_valid, setup_weak, counter_trend, level_confluence, invalidation_near, extended, low_volume.",
            CouncilRole.SentimentFlow => "Focus: read sentiment, news and crowd positioning for the symbol and the market, and the funding and open-interest context on futures. The message carries no news feed, only the candles and the funding rate: infer what you can, say what you know about the period, and state your uncertainty. Suggested flags: crowded_long, crowded_short, funding_extreme, news_risk, sentiment_bullish, sentiment_bearish, low_conviction.",
            CouncilRole.CrossMarketContext => "Focus: the BTC regime and correlations, the alignment of the higher timeframes with the agent's timeframe, and calendar risk (weekend, scheduled macro events, funding times). Give an alignment reading in the assessment and name every conflict. Suggested flags: aligned, conflict_htf, btc_regime_risk, weekend, calendar_risk, correlation_high.",
            _ => $"Focus: you are the final gate. Validate the stop, the targets and the risk of what the strategy and the preceding roles propose, apply the agent's risk limits and decide. Approve an entry with open_long or open_short (entry_type, entry_price for a limit, stop_price, targets, risk_percent at most the agent's risk per trade); reject it with no_trade. With an open position: hold, modify (a tighter stop_price, new targets, take_partial_fraction to take profit on part of it) or close. rationale: why, in plain prose, at most {options.MaxRationaleLength} characters. The system enforces the hard limits after you and clamps or rejects anything outside them, so stay within them.",
        });
        return text.ToString();
    }

    public static string User(CouncilContext context, CouncilRole role, string? experience, IReadOnlyList<PrecedingTurn> preceding, CouncilOptions options)
    {
        var agent = context.Agent;
        var close = context.Evaluation.CandleOpenTime + agent.Timeframe.ToTimeSpan();
        var text = new StringBuilder();

        text.AppendLine($"# Evaluation of {agent.Symbol} ({Market(agent.MarketType)}, {agent.Timeframe.ToLabel()}) at the candle close of {Stamp(close)}");
        text.AppendLine();
        text.AppendLine("## Agent configuration");
        text.AppendLine($"- Market: Binance {Market(agent.MarketType)}, symbol {agent.Symbol}, timeframe {agent.Timeframe.ToLabel()}, {(agent.Mode == AgentMode.Paper ? "paper trading (simulated fills)" : "live trading")}.");
        if (agent.MarketType == MarketType.UsdFutures)
            text.AppendLine($"- Leverage {agent.Leverage?.ToString(CultureInfo.InvariantCulture) ?? "default"}x, {(agent.MarginType?.ToString().ToLowerInvariant() ?? "isolated")} margin, one-way position mode.");
        text.AppendLine($"- Strategy: {context.Strategy.Name} ({context.Strategy.Id} v{context.Strategy.Version}). {context.Strategy.Summary}");
        text.AppendLine($"- Strategy parameters: {JsonSerializer.Serialize(context.Parameters, StrategyJson)}");
        var limits = agent.RiskLimits;
        text.AppendLine($"- Risk limits: risk per trade {Number(limits.RiskPerTradePercent)}% of equity, at most {limits.MaxOpenPositions} open position(s), daily loss limit {Number(limits.DailyLossLimitPercent)}%{(limits.MaxNotional is { } notional ? $", notional at most {Number(notional)}" : "")}.");
        text.AppendLine(agent.MarketType == MarketType.Spot ? "- Direction: long only (Spot)." : "- Direction: long or short.");
        text.AppendLine(context.EntriesAllowed
            ? "- New entries: allowed."
            : "- New entries: NOT allowed this evaluation (the agent is paused, or its open state could not be managed): manage the open position only; never propose an entry.");
        text.AppendLine(agent.Discretionary
            ? "- Discretionary mode: ON. An entry may be proposed without a strategy setup; it must include a stop."
            : "- Discretionary mode: OFF. Entries are only taken on the strategy's setup.");
        text.AppendLine();

        text.AppendLine("## Strategy engine output");
        text.AppendLine(context.Output.Setup is null
            ? "No candidate setup this candle."
            : $"Candidate setup: {context.Output.Setup.Side.ToString().ToLowerInvariant()} {context.Output.Setup.EntryType.ToString().ToLowerInvariant()} entry at {Number(context.Output.Setup.Entry)}, stop {Number(context.Output.Setup.Stop)}, targets [{string.Join(", ", context.Output.Setup.Targets.Select(Number))}].");
        text.AppendLine("```json");
        text.AppendLine(JsonSerializer.Serialize(context.Output, StrategyJson));
        text.AppendLine("```");
        text.AppendLine();

        text.AppendLine($"## Recent candles ({agent.Timeframe.ToLabel()}, oldest first, UTC; open high low close volume)");
        AppendCandles(text, context.Market.Candles, options.RecentCandles);
        if (options.HigherTimeframeCandles > 0)
        {
            foreach (var interval in context.Strategy.HigherTimeframes.Where(i => i != agent.Timeframe).Distinct())
            {
                var series = context.Market.Higher(interval);
                text.AppendLine();
                text.AppendLine($"## Higher timeframe {interval.ToLabel()} (oldest first)");
                if (series.Count == 0)
                    text.AppendLine("Not available.");
                else
                    AppendCandles(text, series, options.HigherTimeframeCandles);
            }
        }

        foreach (var reference in context.Strategy.References.Distinct())
        {
            var series = context.Market.Reference(reference);
            text.AppendLine();
            text.AppendLine($"## Reference {reference.Symbol} {reference.Interval.ToLabel()}");
            text.AppendLine(series.Count == 0 ? "Not available." : $"Last close {Number(series[^1].Close)} at {Stamp(series[^1].CloseTime)}; {Math.Min(series.Count, 5)} most recent closes: {string.Join(", ", series.TakeLast(5).Select(c => Number(c.Close)))}.");
        }

        if (context.Market.MarkPrice is { } mark)
        {
            text.AppendLine();
            text.AppendLine("## Futures mark price and funding");
            text.AppendLine($"Mark {Number(mark.Mark)}{(mark.Index is { } index ? $", index {Number(index)}" : "")}, funding rate {Number(mark.FundingRate * 100)}% per interval, next funding at {Stamp(mark.NextFundingTime)}.");
        }

        text.AppendLine();
        text.AppendLine("## Position and open orders");
        if (context.OpenTrades.Count == 0)
            text.AppendLine("No open position.");
        var last = context.Market.Latest?.Close;
        foreach (var trade in context.OpenTrades)
        {
            var move = last is { } price && trade.EntryPrice != 0
                ? (trade.Side == PositionSide.Long ? price - trade.EntryPrice : trade.EntryPrice - price) / trade.EntryPrice * 100
                : (decimal?)null;
            text.AppendLine($"- {trade.Side.ToString().ToLowerInvariant()} {Number(trade.Size)} {trade.Symbol} entered at {Number(trade.EntryPrice)} on {Stamp(trade.OpenedAt)}; current stop {(trade.CurrentStop is { } stop ? Number(stop) : "none")}; targets {trade.Targets.GetRawText()}{(move is { } m ? $"; {(m >= 0 ? "+" : "")}{Number(Math.Round(m, 2))}% against the entry at the last close" : "")}{(trade.IsUnmanaged ? "; flagged unmanaged" : "")}.");
        }

        if (context.OpenOrders.Count == 0)
            text.AppendLine("No open orders.");
        foreach (var order in context.OpenOrders)
        {
            text.AppendLine($"- {order.Purpose.ToString().ToLowerInvariant()} {order.Type.ToString().ToLowerInvariant()} {order.Side.ToString().ToLowerInvariant()} {Number(order.Quantity)}{(order.Price is { } p ? $" at {Number(p)}" : "")}{(order.StopPrice is { } sp ? $", stop {Number(sp)}" : "")}, {order.Status.ToString().ToLowerInvariant()}{(order.FilledQuantity > 0 ? $", filled {Number(order.FilledQuantity)}" : "")}{(order.ExpiresAt is { } exp ? $", expires {Stamp(exp)}" : "")}.");
        }

        text.AppendLine();
        text.AppendLine("## Your experience in this role");
        text.AppendLine(string.IsNullOrWhiteSpace(experience) ? "No experience recorded yet." : experience.Trim());

        text.AppendLine();
        text.AppendLine("## Preceding roles");
        if (preceding.Count == 0)
            text.AppendLine("You are the first role.");
        foreach (var turn in preceding)
        {
            text.AppendLine($"### {CouncilRoles.Display(turn.Role)}");
            text.AppendLine(turn.Document is { } document
                ? JsonSerializer.Serialize(document, DocumentJson)
                : $"Not available: {Describe(turn.Status)}{(turn.Reason is null ? "" : $" ({turn.Reason})")}.");
        }

        text.AppendLine();
        text.AppendLine("## Your task");
        if (role == CouncilRole.RiskManager)
        {
            if (context.OpenTrades.Count > 0)
                text.AppendLine("A position is open: decide hold, modify (stop_price only tighter than the current stop, new targets, take_partial_fraction) or close. Do not propose a new entry.");
            else if (!context.EntriesAllowed)
                text.AppendLine("No position is open and new entries are not allowed: the only valid decision is no_trade.");
            else if (context.Output.Setup is null && !agent.Discretionary)
                text.AppendLine("No position is open and the strategy reports no setup; discretionary mode is off, so the only valid decision is no_trade.");
            else
                text.AppendLine($"No position is open: decide {(agent.MarketType == MarketType.Spot ? "open_long" : "open_long or open_short")} with entry_type, entry_price (limit), stop_price, targets and risk_percent (at most {Number(limits.RiskPerTradePercent)}%), or no_trade.");
            text.AppendLine("Give the final decision as the JSON document, with a rationale.");
        }
        else
        {
            text.AppendLine(context.OpenTrades.Count > 0
                ? "Assess the open position and the market; propose hold, modify or close from your perspective. The Risk Manager decides."
                : context.Output.Setup is null && !agent.Discretionary
                    ? "No setup and no position: assess the market from your perspective and propose no_trade unless your role sees something the Risk Manager must know; the Risk Manager decides."
                    : "Assess the candidate from your perspective and propose what you would do with entry, stop and targets, or no_trade. The Risk Manager decides.");
            text.AppendLine("Give your output as the JSON document.");
        }

        return text.ToString();
    }

    public static string Describe(RoleOutputStatus status) => status switch
    {
        RoleOutputStatus.Succeeded => "succeeded",
        RoleOutputStatus.Failed => "failed",
        RoleOutputStatus.TimedOut => "timed out",
        RoleOutputStatus.Disabled => "disabled for this agent",
        RoleOutputStatus.Skipped => "skipped",
        _ => status.ToString().ToLowerInvariant(),
    };

    private static void AppendCandles(StringBuilder text, IReadOnlyList<Candle> candles, int count)
    {
        foreach (var candle in candles.TakeLast(count))
            text.AppendLine($"{Stamp(candle.OpenTime)} {Number(candle.Open)} {Number(candle.High)} {Number(candle.Low)} {Number(candle.Close)} {Number(candle.Volume)}");
    }

    private static string Market(MarketType market) => market == MarketType.Spot ? "Spot" : "USDT-M Futures";

    private static string Stamp(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + "Z";

    private static string Number(decimal value) => value.ToString(CultureInfo.InvariantCulture);
}
