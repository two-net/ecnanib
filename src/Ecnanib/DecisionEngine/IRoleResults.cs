using Ecnanib.Agents;
using Ecnanib.Experience;
using Ecnanib.Shared;

namespace Ecnanib.DecisionEngine;

/// <summary>
/// Validates what the model of a role answered (FR-TEAM-6, NFR-SEC-7): against the fixed schema
/// of the role, against the ranges that its values are allowed, and, for a plan, against what a
/// plan may do for its agent (FR-CYC-4 to FR-CYC-7). A result outside any of them is rejected.
/// See "Validating a result" in docs/team.md.
/// </summary>
/// <remarks>
/// <para>
/// It is the first of the three steps that are the only path from a model's output to an order:
/// schema validation, then the Risk Guard, then the Exchange Gateway (NFR-SEC-6). What a model
/// answered is read as data, into a result with the fields of its schema and no others, and is
/// never run, followed, or passed on as it came.
/// </para>
/// <para>
/// The typed results are made here and nowhere else, so whatever holds one holds an answer that
/// was validated.
/// </para>
/// </remarks>
public interface IRoleResults
{
    /// <summary>
    /// The JSON Schema of what a role answers. It is fixed: the provider is sent it with the
    /// call, and the answer is validated against the same document.
    /// </summary>
    string SchemaOf(Role role);

    /// <summary>Reads what the Market Analyst answered.</summary>
    /// <param name="answered">The answer, as the gateway gave it.</param>
    RoleResult<MarketReport> ReadMarketReport(string answered);

    /// <summary>Reads what the Sentiment Analyst answered.</summary>
    /// <param name="answered">The answer, as the gateway gave it.</param>
    RoleResult<SentimentRead> ReadSentiment(string answered);

    /// <summary>
    /// Reads what the Strategist answered, and holds it to what a plan may do for the agent
    /// (FR-CYC-4 to FR-CYC-7). See "What a plan may do" in docs/team.md.
    /// </summary>
    /// <param name="answered">The answer, as the gateway gave it.</param>
    /// <param name="agent">
    /// The agent, as the prompt of the Strategist stated it: its market, which says the actions
    /// that are allowed, its open position, and its limits. A plan that opens a position risks at
    /// most the agent's risk per trade.
    /// </param>
    RoleResult<TradePlan> ReadPlan(string answered, AgentContext agent);

    /// <summary>Reads what the Risk Manager answered about a plan.</summary>
    /// <param name="answered">The answer, as the gateway gave it.</param>
    /// <param name="plan">The plan that was reviewed: a review reduces its risk fraction only to less than it is.</param>
    RoleResult<RiskReview> ReadReview(string answered, TradePlan plan);

    /// <summary>
    /// The JSON Schema of the lesson of a closed position, which the model of the agent's Risk
    /// Manager writes after it (FR-EXP-6). It is fixed, as the schema of a role is.
    /// </summary>
    string LessonSchema { get; }

    /// <summary>
    /// Reads the lesson that a model wrote, and holds it to the limits of a lesson
    /// (<see cref="LessonLimits"/>): each part says something and is short, the takeaway has at
    /// most 60 words, and the tags are few. A lesson is read in a later prompt, so one that
    /// holds what a prompt never holds is rejected (FR-PRM-9).
    /// </summary>
    /// <param name="answered">The answer, as the gateway gave it.</param>
    RoleResult<LessonDraft> ReadLesson(string answered);
}

/// <summary>What became of the answer of a role: a result that is valid, or why it was rejected.</summary>
/// <typeparam name="T">The result of the role.</typeparam>
public sealed record RoleResult<T>
    where T : class
{
    internal RoleResult(T? value, string? json, RoleRejection? rejection)
    {
        Value = value;
        Json = json;
        Rejection = rejection;
    }

    /// <summary>The result, when the answer is inside the schema and the ranges, and null otherwise.</summary>
    public T? Value { get; }

    /// <summary>
    /// The answer on one line of JSON, as a later role reads it and as the record of the call
    /// keeps it. Null for an answer that is not kept: one that is no JSON, one that is too long,
    /// and one that holds what a prompt never holds.
    /// </summary>
    public string? Json { get; }

    /// <summary>Why the answer was rejected, and null for a valid one.</summary>
    public RoleRejection? Rejection { get; }

    /// <summary>Whether the answer is inside the schema and the ranges.</summary>
    public bool IsValid => Value is not null;
}

/// <summary>
/// Why an answer was rejected: the field, and the rule that it broke. Both are the service's own
/// words, so a rejection repeats nothing of what a model wrote.
/// </summary>
/// <param name="Rule">The rule.</param>
/// <param name="Field">
/// The field, by the names of the fixed schema, such as <c>keyLevels[1].price</c>, or
/// <see cref="Answer"/> for the answer as a whole. A field that the schema does not define is
/// never named: its name is the model's.
/// </param>
public sealed record RoleRejection(RejectionRule Rule, string Field)
{
    /// <summary>What <see cref="Field"/> says when the rule is about the answer as a whole.</summary>
    public const string Answer = "answer";

    /// <summary>The rejection as the record of a call keeps it: <c>confidence: OutOfRange</c>.</summary>
    public override string ToString() => $"{Field}: {Rule}";
}

/// <summary>The rules that an answer is held to. A member is stored by its name, as part of a rejection.</summary>
public enum RejectionRule
{
    /// <summary>The answer is no JSON.</summary>
    NotJson,

    /// <summary>The answer is longer than an answer may be.</summary>
    TooLong,

    /// <summary>A value is not of the type that the schema gives its field.</summary>
    WrongType,

    /// <summary>A field of the schema is not there.</summary>
    MissingField,

    /// <summary>The answer has a field that the schema does not define.</summary>
    UnknownField,

    /// <summary>A field is there twice.</summary>
    DuplicateField,

    /// <summary>A value is none of those that the schema lists for its field.</summary>
    NotAllowed,

    /// <summary>A number is outside the range that its field is allowed.</summary>
    OutOfRange,

    /// <summary>A list has more entries than it may have.</summary>
    TooMany,

    /// <summary>A text is longer than it may be.</summary>
    TextTooLong,

    /// <summary>A text that must say something is empty.</summary>
    Empty,

    /// <summary>A field has no value, and the action of the plan or the verdict of the review needs one.</summary>
    Required,

    /// <summary>A field has a value, and the action of the plan or the verdict of the review has no place for one.</summary>
    NotExpected,

    /// <summary>The answer holds what a prompt never holds: a secret, an email address, or an identifier (FR-PRM-9).</summary>
    HoldsWhatAPromptNeverHolds,

    /// <summary>The action of the plan is not one that the market of the agent allows (FR-CYC-4).</summary>
    NotOnMarket,

    /// <summary>The plan opens a position, and the agent has one: it holds one at most, and adds to none (FR-CYC-5).</summary>
    PositionOpen,

    /// <summary>The plan closes, reverses, or adjusts the protection of a position, and the agent has none.</summary>
    NoPosition,

    /// <summary>The plan moves the stop-loss in the direction that adds risk (FR-CYC-6).</summary>
    AddsRisk,
}

/// <summary>
/// The ranges that the values of a result are allowed, beyond what a schema says (NFR-SEC-7).
/// They are fixed, and are no settings. See "The ranges" in docs/team.md.
/// </summary>
public static class RoleRanges
{
    /// <summary>The most characters of an answer as a whole.</summary>
    public const int AnswerCharacters = 16000;

    /// <summary>The most characters of a rationale. The instructions ask for a short one.</summary>
    public const int RationaleCharacters = 2000;

    /// <summary>The most key price levels of a market report.</summary>
    public const int KeyLevels = 12;

    /// <summary>The most notable events of a sentiment report.</summary>
    public const int NotableEvents = 10;

    /// <summary>The most characters of one notable event.</summary>
    public const int NotableEventCharacters = 500;

    /// <summary>The lowest confidence.</summary>
    public const decimal LowestConfidence = 0m;

    /// <summary>The highest confidence.</summary>
    public const decimal HighestConfidence = 1m;

    /// <summary>
    /// The highest risk fraction of a plan for an agent: its risk per trade, as a fraction of its
    /// equity. A plan risks more than nothing, and at most this.
    /// </summary>
    public static decimal HighestRiskFraction(AgentRisk limits)
    {
        ArgumentNullException.ThrowIfNull(limits);

        return limits.RiskPerTradePercent / 100m;
    }
}
