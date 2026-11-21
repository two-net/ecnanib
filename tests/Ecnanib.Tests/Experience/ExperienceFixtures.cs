using System.Net;
using System.Text.RegularExpressions;
using Ecnanib.Agents;
using Ecnanib.DecisionEngine;
using Ecnanib.Experience;
using Ecnanib.Shared;
using Ecnanib.Tests.Accounts;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Tests.Experience;

/// <summary>Closed trades that a test needs a user to have, without the cycles and the orders that lead to them.</summary>
internal static class StoredTradeRows
{
    /// <summary>
    /// Gives an agent of a user a position that is over, stored as the application stores one,
    /// with the models that held its roles and, unless the test says otherwise, with its result.
    /// It is for a test of what is worked out from closed trades: it has no orders, and nothing
    /// is left to settle. What the test does not say of it is as its agent is now.
    /// </summary>
    /// <param name="application">The application.</param>
    /// <param name="userId">The user whose agent it is.</param>
    /// <param name="agentId">The agent.</param>
    /// <param name="closedAt">When the position was closed.</param>
    /// <param name="resultInR">Its result in R, or null for a result that says no R.</param>
    /// <param name="mode">The mode that the agent was in when it was opened.</param>
    /// <param name="market">The market that it was traded on.</param>
    /// <param name="timeframe">The timeframe that the agent worked on.</param>
    /// <param name="holdingSeconds">How long it was held.</param>
    /// <param name="complete">Whether its result is all what the exchange said.</param>
    /// <param name="recorded">Whether its result is recorded.</param>
    /// <param name="closed">Whether it is closed at all: an agent holds one open position at most.</param>
    /// <param name="roles">The model that held each role when it was opened, in place of the models that the roles of the agent have now.</param>
    /// <returns>The id of the position.</returns>
    public static async Task<Guid> GiveTradeAsync(
        this AccountsApplication application,
        Guid userId,
        Guid agentId,
        DateTimeOffset closedAt,
        decimal? resultInR = 1m,
        TradingMode? mode = null,
        Market? market = null,
        Timeframe? timeframe = null,
        long holdingSeconds = 3600,
        bool complete = true,
        bool recorded = true,
        bool closed = true,
        IReadOnlyList<(Role Role, Guid ModelId)>? roles = null)
    {
        await using var context = await application.Contexts.CreateDbContextAsync(TestContext.Current.CancellationToken);

        var agent = await context.Set<Agent>().AsNoTracking().SingleAsync(stored => stored.Id == agentId, TestContext.Current.CancellationToken);

        var position = new Position(
            Guid.CreateVersion7(),
            agentId,
            userId,
            mode ?? agent.Mode,
            market ?? agent.Market,
            agent.Symbol,
            agent.QuoteAsset,
            timeframe ?? agent.Timeframe,
            PositionSide.Long,
            entryPrice: 100_000m,
            quantity: 0.01m,
            stopLoss: 99_000m,
            takeProfit: 102_000m,
            openedAt: closedAt.AddSeconds(-holdingSeconds),
            closedAt: closed ? closedAt : null,
            closed ? ExitReason.TakeProfit : null,
            alertedAt: null,
            fundingDueAt: null);

        context.Add(position);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        // The models that held the roles when the position was opened, as the position names
        // them: those that the test says, or those that the roles of the agent have now.
        roles ??=
        [
            .. (await context.Set<AgentRole>()
                .AsNoTracking()
                .Where(role => role.AgentId == agentId)
                .ToListAsync(TestContext.Current.CancellationToken))
                .Select(role => (role.Role, role.ModelId)),
        ];
        context.AddRange(roles.Select(role => new PositionRole(position.Id, userId, role.Role, role.ModelId)));

        if (closed && recorded)
        {
            // What was risked is 10 of the asset, so the net result is ten times the result in R.
            context.Add(new PositionResult(
                position.Id,
                userId,
                agent.QuoteAsset,
                grossResult: (resultInR ?? 0m) * 10m,
                fees: 0m,
                funding: 0m,
                netResult: (resultInR ?? 0m) * 10m,
                risk: resultInR is null ? null : 10m,
                resultInR,
                holdingSeconds,
                complete,
                recordedAt: closedAt.AddMinutes(1)));
        }

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        return position.Id;
    }

    /// <summary>Deletes an agent of a user, as its user does: what hangs on it goes with it.</summary>
    public static async Task DeleteAgentAsync(this AccountsApplication application, Guid agentId)
    {
        await using var context = await application.Contexts.CreateDbContextAsync(TestContext.Current.CancellationToken);

        await context.Set<Agent>().Where(agent => agent.Id == agentId).ExecuteDeleteAsync(TestContext.Current.CancellationToken);
    }
}

/// <summary>Lessons that a test needs a user to have, without the model that writes them.</summary>
internal static class StoredLessonRows
{
    /// <summary>What the lesson of <see cref="GiveLessonAsync"/> says for its takeaway, unless the test says another.</summary>
    public const string Takeaway = "Wait for the retest of a broken level before entering.";

    /// <summary>
    /// Gives an agent of a user a closed position with its result, and the lesson of that
    /// position, stored as the application stores one once its model has written it. It is for a
    /// test of what is recalled, read, or managed: no model is called. A position has one
    /// lesson, and a lesson is only ever of a position that has its result. What the test does
    /// not say of it is as its agent is now.
    /// </summary>
    /// <param name="application">The application.</param>
    /// <param name="userId">The user whose agent it is.</param>
    /// <param name="agentId">The agent.</param>
    /// <param name="closedAt">When the position was closed.</param>
    /// <param name="takeaway">The takeaway of the lesson, which tells one lesson from another.</param>
    /// <param name="enabled">Whether the lesson is enabled.</param>
    /// <param name="outcome">What became of the lesson: written, or why not, or null for one that waits.</param>
    /// <param name="resultInR">The result of the position in R.</param>
    /// <param name="mode">The mode that the position was in.</param>
    /// <param name="market">The market that it was traded on.</param>
    /// <param name="timeframe">The timeframe that the agent worked on.</param>
    /// <param name="setup">The setup of the lesson.</param>
    /// <returns>The id of the position, which names the lesson.</returns>
    public static async Task<Guid> GiveLessonAsync(
        this AccountsApplication application,
        Guid userId,
        Guid agentId,
        DateTimeOffset closedAt,
        string takeaway = Takeaway,
        bool enabled = true,
        LessonOutcome? outcome = LessonOutcome.Written,
        decimal? resultInR = 1m,
        TradingMode? mode = null,
        Market? market = null,
        Timeframe? timeframe = null,
        string setup = "Long on a breakout above the range high.")
    {
        var positionId = await application.GiveTradeAsync(userId, agentId, closedAt, resultInR, mode, market, timeframe);

        await using var context = await application.Contexts.CreateDbContextAsync(TestContext.Current.CancellationToken);

        var position = await context.Set<Position>().AsNoTracking().SingleAsync(stored => stored.Id == positionId, TestContext.Current.CancellationToken);
        var written = outcome == LessonOutcome.Written;

        // The model that the Risk Manager of the agent has now, where it has one, and the
        // version of the lesson instructions that is in use: what wrote a lesson, as its row says it.
        var modelId = written
            ? await context.Set<AgentRole>()
                .AsNoTracking()
                .Where(role => role.AgentId == agentId && role.Role == Role.RiskManager)
                .Select(role => (Guid?)role.ModelId)
                .SingleOrDefaultAsync(TestContext.Current.CancellationToken)
            : null;
        var templateId = written
            ? await context.Set<PromptTemplate>()
                .AsNoTracking()
                .Where(template => template.Kind == PromptTemplateKind.Lesson && template.IsActive)
                .Select(template => (int?)template.Id)
                .SingleAsync(TestContext.Current.CancellationToken)
            : null;

        context.Add(new Lesson(
            position.Id,
            userId,
            agentId,
            position.Mode,
            position.Market,
            position.Symbol,
            position.Timeframe,
            closedAt,
            resultInR,
            dueAt: closedAt.AddMinutes(1),
            outcome,
            setup: written ? setup : null,
            expected: written ? "Continuation to the next resistance." : null,
            happened: written ? "The price fell back into the range and the stop-loss was executed." : null,
            takeaway: written ? takeaway : null,
            tags: written ? Lesson.NamesOf([LessonTag.Entry, LessonTag.Timing]) : null,
            modelId,
            templateId,
            writtenAt: outcome is null ? null : closedAt.AddMinutes(2),
            enabled));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        return positionId;
    }
}

/// <summary>
/// The calls of analysts and the shadow trades that a test needs a user to have, as they are
/// once they are scored, without the cycles that leave them and the candles that score them.
/// </summary>
internal static class StoredExperienceRows
{
    /// <summary>
    /// Gives an agent of a user calls of an analyst, each of a cycle of its own, an hour apart
    /// and the latest at a time: so many that matched, so many that did not, so many that are
    /// not scored yet, and so many whose candles could no longer be had. What the test does not
    /// say of them is as their agent is now.
    /// </summary>
    /// <param name="application">The application.</param>
    /// <param name="userId">The user whose agent it is.</param>
    /// <param name="agentId">The agent.</param>
    /// <param name="role">The analyst.</param>
    /// <param name="modelId">The model that made the calls.</param>
    /// <param name="latest">The close of the candle of the latest of the calls.</param>
    /// <param name="matched">How many matched.</param>
    /// <param name="missed">How many did not.</param>
    /// <param name="awaiting">How many are not scored yet.</param>
    /// <param name="withoutCandles">How many could not be scored.</param>
    /// <param name="mode">The mode that the agent was in.</param>
    /// <param name="market">The market that it traded on.</param>
    /// <param name="timeframe">The timeframe that it worked on.</param>
    public static async Task GiveCallsAsync(
        this AccountsApplication application,
        Guid userId,
        Guid agentId,
        Role role,
        Guid modelId,
        DateTimeOffset latest,
        int matched = 0,
        int missed = 0,
        int awaiting = 0,
        int withoutCandles = 0,
        TradingMode? mode = null,
        Market? market = null,
        Timeframe? timeframe = null)
    {
        await using var context = await application.Contexts.CreateDbContextAsync(TestContext.Current.CancellationToken);

        var agent = await context.Set<Agent>().AsNoTracking().SingleAsync(stored => stored.Id == agentId, TestContext.Current.CancellationToken);

        CallOutcome?[] outcomes =
        [
            .. Enumerable.Repeat<CallOutcome?>(CallOutcome.Matched, matched),
            .. Enumerable.Repeat<CallOutcome?>(CallOutcome.Missed, missed),
            .. Enumerable.Repeat<CallOutcome?>(null, awaiting),
            .. Enumerable.Repeat<CallOutcome?>(CallOutcome.NoCandles, withoutCandles),
        ];

        foreach (var (outcome, hour) in outcomes.Select((outcome, hour) => (outcome, hour)))
        {
            var closedAt = latest.AddHours(-hour);
            var cycle = Cycle(agent, userId, mode ?? agent.Mode, closedAt, CycleOutcome.Hold);
            var scored = outcome is CallOutcome.Matched or CallOutcome.Missed;

            context.Add(cycle);
            context.Add(new AnalystCall(
                cycle.Id,
                role,
                userId,
                modelId,
                cycle.Mode,
                market ?? agent.Market,
                agent.Symbol,
                timeframe ?? agent.Timeframe,
                closedAt,
                CallDirection.Bullish,
                price: 61310m,
                dueAt: closedAt.AddHours(4),
                outcome,
                laterPrice: scored ? (outcome == CallOutcome.Matched ? 61500m : 61000m) : null,
                scoredAt: outcome is null ? null : closedAt.AddHours(4).AddMinutes(1)));
        }

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Gives an agent of a user the shadow trade of a plan that its Risk Manager vetoed, of a
    /// cycle of its own: one that ended with a result, unless the test says otherwise. What the
    /// test does not say of it is as its agent is now.
    /// </summary>
    /// <param name="application">The application.</param>
    /// <param name="userId">The user whose agent it is.</param>
    /// <param name="agentId">The agent.</param>
    /// <param name="modelId">The model that vetoed the plan.</param>
    /// <param name="closedAt">The close of the candle that the plan was made on.</param>
    /// <param name="resultInR">Its result in R, or null for one that has none.</param>
    /// <param name="outcome">How it ended, or null for one that is open still. Without a word of the test, at its stop-loss or its take-profit, by its result.</param>
    /// <param name="open">Whether it has not ended yet.</param>
    /// <param name="mode">The mode that the agent was in.</param>
    /// <param name="market">The market that it traded on.</param>
    /// <param name="timeframe">The timeframe that it worked on.</param>
    /// <returns>The id of the cycle, which is the key of the shadow trade.</returns>
    public static async Task<Guid> GiveShadowTradeAsync(
        this AccountsApplication application,
        Guid userId,
        Guid agentId,
        Guid modelId,
        DateTimeOffset closedAt,
        decimal? resultInR = -1m,
        ShadowOutcome? outcome = null,
        bool open = false,
        TradingMode? mode = null,
        Market? market = null,
        Timeframe? timeframe = null)
    {
        await using var context = await application.Contexts.CreateDbContextAsync(TestContext.Current.CancellationToken);

        var agent = await context.Set<Agent>().AsNoTracking().SingleAsync(stored => stored.Id == agentId, TestContext.Current.CancellationToken);
        var cycle = Cycle(agent, userId, mode ?? agent.Mode, closedAt, CycleOutcome.Vetoed);
        ShadowOutcome? ended = open ? null : outcome ?? (resultInR > 0m ? ShadowOutcome.TakeProfit : ShadowOutcome.StopLoss);

        context.Add(cycle);
        context.Add(new ShadowTrade(
            cycle.Id,
            userId,
            modelId,
            cycle.Mode,
            market ?? agent.Market,
            agent.Symbol,
            timeframe ?? agent.Timeframe,
            closedAt,
            ShadowSide.Long,
            entryPrice: 61310m,
            resting: false,
            stopLoss: 59000m,
            takeProfit: 64000m,
            dueAt: closedAt.AddHours(1),
            ended,
            resultInR: open ? null : resultInR,
            exitedAt: ended is ShadowOutcome.StopLoss or ShadowOutcome.TakeProfit or ShadowOutcome.TimeUp ? closedAt.AddHours(3) : null,
            evaluatedAt: open ? null : closedAt.AddHours(3).AddMinutes(1)));

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        return cycle.Id;
    }

    /// <summary>
    /// The row of a cycle with no calls, for a row of Experience to hang on. It is written as a
    /// row, and not stored as the application stores a cycle, which would leave rows of its own.
    /// </summary>
    private static DecisionCycle Cycle(Agent agent, Guid userId, TradingMode mode, DateTimeOffset closedAt, CycleOutcome outcome) =>
        new(Guid.CreateVersion7(), agent.Id, userId, mode, closedAt, closedAt.AddSeconds(2), closedAt.AddSeconds(40), outcome);
}

/// <summary>Closed trades that a test of the measures makes up, without a database.</summary>
internal static class Trades
{
    /// <summary>The moment that the trades of a test are counted from.</summary>
    public static readonly DateTimeOffset Start = new(2026, 11, 17, 10, 0, 0, TimeSpan.Zero);

    /// <summary>A trade that was closed some minutes after the start, with a result in R.</summary>
    public static ClosedTrade Closed(
        int minutes, decimal? resultInR, long holdingSeconds = 3600, bool complete = true, TradingMode mode = TradingMode.Demo) =>
        new(
            Guid.CreateVersion7(),
            mode,
            Market.Spot,
            Timeframe.OneHour,
            Start.AddMinutes(minutes),
            new TradeResult(resultInR, holdingSeconds, complete),
            []);

    /// <summary>A trade that was closed some minutes after the start, whose result is not recorded yet.</summary>
    public static ClosedTrade Awaiting(int minutes) =>
        new(Guid.CreateVersion7(), TradingMode.Demo, Market.Spot, Timeframe.OneHour, Start.AddMinutes(minutes), null, []);
}

/// <summary>What a browser reads on the experience screens.</summary>
internal static class ExperienceBrowser
{
    /// <summary>The path of the experience screen.</summary>
    public const string Screen = "/experience";

    /// <summary>The headings of the measures of a track record, in the order of their columns.</summary>
    public static readonly string[] Measures =
    [
        "Closed trades", "Win rate", "Average R", "Total R", "Profit factor", "Largest drawdown", "Average holding time", "Notes",
    ];

    /// <summary>The path of the page of a model's experience record.</summary>
    public static string ModelPage(Guid modelId) => $"/experience/models/{modelId}";

    /// <summary>The headings of the columns of the table with a caption on a page.</summary>
    public static IReadOnlyList<string> ColumnsOf(string page, string caption) =>
        [.. Cells(Part(TableOf(page, caption), "thead"))];

    /// <summary>The rows of the table with a caption on a page: each as the text of its cells, as a person reads them.</summary>
    public static IReadOnlyList<string[]> RowsOf(string page, string caption) =>
        [
            .. Regex.Matches(Part(TableOf(page, caption), "tbody"), "<tr[^>]*>(.*?)</tr>", RegexOptions.Singleline)
                .Select(row => Cells(row.Groups[1].Value).ToArray()),
        ];

    /// <summary>Whether a page has a table with a caption.</summary>
    public static bool HasTable(string page, string caption) => page.Contains($"<caption>{caption}</caption>", StringComparison.Ordinal);

    /// <summary>The value of the choice that a select of the filter on a page starts from, by the name of the select.</summary>
    public static string ChosenIn(string page, string name)
    {
        var select = Regex.Match(page, $"<select[^>]*name=\"{Regex.Escape(name)}\"[^>]*>(.*?)</select>", RegexOptions.Singleline);

        Assert.True(select.Success, $"The page has no choice of {name}:{Environment.NewLine}{page}");

        // The one that is marked, or the first, which is what a browser starts from. A choice of
        // nothing is written as a value with nothing after it.
        var options = Regex.Matches(select.Groups[1].Value, Option);

        return (options.SingleOrDefault(option => option.Groups[2].Success) ?? options[0]).Groups[1].Value;
    }

    /// <summary>The values of the choices that a select of the filter on a page offers, in order, by the name of the select.</summary>
    public static IReadOnlyList<string> ChoicesOf(string page, string name)
    {
        var select = Regex.Match(page, $"<select[^>]*name=\"{Regex.Escape(name)}\"[^>]*>(.*?)</select>", RegexOptions.Singleline);

        Assert.True(select.Success, $"The page has no choice of {name}:{Environment.NewLine}{page}");

        return [.. Regex.Matches(select.Groups[1].Value, Option).Select(option => option.Groups[1].Value)];
    }

    private const string Option = "<option value(?:=\"([^\"]*)\")?( selected)?[^>]*>";

    private static string TableOf(string page, string caption)
    {
        var table = Regex.Match(page, $"<caption>{Regex.Escape(caption)}</caption>(.*?)</table>", RegexOptions.Singleline);

        Assert.True(table.Success, $"The page has no table \"{caption}\":{Environment.NewLine}{page}");

        return table.Groups[1].Value;
    }

    private static string Part(string table, string element) =>
        Regex.Match(table, $"<{element}>(.*?)</{element}>", RegexOptions.Singleline).Groups[1].Value;

    private static IEnumerable<string> Cells(string row) =>
        Regex.Matches(row, "<t[hd][^>]*>(.*?)</t[hd]>", RegexOptions.Singleline)
            .Select(cell => WebUtility.HtmlDecode(Regex.Replace(cell.Groups[1].Value, "<[^>]+>", string.Empty)).Trim());
}
