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
