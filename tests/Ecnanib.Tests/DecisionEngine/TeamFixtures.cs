using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Ecnanib.Agents;
using Ecnanib.AiProviderGateway;
using Ecnanib.Credentials;
using Ecnanib.DecisionEngine;
using Ecnanib.ExchangeGateway;
using Ecnanib.RiskGuard;
using Ecnanib.Shared;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.Agents;
using Ecnanib.Tests.Logging;
using Ecnanib.Tests.RiskGuard;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.DecisionEngine;

/// <summary>
/// What the models of a team answer in the tests: for each role an answer that is inside its
/// schema and its ranges, as one line of JSON.
/// </summary>
internal static class TeamAnswers
{
    /// <summary>The limits of the agents of the tests: a plan risks at most 1.5 percent of the equity.</summary>
    public static readonly AgentRisk Limits = new(
        RiskPerTradePercent: 1.5m,
        MinRewardToRisk: 1.75m,
        LeverageCap: 4,
        LiquidationBuffer: 2.5m,
        DailyLossPercent: 5m,
        DrawdownPercent: 20m,
        MaxEntryDistancePercent: 0.8m);

    /// <summary>What the Market Analyst answers.</summary>
    public const string MarketReport =
        """{"bias":"bullish","keyLevels":[{"price":60000,"kind":"support"},{"price":62500.5,"kind":"resistance"}],"volatility":"normal","rationale":"Higher lows above the 50-period average."}""";

    /// <summary>What the Sentiment Analyst answers.</summary>
    public const string Sentiment =
        """{"bias":"neutral","notableEvents":["A spot ETF saw record inflows."],"confidence":0.6,"liveDataAvailable":true,"rationale":"Mixed headlines, with no clear lead."}""";

    /// <summary>A plan that holds.</summary>
    public static readonly string Hold = Plan("hold", entryType: null, stopLoss: null, takeProfit: null, riskFraction: null);

    /// <summary>A plan that closes the open position.</summary>
    public static readonly string Close = Plan("close", entryType: null, stopLoss: null, takeProfit: null, riskFraction: null);

    /// <summary>A plan that moves the stop-loss of the open position.</summary>
    public static readonly string Adjust = Plan("adjust_protection", entryType: null, stopLoss: 60500m, takeProfit: null, riskFraction: null);

    /// <summary>
    /// What the Strategist answers: unless a test says otherwise, a plan that opens a long
    /// position at the market, and risks 1 percent of the equity.
    /// </summary>
    public static string Plan(
        string action = "open_long",
        string? entryType = "market",
        decimal? entryPrice = null,
        decimal? stopLoss = 59000m,
        decimal? takeProfit = 64000m,
        decimal? riskFraction = 0.01m,
        string rationale = "A pullback into support, with the trend.") =>
        $$"""{"action":"{{action}}","entryType":{{Text(entryType)}},"entryPrice":{{Number(entryPrice)}},"stopLoss":{{Number(stopLoss)}},"takeProfit":{{Number(takeProfit)}},"riskFraction":{{Number(riskFraction)}},"rationale":"{{rationale}}"}""";

    /// <summary>What the Risk Manager answers: unless a test says otherwise, an approval.</summary>
    public static string Review(string verdict = "approve", decimal? riskFraction = null, string rationale = "The stop is beyond the level.") =>
        $$"""{"verdict":"{{verdict}}","riskFraction":{{Number(riskFraction)}},"rationale":"{{rationale}}"}""";

    /// <summary>
    /// What the model of a Risk Manager answers when it writes the lesson of a closed position
    /// (FR-EXP-6): unless a test says otherwise, a lesson that is inside its schema and its limits.
    /// </summary>
    public static string Lesson(
        string setup = "Long BTCUSDT on the hour, on a breakout above the range high.",
        string expected = "Continuation to the next resistance.",
        string happened = "The price fell back into the range, and the stop-loss was executed at -1.07 R.",
        string takeaway = "Wait for the retest of a broken level before entering.",
        string tags = "\"entry\",\"timing\"") =>
        $$"""{"setup":{{Quoted(setup)}},"expected":{{Quoted(expected)}},"happened":{{Quoted(happened)}},"takeaway":{{Quoted(takeaway)}},"tags":[{{tags}}]}""";

    /// <summary>What a role answers, unless a test says otherwise: a valid answer of its own schema.</summary>
    public static string Of(Role role) => role switch
    {
        Role.MarketAnalyst => MarketReport,
        Role.SentimentAnalyst => Sentiment,
        Role.Strategist => Plan(),
        Role.RiskManager => Review(),
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "No such role."),
    };

    private static string Text(string? value) => value is null ? "null" : $"\"{value}\"";

    // A text as JSON writes it: a line break in it is an escape, as a model answers one.
    private static string Quoted(string value) => System.Text.Json.JsonSerializer.Serialize(value);

    private static string Number(decimal? value) => value is { } number ? number.ToString(CultureInfo.InvariantCulture) : "null";
}

/// <summary>What a test reads of a plan, to say that two plans are the same but for their risk.</summary>
internal static class Plans
{
    /// <summary>Everything of a plan but its risk fraction.</summary>
    public static (PlanAction, EntryType?, decimal?, decimal?, decimal?, string) ButForItsRisk(this TradePlan plan) =>
        (plan.Action, plan.EntryType, plan.EntryPrice, plan.StopLoss, plan.TakeProfit, plan.Rationale);

    /// <summary>
    /// Reads a plan for an agent that may have it: see <see cref="PlanAgents.For"/>. A test of
    /// what a plan may do names the agent itself.
    /// </summary>
    public static RoleResult<TradePlan> ReadPlan(this IRoleResults results, string answered) =>
        results.ReadPlan(answered, PlanAgents.For(answered));
}

/// <summary>
/// The agents that the tests read a plan for: a market, and the position that the agent holds,
/// as the prompt of the Strategist states them (FR-CYC-4 to FR-CYC-6).
/// </summary>
internal static class PlanAgents
{
    /// <summary>A long position: its stop-loss rests below its entry, and its take-profit above.</summary>
    public static readonly PositionContext Long = new(
        PositionSide.Long, EntryPrice: 60000m, Quantity: 0.01m, StopLoss: 59500m, TakeProfit: 64000m);

    /// <summary>A short position: its stop-loss rests above its entry, and its take-profit below.</summary>
    public static readonly PositionContext Short = new(
        PositionSide.Short, EntryPrice: 60000m, Quantity: 0.01m, StopLoss: 61000m, TakeProfit: 56000m);

    /// <summary>An agent on a market, with the position that it holds, or none.</summary>
    public static AgentContext On(Market market, PositionContext? position = null, AgentRisk? limits = null) =>
        new(market, Timeframe.OneHour, "BTCUSDT", "USDT", Equity: 1000m, position, limits ?? TeamAnswers.Limits);

    /// <summary>
    /// An agent that may have the plan of an answer, whatever its action: on Futures, which
    /// allows every action, with a long position where the action is one that needs a position,
    /// and with none where it is not.
    /// </summary>
    public static AgentContext For(string answered, AgentRisk? limits = null) =>
        On(Market.Futures, NeedsPosition(answered) ? Long : null, limits);

    /// <summary>What an agent with a position holds, for a cycle that builds its data package.</summary>
    public static StandInHoldings Holding(PositionContext position) =>
        new(new AgentHoldings(Equity: 1000m, position, Orders: [], EquityAtDayStart: 1000m, PeakEquity: 1000m));

    /// <summary>The same for the plan of an answer, or null where its action needs no position: the agent then holds nothing.</summary>
    public static StandInHoldings? HoldingFor(string answered) => NeedsPosition(answered) ? Holding(Long) : null;

    /// <summary>Whether the action of an answer is one that needs a position: close, reverse, and adjust protection.</summary>
    public static bool NeedsPosition(string answered)
    {
        try
        {
            using var answer = JsonDocument.Parse(answered);

            return answer.RootElement.ValueKind == JsonValueKind.Object
                && answer.RootElement.TryGetProperty("action", out var action)
                && action.ValueKind == JsonValueKind.String
                && action.GetString() is "close" or "reverse" or "adjust_protection";
        }
        catch (JsonException)
        {
            // An answer that is no JSON names no action.
            return false;
        }
    }
}

/// <summary>Data packages and reports as a cycle has them, for a test that says what a role is given.</summary>
internal static class TeamPackages
{
    /// <summary>The close that the packages of the tests are for.</summary>
    public static readonly DateTimeOffset ClosedAt = new(2026, 11, 17, 10, 0, 0, TimeSpan.Zero);

    /// <summary>The text of a package, in the lines that what builds one writes.</summary>
    public const string Text =
        "Closed 1H candles: 1, oldest first, through 2026-11-17T10:00Z\n" +
        "Each line: close time (UTC), open, high, low, close, volume\n" +
        "2026-11-17T10:00Z, 60900, 61420, 60880.25, 61310, 1842.337\n" +
        "\n" +
        "Open orders: none";

    /// <summary>A data package of an agent, with the text above.</summary>
    public static DataPackage Of(AgentContext agent) =>
        new(
            ClosedAt,
            agent,
            Orders: [],
            new CandleSeries(CandleInterval.OneHour, [], ComputedFrom: 0, new IndicatorValues(null, null, null, null, null, null, null, null)),
            Context: [],
            Futures: null,
            Text);

    /// <summary>The report of a Sentiment Analyst whose call succeeded, marked by whether its provider ran a search.</summary>
    public static SentimentReport SentimentOf(string answered, bool liveData) =>
        SentimentReport.Of(
            new RoleCall(
                new AgentRoleView(Role.SentimentAnalyst, Guid.CreateVersion7(), new AiProviderCode("grok"), "grok-4.7", DisplayName: null),
                new AiCallRequest("grok-4.7", "The prompt.", "{}", LiveSearch: liveData)),
            new AiCallResult(AiCallOutcome.Succeeded, answered, new AiTokenUsage(100, 50), TimeSpan.FromSeconds(2), liveData ? 1 : 0));
}

/// <summary>The record of a cycle that a test needs an agent to have, without a cycle that runs.</summary>
internal static class StoredCycleRows
{
    /// <summary>
    /// Gives an agent of a user the record of a cycle, stored as the application stores one: a
    /// cycle for a close, which ended as the test says, with one call of each role of the
    /// agent's team, each with a valid answer and the templates that are in use for the agent.
    /// The plan of the cycle went on to the Risk Guard, which accepted it: the cycle has one
    /// check, with the outcome of every rule. And the cycle placed its orders, for a position
    /// that is over by now: see <see cref="StoredOrderRows.GiveOrdersAsync"/>. It is for a test
    /// that is about something else, and needs a row in every table that holds rows of users.
    /// The agent has a team, as <c>GiveAgentAsync</c> gives it one. The cycle keeps the summary
    /// of a data package: see <see cref="CycleSummaries.Of"/>.
    /// </summary>
    /// <remarks>
    /// The cycle is stored by the application, so it leaves what a cycle leaves for Experience:
    /// the call of its Market Analyst, which is bullish, to be scored, and none of its Sentiment
    /// Analyst, which is neutral. One that ended with a veto leaves the shadow trade of its plan
    /// as well, so a test that needs a row in every table gives its agent such a cycle too.
    /// </remarks>
    /// <param name="application">The application.</param>
    /// <param name="userId">The user.</param>
    /// <param name="agentId">The agent.</param>
    /// <param name="closedAt">The close that the cycle was for.</param>
    /// <param name="outcome">How the cycle ended.</param>
    /// <param name="orders">Whether the cycle placed orders. A test of a cycle that placed none says so.</param>
    /// <param name="noEntry">Why the cycle placed no entry, where the test says that it placed none.</param>
    /// <param name="kind">What started the cycle: the close of a candle, unless the test says that its user ran it as a test.</param>
    /// <param name="answers">What each role answered, in place of <see cref="TeamAnswers.Of"/>.</param>
    /// <param name="position">The position that the agent held when the cycle ran, as the summary of its data package says it.</param>
    /// <returns>The id of the cycle.</returns>
    public static async Task<Guid> GiveCycleAsync(
        this AccountsApplication application,
        Guid userId,
        Guid agentId,
        DateTimeOffset closedAt,
        CycleOutcome outcome = CycleOutcome.Approved,
        bool orders = true,
        NoEntryReason? noEntry = null,
        CycleKind kind = CycleKind.Scheduled,
        Func<Role, string>? answers = null,
        PositionContext? position = null)
    {
        await using var context = await application.Contexts.CreateDbContextAsync(TestContext.Current.CancellationToken);

        var agent = await context.Set<Agent>().AsNoTracking().SingleAsync(stored => stored.Id == agentId, TestContext.Current.CancellationToken);
        var roles = await context.Set<AgentRole>()
            .AsNoTracking()
            .Where(given => given.AgentId == agentId)
            .ToListAsync(TestContext.Current.CancellationToken);
        var inUse = await application.PromptTemplates.InUseAsync(agent.Market, agent.Timeframe, TestContext.Current.CancellationToken);

        var cycleId = Guid.CreateVersion7();
        var cycle = new DecisionCycle(
            cycleId,
            agentId,
            userId,
            agent.Mode,
            closedAt,
            closedAt.AddSeconds(2),
            closedAt.AddSeconds(40),
            outcome,
            CycleSummaries.Of(agent, closedAt, position).ToJson(),
            noEntry,
            kind);

        RoleOutput[] outputs =
        [
            .. roles.OrderBy(given => given.Role).Select(given =>
            {
                var templates = inUse.For(given.Role);

                return new RoleOutput(
                    Guid.CreateVersion7(),
                    cycleId,
                    userId,
                    given.Role,
                    attempt: 1,
                    given.ModelId,
                    templates[0].Id,
                    templates[1].Id,
                    templates[2].Id,
                    templates[3].Id,
                    calledAt: closedAt.AddSeconds(3),
                    latencyMs: 1500,
                    inputTokens: 1200,
                    outputTokens: 150,
                    liveSearches: null,
                    RoleCallOutcome.Succeeded,
                    RoleValidation.Valid,
                    rejection: null,
                    result: (answers ?? TeamAnswers.Of)(given.Role));
            }),
        ];

        await application.CycleRecords.StoreAsync(
            cycle, outputs, [GuardedCycles.Check(cycleId, userId, closedAt.AddSeconds(39))], TestContext.Current.CancellationToken);

        if (orders)
        {
            // Written as the check was over, and before the cycle ended: a cycle is recorded after its orders.
            await application.GiveOrdersAsync(userId, agentId, cycleId, closedAt.AddSeconds(39));
        }

        return cycleId;
    }

    /// <summary>
    /// Gives an agent of a user the record of a cycle with the calls that the test names, and
    /// nothing else: no check of the Risk Guard, no order, and no summary of a data package, so
    /// the cycle leaves nothing for Experience. It is for a test of what the calls add up to,
    /// which says of each call when it was made, with which model, how it ended, and what its
    /// provider counted. The catalog is given each model that a call names.
    /// </summary>
    /// <param name="application">The application.</param>
    /// <param name="userId">The user.</param>
    /// <param name="agentId">The agent.</param>
    /// <param name="closedAt">The close that the cycle was for.</param>
    /// <param name="calls">The calls of the cycle: one for a role and an attempt at the most.</param>
    /// <param name="kind">What started the cycle: the close of a candle, unless the test says that its user ran it as a test.</param>
    /// <returns>The id of the cycle.</returns>
    public static async Task<Guid> GiveCallsAsync(
        this AccountsApplication application,
        Guid userId,
        Guid agentId,
        DateTimeOffset closedAt,
        IReadOnlyList<GivenCall> calls,
        CycleKind kind = CycleKind.Scheduled)
    {
        Agent agent;

        await using (var context = await application.Contexts.CreateDbContextAsync(TestContext.Current.CancellationToken))
        {
            agent = await context.Set<Agent>().AsNoTracking().SingleAsync(stored => stored.Id == agentId, TestContext.Current.CancellationToken);
        }

        var inUse = await application.PromptTemplates.InUseAsync(agent.Market, agent.Timeframe, TestContext.Current.CancellationToken);

        var cycleId = Guid.CreateVersion7();
        var cycle = new DecisionCycle(
            cycleId, agentId, userId, agent.Mode, closedAt, closedAt.AddSeconds(2), closedAt.AddSeconds(40), CycleOutcome.Hold, kind: kind);

        var outputs = new List<RoleOutput>();

        foreach (var call in calls)
        {
            var modelId = await application.GiveModelAsync(call.Provider, call.Model, closedAt);
            var templates = inUse.For(call.Role);
            var answered = call.Outcome == RoleCallOutcome.Succeeded;

            outputs.Add(new RoleOutput(
                Guid.CreateVersion7(),
                cycleId,
                userId,
                call.Role,
                call.Attempt,
                modelId,
                templates[0].Id,
                templates[1].Id,
                templates[2].Id,
                templates[3].Id,
                call.CalledAt,

                // A call that was not made took no time.
                latencyMs: call.Outcome == RoleCallOutcome.NoKey ? 0 : 1500,
                call.InputTokens,
                call.OutputTokens,
                call.LiveSearches,
                call.Outcome,
                answered ? RoleValidation.Valid : RoleValidation.NoResult,
                rejection: null,
                result: answered ? TeamAnswers.Of(call.Role) : null));
        }

        await application.CycleRecords.StoreAsync(cycle, outputs, [], TestContext.Current.CancellationToken);

        return cycleId;
    }
}

/// <summary>A call of a role that a test gives a cycle, with what the test says of it.</summary>
/// <param name="Role">The role that was called.</param>
/// <param name="CalledAt">When the call was made.</param>
/// <param name="InputTokens">The tokens of the prompt, or null when the provider counted none.</param>
/// <param name="OutputTokens">The tokens of the answer, or null when the provider counted none.</param>
/// <param name="Outcome">How the call ended.</param>
/// <param name="Provider">The provider of the model that was called.</param>
/// <param name="Model">The identifier of that model.</param>
/// <param name="Attempt">Which call of the role in its cycle.</param>
/// <param name="LiveSearches">How many live searches the provider ran, for a call that offered the model one.</param>
internal sealed record GivenCall(
    Role Role,
    DateTimeOffset CalledAt,
    int? InputTokens = 1200,
    int? OutputTokens = 150,
    RoleCallOutcome Outcome = RoleCallOutcome.Succeeded,
    string Provider = "claude",
    string Model = StoredAgentRows.TeamModel,
    int Attempt = 1,
    int? LiveSearches = null);

/// <summary>The summary of a data package, as the record of a cycle keeps it, for a test that needs a cycle to have one.</summary>
internal static class CycleSummaries
{
    /// <summary>
    /// The summary of the data package of a cycle of a stored agent: the candles of its own
    /// timeframe and of the week, each with its latest candle and its indicators, and on Futures
    /// the mark price, the funding, and the open interest. The week has too few candles for an
    /// average of 200. It was built 3 seconds after the close.
    /// </summary>
    public static DataPackageSummary Of(Agent agent, DateTimeOffset closedAt, PositionContext? position = null, params OpenOrder[] orders) =>
        new(
            closedAt.AddSeconds(3),
            agent.Market,
            agent.Timeframe,
            agent.Symbol,
            agent.QuoteAsset,
            agent.AllocatedCapital,
            position,
            AgentContext.Of(agent, agent.AllocatedCapital, position).Limits,
            [
                new SeriesSummary(
                    agent.Timeframe.Code(),
                    Candles: 120,
                    ComputedFrom: 498,
                    new CandleSummary(closedAt, 60900m, 61420m, 60880.25m, 61310m, 1842.337m),
                    new IndicatorValues(61122.48m, 60310.9m, 59876.12m, 61190.33m, 60402.71m, 58.2m, 512.75m, 1930.11m)),
                new SeriesSummary(
                    "1W",
                    Candles: 30,
                    ComputedFrom: 61,
                    new CandleSummary(closedAt, 58000m, 61420m, 57500.75m, 61310m, 9500.125m),
                    new IndicatorValues(52110.4m, 47020.15m, null, 53340.92m, 48811.07m, 63.41m, 4120.6m, 8930.25m)),
            ],
            orders,
            agent.Market == Market.Futures ? new FuturesData(61888.1m, 0.0001m, closedAt.AddHours(6), 82450.5m) : null);
}

/// <summary>What the team writes to the log, and the areas it writes under.</summary>
internal static class TeamLog
{
    public const string Area = "Ecnanib.DecisionEngine.TeamCycle";

    public const string RolesArea = "Ecnanib.DecisionEngine.RoleRuns";

    public const string Decided =
        "The team of agent {AgentId} decided for the close at {ClosedAt}: {CycleOutcome}, for a plan to {PlanAction}";
    public const string NoDecision =
        "The cycle of agent {AgentId} for the close at {ClosedAt} ended with no decision and no new entry: the {Role} gave no valid result";
    public const string Stale =
        "The decision of agent {AgentId} for the close at {ClosedAt} is stale: it completed {CompletedAfterSeconds} s after the close, " +
        "and the stale limit of {Timeframe} is {StaleSeconds} s. Its plan to {PlanAction} opens no position, and what goes on is {GoesOn}";
    public const string SentimentUnavailable =
        "The cycle of agent {AgentId} for the close at {ClosedAt} continues with sentiment marked unavailable";
    public const string NotRecorded =
        "The record of the cycle of agent {AgentId} for the close at {ClosedAt} could not be stored ({CycleOutcome})";

    public const string NoValidResult = "The {Role} of agent {AgentId} gave no valid result for the close at {ClosedAt}";
    public const string Rejected =
        "The result of the {Role} of agent {AgentId} was rejected on attempt {Attempt}: {RejectedField} is {RejectionRule}";
    public const string NoKey =
        "The {Role} of agent {AgentId} is not called: user {UserId} has no key for the AI provider {Provider} that can be used ({KeyHandOverOutcome})";
    public const string CheckedAgain =
        "The AI provider {Provider} refused the key of user {UserId} in a call, so the key was checked again: {KeyCheckOutcome}";
}

/// <summary>One call of a model, as <see cref="StandInModels"/> was asked for it.</summary>
/// <param name="Role">The role that the call is for: the one whose schema the call asks for.</param>
/// <param name="Provider">The provider that was asked.</param>
/// <param name="Request">What the call was made with: the model, the prompt, the schema, and whether the model may search.</param>
/// <param name="ApiKey">The key that the call was made with.</param>
/// <param name="Number">Which call of its role it is, of all that the stand-in was asked for: 1 for the first.</param>
internal sealed record AskedModel(Role Role, AiProviderCode Provider, AiCallRequest Request, string ApiKey, int Number);

/// <summary>One call of a model for the lesson of a closed position, as <see cref="StandInModels"/> was asked for it.</summary>
/// <param name="Provider">The provider that was asked.</param>
/// <param name="Request">What the call was made with: the model, the prompt, the schema, and whether the model may search.</param>
/// <param name="ApiKey">The key that the call was made with.</param>
internal sealed record AskedLesson(AiProviderCode Provider, AiCallRequest Request, string ApiKey);

/// <summary>
/// Stands for the AI providers where the team of an agent calls its models: it answers each
/// role with a valid result of its schema, unless the test says what a call of a role is
/// answered with, and keeps every call in the order the calls came in. A test can hold the calls
/// of a role until it lets them go, to say which roles are called at the same time, and which
/// only after another.
/// </summary>
internal sealed class StandInModels : IAiProviderGateway
{
    private readonly Lock _gate = new();
    private readonly List<AskedModel> _calls = [];
    private readonly Dictionary<Role, Queue<Func<AskedModel, AiCallResult>>> _said = [];
    private readonly HashSet<Role> _holds = [];
    private readonly List<(Role Role, TaskCompletionSource Held)> _held = [];
    private readonly List<AskedLesson> _lessons = [];
    private readonly Queue<AiCallResult> _lessonsSaid = new();
    private int _running;
    private int _most;

    public IReadOnlyCollection<AiProviderCode> Providers { get; } =
        [new("claude"), new("gemini"), new("gpt"), new("grok")];

    /// <summary>Every call, in the order the calls came in.</summary>
    public IReadOnlyList<AskedModel> Calls
    {
        get
        {
            lock (_gate)
            {
                return [.. _calls];
            }
        }
    }

    /// <summary>The roles that were called, in the order their calls came in.</summary>
    public IReadOnlyList<Role> Roles => [.. Calls.Select(call => call.Role)];

    /// <summary>The most calls that were under way at once.</summary>
    public int MostAtOnce
    {
        get
        {
            lock (_gate)
            {
                return _most;
            }
        }
    }

    /// <summary>The calls of a role, in the order they came in.</summary>
    public IReadOnlyList<AskedModel> CallsOf(Role role) => [.. Calls.Where(call => call.Role == role)];

    /// <summary>
    /// The calls for the lesson of a closed position, in the order they came in. They are of no
    /// cycle, and are none of <see cref="Calls"/>.
    /// </summary>
    public IReadOnlyList<AskedLesson> LessonCalls
    {
        get
        {
            lock (_gate)
            {
                return [.. _lessons];
            }
        }
    }

    /// <summary>What the next calls for a lesson are answered with, one after the other. A call after them is answered with a valid lesson.</summary>
    public void SaysLesson(params AiCallResult[] answers)
    {
        lock (_gate)
        {
            foreach (var answer in answers)
            {
                _lessonsSaid.Enqueue(answer);
            }
        }
    }

    /// <summary>The same, with what the model answers: each a result that the call succeeded with.</summary>
    public void SaysLesson(params string[] answers) => SaysLesson([.. answers.Select(answer => Answered(answer))]);

    /// <summary>What the next calls of a role are answered with, one after the other. A call after them is answered with a valid result.</summary>
    public void Says(Role role, params AiCallResult[] answers) =>
        Says(role, [.. answers.Select(answer => new Func<AskedModel, AiCallResult>(_ => answer))]);

    /// <summary>The same, with what the model of the role answers: each a result that the call succeeded with.</summary>
    public void Says(Role role, params string[] answers) => Says(role, [.. answers.Select(answer => Answered(answer))]);

    /// <summary>The same, with an answer that goes by the call.</summary>
    public void Says(Role role, params Func<AskedModel, AiCallResult>[] answers)
    {
        lock (_gate)
        {
            if (!_said.TryGetValue(role, out var said))
            {
                _said[role] = said = new Queue<Func<AskedModel, AiCallResult>>();
            }

            foreach (var answer in answers)
            {
                said.Enqueue(answer);
            }
        }
    }

    /// <summary>The calls of these roles go on until the test lets them go.</summary>
    public void Hold(params Role[] roles)
    {
        lock (_gate)
        {
            _holds.UnionWith(roles);
        }
    }

    /// <summary>Lets the calls of a role end, and holds none of it from now on.</summary>
    public void Release(Role role)
    {
        lock (_gate)
        {
            _holds.Remove(role);

            foreach (var (_, held) in _held.Where(held => held.Role == role))
            {
                held.TrySetResult();
            }

            _held.RemoveAll(held => held.Role == role);
        }
    }

    /// <summary>Lets every call end that is held, and holds none from now on.</summary>
    public void ReleaseAll()
    {
        foreach (var role in Enum.GetValues<Role>())
        {
            Release(role);
        }
    }

    /// <summary>Waits until so many calls of a role have come in.</summary>
    public Task StartedAsync(Role role, int calls = 1) =>
        Wait.UntilAsync(() => CallsOf(role).Count >= calls, $"{calls} calls of the {role}");

    public async Task<AiCallResult> CompleteAsync(
        AiProviderCode provider, AiCallRequest request, string apiKey, CancellationToken cancellationToken)
    {
        // The lesson of a closed position is asked for with a schema of its own, by no role of
        // a cycle: it is answered apart, and kept apart.
        if (request.ResultSchema.Contains("\"takeaway\"", StringComparison.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();

            lock (_gate)
            {
                _lessons.Add(new AskedLesson(provider, request, apiKey));

                return _lessonsSaid.Count > 0 ? _lessonsSaid.Dequeue() : Answered(TeamAnswers.Lesson());
            }
        }

        var role = RoleOf(request.ResultSchema);

        AskedModel asked;
        Func<AskedModel, AiCallResult>? said = null;
        TaskCompletionSource? held = null;

        lock (_gate)
        {
            asked = new AskedModel(role, provider, request, apiKey, _calls.Count(call => call.Role == role) + 1);
            _calls.Add(asked);

            if (_said.TryGetValue(role, out var answers) && answers.Count > 0)
            {
                said = answers.Dequeue();
            }

            if (_holds.Contains(role))
            {
                held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _held.Add((role, held));
            }

            _running++;
            _most = Math.Max(_most, _running);
        }

        try
        {
            if (held is not null)
            {
                await held.Task.WaitAsync(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();

            // A model that was offered a search ran one, as a provider says.
            return said?.Invoke(asked) ?? Answered(TeamAnswers.Of(role), liveSearches: request.LiveSearch ? 1 : null);
        }
        finally
        {
            lock (_gate)
            {
                _running--;
            }
        }
    }

    /// <summary>A call that succeeded, with what the model answered.</summary>
    public static AiCallResult Answered(string answered, int inputTokens = 1200, int outputTokens = 150, double seconds = 1.5, int? liveSearches = null) =>
        new(AiCallOutcome.Succeeded, answered, new AiTokenUsage(inputTokens, outputTokens), TimeSpan.FromSeconds(seconds), liveSearches);

    /// <summary>A call that the provider did not answer within its time limit.</summary>
    public static AiCallResult TimedOut => new(AiCallOutcome.TimedOut, null, null, TimeSpan.FromSeconds(30));

    /// <summary>A call whose answer was no JSON object: the model refused, or was cut short. It was paid for.</summary>
    public static AiCallResult Invalid => new(AiCallOutcome.InvalidResult, null, new AiTokenUsage(1200, 16000), TimeSpan.FromSeconds(9));

    /// <summary>A call that failed: no connection, or a fault of the provider.</summary>
    public static AiCallResult Failed => new(AiCallOutcome.Failed, null, null, TimeSpan.FromSeconds(0.2));

    /// <summary>A call whose key the provider refused.</summary>
    public static AiCallResult Refused => new(AiCallOutcome.AuthenticationFailed, null, null, TimeSpan.FromSeconds(0.3));

    /// <summary>A call of a model that the provider does not have.</summary>
    public static AiCallResult NotFound => new(AiCallOutcome.ModelNotFound, null, null, TimeSpan.FromSeconds(0.3));

    /// <summary>The role that a call is for: the one that answers the schema that the call asks for.</summary>
    private static Role RoleOf(string schema) =>
        schema.Contains("\"keyLevels\"", StringComparison.Ordinal) ? Role.MarketAnalyst
        : schema.Contains("\"notableEvents\"", StringComparison.Ordinal) ? Role.SentimentAnalyst
        : schema.Contains("\"action\"", StringComparison.Ordinal) ? Role.Strategist
        : schema.Contains("\"verdict\"", StringComparison.Ordinal) ? Role.RiskManager
        : throw new ArgumentException("The call asks for a schema of no role.", nameof(schema));
}

/// <summary>
/// Stands for what hands a stored key to a call, for agents that need no rows: every user has a
/// key for every provider, unless the test says otherwise, and it keeps who asked.
/// </summary>
internal sealed class StandInKeys : IKeyHandOver
{
    private readonly ConcurrentQueue<(Guid UserId, string Provider)> _asked = new();

    /// <summary>What a user is handed for a provider, when it is not their key.</summary>
    public Func<Guid, AiProviderCode, KeyHandOverOutcome>? Outcome { get; set; }

    /// <summary>Who asked for a key of which provider, in order.</summary>
    public IReadOnlyList<(Guid UserId, string Provider)> Asked => [.. _asked];

    /// <summary>The key of a user for a provider, which is the user's own.</summary>
    public static string KeyOf(Guid userId, AiProviderCode provider) => $"key-of-{userId:N}-for-{provider.Value}";

    public Task<HandedKey> AiKeyAsync(Guid userId, AiProviderCode provider, CancellationToken cancellationToken)
    {
        _asked.Enqueue((userId, provider.Value));

        var outcome = Outcome?.Invoke(userId, provider) ?? KeyHandOverOutcome.Handed;

        return Task.FromResult(outcome == KeyHandOverOutcome.Handed ? HandedKey.Of(KeyOf(userId, provider)) : HandedKey.None(outcome));
    }

    /// <summary>The Binance credential of a user for an environment, which is the user's own.</summary>
    public Task<HandedExchangeKey> ExchangeKeyAsync(Guid userId, TradingMode environment, CancellationToken cancellationToken) =>
        Task.FromResult(HandedExchangeKey.Of($"api-key-of-{userId:N}-for-{environment}", $"secret-key-of-{userId:N}-for-{environment}"));
}

/// <summary>
/// Stands for what places the orders of a cycle, for agents that need no rows: it places
/// nothing, and keeps what it was handed, in order.
/// </summary>
internal sealed class StandInPlanOrders : IPlanOrders
{
    private readonly ConcurrentQueue<(Guid AgentId, Guid CycleId, GuardResult Accepted)> _placed = new();
    private readonly ConcurrentQueue<Guid> _settled = new();

    /// <summary>What was handed over to be placed: for which agent, in which cycle, and what the Risk Guard accepted.</summary>
    public IReadOnlyList<(Guid AgentId, Guid CycleId, GuardResult Accepted)> Placed => [.. _placed];

    /// <summary>The agents whose orders were asked to be settled, in order.</summary>
    public IReadOnlyList<Guid> Settled => [.. _settled];

    /// <summary>The error that placing the orders of a plan ends with, when the test gives it one.</summary>
    public Exception? Fails { get; set; }

    /// <summary>Why no entry is placed for a plan that is handed over, when the test says that none is.</summary>
    public NoEntryReason? NoEntry { get; set; }

    public Task<NoEntryReason?> PlaceAsync(CycleStart cycle, GuardResult accepted, CancellationToken cancellationToken)
    {
        _placed.Enqueue((cycle.Agent.Id, cycle.CycleId, accepted));

        return Fails is { } error ? Task.FromException<NoEntryReason?>(error) : Task.FromResult(NoEntry);
    }

    public Task SettleAsync(Guid agentId, CancellationToken cancellationToken)
    {
        _settled.Enqueue(agentId);

        return Task.CompletedTask;
    }

    public Task<bool> SettleBeforeStopAsync(Guid agentId, CancellationToken cancellationToken)
    {
        _settled.Enqueue(agentId);

        return Task.FromResult(true);
    }

    public Task<bool> ReconcileAsync(Guid agentId, CancellationToken cancellationToken) => Task.FromResult(true);

    public Task<PositionCloseOutcome> CloseAtMarketAsync(Guid agentId, CancellationToken cancellationToken) =>
        Task.FromResult(PositionCloseOutcome.NothingOpen);

    public Task<PositionCloseOutcome> CloseForLossLimitAsync(Guid agentId, CancellationToken cancellationToken) =>
        Task.FromResult(PositionCloseOutcome.NothingOpen);

    public Task<AccountConflict?> AccountConflictAsync(Agent agent, CancellationToken cancellationToken) =>
        Task.FromResult<AccountConflict?>(null);
}

/// <summary>The record of a cycle, as <see cref="StandInCycleRecords"/> was handed it.</summary>
internal sealed record RecordedCycle(DecisionCycle Cycle, IReadOnlyList<RoleOutput> Outputs, IReadOnlyList<RiskCheckRecord> Checks)
{
    /// <summary>The one check of the Risk Guard of the cycle.</summary>
    public RiskCheckRecord Check => Assert.Single(Checks);

    /// <summary>What a check of the cycle made of a rule.</summary>
    public RuleOutcome OutcomeOf(GuardRule rule, int sequence = 1) =>
        Assert.Single(Checks, check => check.Check.Sequence == sequence).Rules.Single(stored => stored.Rule == rule).Outcome;

    /// <summary>The calls of a role, in the order they were made.</summary>
    public IReadOnlyList<RoleOutput> Of(Role role) => [.. Outputs.Where(output => output.Role == role).OrderBy(output => output.Attempt)];

    /// <summary>The roles that were called, with how each call ended and what was made of its answer, in the order of the roles.</summary>
    public IReadOnlyList<(Role Role, int Attempt, RoleCallOutcome Outcome, RoleValidation Validation)> Calls =>
        [.. Outputs.OrderBy(output => output.Role).ThenBy(output => output.Attempt).Select(output => (output.Role, output.Attempt, output.Outcome, output.Validation))];
}

/// <summary>
/// Stands for what records a cycle, for agents that need no rows: it keeps the records in the
/// order they were stored, and fails when the test says so.
/// </summary>
internal sealed class StandInCycleRecords : ICycleRecords
{
    private readonly ConcurrentQueue<RecordedCycle> _stored = new();

    /// <summary>The error that storing a record ends with, when the test gives it one.</summary>
    public Exception? Fails { get; set; }

    /// <summary>The records, in the order they were stored.</summary>
    public IReadOnlyList<RecordedCycle> Stored => [.. _stored];

    /// <summary>The one record of the cycles of an agent.</summary>
    public RecordedCycle Of(Guid agentId) => Assert.Single(Stored, stored => stored.Cycle.AgentId == agentId);

    public Task StoreAsync(
        DecisionCycle cycle,
        IReadOnlyList<RoleOutput> outputs,
        IReadOnlyList<RiskCheckRecord> checks,
        CancellationToken cancellationToken)
    {
        if (Fails is { } error)
        {
            throw error;
        }

        _stored.Enqueue(new RecordedCycle(cycle, outputs, checks));

        return Task.CompletedTask;
    }
}

/// <summary>
/// Stands for what checks a stored key again, for users that need no rows: it keeps whose key
/// it was asked to check, and answers that the provider refused it. It says of every user that
/// their Binance key lets an agent trade, unless the test says what stands in the way.
/// </summary>
internal sealed class StandInKeyChecks : IKeyValidation
{
    private readonly ConcurrentQueue<(Guid UserId, string Provider)> _checked = new();

    /// <summary>What about the Binance key of every user keeps an agent from trading, when something does (FR-KEY-5).</summary>
    public StartBlock? Block { get; set; }

    /// <summary>Whose key of which provider it was asked to check again, in order.</summary>
    public IReadOnlyList<(Guid UserId, string Provider)> Checked => [.. _checked];

    public Task<KeyCheckOutcome> RevalidateAiKeyAsync(Guid userId, AiProviderCode provider, CancellationToken cancellationToken)
    {
        _checked.Enqueue((userId, provider.Value));

        return Task.FromResult(KeyCheckOutcome.Invalid);
    }

    public Task<KeyCheckOutcome> RevalidateExchangeKeyAsync(Guid userId, TradingMode environment, CancellationToken cancellationToken) =>
        throw new NotSupportedException("What runs a team calls no exchange.");

    public Task<StartBlock?> BlockForAsync(Guid userId, TradingMode environment, Market market, CancellationToken cancellationToken) =>
        Task.FromResult(Block);
}

/// <summary>The stand-ins behind a cycle that runs the team of an agent that needs no rows.</summary>
internal sealed class StandInTeam
{
    /// <summary>What stands for the AI providers.</summary>
    public StandInModels Models { get; } = new();

    /// <summary>What stands for the stored keys of the users.</summary>
    public StandInKeys Keys { get; } = new();

    /// <summary>What stands for the record of the cycles.</summary>
    public StandInCycleRecords Records { get; } = new();

    /// <summary>What stands for the check of a key that a provider refused.</summary>
    public StandInKeyChecks KeyChecks { get; } = new();

    /// <summary>What stands for what places the orders of a cycle: it places nothing.</summary>
    public StandInPlanOrders Orders { get; } = new();

    /// <summary>Puts the stand-ins behind the interfaces that what runs a team asks.</summary>
    public void Behind(IServiceCollection services)
    {
        services.AddSingleton<IAiProviderGateway>(Models);
        services.AddSingleton<IKeyHandOver>(Keys);
        services.AddSingleton<ICycleRecords>(Records);
        services.AddSingleton<IKeyValidation>(KeyChecks);
        services.AddSingleton<IPlanOrders>(Orders);
    }
}
