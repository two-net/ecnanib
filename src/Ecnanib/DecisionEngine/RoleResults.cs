using System.Text.Encodings.Web;
using System.Text.Json;
using Ecnanib.Agents;
using Ecnanib.Shared;

namespace Ecnanib.DecisionEngine;

/// <summary>
/// Validates what the model of a role answered (FR-TEAM-6, NFR-SEC-7). An answer is valid when
/// it is inside the schema of its role, when its values are inside their ranges, when a plan
/// does what a plan may do for its agent (FR-CYC-4 to FR-CYC-7), and, where a later role reads
/// it, when it holds nothing that a prompt never holds. See "Validating a result" in
/// docs/team.md.
/// </summary>
internal sealed class RoleResults(PromptScreen screen) : IRoleResults
{
    // Every character as it is, but those that JSON itself escapes: a line break inside a text
    // is written as two characters, so an answer is one line whatever a model wrote. It is how
    // the report of the Sentiment Analyst is written.
    private static readonly JsonSerializerOptions OneLine = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // The schemas, read once: they are fixed.
    private static readonly IReadOnlyDictionary<Role, JsonElement> Schemas = Enum.GetValues<Role>()
        .ToDictionary(role => role, role => JsonDocument.Parse(RoleSchemas.Of(role)).RootElement.Clone());

    public string SchemaOf(Role role) => RoleSchemas.Of(role);

    public RoleResult<MarketReport> ReadMarketReport(string answered) =>
        Read(Role.MarketAnalyst, answered, placed: true, MarketReportOf);

    public RoleResult<SentimentRead> ReadSentiment(string answered) =>
        Read(Role.SentimentAnalyst, answered, placed: true, SentimentOf);

    public RoleResult<TradePlan> ReadPlan(string answered, AgentContext agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(agent.Limits);

        return Read(Role.Strategist, answered, placed: true, plan => PlanOf(plan, agent));
    }

    public RoleResult<RiskReview> ReadReview(string answered, TradePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        // No prompt reads a review, so what it holds refuses none. It is not kept all the same.
        return Read(Role.RiskManager, answered, placed: false, review => ReviewOf(review, plan));
    }

    /// <summary>
    /// Reads an answer: the schema first, then the values, then what the text holds as it is
    /// placed in a prompt.
    /// </summary>
    /// <param name="role">The role that answered.</param>
    /// <param name="answered">The answer.</param>
    /// <param name="placed">Whether a later role reads the answer in its prompt.</param>
    /// <param name="typed">Reads the values of an answer that is inside its schema, or says which is outside its range.</param>
    private RoleResult<T> Read<T>(Role role, string answered, bool placed, Func<JsonElement, Values<T>> typed)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(answered);

        if (answered.Length > RoleRanges.AnswerCharacters)
        {
            return Rejected<T>(RejectionRule.TooLong, RoleRejection.Answer, json: null);
        }

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(answered);
        }
        catch (JsonException)
        {
            return Rejected<T>(RejectionRule.NotJson, RoleRejection.Answer, json: null);
        }

        using (document)
        {
            var answer = document.RootElement;

            // The text as a later role reads it, and as the record keeps it. It is screened as
            // it is placed: an escape of the answer is a character here.
            var written = JsonSerializer.Serialize(answer, OneLine);
            var kept = screen.LeakIn(written) is null ? written : null;

            if (SchemaCheck.Violation(Schemas[role], answer) is { } outside)
            {
                return new RoleResult<T>(null, kept, outside);
            }

            var read = typed(answer);

            if (read.Rejection is { } rejection)
            {
                return new RoleResult<T>(null, kept, rejection);
            }

            if (placed && kept is null)
            {
                return Rejected<T>(RejectionRule.HoldsWhatAPromptNeverHolds, RoleRejection.Answer, json: null);
            }

            return new RoleResult<T>(read.Value, kept, null);
        }
    }

    private static Values<MarketReport> MarketReportOf(JsonElement answer)
    {
        var levels = answer.GetProperty("keyLevels");

        if (levels.GetArrayLength() > RoleRanges.KeyLevels)
        {
            return Broke(RejectionRule.TooMany, "keyLevels");
        }

        var keyLevels = new List<KeyLevel>();

        foreach (var level in levels.EnumerateArray())
        {
            if (PriceOf(level.GetProperty("price")) is not { } price)
            {
                return Broke(RejectionRule.OutOfRange, $"keyLevels[{keyLevels.Count}].price");
            }

            keyLevels.Add(new KeyLevel(price, Named<LevelKind>(level.GetProperty("kind"))));
        }

        if (RationaleOf(answer) is { } rejection)
        {
            return rejection;
        }

        return new MarketReport(
            Named<Bias>(answer.GetProperty("bias")),
            keyLevels,
            Named<Volatility>(answer.GetProperty("volatility")),
            answer.GetProperty("rationale").GetString()!);
    }

    private static Values<SentimentRead> SentimentOf(JsonElement answer)
    {
        var events = answer.GetProperty("notableEvents");

        if (events.GetArrayLength() > RoleRanges.NotableEvents)
        {
            return Broke(RejectionRule.TooMany, "notableEvents");
        }

        var notable = new List<string>();

        foreach (var one in events.EnumerateArray().Select(one => one.GetString()!))
        {
            if (string.IsNullOrWhiteSpace(one))
            {
                return Broke(RejectionRule.Empty, $"notableEvents[{notable.Count}]");
            }

            if (one.Length > RoleRanges.NotableEventCharacters)
            {
                return Broke(RejectionRule.TextTooLong, $"notableEvents[{notable.Count}]");
            }

            notable.Add(one);
        }

        if (!answer.GetProperty("confidence").TryGetDecimal(out var confidence)
            || confidence < RoleRanges.LowestConfidence
            || confidence > RoleRanges.HighestConfidence)
        {
            return Broke(RejectionRule.OutOfRange, "confidence");
        }

        if (RationaleOf(answer) is { } rejection)
        {
            return rejection;
        }

        return new SentimentRead(
            Named<Bias>(answer.GetProperty("bias")),
            notable,
            confidence,
            answer.GetProperty("liveDataAvailable").GetBoolean(),
            answer.GetProperty("rationale").GetString()!);
    }

    /// <summary>
    /// A plan does what a plan may do for its agent (FR-CYC-4 to FR-CYC-7), and has the fields
    /// that its action needs, and no others (SRS 4.5). Where a stop-loss stands to its entry is
    /// not asked here: the Risk Guard asks, with the price (FR-RSK-4).
    /// </summary>
    /// <remarks>
    /// The agent is as the prompt of the Strategist stated it, so a plan is held to what its
    /// model was told. What the agent holds when an order is due is the Risk Guard's to ask.
    /// </remarks>
    private static Values<TradePlan> PlanOf(JsonElement answer, AgentContext agent)
    {
        var action = Named<PlanAction>(answer.GetProperty("action"));
        var position = agent.Position;

        // The action is one that the market allows: Spot has no short, and so no reverse (FR-CYC-4).
        if (!PlanActions.AllowedOn(agent.Market).Contains(action))
        {
            return Broke(RejectionRule.NotOnMarket, "action");
        }

        // An agent holds one position at most, and adds to none (FR-CYC-5). So a plan opens one
        // only for an agent that has none, and what closes, reverses, or adjusts needs one.
        if (position is not null && action is PlanAction.OpenLong or PlanAction.OpenShort)
        {
            return Broke(RejectionRule.PositionOpen, "action");
        }

        if (position is null && action is PlanAction.Close or PlanAction.Reverse or PlanAction.AdjustProtection)
        {
            return Broke(RejectionRule.NoPosition, "action");
        }

        var highestRiskFraction = RoleRanges.HighestRiskFraction(agent.Limits);
        var entry = answer.GetProperty("entryType");
        EntryType? entryType = entry.ValueKind == JsonValueKind.Null ? null : Named<EntryType>(entry);

        // A number that is there is one that a price or a fraction can be.
        if (Optional(answer, "entryPrice", out var entryPrice) is { } wrongEntry)
        {
            return wrongEntry;
        }

        if (Optional(answer, "stopLoss", out var stopLoss) is { } wrongStop)
        {
            return wrongStop;
        }

        if (Optional(answer, "takeProfit", out var takeProfit) is { } wrongTarget)
        {
            return wrongTarget;
        }

        var fraction = answer.GetProperty("riskFraction");
        decimal? riskFraction = null;

        if (fraction.ValueKind != JsonValueKind.Null)
        {
            if (!fraction.TryGetDecimal(out var risked) || risked <= 0m || risked > highestRiskFraction)
            {
                return Broke(RejectionRule.OutOfRange, "riskFraction");
            }

            riskFraction = risked;
        }

        var opens = action is PlanAction.OpenLong or PlanAction.OpenShort or PlanAction.Reverse;

        // A plan that opens a position says how it enters, where it is wrong, where it is done,
        // and how much it risks. A limit entry names its price, and a market entry has none.
        // A plan that adjusts protection names a stop-loss or a take-profit, and nothing else.
        // A plan that holds or closes names nothing.
        (string Field, bool Has, bool? Needs)[] fields =
        [
            ("entryType", entryType is not null, opens),
            ("entryPrice", entryPrice is not null, opens && entryType == EntryType.Limit),
            ("stopLoss", stopLoss is not null, opens ? true : action == PlanAction.AdjustProtection ? null : false),
            ("takeProfit", takeProfit is not null, opens ? true : action == PlanAction.AdjustProtection ? null : false),
            ("riskFraction", riskFraction is not null, opens),
        ];

        foreach (var (field, has, needs) in fields)
        {
            if (needs == true && !has)
            {
                return Broke(RejectionRule.Required, field);
            }

            if (needs == false && has)
            {
                return Broke(RejectionRule.NotExpected, field);
            }
        }

        if (action == PlanAction.AdjustProtection && stopLoss is null && takeProfit is null)
        {
            return Broke(RejectionRule.Required, "stopLoss");
        }

        // An adjustment moves the stop-loss only in the direction that reduces risk: up for a
        // long, and down for a short (FR-CYC-6). A stop-loss that stays where it is adds none,
        // and a position that has none resting takes any. The take-profit moves either way.
        if (action == PlanAction.AdjustProtection
            && stopLoss is { } moved
            && position is { StopLoss: { } resting }
            && (position.Side == PositionSide.Long ? moved < resting : moved > resting))
        {
            return Broke(RejectionRule.AddsRisk, "stopLoss");
        }

        if (RationaleOf(answer) is { } rejection)
        {
            return rejection;
        }

        return new TradePlan(
            action, entryType, entryPrice, stopLoss, takeProfit, riskFraction, answer.GetProperty("rationale").GetString()!);

        static RoleRejection? Optional(JsonElement answer, string field, out decimal? price)
        {
            price = null;
            var value = answer.GetProperty(field);

            if (value.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            price = PriceOf(value);

            return price is null ? Broke(RejectionRule.OutOfRange, field) : null;
        }
    }

    /// <summary>
    /// A review reduces the risk fraction of a plan to less than it is, and names a fraction for
    /// nothing else (FR-TEAM-7): it cannot increase the risk.
    /// </summary>
    private static Values<RiskReview> ReviewOf(JsonElement answer, TradePlan plan)
    {
        var verdict = Named<ReviewVerdict>(answer.GetProperty("verdict"));
        var fraction = answer.GetProperty("riskFraction");
        decimal? riskFraction = null;

        if (verdict != ReviewVerdict.Reduce)
        {
            // An approval and a veto reduce nothing.
            if (fraction.ValueKind != JsonValueKind.Null)
            {
                return Broke(RejectionRule.NotExpected, "riskFraction");
            }
        }
        else if (plan.RiskFraction is not { } planned)
        {
            // A plan that risks nothing has nothing to reduce.
            return Broke(RejectionRule.NotExpected, "verdict");
        }
        else if (fraction.ValueKind == JsonValueKind.Null)
        {
            return Broke(RejectionRule.Required, "riskFraction");
        }
        else if (!fraction.TryGetDecimal(out var reduced) || reduced <= 0m || reduced >= planned)
        {
            return Broke(RejectionRule.OutOfRange, "riskFraction");
        }
        else
        {
            riskFraction = reduced;
        }

        if (RationaleOf(answer) is { } rejection)
        {
            return rejection;
        }

        return new RiskReview(verdict, riskFraction, answer.GetProperty("rationale").GetString()!);
    }

    /// <summary>What the rationale of an answer breaks, or null: it says something, and is short.</summary>
    private static RoleRejection? RationaleOf(JsonElement answer)
    {
        var rationale = answer.GetProperty("rationale").GetString()!;

        if (string.IsNullOrWhiteSpace(rationale))
        {
            return new RoleRejection(RejectionRule.Empty, "rationale");
        }

        return rationale.Length > RoleRanges.RationaleCharacters ? new RoleRejection(RejectionRule.TextTooLong, "rationale") : null;
    }

    /// <summary>A price: an exact decimal above nothing. Null for a number that is none.</summary>
    private static decimal? PriceOf(JsonElement value) =>
        value.TryGetDecimal(out var price) && price > 0m ? price : null;

    /// <summary>
    /// The member that a text of a schema names: <c>open_long</c> is <c>OpenLong</c>. The schema
    /// lists the texts, and the answer was checked against it, so there is one.
    /// </summary>
    private static TEnum Named<TEnum>(JsonElement value)
        where TEnum : struct, Enum =>
        Enum.Parse<TEnum>(value.GetString()!.Replace("_", string.Empty, StringComparison.Ordinal), ignoreCase: true);

    private static RoleRejection Broke(RejectionRule rule, string field) => new(rule, field);

    private static RoleResult<T> Rejected<T>(RejectionRule rule, string field, string? json)
        where T : class =>
        new(null, json, new RoleRejection(rule, field));

    /// <summary>The values of an answer, or the rule that one of them broke.</summary>
    private readonly record struct Values<T>(T? Value, RoleRejection? Rejection)
        where T : class
    {
        public static implicit operator Values<T>(T value) => new(value, null);

        public static implicit operator Values<T>(RoleRejection rejection) => new(null, rejection);
    }
}
