using Ecnanib.Shared;

namespace Ecnanib.DecisionEngine;

/// <summary>
/// The fixed schema of what each role answers (FR-TEAM-6): the JSON Schema that the provider of
/// the role's model is sent with the call, and that the answer is validated against. See
/// "The schemas" in docs/team.md.
/// </summary>
/// <remarks>
/// <para>
/// A schema stays inside what all four providers take: every object lists all of its properties
/// in <c>required</c> and sets <c>additionalProperties</c> to <c>false</c>, a value that may be
/// absent is <c>null</c>, and nothing says a range. See "Result schemas" in docs/ai-providers.md.
/// The ranges are checked by what validates the answer: <see cref="RoleRanges"/>.
/// </para>
/// <para>
/// Each is one line, written as it is sent: the same in every checkout. The names of the fields
/// are what the instructions of the role name, which <c>RoleSchemaTests</c> holds them to.
/// </para>
/// </remarks>
internal static class RoleSchemas
{
    /// <summary>The report of the Market Analyst: bias, key price levels, and volatility read (SRS 4.5).</summary>
    public const string MarketAnalyst =
        """{"type":"object","properties":{"bias":{"type":"string","enum":["bullish","bearish","neutral"]}""" +
        ""","keyLevels":{"type":"array","items":{"type":"object","properties":{"price":{"type":"number"}""" +
        ""","kind":{"type":"string","enum":["support","resistance"]}},"required":["price","kind"],"additionalProperties":false}}""" +
        ""","volatility":{"type":"string","enum":["low","normal","high"]}""" +
        ""","rationale":{"type":"string"}}""" +
        ""","required":["bias","keyLevels","volatility","rationale"],"additionalProperties":false}""";

    /// <summary>The report of the Sentiment Analyst: bias, notable events, confidence, and whether live data was available (SRS 4.5).</summary>
    public const string SentimentAnalyst =
        """{"type":"object","properties":{"bias":{"type":"string","enum":["bullish","bearish","neutral"]}""" +
        ""","notableEvents":{"type":"array","items":{"type":"string"}}""" +
        ""","confidence":{"type":"number"}""" +
        ""","liveDataAvailable":{"type":"boolean"}""" +
        ""","rationale":{"type":"string"}}""" +
        ""","required":["bias","notableEvents","confidence","liveDataAvailable","rationale"],"additionalProperties":false}""";

    /// <summary>The trade plan of the Strategist: action, entry type and price, stop-loss, take-profit, risk fraction, rationale (SRS 4.5).</summary>
    public const string Strategist =
        """{"type":"object","properties":{"action":{"type":"string","enum":["open_long","open_short","hold","close","reverse","adjust_protection"]}""" +
        ""","entryType":{"type":["string","null"],"enum":["market","limit",null]}""" +
        ""","entryPrice":{"type":["number","null"]}""" +
        ""","stopLoss":{"type":["number","null"]}""" +
        ""","takeProfit":{"type":["number","null"]}""" +
        ""","riskFraction":{"type":["number","null"]}""" +
        ""","rationale":{"type":"string"}}""" +
        ""","required":["action","entryType","entryPrice","stopLoss","takeProfit","riskFraction","rationale"],"additionalProperties":false}""";

    /// <summary>The review of the Risk Manager: approve, reduce, or veto, with reasons (SRS 4.5).</summary>
    public const string RiskManager =
        """{"type":"object","properties":{"verdict":{"type":"string","enum":["approve","reduce","veto"]}""" +
        ""","riskFraction":{"type":["number","null"]}""" +
        ""","rationale":{"type":"string"}}""" +
        ""","required":["verdict","riskFraction","rationale"],"additionalProperties":false}""";

    /// <summary>
    /// The lesson of a closed position, which the model of the agent's Risk Manager writes after
    /// it: the setup, what was expected, what happened, one takeaway, and what it is about
    /// (FR-EXP-6). It is the schema of no role: no cycle asks for it. The tags are the members
    /// of <see cref="Ecnanib.Experience.LessonTag"/>, as a schema writes them.
    /// </summary>
    public const string Lesson =
        """{"type":"object","properties":{"setup":{"type":"string"}""" +
        ""","expected":{"type":"string"}""" +
        ""","happened":{"type":"string"}""" +
        ""","takeaway":{"type":"string"}""" +
        ""","tags":{"type":"array","items":{"type":"string","enum":["entry","stop_loss","take_profit","risk","trend","range","volatility","sentiment","funding","fees","timing"]}}}""" +
        ""","required":["setup","expected","happened","takeaway","tags"],"additionalProperties":false}""";

    /// <summary>The schema of what a role answers.</summary>
    public static string Of(Role role) => role switch
    {
        Role.MarketAnalyst => MarketAnalyst,
        Role.SentimentAnalyst => SentimentAnalyst,
        Role.Strategist => Strategist,
        Role.RiskManager => RiskManager,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "No such role."),
    };
}
