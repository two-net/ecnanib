using System.Net;
using Ecnanib.Kernel;
using Ecnanib.Modules.Execution.Contracts;
using Ecnanib.Tests.Agents;
using Ecnanib.Tests.Exchange;
using Ecnanib.Tests.Persistence;

namespace Ecnanib.Tests.Execution;

/// <summary>
/// What's checked right before an entry is sent: the cycle's deadline (FR-CYC-06), the Futures account and symbol
/// (FR-RISK-09), the protection the pair can take, and that Binance's position is the agent's own. The agent's orders
/// are reconciled first, so an add is sent against the position, and orders a gone Futures position left are cancelled.
/// </summary>
public sealed class EntryGuardTests(PostgresFixture postgres)
{
    [Fact]
    public async Task No_entry_is_sent_once_the_cycles_deadline_has_passed()
    {
        await using var app = new ExecutionApp(postgres);
        var (user, agent) = await app.AgentAsync();

        var result = await app.ExecuteAsync(app.Request(user, agent, ExecutionApp.Long(Market.Spot), deadline: app.Clock.GetUtcNow().AddSeconds(-1)));

        Assert.Equal((ExecutionStatus.NotSent, ExecutionReason.PastDeadline), (result.Status, result.Reason));
        Assert.Empty(app.Book.Placed);
    }

    [Theory]
    [InlineData(true, false, false, Modules.Execution.Contracts.FuturesSetupOutcome.HedgeMode)]
    [InlineData(true, false, true, Modules.Execution.Contracts.FuturesSetupOutcome.MultiAssetsMode)]
    [InlineData(false, false, false, Modules.Execution.Contracts.FuturesSetupOutcome.TradingDisabled)]
    public async Task No_Futures_entry_is_sent_unless_the_account_and_symbol_are_ready(
        bool canTrade, bool _, bool multiAssets, Modules.Execution.Contracts.FuturesSetupOutcome expected)
    {
        await using var app = new ExecutionApp(postgres);
        var (user, agent) = await app.AgentAsync(Market.Futures);
        var hedge = expected == Modules.Execution.Contracts.FuturesSetupOutcome.HedgeMode;
        app.Agents.Binance.On(
            HttpMethod.Get,
            AgentApp.FuturesAccountConfigPath,
            _ => FakeBinance.Json(BinanceSamples.FuturesAccountConfig(canTrade, hedgeMode: hedge, multiAssets: multiAssets)));

        var result = await app.ExecuteAsync(app.Request(user, agent, ExecutionApp.Long(Market.Futures)));

        Assert.Equal((ExecutionStatus.NotSent, ExecutionReason.FuturesSetup, expected), (result.Status, result.Reason, result.FuturesSetup));
        Assert.Empty(app.Book.Placed);
    }

    [Fact]
    public async Task A_Spot_add_is_sent_while_the_agent_holds_a_position_and_the_earlier_part_takes_its_tighter_stop()
    {
        await using var app = new ExecutionApp(postgres);
        var (user, agent) = await app.AgentAsync();
        Assert.Equal(ExecutionStatus.Protected, (await app.ExecuteAsync(app.Request(user, agent, ExecutionApp.Long(Market.Spot)))).Status);

        // The position is in profit, so it may be added to, with one stop for all of it (FR-RISK-06, FR-RISK-07).
        app.Book.Price = 60_100m;
        var add = ExecutionApp.Add(Market.Spot, ExecutionApp.Held(0.01664m), price: 60_100m, stop: 59_850m);
        var second = await app.ExecuteAsync(app.Request(user, agent, add, close: app.LastClose.AddHours(1)));

        Assert.Equal(ExecutionStatus.Protected, second.Status);
        Assert.Equal(2, app.Book.SentTo(FakeOrderBook.SpotOrder).Count(sent => sent.Type == "MARKET"));
        await ExecutionApp.UntilAsync(async () => (await app.PartsAsync(agent.Id)).All(part => part.StartsWith("Resting", StringComparison.Ordinal) && part.Contains(" 59850 ", StringComparison.Ordinal)));
        Assert.All(app.Book.Orders.Where(order => order.Type == "STOP_LOSS" && order.Status == "NEW"), stop => Assert.Equal(59_850m, stop.StopPrice));
        Assert.Equal(2, app.Book.Orders.Count(order => order.Type == "STOP_LOSS" && order.Status == "NEW"));
    }

    [Fact]
    public async Task A_Spot_entry_after_the_stop_filled_is_sized_as_a_new_position()
    {
        await using var app = new ExecutionApp(postgres);
        var (user, agent) = await app.AgentAsync();
        Assert.Equal(ExecutionStatus.Protected, (await app.ExecuteAsync(app.Request(user, agent, ExecutionApp.Long(Market.Spot)))).Status);

        // The stop filled, so the OCO's target expired and nothing is held any more.
        app.Book.Trigger(app.Book.Orders.Single(order => order.Type == "STOP_LOSS").ClientOrderId);
        var second = await app.ExecuteAsync(app.Request(user, agent, ExecutionApp.Long(Market.Spot), close: app.LastClose.AddHours(1)));

        Assert.Equal(ExecutionStatus.Protected, second.Status);
        Assert.Equal(["0 Ended 0.01664", "0 Resting 0.01664"], await app.ProtectionsAsync(agent.Id));
        Assert.Equal("Ended 0.01664 59800 60400 StopFilled", (await app.PartsAsync(agent.Id))[0]);
    }

    [Fact]
    public async Task No_Futures_entry_is_sent_while_the_account_holds_a_position_in_the_pair_that_isnt_the_agents()
    {
        await using var app = new ExecutionApp(postgres);
        var (user, agent) = await app.AgentAsync(Market.Futures);
        app.Book.FuturesPositionOwned = 0.01m;

        var result = await app.ExecuteAsync(app.Request(user, agent, ExecutionApp.Long(Market.Futures)));

        Assert.Equal((ExecutionStatus.NotSent, ExecutionReason.ForeignPosition), (result.Status, result.Reason));
        Assert.Empty(app.Book.Placed);
    }

    [Fact]
    public async Task A_Futures_take_profit_left_by_a_stop_that_filled_is_cancelled_before_the_next_entry()
    {
        await using var app = new ExecutionApp(postgres);
        var (user, agent) = await app.AgentAsync(Market.Futures);
        await app.ExecuteAsync(app.Request(user, agent, ExecutionApp.Long(Market.Futures)));
        var stop = app.Book.Orders.Single(order => order.Type == "STOP_MARKET");
        var target = app.Book.Orders.Single(order => order.Type == "TAKE_PROFIT_MARKET");

        app.Book.Trigger(stop.ClientOrderId);
        var result = await app.ExecuteAsync(app.Request(user, agent, ExecutionApp.Long(Market.Futures), close: app.LastClose.AddHours(1)));

        Assert.Equal(ExecutionStatus.Protected, result.Status);
        Assert.Equal("CANCELED", target.Status);
        Assert.Equal(2, app.Book.SentTo(FakeOrderBook.FuturesOrder).Count);
    }

    [Fact]
    public async Task An_entry_Binance_rejects_on_a_filter_is_recorded_and_the_pairs_filters_are_read_again()
    {
        await using var app = new ExecutionApp(postgres);
        var (user, agent) = await app.AgentAsync();
        app.Clock.Advance(TimeSpan.FromMinutes(2));
        var before = app.Agents.Binance.To("/api/v3/exchangeInfo").Count;
        app.Book.Fail(HttpMethod.Post, FakeOrderBook.SpotOrder, Fault.Rejected(-1013, "Filter failure: LOT_SIZE"));

        var result = await app.ExecuteAsync(app.Request(user, agent, ExecutionApp.Long(Market.Spot)));

        Assert.Equal((ExecutionStatus.Rejected, -1013), (result.Status, result.ExchangeCode));
        Assert.True(app.Agents.Binance.To("/api/v3/exchangeInfo").Count > before);
        Assert.Equal(1L, await app.CountAsync(agent.Id, "status = 'Rejected' AND error_code = -1013 AND resolved"));
    }

    [Fact]
    public async Task An_entry_Binance_is_limiting_is_not_tried_again()
    {
        await using var app = new ExecutionApp(postgres);
        var (user, agent) = await app.AgentAsync(Market.Futures);
        app.Book.Fail(HttpMethod.Post, FakeOrderBook.FuturesOrder, Fault.Throttled());

        var result = await app.ExecuteAsync(app.Request(user, agent, ExecutionApp.Long(Market.Futures)));

        Assert.Equal((ExecutionStatus.NotSent, ExecutionReason.ExchangeUnavailable), (result.Status, result.Reason));
        Assert.Single(app.Book.Placed);
        Assert.Equal(1L, await app.CountAsync(agent.Id, "status = 'NotPlaced' AND resolved"));
    }

    [Fact]
    public async Task The_same_cycles_entry_can_never_be_sent_twice_because_its_client_order_ID_is_taken()
    {
        await using var app = new ExecutionApp(postgres);
        var (user, agent) = await app.AgentAsync();
        var request = app.Request(user, agent, ExecutionApp.Long(Market.Spot));
        app.Book.Fail(HttpMethod.Post, FakeOrderBook.SpotOrder, Fault.Rejected(-2010, "Account has insufficient balance for requested action."));

        Assert.Equal(ExecutionStatus.Rejected, (await app.ExecuteAsync(request)).Status);
        await Assert.ThrowsAnyAsync<Exception>(() => app.ExecuteAsync(request));

        Assert.Single(app.Book.Placed);
    }

    [Fact]
    public async Task Client_order_IDs_are_derived_from_the_agent_cycle_and_leg_and_start_with_the_agents_prefix()
    {
        await using var app = new ExecutionApp(postgres);
        var (user, agent) = await app.AgentAsync();
        var (otherUser, other) = await app.AgentAsync(Market.Futures);

        await app.ExecuteAsync(app.Request(user, agent, ExecutionApp.Long(Market.Spot)));
        await app.ExecuteAsync(app.Request(otherUser, other, ExecutionApp.Long(Market.Futures)));

        var ids = app.Book.Orders.Select(order => order.ClientOrderId).ToList();
        Assert.All(ids, id => Assert.Matches("^[.A-Z:/a-z0-9_-]{1,36}$", id));
        Assert.All(ids, id => Assert.Matches("^[a-zA-Z0-9_-]{1,36}$", id));
        Assert.Equal(ids.Count, ids.Distinct().Count());

        var spot = app.Book.Orders.Where(order => !order.Futures).Select(order => order.ClientOrderId[..23]).Distinct();
        var futures = app.Book.Orders.Where(order => order.Futures).Select(order => order.ClientOrderId[..23]).Distinct();
        Assert.NotEqual(Assert.Single(spot), Assert.Single(futures));
    }
}
