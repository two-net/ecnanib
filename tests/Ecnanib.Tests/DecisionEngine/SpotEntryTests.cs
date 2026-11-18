using Ecnanib.DecisionEngine;
using Ecnanib.ExchangeGateway;
using Ecnanib.Notifications;
using Ecnanib.Shared;
using Ecnanib.Tests.Credentials;
using Ecnanib.Tests.ExchangeGateway;
using Ecnanib.Tests.Logging;
using Ecnanib.Tests.RiskGuard;

namespace Ecnanib.Tests.DecisionEngine;

/// <summary>
/// Spot agents are long-only: they buy the base asset with the quote asset and sell to exit. They
/// do not borrow or short (FR-BIN-S1). If the quote balance is below what the plan needs, the
/// entry is skipped and the user is notified (FR-BIN-S3). See "When no entry is placed" in
/// docs/orders.md.
/// </summary>
public class SpotEntryTests
{
    private const string NotEntered = "No entry was placed for agent {AgentId} for the close at {ClosedAt}: {NoEntryReason}";

    [Fact]
    public async Task An_agent_of_the_Spot_market_places_no_order_that_sells_to_open()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();

        // A plan of the Strategist that shorts on Spot is refused when it is read. What places
        // the orders holds to it as well: a sale that opens would sell what else the account holds.
        await application.PlaceAsync(agent, GuardCases.OpenShort with { Market = Market.Spot });

        Assert.Empty(application.Binance.Asked);
        Assert.Empty(await application.OrdersOfAsync(agent.Id));
        Assert.Equal("SpotLongOnly", Assert.Single(application.Application.Events(), said => said.Template == NotEntered).Text("NoEntryReason"));
    }

    [Fact]
    public async Task An_entry_that_the_quote_balance_does_not_cover_is_skipped_and_its_user_is_notified()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();

        // The plan buys 0.01 at 60000, which takes 600 USDT. The account holds more than that,
        // of which 120.5 is free: the rest is held by orders that the service does not know.
        application.Application.Providers.Balances = (_, _) => Holding(new ExchangeBalance("USDT", Total: 2000m, Available: 120.5m));

        await application.PlaceAsync(agent, GuardCases.OpenLong);

        // Nothing is sent, and no row is written: the entry is skipped.
        Assert.Empty(application.Binance.Asked);
        Assert.Empty(await application.OrdersOfAsync(agent.Id));
        Assert.Equal("QuoteBalance", Assert.Single(application.Application.Events(), said => said.Template == NotEntered).Text("NoEntryReason"));

        // The account of the agent's market and mode was read, once.
        Assert.Equal([(Market.Spot, TradingMode.Demo)], application.Application.Providers.BalancesAsked);

        // Its user is told, about the agent, with what the entry needs and what the account has.
        var notification = Assert.Single(await application.NotificationsOfAsync(agent.UserId));
        Assert.Equal((NotificationType.EntrySkipped, agent.Id, false), (notification.Type, notification.AgentId, notification.IsRead));
        Assert.Equal(
            "An entry in BTCUSDT (Demo) was skipped: it needs 600 USDT, and the account has 120.5 USDT free. " +
            "This is said once in a day for an agent.",
            notification.Content);

        // The agent runs on: a plan of a later cycle is placed once the account covers it.
        application.Application.Providers.Balances = (_, _) => Holding(new ExchangeBalance("USDT", Total: 600m, Available: 600m));
        await application.PlaceAsync(agent, GuardCases.OpenLong);

        Assert.Single(await application.PositionsOfAsync(agent.Id));
    }

    [Fact]
    public async Task An_account_that_holds_none_of_the_quote_asset_covers_no_entry()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();

        // Binance leaves out an asset that the account holds none of.
        application.Application.Providers.Balances = (_, _) => Holding(new ExchangeBalance("BTC", Total: 3m, Available: 3m));

        await application.PlaceAsync(agent, GuardCases.OpenLong);

        Assert.Empty(application.Binance.Asked);
        Assert.Equal(
            "An entry in BTCUSDT (Demo) was skipped: it needs 600 USDT, and the account has 0 USDT free. " +
            "This is said once in a day for an agent.",
            Assert.Single(await application.NotificationsOfAsync(agent.UserId)).Content);
    }

    [Fact]
    public async Task An_entry_at_a_price_needs_its_quantity_at_that_price()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();

        // 0.011 at 59900 takes 658.9 USDT, which is more than at the market the plan would take.
        application.Application.Providers.Balances = (_, _) => Holding(new ExchangeBalance("USDT", Total: 650m, Available: 650m));

        await application.PlaceAsync(agent, GuardCases.OpenLong with { EntryPrice = 59900m });

        Assert.Empty(application.Binance.Asked);
        Assert.StartsWith(
            "An entry in BTCUSDT (Demo) was skipped: it needs 658.9 USDT, and the account has 650 USDT free.",
            Assert.Single(await application.NotificationsOfAsync(agent.UserId)).Content,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task That_entries_are_skipped_is_said_once_in_a_day_for_an_agent()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();
        application.Application.Providers.Balances = (_, _) => Holding();

        // A cycle that decides to enter comes with every candle, and each of its entries is skipped.
        for (var hour = 0; hour < 3; hour++)
        {
            await application.PlaceAsync(agent, GuardCases.OpenLong);
            application.Clock.Set(application.Clock.GetUtcNow().AddHours(1));
        }

        Assert.Equal(3, application.Application.Events().Count(said => said.Template == NotEntered));
        Assert.Single(await application.NotificationsOfAsync(agent.UserId));

        // A day after it was said, it is said again.
        application.Clock.Set(OrdersApplication.Start.AddDays(1).AddMinutes(1));
        await application.PlaceAsync(agent, GuardCases.OpenLong);

        Assert.Equal(2, (await application.NotificationsOfAsync(agent.UserId)).Count);
    }

    [Theory]
    [InlineData(ExchangeCallOutcome.Failed, false)]
    [InlineData(ExchangeCallOutcome.TimedOut, false)]
    [InlineData(ExchangeCallOutcome.RateLimited, false)]
    [InlineData(ExchangeCallOutcome.AuthenticationFailed, true)]
    public async Task No_entry_is_placed_while_the_account_cannot_be_read(ExchangeCallOutcome outcome, bool rechecksKey)
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();
        var checks = application.Application.Providers.Asked.Count;

        application.Application.Providers.Balances = (_, _) => new ExchangeBalances(outcome, []);

        await application.PlaceAsync(agent, GuardCases.OpenLong);

        // A failure leads to no entry (NFR-REL-2). Nobody is told: it is not known that the
        // account is short.
        Assert.Empty(application.Binance.Asked);
        Assert.Empty(await application.OrdersOfAsync(agent.Id));
        Assert.Equal("BalanceUnknown", Assert.Single(application.Application.Events(), said => said.Template == NotEntered).Text("NoEntryReason"));
        Assert.Empty(await application.NotificationsOfAsync(agent.UserId));

        // A key that Binance refuses is checked again, which decides whether it is invalid (FR-KEY-3).
        Assert.Equal(rechecksKey ? checks + 1 : checks, application.Application.Providers.Asked.Count);
    }

    [Fact]
    public async Task An_agent_that_no_longer_opens_positions_is_not_told_that_its_account_is_short()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync(state: Ecnanib.Agents.AgentState.Paused);
        application.Application.Providers.Balances = (_, _) => Holding();

        await application.PlaceAsync(agent, GuardCases.OpenLong);

        // Why no entry is placed is that the agent is paused: the account is not even read.
        Assert.Equal("NotRunning", Assert.Single(application.Application.Events(), said => said.Template == NotEntered).Text("NoEntryReason"));
        Assert.Empty(application.Application.Providers.BalancesAsked);
        Assert.Empty(await application.NotificationsOfAsync(agent.UserId));
    }

    [Fact]
    public async Task An_entry_that_Binance_refuses_has_the_account_read_once_more_and_its_user_told_when_it_is_short()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();

        // The account covers the entry when it is read. Another agent of the same account
        // spends it before the order arrives, and Binance refuses the entry.
        var reads = 0;
        application.Application.Providers.Balances = (_, _) =>
            ++reads == 1 ? Holding(new ExchangeBalance("USDT", 700m, 700m)) : Holding(new ExchangeBalance("USDT", 700m, 40m));
        application.Binance.Fate = _ => OrderFate.Refused;

        await application.PlaceAsync(agent, GuardCases.OpenLong);

        Assert.Equal(OrderStatus.Rejected, Assert.Single(await application.OrdersOfAsync(agent.Id)).Status);
        Assert.Equal(2, reads);

        var notification = Assert.Single(await application.NotificationsOfAsync(agent.UserId));
        Assert.Equal(NotificationType.EntrySkipped, notification.Type);
        Assert.StartsWith(
            "An entry in BTCUSDT (Demo) was skipped: it needs 600 USDT, and the account has 40 USDT free.", notification.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_entry_on_the_Futures_market_is_not_held_to_the_balance_of_the_Spot_account()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync(market: Market.Futures);

        // The account of Spot holds nothing. The entry is held to the margin that the wallet
        // of Futures has for it, which is another check (FR-BIN-F6): see FuturesEntryTests.
        application.Application.Providers.Balances = (market, _) => market == Market.Spot ? Holding() : StandInProviders.Funded;

        await application.PlaceAsync(agent, GuardCases.OpenLongOnFutures);

        Assert.Equal([(Market.Futures, TradingMode.Demo)], application.Application.Providers.BalancesAsked);
        Assert.Single(await application.PositionsOfAsync(agent.Id));
    }

    [Fact]
    public async Task No_entry_is_placed_on_the_Spot_market_in_a_symbol_that_takes_no_pair()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();

        // The symbol takes a stop-loss, and no pair of a stop-loss and a take-profit: the
        // protection of a position in it could not rest as the specification has it (FR-BIN-S2).
        application.Binance.Instead = request => request.RequestUri!.AbsolutePath == "/api/v3/exchangeInfo"
            ? StandInSystem.Json(
                System.Net.HttpStatusCode.OK,
                BinanceCalls.SpotSymbols.Replace("\"ocoAllowed\": true, \"otoAllowed\": true", "\"ocoAllowed\": false", StringComparison.Ordinal))
            : null;

        await application.PlaceAsync(agent, GuardCases.OpenLong);

        Assert.Empty(application.Binance.Asked);
        Assert.Equal("NoPairOrders", Assert.Single(application.Application.Events(), said => said.Template == NotEntered).Text("NoEntryReason"));
    }

    private static ExchangeBalances Holding(params ExchangeBalance[] balances) => new(ExchangeCallOutcome.Succeeded, balances);
}
