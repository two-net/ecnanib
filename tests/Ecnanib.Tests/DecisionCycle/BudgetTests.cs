using System.Text.Json;
using Ecnanib.Components;
using Ecnanib.Kernel;
using Ecnanib.Modules.Agents.Contracts;
using Ecnanib.Modules.DecisionCycle.Contracts;
using Ecnanib.Modules.Notifications.Contracts;
using Ecnanib.Tests.Agents;
using Ecnanib.Tests.Identity;
using Ecnanib.Tests.Persistence;
using Microsoft.Extensions.DependencyInjection;
using WebUtility = System.Net.WebUtility;

namespace Ecnanib.Tests.DecisionCycle;

/// <summary>
/// FR-CYC-07 with FR-AGT-02's AI budget: once an agent's spend this UTC day or month reaches a limit, its cycles ask
/// no AI and make no entry until the period ends, it stays Active, and its user is told once. Every billed call counts,
/// priced models' in dollars and unpriced ones' in tokens.
/// </summary>
public sealed class BudgetTests(PostgresFixture postgres)
{
    /// <summary>
    /// What a cycle that buys costs at <see cref="Priced"/>'s prices: three calls, each 1,000 input tokens at $1,000 a
    /// million and 200 output tokens at $5,000 a million, so $2 each.
    /// </summary>
    private const decimal CycleCost = 6m;

    /// <summary>The same cycle's tokens: three calls of 1,200.</summary>
    private const long CycleTokens = 3_600;

    private const string RanOut = "Its AI budget has run out, so it asks no AI and opens no trades until";

    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    private static readonly TimeSpan Day = TimeSpan.FromDays(1);

    private static CancellationToken Cancellation => CycleApp.Cancellation;

    [Fact]
    public async Task Once_the_daily_budget_has_run_out_a_cycle_asks_no_AI_and_its_user_is_told_once()
    {
        await using var app = PricedApp();
        var (user, agent) = await app.ActiveAgentAsync(WithBudget(new AiBudget(CycleCost, null, null, null)));

        await app.RunAsync(user, agent);
        Assert.Equal(3, Completions(app));

        await NextHourAsync(app, user, agent);
        await NextHourAsync(app, user, agent);

        Assert.Equal(["OverBudget", "OverBudget", "AwaitingApproval"], await app.CyclesAsync(agent.Id));
        Assert.Equal(3, Completions(app));
        Assert.Equal(["TechnicalAnalyst Skipped", "Strategist Skipped", "RiskCritic Skipped"], await app.RolesAsync(user.Id, agent.Id));
        Assert.Equal(AgentStatus.Active, await app.Agents.StatusAsync(user.Id, agent.Id));

        var notice = Assert.Single(await NoticesAsync(app, user));
        Assert.Equal(
            ($"/agents/{agent.Id}/settings", "Scalper", true),
            (notice.Link, notice.Subject, notice.Demo));

        var budget = await LatestBudgetAsync(app, agent.Id);
        Assert.Equal("DailyUsd", budget.GetProperty("reached").GetString());
        Assert.Equal(BudgetPeriods.NextDay(app.Clock.GetUtcNow()), budget.GetProperty("until").GetDateTimeOffset());
        Assert.Equal(CycleCost, budget.GetProperty("spend").GetProperty("dayUsd").GetDecimal());
        Assert.Equal(CycleCost, budget.GetProperty("budget").GetProperty("dailyUsd").GetDecimal());

        // The council view hears how it ended, like any other cycle (FR-ROLE-08).
        Assert.Equal(CycleOutcome.OverBudget, app.Observer.Changes.Last(change => change.AgentId == agent.Id).Outcome);
    }

    [Fact]
    public async Task A_new_day_ends_it_and_running_out_again_tells_the_user_again()
    {
        await using var app = PricedApp();
        var (user, agent) = await app.ActiveAgentAsync(WithBudget(new AiBudget(CycleCost, null, null, null)));

        await app.RunAsync(user, agent);
        await NextHourAsync(app, user, agent);

        app.Clock.Advance(Day);
        await app.RunAsync(user, agent);
        await NextHourAsync(app, user, agent);

        Assert.Equal(["OverBudget", "AwaitingApproval", "OverBudget", "AwaitingApproval"], await app.CyclesAsync(agent.Id));
        Assert.Equal(2, (await NoticesAsync(app, user)).Count);
    }

    [Fact]
    public async Task A_monthly_limit_lasts_until_the_month_ends()
    {
        await using var app = PricedApp();
        var (user, agent) = await app.ActiveAgentAsync(WithBudget(new AiBudget(null, 2 * CycleCost, null, null)));

        await app.RunAsync(user, agent);
        app.Clock.Advance(Day);
        await app.RunAsync(user, agent);
        app.Clock.Advance(Day);
        await app.RunAsync(user, agent);

        var budget = await LatestBudgetAsync(app, agent.Id);
        Assert.Equal("MonthlyUsd", budget.GetProperty("reached").GetString());
        Assert.Equal(BudgetPeriods.NextMonth(app.Clock.GetUtcNow()), budget.GetProperty("until").GetDateTimeOffset());

        var now = app.Clock.GetUtcNow();
        app.Clock.Advance(BudgetPeriods.NextMonth(now) + Hour - now);
        await app.RunAsync(user, agent);

        Assert.Equal(["AwaitingApproval", "OverBudget", "AwaitingApproval", "AwaitingApproval"], await app.CyclesAsync(agent.Id));
    }

    [Fact]
    public async Task Token_caps_count_calls_to_a_model_with_no_price_and_dollar_limits_do_not()
    {
        await using var app = new CycleApp(postgres);
        MidMonth(app);
        var (user, agent) = await app.ActiveAgentAsync(WithBudget(new AiBudget(0.01m, 0.01m, CycleTokens, null)));

        await app.RunAsync(user, agent);
        await NextHourAsync(app, user, agent);

        Assert.Equal(["OverBudget", "AwaitingApproval"], await app.CyclesAsync(agent.Id));

        var budget = await LatestBudgetAsync(app, agent.Id);
        Assert.Equal("DailyTokens", budget.GetProperty("reached").GetString());
        Assert.Equal(CycleTokens, budget.GetProperty("spend").GetProperty("dayTokens").GetInt64());
        Assert.Equal(0m, budget.GetProperty("spend").GetProperty("dayUsd").GetDecimal());
    }

    [Fact]
    public async Task Token_caps_do_not_count_calls_to_a_model_with_a_price()
    {
        await using var app = PricedApp();
        var (user, agent) = await app.ActiveAgentAsync(WithBudget(new AiBudget(null, null, 1, 1)));

        await app.RunAsync(user, agent);
        await NextHourAsync(app, user, agent);

        Assert.Equal(["AwaitingApproval", "AwaitingApproval"], await app.CyclesAsync(agent.Id));

        var budget = await LatestBudgetAsync(app, agent.Id);
        Assert.Equal(JsonValueKind.Null, budget.GetProperty("reached").ValueKind);
        Assert.Equal(0L, budget.GetProperty("spend").GetProperty("dayTokens").GetInt64());
        Assert.Equal(CycleCost, budget.GetProperty("spend").GetProperty("dayUsd").GetDecimal());
    }

    /// <summary>Each attempt was billed, so a broken answer and its retry count as well as an answer that conforms.</summary>
    [Fact]
    public async Task Calls_that_broke_their_contract_count_because_they_were_billed()
    {
        await using var app = PricedApp();
        var (user, agent) = await app.ActiveAgentAsync(WithBudget(new AiBudget(CycleCost, null, null, null)));
        app.Replies[AiRole.Strategist] = _ => CycleApp.Reply("Not JSON.");

        // The Technical Analyst's call, then the Strategist's answer and its retry.
        await app.RunAsync(user, agent);
        await NextHourAsync(app, user, agent);

        Assert.Equal(["OverBudget", "Hold RoleFailed"], await app.CyclesAsync(agent.Id));
    }

    [Fact]
    public async Task Raising_the_limit_lets_the_next_cycle_ask_again()
    {
        await using var app = PricedApp();
        var (user, agent) = await app.ActiveAgentAsync(WithBudget(new AiBudget(CycleCost, null, null, null)));

        await app.RunAsync(user, agent);
        await NextHourAsync(app, user, agent);

        var current = (await app.Agents.Agents.FindAsync(user.Id, agent.Id, Cancellation))!;
        var saved = await app.Agents.Agents.UpdateAsync(
            user.Id, agent.Id, current.Version, current.Settings with { Budget = new AiBudget(2 * CycleCost, null, null, null) }, Cancellation);
        Assert.Equal(AgentSaveOutcome.Saved, saved.Outcome);

        await NextHourAsync(app, user, agent);

        Assert.Equal(["AwaitingApproval", "OverBudget", "AwaitingApproval"], await app.CyclesAsync(agent.Id));
    }

    [Fact]
    public async Task An_agent_with_no_limit_is_never_over_budget_and_its_trace_has_no_budget()
    {
        await using var app = PricedApp();
        var (user, agent) = await app.ActiveAgentAsync();

        await app.RunAsync(user, agent);
        await NextHourAsync(app, user, agent);

        Assert.Equal(["AwaitingApproval", "AwaitingApproval"], await app.CyclesAsync(agent.Id));
        Assert.Equal(["", ""], await BudgetsAsync(app, agent.Id));
    }

    /// <summary>NFR-SEC-04: an agent's spend is its own, and only its user can read it.</summary>
    [Fact]
    public async Task Spend_is_each_agents_own_and_only_its_users_can_read_it()
    {
        await using var app = PricedApp();
        var (user, agent) = await app.ActiveAgentAsync(WithBudget(new AiBudget(CycleCost, null, null, null)));
        var (other, otherAgent) = await app.ActiveAgentAsync(WithBudget(new AiBudget(CycleCost, null, null, null)));

        await app.RunAsync(user, agent);
        await app.RunAsync(other, otherAgent);

        Assert.Equal(["AwaitingApproval"], await app.CyclesAsync(otherAgent.Id));

        var spending = app.Agents.App.Services.GetRequiredService<IAiSpending>();
        var spend = Assert.Single(await spending.ListAsync(user.Id, Cancellation));
        Assert.Equal((agent.Id, CycleCost, CycleCost, 0L), (spend.Key, spend.Value.DayUsd, spend.Value.MonthUsd, spend.Value.MonthTokens));
        Assert.Equal(CycleCost, (await spending.FindAsync(user.Id, agent.Id, Cancellation)).MonthUsd);

        var stranger = await app.Agents.UserAsync();
        Assert.Empty(await spending.ListAsync(stranger.Id, Cancellation));
        Assert.Equal(0m, (await spending.FindAsync(stranger.Id, agent.Id, Cancellation)).MonthUsd);
    }

    [Fact]
    public async Task The_agent_list_and_its_settings_say_until_when_it_only_protects_its_positions()
    {
        await using var app = PricedApp();
        using var visitor = Visitor.To(app.Agents.App);
        var email = await visitor.SignUpAsync();
        var id = await AccountRows.IdAsync(app.ConnectionString, email, Cancellation);
        var user = new AgentApp.User(id, await app.Agents.AddBinanceAsync(id), await app.Agents.AddAiAsync(id));
        var draft = await app.Agents.CreateAsync(id, AgentApp.Ready(user) with { Budget = new AiBudget(CycleCost, null, null, null) });
        var agent = (await app.Agents.Agents.ActivateAsync(id, draft.Id, Cancellation)).Agent!;
        var settingsPage = $"/agents/{agent.Id}/settings";

        Assert.DoesNotContain(RanOut, await PageAsync(visitor, "/agents"), StringComparison.Ordinal);
        Assert.DoesNotContain(RanOut, await PageAsync(visitor, settingsPage), StringComparison.Ordinal);

        await app.RunAsync(user, agent);

        var expected = $"{RanOut} {DisplayTime.Utc.Format(BudgetPeriods.NextDay(app.Clock.GetUtcNow()))}";
        Assert.Contains(expected, await PageAsync(visitor, "/agents"), StringComparison.Ordinal);
        Assert.Contains(expected, await PageAsync(visitor, settingsPage), StringComparison.Ordinal);

        // A paused agent runs no cycles at all, which its status already says.
        await app.Agents.Agents.PauseAsync(id, agent.Id, Cancellation);
        Assert.DoesNotContain(RanOut, await PageAsync(visitor, "/agents"), StringComparison.Ordinal);
        Assert.DoesNotContain(RanOut, await PageAsync(visitor, settingsPage), StringComparison.Ordinal);
    }

    /// <summary>A Spot BTCUSDT 1H agent's app with a price for the test model, at noon mid-month.</summary>
    private CycleApp PricedApp()
    {
        var app = new CycleApp(postgres, Priced());
        MidMonth(app);

        return app;
    }

    private static Dictionary<string, string?> Priced() => new()
    {
        ["Catalog:Overrides:0:Provider"] = AgentApp.Provider,
        ["Catalog:Overrides:0:Model"] = AgentApp.Model,
        ["Catalog:Overrides:0:InputPricePerMillion"] = "1000",
        ["Catalog:Overrides:0:OutputPricePerMillion"] = "5000",
    };

    /// <summary>Noon on the 15th of next month, so that no day or month ends while a test runs, unless it moves the clock there.</summary>
    private static void MidMonth(CycleApp app)
    {
        var now = app.Clock.GetUtcNow();
        app.Clock.Advance(BudgetPeriods.NextMonth(now) + TimeSpan.FromDays(14) + TimeSpan.FromHours(12) - now);
    }

    private static Func<AgentApp.User, AgentSettings, AgentSettings> WithBudget(AiBudget budget) =>
        (_, settings) => settings with { Budget = budget };

    /// <summary>The next hour's cycle, as the scheduler would run it when that candle closes.</summary>
    private static Task NextHourAsync(CycleApp app, AgentApp.User user, AgentRecord agent)
    {
        app.Clock.Advance(Hour);

        return app.RunAsync(user, agent);
    }

    private static int Completions(CycleApp app) => app.Agents.Ai.Requests.Count(request => request.Path == "/v1/complete");

    private static async Task<List<NotificationRecord>> NoticesAsync(CycleApp app, AgentApp.User user) =>
    [
        .. (await app.Agents.App.Services.GetRequiredService<INotifications>().ListAsync(user.Id, before: null, Cancellation))
            .Where(notice => notice.Type == NotificationType.AgentPausedBudget),
    ];

    /// <summary>Each of the agent's cycles' budget check, newest first, or empty where it has none.</summary>
    private static Task<List<string>> BudgetsAsync(CycleApp app, Guid agentId) =>
        Sql.ColumnAsync(
            app.ConnectionString,
            $"SELECT coalesce(budget::text, '') FROM decision_cycles WHERE agent_id = '{agentId}' ORDER BY candle_close DESC",
            Cancellation);

    private static async Task<JsonElement> LatestBudgetAsync(CycleApp app, Guid agentId) =>
        JsonDocument.Parse((await BudgetsAsync(app, agentId))[0]).RootElement.Clone();

    private static async Task<string> PageAsync(Visitor visitor, string path) => WebUtility.HtmlDecode(await visitor.PageAsync(path));
}
