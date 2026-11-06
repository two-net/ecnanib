using System.Globalization;
using System.Net.Http;
using Ecnanib.Kernel;
using Ecnanib.Modules.Agents.Contracts;
using Ecnanib.Modules.AiCouncil.Contracts;
using Ecnanib.Modules.DecisionCycle.Contracts;
using Ecnanib.Modules.Notifications.Contracts;
using Ecnanib.Modules.RiskEngine.Contracts;
using Ecnanib.Tests.Agents;
using Ecnanib.Tests.Exchange;
using Ecnanib.Tests.Execution;
using Ecnanib.Tests.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.DecisionCycle;

/// <summary>
/// SRS 3.11 and UC-03: in Confirm mode an order the Risk Engine sized waits for its user, who approves or rejects it. It
/// expires after its timeframe's window, and an approved one is checked again on the current price before it's sent.
/// Risk-reducing actions go at once when the agent allows it.
/// </summary>
public sealed class ApprovalTests(PostgresFixture postgres)
{
    private const string Sell = """
        {"action":"SELL","side":null,"entry_type":null,"entry_price":null,"stop_price":null,"target_prices":[],
         "risk_percent":null,"limit_ttl_candles":null,"close_fraction":null,"invalidation":null,
         "rationale":"Momentum has faded.","confidence":0.6}
        """;

    private const string AdjustTarget = """
        {"action":"ADJUST_TARGET","side":null,"entry_type":null,"entry_price":null,"stop_price":null,"target_prices":[60160],
         "risk_percent":null,"limit_ttl_candles":null,"close_fraction":null,"invalidation":null,
         "rationale":"Room to run.","confidence":0.6}
        """;

    private static readonly TimeSpan HourWindow = TimeSpan.FromMinutes(20);

    private static CancellationToken Cancellation => CycleApp.Cancellation;

    [Fact]
    public async Task A_Confirm_agents_trade_waits_as_a_pending_approval_and_its_user_is_told()
    {
        await using var app = new CycleApp(postgres);
        var book = new FakeOrderBook(app.Agents.Binance, app.Clock) { Price = CycleApp.Ask };
        var (user, agent) = await app.ActiveAgentAsync();

        await app.RunAsync(user, agent);

        Assert.Equal(["AwaitingApproval"], await app.CyclesAsync(agent.Id));
        Assert.Empty(book.Placed);

        var approval = await app.PendingAsync(user);
        var proposal = approval.Proposal;
        var sized = (await app.EvaluationAsync(agent.Id))!.Value.GetProperty("sizedOrder");
        Assert.Equal((ApprovalStatus.Pending, agent.Id), (approval.Status, approval.AgentId));
        Assert.Equal(HourWindow, approval.ExpiresAt - approval.CreatedAt);
        Assert.Equal((IntentAction.Buy, "BTCUSDT", "BTC", "USDT", TradingEnvironment.Demo), (proposal.Action, proposal.Symbol, proposal.BaseAsset, proposal.QuoteAsset, proposal.Environment));
        Assert.Equal(sized.GetProperty("moneyAtRisk").GetDecimal(), proposal.MoneyAtRisk);
        Assert.Equal(sized.GetProperty("quantity").GetDecimal(), proposal.Quantity);
        Assert.Equal((60118m, 60135m, CycleApp.Ask), (proposal.StopPrice, proposal.TargetPrice, proposal.ReferencePrice));
        Assert.Equal(("Momentum above the 20 EMA.", VerdictKind.Approve, "Sound."), (proposal.StrategistRationale, proposal.Verdict, proposal.CriticRationale));

        var notice = Assert.Single(await NoticesAsync(app, user, NotificationType.ApprovalPending));
        Assert.Equal(($"/approvals/{approval.Id}", agent.Settings.Name, true), (notice.Link, notice.Subject, notice.Demo));
        Assert.Equal(CycleOutcome.AwaitingApproval, app.Observer.Changes[^1].Outcome);
    }

    [Fact]
    public async Task Approving_checks_it_again_and_sends_what_the_Risk_Engine_sizes_now()
    {
        await using var app = new CycleApp(postgres);
        var book = new FakeOrderBook(app.Agents.Binance, app.Clock) { Price = CycleApp.Ask };
        var (user, agent) = await app.ActiveAgentAsync();
        await app.RunAsync(user, agent);
        var pending = await app.PendingAsync(user);

        var result = await app.Approvals.ApproveAsync(user.Id, pending.Id, Cancellation);

        Assert.Equal(ApprovalOutcome.Done, result.Outcome);
        var approval = result.Approval!;
        Assert.Equal((ApprovalStatus.Executed, (ApprovalEnd?)null, true), (approval.Status, approval.End, approval.DecidedByUser));
        Assert.True(approval.Revalidation!.Passed);
        Assert.Empty(approval.Revalidation.Failures);
        Assert.Equal(Modules.Execution.Contracts.ExecutionStatus.Protected, approval.Execution!.Status);
        Assert.Equal(["MARKET", "OCO"], book.Placed.Select(sent => sent.Type));
        Assert.Equal(3L, await ScalarAsync(app, $"SELECT count(*) FROM orders WHERE agent_id = '{agent.Id}' AND cycle_id = '{pending.CycleId}'"));
        Assert.Equal(0L, await ScalarAsync(app, CycleExecutionTests.Ac01));

        // The cycle's outcome stays what it decided; the approval says how it went.
        Assert.Equal(["AwaitingApproval"], await app.CyclesAsync(agent.Id));

        var record = Assert.Single(await app.Agents.AuditAsync(user.Id), row => row.Action == nameof(AuditAction.TradeApproved));
        Assert.Equal(("User", user.Id, "Approval", pending.Id.ToString()), (record.ActorKind, record.ActorId, record.TargetType, record.TargetId));
        Assert.Contains("\"Buy\"", record.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_approval_that_no_longer_passes_on_the_current_price_is_rejected_automatically_and_nothing_is_sent()
    {
        await using var app = new CycleApp(postgres);
        var book = new FakeOrderBook(app.Agents.Binance, app.Clock) { Price = CycleApp.Ask };
        var (user, agent) = await app.ActiveAgentAsync();
        await app.RunAsync(user, agent);
        var pending = await app.PendingAsync(user);

        // The price falls through the stop meanwhile. A price over a minute old isn't used again.
        app.Clock.Advance(TimeSpan.FromMinutes(2));
        app.Agents.Binance.On(
            HttpMethod.Get,
            "/api/v3/ticker/24hr",
            _ => FakeBinance.Json(BinanceSamples.SpotTicker24h.Replace("60123.4", "60110.4", StringComparison.Ordinal)));

        var approval = (await app.Approvals.ApproveAsync(user.Id, pending.Id, Cancellation)).Approval!;

        Assert.Equal(ApprovalStatus.Invalid, approval.Status);
        Assert.False(approval.Revalidation!.Passed);
        Assert.Contains(approval.Revalidation.Failures, rule => rule.Reason == RiskReason.StopWrongSide);
        Assert.Null(approval.Execution);
        Assert.Empty(book.Placed);
        Assert.Equal(0L, await ScalarAsync(app, $"SELECT count(*) FROM orders WHERE agent_id = '{agent.Id}'"));

        var audit = await app.Agents.AuditAsync(user.Id);
        Assert.Contains(audit, row => row.Action == nameof(AuditAction.TradeApproved) && row.ActorKind == "User");
        Assert.Contains(audit, row => row.Action == nameof(AuditAction.TradeRejected) && row.ActorKind == "System" && row.ActorId == null);
    }

    [Fact]
    public async Task A_loss_limit_the_second_look_finds_pauses_the_agent()
    {
        await using var app = new CycleApp(postgres);
        var book = new FakeOrderBook(app.Agents.Binance, app.Clock) { Price = CycleApp.Ask };
        var (user, agent) = await app.ActiveAgentAsync();
        await app.RunAsync(user, agent);
        var pending = await app.PendingAsync(user);

        // A trade that closed meanwhile lost 3% of the capital of 100: the daily limit.
        app.Clock.Advance(TimeSpan.FromMinutes(1));
        await ClosedAsync(app, user, agent, -3m, app.Clock.GetUtcNow());

        var approval = (await app.Approvals.ApproveAsync(user.Id, pending.Id, Cancellation)).Approval!;

        Assert.Equal(ApprovalStatus.Invalid, approval.Status);
        Assert.Contains(approval.Revalidation!.Failures, rule => rule.Reason == RiskReason.DailyLossLimitReached);
        Assert.Empty(book.Placed);
        var paused = await app.Agents.Agents.FindAsync(user.Id, agent.Id, Cancellation);
        Assert.Equal((AgentStatus.Paused, AgentStatusReason.DailyLossLimit), (paused!.Status, paused.StatusReason));
    }

    [Fact]
    public async Task Rejecting_sends_nothing_and_is_audited()
    {
        await using var app = new CycleApp(postgres);
        var book = new FakeOrderBook(app.Agents.Binance, app.Clock) { Price = CycleApp.Ask };
        var (user, agent) = await app.ActiveAgentAsync();
        await app.RunAsync(user, agent);
        var pending = await app.PendingAsync(user);

        var result = await app.Approvals.RejectAsync(user.Id, pending.Id, Cancellation);

        Assert.Equal((ApprovalOutcome.Done, ApprovalStatus.Rejected, true), (result.Outcome, result.Approval!.Status, result.Approval.DecidedByUser));
        Assert.NotNull(result.Approval.DecidedAt);
        Assert.Empty(book.Placed);
        var record = Assert.Single(await app.Agents.AuditAsync(user.Id), row => row.Action == nameof(AuditAction.TradeRejected));
        Assert.Equal(("User", user.Id), (record.ActorKind, record.ActorId));

        // Decided once.
        var again = await app.Approvals.ApproveAsync(user.Id, pending.Id, Cancellation);
        Assert.Equal((ApprovalOutcome.NotPending, ApprovalStatus.Rejected), (again.Outcome, again.Approval!.Status));
        Assert.Empty(book.Placed);
    }

    [Fact]
    public async Task An_approval_expires_after_its_window_and_its_user_is_told_once()
    {
        await using var app = new CycleApp(postgres);
        var book = new FakeOrderBook(app.Agents.Binance, app.Clock) { Price = CycleApp.Ask };
        var (user, agent) = await app.ActiveAgentAsync();
        await app.RunAsync(user, agent);
        var pending = await app.PendingAsync(user);

        app.Clock.Advance(TimeSpan.FromMinutes(10));
        await app.Agents.RunJobAsync("ApprovalExpiryJob");
        Assert.Equal(ApprovalStatus.Pending, (await app.Approvals.FindAsync(user.Id, pending.Id, Cancellation))!.Status);

        app.Clock.Advance(TimeSpan.FromMinutes(11));
        await app.Agents.RunJobAsync("ApprovalExpiryJob");
        await app.Agents.RunJobAsync("ApprovalExpiryJob");

        var expired = (await app.Approvals.FindAsync(user.Id, pending.Id, Cancellation))!;
        Assert.Equal((ApprovalStatus.Expired, ApprovalEnd.WindowEnded, false), (expired.Status, expired.End, expired.DecidedByUser));
        var notice = Assert.Single(await NoticesAsync(app, user, NotificationType.ApprovalExpired));
        Assert.Equal(($"/approvals/{pending.Id}", true), (notice.Link, notice.Demo));

        var late = await app.Approvals.ApproveAsync(user.Id, pending.Id, Cancellation);
        Assert.Equal((ApprovalOutcome.NotPending, ApprovalStatus.Expired), (late.Outcome, late.Approval!.Status));
        Assert.Empty(book.Placed);
    }

    [Fact]
    public async Task Approving_after_the_window_expires_it_even_before_the_job_has()
    {
        await using var app = new CycleApp(postgres);
        var book = new FakeOrderBook(app.Agents.Binance, app.Clock) { Price = CycleApp.Ask };
        var (user, agent) = await app.ActiveAgentAsync();
        await app.RunAsync(user, agent);
        var pending = await app.PendingAsync(user);

        app.Clock.Advance(HourWindow);
        var result = await app.Approvals.ApproveAsync(user.Id, pending.Id, Cancellation);

        Assert.Equal((ApprovalOutcome.NotPending, ApprovalStatus.Expired, ApprovalEnd.WindowEnded), (result.Outcome, result.Approval!.Status, result.Approval.End));
        Assert.Single(await NoticesAsync(app, user, NotificationType.ApprovalExpired));
        Assert.Empty(book.Placed);
        Assert.DoesNotContain(await app.Agents.AuditAsync(user.Id), row => row.Action == nameof(AuditAction.TradeApproved));
    }

    [Fact]
    public async Task A_paused_agents_approval_expires_and_cannot_be_approved()
    {
        await using var app = new CycleApp(postgres);
        var book = new FakeOrderBook(app.Agents.Binance, app.Clock) { Price = CycleApp.Ask };
        var (user, agent) = await app.ActiveAgentAsync();
        await app.RunAsync(user, agent);
        var first = await app.PendingAsync(user);
        await app.Agents.Agents.PauseAsync(user.Id, agent.Id, Cancellation);

        var result = await app.Approvals.ApproveAsync(user.Id, first.Id, Cancellation);

        Assert.Equal((ApprovalStatus.Expired, ApprovalEnd.AgentNotActive), (result.Approval!.Status, result.Approval.End));
        Assert.Empty(book.Placed);

        // The job finds one the user doesn't open, too.
        await app.Agents.Agents.ActivateAsync(user.Id, agent.Id, Cancellation);
        app.Clock.Advance(TimeSpan.FromHours(1));
        await app.RunAsync(user, agent);
        var second = await app.PendingAsync(user);
        await app.Agents.Agents.PauseAsync(user.Id, agent.Id, Cancellation);
        await app.Agents.RunJobAsync("ApprovalExpiryJob");

        Assert.Equal(
            (ApprovalStatus.Expired, (ApprovalEnd?)ApprovalEnd.AgentNotActive),
            ((await app.Approvals.FindAsync(user.Id, second.Id, Cancellation))!.Status, (await app.Approvals.FindAsync(user.Id, second.Id, Cancellation))!.End));
        Assert.Equal(2, (await NoticesAsync(app, user, NotificationType.ApprovalExpired)).Count);
    }

    [Fact]
    public async Task Two_approvals_of_one_at_once_send_one_order()
    {
        await using var app = new CycleApp(postgres);
        var book = new FakeOrderBook(app.Agents.Binance, app.Clock) { Price = CycleApp.Ask };
        var (user, agent) = await app.ActiveAgentAsync();
        await app.RunAsync(user, agent);
        var pending = await app.PendingAsync(user);

        var results = await Task.WhenAll(
            app.Approvals.ApproveAsync(user.Id, pending.Id, Cancellation),
            app.Approvals.ApproveAsync(user.Id, pending.Id, Cancellation));

        Assert.Single(results, result => result.Outcome == ApprovalOutcome.Done);
        Assert.Single(book.Placed, sent => sent.Type == "MARKET");
        Assert.Single(await app.Agents.AuditAsync(user.Id), row => row.Action == nameof(AuditAction.TradeApproved));
        Assert.Equal(ApprovalStatus.Executed, (await app.Approvals.FindAsync(user.Id, pending.Id, Cancellation))!.Status);
    }

    [Fact]
    public async Task A_newer_proposal_replaces_the_agents_pending_one()
    {
        await using var app = new CycleApp(postgres);
        var (user, agent) = await app.ActiveAgentAsync();
        await app.RunAsync(user, agent);
        var first = await app.PendingAsync(user);

        app.Clock.Advance(TimeSpan.FromHours(1));
        await app.RunAsync(user, agent);

        var list = await app.Approvals.ListAsync(user.Id, Cancellation);
        var second = Assert.Single(list.Pending);
        Assert.NotEqual(first.Id, second.Id);
        var replaced = Assert.Single(list.Decided);
        Assert.Equal((first.Id, ApprovalStatus.Expired, (ApprovalEnd?)ApprovalEnd.Superseded), (replaced.Id, replaced.Status, replaced.End));

        // Its user hears of the new one, which is enough.
        Assert.Equal(2, (await NoticesAsync(app, user, NotificationType.ApprovalPending)).Count);
        Assert.Empty(await NoticesAsync(app, user, NotificationType.ApprovalExpired));
    }

    [Fact]
    public async Task A_Confirm_agent_sells_at_once_when_it_may_reduce_risk_without_asking()
    {
        await using var app = new CycleApp(postgres);
        var book = new FakeOrderBook(app.Agents.Binance, app.Clock) { Price = CycleApp.Ask };
        var (user, agent) = await app.ActiveAgentAsync();
        await HoldAsync(app, user, agent);

        app.Clock.Advance(TimeSpan.FromHours(1));
        app.Replies[AiRole.Strategist] = _ => CycleApp.Reply(Sell);
        await app.RunAsync(user, agent);

        Assert.Equal(["Executed", "AwaitingApproval"], await app.CyclesAsync(agent.Id));
        Assert.Single(book.SentTo(FakeOrderBook.SpotOrder), sent => sent.Side == "SELL");
        Assert.Empty((await app.Approvals.ListAsync(user.Id, Cancellation)).Pending);
        Assert.Equal(0L, await ScalarAsync(app, CycleExecutionTests.Ac01));
    }

    [Fact]
    public async Task With_the_option_off_even_a_sell_waits_for_approval()
    {
        await using var app = new CycleApp(postgres);
        var book = new FakeOrderBook(app.Agents.Binance, app.Clock) { Price = CycleApp.Ask };
        var (user, agent) = await app.ActiveAgentAsync((_, settings) => settings with { AutoExecuteRiskReducing = false });
        await HoldAsync(app, user, agent);

        app.Clock.Advance(TimeSpan.FromHours(1));
        app.Replies[AiRole.Strategist] = _ => CycleApp.Reply(Sell);
        await app.RunAsync(user, agent);

        Assert.Equal(["AwaitingApproval", "AwaitingApproval"], await app.CyclesAsync(agent.Id));
        Assert.DoesNotContain(book.SentTo(FakeOrderBook.SpotOrder), sent => sent.Side == "SELL");
        var sell = await app.PendingAsync(user);
        Assert.Equal((IntentAction.Sell, (decimal?)null), (sell.Proposal.Action, sell.Proposal.MoneyAtRisk));
        Assert.Equal(CycleApp.Ask - 0.02m, sell.Proposal.ReferencePrice);

        var approved = (await app.Approvals.ApproveAsync(user.Id, sell.Id, Cancellation)).Approval!;

        Assert.Equal(ApprovalStatus.Executed, approved.Status);
        Assert.Single(book.SentTo(FakeOrderBook.SpotOrder), sent => sent.Side == "SELL");
        Assert.Equal(0L, await ScalarAsync(app, CycleExecutionTests.Ac01));
    }

    [Fact]
    public async Task Moving_a_target_waits_for_approval_even_when_risk_reducing_actions_go_at_once()
    {
        await using var app = new CycleApp(postgres);
        _ = new FakeOrderBook(app.Agents.Binance, app.Clock) { Price = CycleApp.Ask };
        var (user, agent) = await app.ActiveAgentAsync();
        await HoldAsync(app, user, agent);

        app.Clock.Advance(TimeSpan.FromHours(1));
        app.Replies[AiRole.Strategist] = _ => CycleApp.Reply(AdjustTarget);
        await app.RunAsync(user, agent);

        Assert.Equal(["AwaitingApproval", "AwaitingApproval"], await app.CyclesAsync(agent.Id));
        var pending = await app.PendingAsync(user);
        Assert.Equal((IntentAction.AdjustTarget, (decimal?)60160m), (pending.Proposal.Action, pending.Proposal.TargetPrice));
    }

    [Fact]
    public async Task An_approval_a_crash_left_being_sent_is_interrupted_at_the_next_start()
    {
        await using var app = new CycleApp(postgres);
        var (user, agent) = await app.ActiveAgentAsync();
        await app.RunAsync(user, agent);
        var pending = await app.PendingAsync(user);
        await Sql.ExecuteAsync(app.ConnectionString, $"UPDATE approvals SET status = 'Approving' WHERE id = '{pending.Id}'", Cancellation);

        await using (var restarted = new AppFactory(app.ConnectionString))
        {
            restarted.Start();
        }

        Assert.Equal(ApprovalStatus.Interrupted, (await app.Approvals.FindAsync(user.Id, pending.Id, Cancellation))!.Status);
    }

    [Fact]
    public async Task Another_users_approval_behaves_as_one_that_does_not_exist()
    {
        await using var app = new CycleApp(postgres);
        var book = new FakeOrderBook(app.Agents.Binance, app.Clock) { Price = CycleApp.Ask };
        var (user, agent) = await app.ActiveAgentAsync();
        await app.RunAsync(user, agent);
        var pending = await app.PendingAsync(user);
        var stranger = await app.Agents.UserAsync();

        Assert.Null(await app.Approvals.FindAsync(stranger.Id, pending.Id, Cancellation));
        Assert.Equal(ApprovalOutcome.NotFound, (await app.Approvals.ApproveAsync(stranger.Id, pending.Id, Cancellation)).Outcome);
        Assert.Equal(ApprovalOutcome.NotFound, (await app.Approvals.RejectAsync(stranger.Id, pending.Id, Cancellation)).Outcome);
        Assert.Empty((await app.Approvals.ListAsync(stranger.Id, Cancellation)).Pending);

        Assert.Equal(ApprovalStatus.Pending, (await app.Approvals.FindAsync(user.Id, pending.Id, Cancellation))!.Status);
        Assert.Empty(book.Placed);
        Assert.DoesNotContain(await app.Agents.AuditAsync(stranger.Id), row => row.Action.StartsWith("Trade", StringComparison.Ordinal));
        Assert.DoesNotContain(await app.Agents.AuditAsync(user.Id), row => row.Action.StartsWith("Trade", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Agents:ApprovalWindowMinutes:1H", "0")]
    [InlineData("Agents:ApprovalWindowMinutes:1H", "60")]
    [InlineData("Agents:ApprovalWindowMinutes:15m", "five")]
    [InlineData("Agents:ApprovalWindowMinutes:2H", "5")]
    public async Task An_invalid_approval_window_stops_startup_and_is_named(string key, string value)
    {
        await using var app = new AppFactory(postgres.NewConnectionString(), settings: new Dictionary<string, string?> { [key] = value });

        var exception = Assert.ThrowsAny<Exception>(app.Start);

        Assert.Contains(key, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Each_timeframe_has_its_own_window()
    {
        await using var app = new CycleApp(postgres, new Dictionary<string, string?> { ["Agents:ApprovalWindowMinutes:1H"] = "45" });
        var (user, agent) = await app.ActiveAgentAsync();

        await app.RunAsync(user, agent);

        var approval = await app.PendingAsync(user);
        Assert.Equal(TimeSpan.FromMinutes(45), approval.ExpiresAt - approval.CreatedAt);
    }

    /// <summary>The first cycle's buy, approved, so the agent holds a position for the next.</summary>
    private static async Task HoldAsync(CycleApp app, AgentApp.User user, AgentRecord agent)
    {
        await app.RunAsync(user, agent);
        var buy = await app.PendingAsync(user);

        Assert.Equal(ApprovalStatus.Executed, (await app.Approvals.ApproveAsync(user.Id, buy.Id, Cancellation)).Approval!.Status);
    }

    private static async Task<List<NotificationRecord>> NoticesAsync(CycleApp app, AgentApp.User user, NotificationType type) =>
    [
        .. (await app.Agents.App.Services.GetRequiredService<INotifications>().ListAsync(user.Id, before: null, Cancellation))
            .Where(notice => notice.Type == type),
    ];

    private static Task<long> ScalarAsync(CycleApp app, string sql) => Sql.ScalarAsync<long>(app.ConnectionString, sql, Cancellation);

    /// <summary>A trade of the agent's that closed at <paramref name="closedAt"/> with <paramref name="pnl"/>, as the journal books one.</summary>
    private static Task ClosedAsync(CycleApp app, AgentApp.User user, AgentRecord agent, decimal pnl, DateTimeOffset closedAt) =>
        Sql.ExecuteAsync(
            app.ConnectionString,
            $"""
            INSERT INTO trades (id, user_id, agent_id, entry_id, credential_id, market, environment, symbol, base_asset, quote_asset, side, status,
                opened_at, closed_at, quantity, entry_price, initial_stop, exit_quantity, exit_price, unpriced_quantity, fees, funding, realized_pnl,
                exit_reason, settled_at, settle_attempts, created_at, updated_at)
            VALUES ('{Guid.NewGuid()}', '{user.Id}', '{agent.Id}', '{Guid.NewGuid()}', '{user.ExchangeKey}', 'Spot', 'Demo', 'BTCUSDT', 'BTC', 'USDT',
                'Long', 'Closed', '{closedAt.AddHours(-1):O}', '{closedAt:O}', 0.001, 60000, 59900, 0.001, 59900, 0, 0, 0,
                {pnl.ToString(CultureInfo.InvariantCulture)}, 'StopFilled', '{closedAt:O}', 0, '{closedAt:O}', '{closedAt:O}')
            """,
            Cancellation);
}
