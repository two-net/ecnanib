using System.Globalization;
using Ecnanib.Kernel;
using Ecnanib.Modules.Agents.Contracts;
using Ecnanib.Modules.Execution.Contracts;
using Ecnanib.Tests.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Execution;

/// <summary>
/// FR-EXE-07: the trade journal records each entry and what became of it. A trade is booked from the records the moment
/// its last part ends, with provisional figures, and the journal's sweep settles it from Binance's fills, fees and
/// funding: fees in the quote asset, converted at the fill's minute; funding shared by what each trade held; a close
/// outside Ecnanib priced from the account's own trades; and the excursions from candles.
/// </summary>
public sealed class TradeJournalTests(PostgresFixture postgres)
{
    private const decimal Price = ExecutionApp.Price;

    private static CancellationToken Cancellation => ExecutionApp.Cancellation;

    [Fact]
    public async Task A_Futures_long_stopped_out_closes_at_once_and_settles_from_its_fills_fees_and_candles()
    {
        await using var app = new ExecutionApp(postgres);
        var (user, agent) = await app.AgentAsync(Market.Futures);
        var request = app.Request(user, agent, ExecutionApp.Long(Market.Futures));
        await app.ExecuteAsync(request);
        var placed = app.FuturesLong();
        var q = placed.Quantity;

        Assert.Equal([$"Open - {T(q)} 60000 - 0 - - - - - - Provisional"], await app.TradesAsync(agent.Id));

        app.Book.Trigger(placed.Stop);
        await app.ReconcileAsync();

        // Binance's algo order reports no quantity, so the stop's trigger price stands in until settling.
        var gross = -200m * q;
        Assert.Equal([$"Closed StopFilled {T(q)} 60000 59800 0 - - {T(gross)} -1 - - Provisional"], await app.TradesAsync(agent.Id));
        Assert.Equal([request.CycleId.ToString()], await CyclesAsync(app, agent.Id));

        await app.JournalAsync();

        var fees = (q * 60_000m * FakeOrderBook.FuturesFee) + (q * 59_800m * FakeOrderBook.FuturesFee);
        var realized = gross - fees;
        Assert.Equal(
            [$"Closed StopFilled {T(q)} 60000 59800 0 {T(fees)} 0 {T(realized)} {T(Math.Round(realized / (200m * q), 4))} {T(300m * q)} {T(200m * q)} Settled"],
            await app.TradesAsync(agent.Id));
        Assert.Equal(
            [
                $"Agent Buy 60000 {T(q)} {T(q * 60_000m * FakeOrderBook.FuturesFee)} USDT {T(q * 60_000m * FakeOrderBook.FuturesFee)}",
                $"Agent Sell 59800 {T(q)} {T(q * 59_800m * FakeOrderBook.FuturesFee)} USDT {T(q * 59_800m * FakeOrderBook.FuturesFee)}",
            ],
            await app.FillsAsync(agent.Id));
    }

    [Fact]
    public async Task A_Futures_short_that_reaches_its_target_counts_its_PnL_and_excursions_the_other_way()
    {
        await using var app = new ExecutionApp(postgres);
        var (user, agent) = await app.AgentAsync(Market.Futures);
        await app.ExecuteAsync(app.Request(user, agent, ExecutionApp.Short()));
        var entry = app.Book.Orders.Single(order => order is { Futures: true, Algo: false, Type: "MARKET", Side: "SELL" });
        var q = entry.Quantity;
        await ExecutionApp.UntilAsync(() => Task.FromResult(app.Book.Orders.Any(order => order is { Type: "TAKE_PROFIT_MARKET", Status: "NEW" })));

        app.Book.Trigger(app.Book.Orders.Single(order => order is { Type: "TAKE_PROFIT_MARKET", Status: "NEW" }).ClientOrderId);
        await app.ReconcileAsync();
        await app.JournalAsync();

        var fees = (q * 60_000m * FakeOrderBook.FuturesFee) + (q * 59_600m * FakeOrderBook.FuturesFee);
        var realized = (400m * q) - fees;
        Assert.Equal(
            [$"Closed TargetFilled {T(q)} 60000 59600 0 {T(fees)} 0 {T(realized)} {T(Math.Round(realized / (200m * q), 4))} {T(200m * q)} {T(300m * q)} Settled"],
            await app.TradesAsync(agent.Id));
    }

    [Fact]
    public async Task A_Spot_target_settles_with_the_base_commission_at_the_fill_price_on_what_the_parts_held()
    {
        await using var app = new ExecutionApp(postgres);
        var (user, agent) = await app.AgentAsync();
        await app.ExecuteAsync(app.Request(user, agent, ExecutionApp.Long(Market.Spot)));
        var bought = app.Book.Orders.Single(order => order is { Futures: false, Type: "MARKET", Side: "BUY" }).Executed;
        var target = app.Book.Orders.Single(order => order.Type == "LIMIT_MAKER");
        var held = target.Quantity;

        app.Book.Trigger(target.ClientOrderId);
        await app.ReconcileAsync();
        await app.JournalAsync();

        var fees = (bought * FakeOrderBook.SpotFeeDefault * 60_000m) + (held * 60_400m * FakeOrderBook.SpotFeeDefault);
        var realized = (400m * held) - fees;
        Assert.Equal(
            [$"Closed TargetFilled {T(held)} 60000 60400 0 {T(fees)} 0 {T(realized)} {T(Math.Round(realized / (200m * held), 4))} {T(300m * held)} {T(200m * held)} Settled"],
            await app.TradesAsync(agent.Id));
        Assert.Equal(
            [
                $"Agent Buy 60000 {T(bought)} {T(bought * FakeOrderBook.SpotFeeDefault)} BTC {T(bought * FakeOrderBook.SpotFeeDefault * 60_000m)}",
                $"Agent Sell 60400 {T(held)} {T(held * 60_400m * FakeOrderBook.SpotFeeDefault)} USDT {T(held * 60_400m * FakeOrderBook.SpotFeeDefault)}",
            ],
            await app.FillsAsync(agent.Id));
    }

    [Fact]
    public async Task A_fee_in_BNB_is_converted_at_its_minutes_price_and_one_Binance_cant_price_is_left_unconverted()
    {
        await using var app = new ExecutionApp(postgres);
        var (user, agent) = await app.AgentAsync(Market.Futures);
        app.Book.FeeAsset = "BNB";
        await app.ExecuteAsync(app.Request(user, agent, ExecutionApp.Long(Market.Futures)));
        var placed = app.FuturesLong();
        var q = placed.Quantity;
        app.Book.FeeAsset = "XYZ";
        app.Book.Trigger(placed.Stop);
        await app.ReconcileAsync();

        // Binance can't give BNB's price this time, so the trade waits for the next pass.
        app.Book.Fail(HttpMethod.Get, FakeOrderBook.SpotKlines, Fault.ServerError(accepted: false));
        await app.JournalAsync();
        Assert.EndsWith(" Provisional", Assert.Single(await app.TradesAsync(agent.Id)), StringComparison.Ordinal);

        app.Clock.Advance(TimeSpan.FromMinutes(2));
        await app.JournalAsync();

        var bnb = q * 60_000m * FakeOrderBook.FuturesFee / 600m;
        var xyz = q * 59_800m * FakeOrderBook.FuturesFee / 600m;
        Assert.Equal(
            [$"Agent Buy 60000 {T(q)} {T(bnb)} BNB {T(bnb * 600m)}", $"Agent Sell 59800 {T(q)} {T(xyz)} XYZ -"],
            await app.FillsAsync(agent.Id));
        Assert.Contains($" {T(bnb * 600m)} 0 {T((-200m * q) - (bnb * 600m))} ", Assert.Single(await app.TradesAsync(agent.Id)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_partial_exit_leaves_the_trade_open_until_the_rest_is_stopped_out()
    {
        await using var app = new ExecutionApp(postgres);
        var (user, agent) = await app.AgentAsync(Market.Futures);
        await app.ExecuteAsync(app.Request(user, agent, ExecutionApp.Long(Market.Futures, target: false)));
        var q = app.FuturesLong().Quantity;
        var exit = ExecutionApp.Exit(Market.Futures, ExecutionApp.Held(q, target: null), fraction: 0.5m);
        var sold = exit.Quantity!.Value;
        var rest = q - sold;
        app.Book.Price = 60_100m;

        var result = await app.ExecuteAsync(app.Request(user, agent, exit, close: app.LastClose.AddHours(1)));

        Assert.Equal(ExecutionStatus.Exited, result.Status);
        Assert.StartsWith($"Open - {T(q)} 60000 ", Assert.Single(await app.TradesAsync(agent.Id)), StringComparison.Ordinal);

        await ExecutionApp.UntilAsync(() => Task.FromResult(app.Book.Orders.Any(order => order is { Type: "STOP_MARKET", Status: "NEW" } && order.Quantity == rest)));
        app.Book.Trigger(app.Book.Orders.Single(order => order is { Type: "STOP_MARKET", Status: "NEW" }).ClientOrderId);
        await app.ReconcileAsync();
        await app.JournalAsync();

        var exitPrice = ((60_100m * sold) + (59_800m * rest)) / q;
        var fees = (q * 60_000m * FakeOrderBook.FuturesFee) + (sold * 60_100m * FakeOrderBook.FuturesFee) + (rest * 59_800m * FakeOrderBook.FuturesFee);
        var realized = (100m * sold) - (200m * rest) - fees;

        // The candles are 300 below and 200 above the price, which is now 60100.
        Assert.Equal(
            [$"Closed StopFilled {T(q)} 60000 {T(exitPrice)} 0 {T(fees)} 0 {T(realized)} {T(Math.Round(realized / (200m * q), 4))} {T(200m * q)} {T(300m * q)} Settled"],
            await app.TradesAsync(agent.Id));
        Assert.Equal(3, (await app.FillsAsync(agent.Id)).Count);
    }

    [Fact]
    public async Task An_add_is_a_trade_of_its_own_and_funding_is_shared_by_what_each_held()
    {
        await using var app = new ExecutionApp(postgres);
        var (user, agent) = await app.AgentAsync(Market.Futures);
        await app.ExecuteAsync(app.Request(user, agent, ExecutionApp.Long(Market.Futures, target: false)));
        var first = app.FuturesLong().Quantity;

        app.Clock.Advance(TimeSpan.FromMinutes(1));
        app.Book.Price = 60_100m;
        var add = ExecutionApp.Add(Market.Futures, ExecutionApp.Held(first, target: null), price: 60_100m, stop: 59_900m);
        await app.ExecuteAsync(app.Request(user, agent, add, close: app.LastClose.AddHours(1)));
        var second = add.Quantity!.Value;

        app.Book.Funding.Add((app.Clock.GetUtcNow().AddSeconds(30), -10m, 0.0001m));
        app.Clock.Advance(TimeSpan.FromMinutes(2));
        await ExecutionApp.UntilAsync(() => Task.FromResult(
            app.Book.Orders.Count(order => order is { Type: "STOP_MARKET", Status: "NEW", StopPrice: 59_900m }) == 2
            && !app.Book.Orders.Any(order => order is { Type: "STOP_MARKET", Status: "NEW", StopPrice: 59_800m })));

        foreach (var stop in app.Book.Orders.Where(order => order is { Type: "STOP_MARKET", Status: "NEW" }).ToList())
        {
            app.Book.Trigger(stop.ClientOrderId);
        }

        await app.ReconcileAsync();
        await app.JournalAsync();

        Assert.Equal(
            [T(Share(first)), T(Share(second))],
            await Sql.ColumnAsync(app.ConnectionString, $"SELECT trim_scale(funding)::text FROM trades WHERE agent_id = '{agent.Id}' ORDER BY opened_at", Cancellation));
        Assert.Equal(
            ["0.0001 -10", "0.0001 -10"],
            await Sql.ColumnAsync(
                app.ConnectionString,
                $"SELECT concat_ws(' ', trim_scale(p.rate)::text, trim_scale(p.payment_amount)::text) FROM funding_payments p JOIN trades t ON t.id = p.trade_id WHERE t.agent_id = '{agent.Id}'",
                Cancellation));
        Assert.All(await app.TradesAsync(agent.Id), trade => Assert.StartsWith("Closed StopFilled ", trade, StringComparison.Ordinal));

        decimal Share(decimal quantity) => Math.Round(-10m * quantity / (first + second), 8, MidpointRounding.ToZero);
    }

    [Fact]
    public async Task Parts_closed_outside_Ecnanib_are_priced_from_the_users_trade_each_share_of_it_taken_once()
    {
        await using var app = new ExecutionApp(postgres);
        var (user, agent) = await app.AgentAsync(Market.Futures);
        await app.ExecuteAsync(app.Request(user, agent, ExecutionApp.Long(Market.Futures, target: false)));
        var first = app.FuturesLong().Quantity;
        app.Book.Price = 60_100m;
        var add = ExecutionApp.Add(Market.Futures, ExecutionApp.Held(first, target: null), price: 60_100m, stop: 59_900m);
        await app.ExecuteAsync(app.Request(user, agent, add, close: app.LastClose.AddHours(1)));
        var second = add.Quantity!.Value;

        app.Book.Price = 60_050m;
        app.Book.TradeOutside(futures: true, "SELL", first + second);
        await app.ReconcileAsync();

        Assert.All(await app.TradesAsync(agent.Id), trade => Assert.StartsWith("Closed ClosedOutside ", trade, StringComparison.Ordinal));

        await app.JournalAsync();

        var fee = (first + second) * 60_050m * FakeOrderBook.FuturesFee;
        Assert.Equal(
            [
                $"Outside Sell 60050 {T(first)} {T(Math.Round(fee * first / (first + second), 8, MidpointRounding.ToZero))} USDT",
                $"Outside Sell 60050 {T(second)} {T(Math.Round(fee * second / (first + second), 8, MidpointRounding.ToZero))} USDT",
            ],
            await Sql.ColumnAsync(
                app.ConnectionString,
                $"""
                SELECT concat_ws(' ', f.source, f.side, trim_scale(f.price)::text, trim_scale(f.quantity)::text, trim_scale(f.fee)::text, f.fee_asset)
                FROM fills f JOIN trades t ON t.id = f.trade_id WHERE t.agent_id = '{agent.Id}' AND f.source = 'Outside' ORDER BY t.opened_at
                """,
                Cancellation));
        Assert.All(await app.TradesAsync(agent.Id), trade => Assert.Contains(" 60050 0 ", trade, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_part_gone_with_no_trade_of_the_users_to_show_for_it_is_left_unpriced()
    {
        await using var app = new ExecutionApp(postgres);
        var (user, agent) = await app.AgentAsync(Market.Futures);
        await app.ExecuteAsync(app.Request(user, agent, ExecutionApp.Long(Market.Futures)));
        var q = app.FuturesLong().Quantity;

        app.Book.FuturesPositionOwned = -q;
        await app.ReconcileAsync();
        await app.JournalAsync();

        var fee = q * 60_000m * FakeOrderBook.FuturesFee;
        Assert.Equal([$"Closed ClosedOutside {T(q)} 60000 - {T(q)} {T(fee)} 0 {T(-fee)} - {T(300m * q)} {T(200m * q)} Settled"], await app.TradesAsync(agent.Id));
    }

    [Fact]
    public async Task An_exit_books_its_trade_before_it_returns()
    {
        await using var app = new ExecutionApp(postgres);
        var (user, agent) = await app.AgentAsync(Market.Futures);
        await app.ExecuteAsync(app.Request(user, agent, ExecutionApp.Long(Market.Futures)));
        var q = app.FuturesLong().Quantity;
        app.Book.Price = 60_200m;

        await app.ExecuteAsync(app.Request(user, agent, ExecutionApp.Exit(Market.Futures, ExecutionApp.Held(q)), close: app.LastClose.AddHours(1)));

        Assert.Equal([$"Closed Exited {T(q)} 60000 60200 0 - - {T(200m * q)} 1 - - Provisional"], await app.TradesAsync(agent.Id));
    }

    [Fact]
    public async Task A_key_unusable_for_a_day_settles_the_trade_from_its_orders_with_its_fees_and_funding_unknown()
    {
        await using var app = new ExecutionApp(postgres);
        var (user, agent) = await app.AgentAsync(Market.Futures);
        await app.ExecuteAsync(app.Request(user, agent, ExecutionApp.Long(Market.Futures)));
        var placed = app.FuturesLong();
        app.Book.Trigger(placed.Stop);
        await app.ReconcileAsync();
        await Sql.ExecuteAsync(app.ConnectionString, $"UPDATE credentials SET status = 'Invalid' WHERE id = '{user.ExchangeKey}'", Cancellation);

        await app.JournalAsync();
        Assert.EndsWith(" Provisional", Assert.Single(await app.TradesAsync(agent.Id)), StringComparison.Ordinal);
        Assert.Equal(1, await Sql.ScalarAsync<int>(app.ConnectionString, $"SELECT settle_attempts FROM trades WHERE agent_id = '{agent.Id}'", Cancellation));

        app.Clock.Advance(TimeSpan.FromHours(25));
        await app.JournalAsync();

        var q = placed.Quantity;
        Assert.Equal([$"Closed StopFilled {T(q)} 60000 59800 0 - - {T(-200m * q)} -1 - - Settled"], await app.TradesAsync(agent.Id));
    }

    [Fact]
    public async Task Candles_Binance_cant_give_leave_the_excursions_out_and_the_rest_is_settled()
    {
        await using var app = new ExecutionApp(postgres);
        var (user, agent) = await app.AgentAsync(Market.Futures);
        await app.ExecuteAsync(app.Request(user, agent, ExecutionApp.Long(Market.Futures)));
        app.Book.Trigger(app.FuturesLong().Stop);
        await app.ReconcileAsync();
        app.Book.Fail(HttpMethod.Get, FakeOrderBook.FuturesKlines, Fault.ServerError(accepted: false));

        await app.JournalAsync();

        Assert.EndsWith(" - - Settled", Assert.Single(await app.TradesAsync(agent.Id)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_results_count_entries_Binance_may_have_taken_and_closed_trades_by_the_day_they_closed()
    {
        await using var app = new ExecutionApp(postgres);
        var (user, agent) = await app.AgentAsync(Market.Futures);
        await app.ExecuteAsync(app.Request(user, agent, ExecutionApp.Long(Market.Futures)));
        var q = app.FuturesLong().Quantity;
        app.Book.Trigger(app.FuturesLong().Stop);
        await app.ReconcileAsync();
        app.Book.Fail(HttpMethod.Post, FakeOrderBook.FuturesOrder, Fault.Rejected(-2019, "Margin is insufficient."));
        Assert.Equal(ExecutionStatus.Rejected, (await app.ExecuteAsync(app.Request(user, agent, ExecutionApp.Long(Market.Futures), close: app.LastClose.AddHours(1)))).Status);

        var journal = app.Agents.App.Services.GetRequiredService<ITradeJournal>();
        var now = app.Clock.GetUtcNow();
        var results = await journal.ReadResultsAsync(user.Id, agent.Id, TradingEnvironment.Demo, now, now.AddHours(-1), Cancellation);
        var afterward = await journal.ReadResultsAsync(user.Id, agent.Id, TradingEnvironment.Demo, now.AddDays(8), now.AddDays(8), Cancellation);

        Assert.Equal((-200m * q, -200m * q, 1, 1), (results.RealizedToday, results.RealizedThisWeek, results.ConsecutiveLosses, results.EntriesToday));
        Assert.NotNull(results.LastLossClosedAt);
        Assert.Equal((0m, 0m, 0, 0), (afterward.RealizedToday, afterward.RealizedThisWeek, afterward.ConsecutiveLosses, afterward.EntriesToday));
        Assert.Equal(results.LastLossClosedAt, afterward.LastLossClosedAt);
    }

    /// <summary>
    /// FR-EXP-08: an agent switched to Live today is held to its Live results only, so its Demo losses don't pause it, and
    /// its Demo profits don't loosen its limits.
    /// </summary>
    [Fact]
    public async Task The_results_count_only_what_the_agent_did_in_the_environment_asked_for()
    {
        await using var app = new ExecutionApp(postgres);
        var (user, agent) = await app.AgentAsync(Market.Futures);
        await app.ExecuteAsync(app.Request(user, agent, ExecutionApp.Long(Market.Futures)));
        app.Book.Trigger(app.FuturesLong().Stop);
        await app.ReconcileAsync();

        var journal = app.Agents.App.Services.GetRequiredService<ITradeJournal>();
        var now = app.Clock.GetUtcNow();
        var demo = await journal.ReadResultsAsync(user.Id, agent.Id, TradingEnvironment.Demo, now, now.AddHours(-1), Cancellation);
        var live = await journal.ReadResultsAsync(user.Id, agent.Id, TradingEnvironment.Live, now, now.AddHours(-1), Cancellation);

        Assert.Equal((1, 1), (demo.ConsecutiveLosses, demo.EntriesToday));
        Assert.Equal((0m, 0m, 0, 0, (DateTimeOffset?)null), (live.RealizedToday, live.RealizedThisWeek, live.ConsecutiveLosses, live.EntriesToday, live.LastLossClosedAt));
    }

    [Fact]
    public async Task Deleting_an_agent_deletes_its_journal()
    {
        await using var app = new ExecutionApp(postgres);
        var (user, agent) = await app.AgentAsync(Market.Futures);
        await app.ExecuteAsync(app.Request(user, agent, ExecutionApp.Long(Market.Futures)));
        app.Book.Trigger(app.FuturesLong().Stop);
        await app.ReconcileAsync();
        await app.JournalAsync();

        Assert.Equal(TransitionOutcome.Done, (await app.Agents.Agents.StopAsync(user.Id, agent.Id, Cancellation)).Outcome);
        Assert.Equal(TransitionOutcome.Done, (await app.Agents.Agents.ArchiveAsync(user.Id, agent.Id, Cancellation)).Outcome);
        Assert.Equal(TransitionOutcome.Done, (await app.Agents.Agents.DeleteAsync(user.Id, agent.Id, Cancellation)).Outcome);

        foreach (var table in new[] { "trades", "fills", "trade_cycles", "orders" })
        {
            Assert.Equal(0L, await Sql.ScalarAsync<long>(app.ConnectionString, $"SELECT count(*) FROM {table}", Cancellation));
        }
    }

    private static Task<List<string>> CyclesAsync(ExecutionApp app, Guid agentId) =>
        Sql.ColumnAsync(
            app.ConnectionString,
            $"SELECT c.cycle_id::text FROM trade_cycles c JOIN trades t ON t.id = c.trade_id WHERE t.agent_id = '{agentId}' ORDER BY c.cycle_id",
            Cancellation);

    /// <summary>A number as <c>trim_scale</c> writes it.</summary>
    private static string T(decimal value) => value.ToString("0.############################", CultureInfo.InvariantCulture);
}
