using System.Net;
using Ecnanib.Agents;
using Ecnanib.DecisionEngine;
using Ecnanib.ExchangeGateway;
using Ecnanib.Notifications;
using Ecnanib.RiskGuard;
using Ecnanib.Shared;
using Ecnanib.Tests.Credentials;
using Ecnanib.Tests.ExchangeGateway;
using Ecnanib.Tests.Logging;
using Ecnanib.Tests.RiskGuard;

namespace Ecnanib.Tests.DecisionEngine;

/// <summary>
/// Futures agents trade USDⓈ-margined perpetual contracts and can be long or short (FR-BIN-F1).
/// Futures agents use isolated margin and one-way position mode. The system sets margin type and
/// leverage for the agent's symbol before entry. It does not change account-wide settings; if
/// the account is in a conflicting mode or already holds a position in the symbol, the agent
/// does not start and the user is told what to change (FR-BIN-F2). An entry is skipped, and the
/// user notified, when the Futures wallet lacks the margin the plan needs (FR-BIN-F6). See "The
/// account of Futures" and "The margin of Futures" in docs/orders.md.
/// </summary>
public class FuturesEntryTests
{
    private const string NotEntered = "No entry was placed for agent {AgentId} for the close at {ClosedAt}: {NoEntryReason}";

    private const string MarginPath = "/fapi/v1/marginType";
    private const string LeveragePath = "/fapi/v1/leverage";

    [Fact]
    public async Task Before_an_entry_the_symbol_is_given_isolated_margin_and_the_leverage_that_the_Risk_Guard_computed()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync(market: Market.Futures);

        // The order is worth 1200 with an equity of 1000, which takes a leverage of 2.
        var size = GuardCheck.Of(GuardCases.OpenLongOnFutures).Size!;
        Assert.Equal(2, size.Leverage);

        await application.PlaceAsync(agent, GuardCases.OpenLongOnFutures);

        // The account is read first, and it is the one of the symbol in the environment of the
        // agent. Then the margin of the symbol and its leverage, and only then the entry, with
        // its stop-loss and its take-profit after it.
        Assert.Equal([(TradingMode.Demo, agent.Symbol)], application.Application.Providers.FuturesAccountsAsked);
        Assert.Equal([(Market.Futures, TradingMode.Demo)], application.Application.Providers.BalancesAsked);
        Assert.Equal(
            [MarginPath, LeveragePath, "/fapi/v1/order", "/fapi/v1/algoOrder", "/fapi/v1/algoOrder"],
            application.Binance.Changes);
        Assert.True(application.Binance.Isolated);
        Assert.Equal([size.Leverage], application.Binance.Leverages);
        Assert.Single(await application.PositionsOfAsync(agent.Id));
    }

    [Fact]
    public async Task The_leverage_is_set_anew_for_each_entry_and_a_symbol_that_is_isolated_already_stays_so()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync(market: Market.Futures);

        await application.PlaceAsync(agent, GuardCases.OpenLongOnFutures);
        Assert.Equal(PositionCloseOutcome.Closed, await application.CloseAsync(agent.Id));

        // A stop-loss that is nearer makes for a larger order at the same risk: 0.066, worth
        // 3960, which takes a leverage of 4.
        var larger = GuardCases.OpenLongOnFutures with { StopLoss = 59700m };
        Assert.Equal(4, GuardCheck.Of(larger).Size!.Leverage);

        await application.PlaceAsync(agent, larger);

        // Binance answers the margin that is isolated already with an error of its own, which
        // keeps no entry from being placed. The leverage is the one of each order.
        Assert.Equal([2, 4], application.Binance.Leverages);
        Assert.Equal(2, application.Binance.Changes.Count(path => path == MarginPath));
        Assert.Equal(2, (await application.PositionsOfAsync(agent.Id)).Count);
    }

    [Fact]
    public async Task An_entry_that_the_margin_of_the_Futures_wallet_does_not_cover_is_skipped_and_its_user_is_notified()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync(market: Market.Futures);

        // The order is worth 1200 at a leverage of 2, so its margin is 600. The wallet has
        // 120.5 for a new order: what its other positions and orders hold is not there for it.
        application.Application.Providers.Balances = (_, _) => Holding(new ExchangeBalance("USDT", Total: 2000m, Available: 120.5m));

        await application.PlaceAsync(agent, GuardCases.OpenLongOnFutures);

        // Nothing was sent to Binance: no order, and no setting of the symbol either.
        Assert.Empty(application.Binance.Changes);
        Assert.Empty(await application.OrdersOfAsync(agent.Id));
        Assert.Equal("Margin", Assert.Single(application.Application.Events(), said => said.Template == NotEntered).Text("NoEntryReason"));
        Assert.Equal([(Market.Futures, TradingMode.Demo)], application.Application.Providers.BalancesAsked);

        // Its user is told what the entry needs, and what the wallet has (FR-BIN-F6).
        var notification = Assert.Single(await application.NotificationsOfAsync(agent.UserId));
        Assert.Equal((NotificationType.EntrySkipped, agent.Id), (notification.Type, notification.AgentId));
        Assert.Equal(
            "An entry in BTCUSDT (Demo) was skipped: it needs 600 USDT of margin at a leverage of 2, and the Futures wallet has " +
            "120.5 USDT available. This is said once in a day for an agent.",
            notification.Content);

        // A wallet that has exactly the margin covers the entry.
        application.Application.Providers.Balances = (_, _) => Holding(new ExchangeBalance("USDT", Total: 600m, Available: 600m));
        await application.PlaceAsync(agent, GuardCases.OpenLongOnFutures);

        Assert.Single(await application.PositionsOfAsync(agent.Id));
    }

    [Fact]
    public async Task An_entry_at_a_price_needs_the_margin_of_its_quantity_at_that_price()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync(market: Market.Futures);
        var plan = GuardCases.OpenLongOnFutures with { EntryPrice = 59900m };
        var size = GuardCheck.Of(plan).Size!;
        var margin = size.Quantity * 59900m / size.Leverage;

        // A cent less than the margin of the order at its price.
        application.Application.Providers.Balances = (_, _) => Holding(new ExchangeBalance("USDT", margin, margin - 0.01m));
        await application.PlaceAsync(agent, plan);

        Assert.Empty(application.Binance.Asked);
        Assert.Equal("Margin", Assert.Single(application.Application.Events(), said => said.Template == NotEntered).Text("NoEntryReason"));

        // And the margin itself: the entry is placed, at its price, where it rests.
        application.Application.Providers.Balances = (_, _) => Holding(new ExchangeBalance("USDT", margin, margin));
        await application.PlaceAsync(agent, plan);

        Assert.Equal(("LIMIT", (decimal?)59900m), (Assert.Single(application.Binance.Asked).Type, application.Binance.Asked[0].Limit));
    }

    [Fact]
    public async Task That_entries_are_skipped_for_their_margin_is_said_once_in_a_day_for_an_agent()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync(market: Market.Futures);
        application.Application.Providers.Balances = (_, _) => Holding();

        // A cycle that decides to enter comes with every candle, and each of its entries is skipped.
        for (var hour = 0; hour < 3; hour++)
        {
            await application.PlaceAsync(agent, GuardCases.OpenLongOnFutures);
            application.Clock.Set(application.Clock.GetUtcNow().AddHours(1));
        }

        Assert.Equal(3, application.Application.Events().Count(said => said.Template == NotEntered));
        Assert.Single(await application.NotificationsOfAsync(agent.UserId));
    }

    [Theory]
    [InlineData(ExchangeCallOutcome.Failed, false)]
    [InlineData(ExchangeCallOutcome.TimedOut, false)]
    [InlineData(ExchangeCallOutcome.RateLimited, false)]
    [InlineData(ExchangeCallOutcome.AuthenticationFailed, true)]
    public async Task No_entry_is_placed_while_the_wallet_cannot_be_read(ExchangeCallOutcome outcome, bool rechecksKey)
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync(market: Market.Futures);
        var checks = application.Application.Providers.Asked.Count;

        application.Application.Providers.Balances = (_, _) => new ExchangeBalances(outcome, []);

        await application.PlaceAsync(agent, GuardCases.OpenLongOnFutures);

        // A failure leads to no entry (NFR-REL-2). Nobody is told: it is not known that the
        // wallet is short.
        Assert.Empty(application.Binance.Changes);
        Assert.Empty(await application.OrdersOfAsync(agent.Id));
        Assert.Equal("BalanceUnknown", Assert.Single(application.Application.Events(), said => said.Template == NotEntered).Text("NoEntryReason"));
        Assert.Empty(await application.NotificationsOfAsync(agent.UserId));

        // A key that Binance refuses is checked again, which decides whether it is invalid (FR-KEY-3).
        Assert.Equal(rechecksKey ? checks + 1 : checks, application.Application.Providers.Asked.Count);
    }

    [Fact]
    public async Task An_entry_that_Binance_refuses_has_the_wallet_read_once_more_and_its_user_told_when_it_is_short()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync(market: Market.Futures);

        // The wallet covers the margin when it is read. Another position of the account takes
        // it before the order arrives, and Binance refuses the entry.
        var reads = 0;
        application.Application.Providers.Balances = (_, _) =>
            ++reads == 1 ? Holding(new ExchangeBalance("USDT", 700m, 700m)) : Holding(new ExchangeBalance("USDT", 700m, 40m));
        application.Binance.Fate = _ => OrderFate.Refused;

        await application.PlaceAsync(agent, GuardCases.OpenLongOnFutures);

        Assert.Equal(OrderStatus.Rejected, Assert.Single(await application.OrdersOfAsync(agent.Id)).Status);
        Assert.Equal(2, reads);

        var notification = Assert.Single(await application.NotificationsOfAsync(agent.UserId));
        Assert.Equal(NotificationType.EntrySkipped, notification.Type);
        Assert.StartsWith(
            "An entry in BTCUSDT (Demo) was skipped: it needs 600 USDT of margin at a leverage of 2, and the Futures wallet has 40 USDT available.",
            notification.Content,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, false, false, "your Binance Futures account is in hedge mode, and the agent trades in one-way mode. Switch the position mode of the account to one-way at Binance")]
    [InlineData(false, true, false, "your Binance Futures account is in Multi-Assets mode, in which Binance takes no isolated margin. Switch the asset mode of the account to single-asset at Binance")]
    [InlineData(false, false, true, "your Binance Futures account holds a position in BTCUSDT that the agent did not open. Close it at Binance")]
    public async Task An_account_that_conflicts_gets_no_entry_and_its_user_is_told_what_to_change_and_the_agent_waits_for_them(
        bool hedge, bool multiAssets, bool holds, string toChange)
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync(market: Market.Futures);

        // The account is in a mode that holds for all of it, which the service does not change,
        // or holds a position in the symbol that is not the agent's: the agent holds nothing.
        application.Application.Providers.FuturesAccount = (_, _) => new ExchangeFuturesAccount(ExchangeCallOutcome.Succeeded, hedge, multiAssets, holds);

        await application.PlaceAsync(agent, GuardCases.OpenLongOnFutures);

        // Nothing was sent to Binance, and nothing of the account was changed: neither an
        // order, nor a setting of the symbol. The wallet was not even read.
        Assert.DoesNotContain(application.Binance.System.Calls, call => call.Method != "GET");
        Assert.Empty(await application.OrdersOfAsync(agent.Id));
        Assert.Empty(application.Application.Providers.BalancesAsked);
        Assert.Equal("AccountConflict", Assert.Single(application.Application.Events(), said => said.Template == NotEntered).Text("NoEntryReason"));

        // Its user is told what to change (FR-BIN-F2).
        var notification = Assert.Single(await application.NotificationsOfAsync(agent.UserId));
        Assert.Equal((NotificationType.AttentionRequired, agent.Id), (notification.Type, notification.AgentId));
        Assert.Equal(
            $"An entry in BTCUSDT (Demo) was skipped, and the agent waits for you: {toChange}, and resume the agent.", notification.Content);

        // And the agent waits for them: an exchange setting conflicts (SRS 3.3).
        var waiting = await application.AgentAsync(agent.Id);
        Assert.Equal((AgentState.AttentionRequired, AgentStateCause.ExchangeSettingConflict), (waiting.State, waiting.StateCause));
    }

    [Fact]
    public async Task A_position_that_the_account_still_says_right_after_a_reverse_closed_it_alarms_nobody()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync(market: Market.Futures);
        await application.PlaceAsync(agent, GuardCases.OpenLongOnFutures);

        // The plan reverses: the long position is closed, and Binance still lists it a moment later.
        application.Application.Providers.FuturesAccount = (_, _) =>
            new ExchangeFuturesAccount(ExchangeCallOutcome.Succeeded, HedgeMode: false, MultiAssetsMargin: false, HoldsPosition: true);

        await application.PlaceAsync(agent, GuardCases.Reverse);

        // The position is closed, and no entry is placed on the other side beside what the
        // account may still hold. It may be the agent's own, so nobody is told, and the agent
        // runs on: a position that is still there is found by its next entry.
        var position = Assert.Single(await application.PositionsOfAsync(agent.Id));
        Assert.Equal(ExitReason.TeamClose, position.ExitReason);
        Assert.Equal("AccountUnknown", Assert.Single(application.Application.Events(), said => said.Template == NotEntered).Text("NoEntryReason"));
        Assert.Empty(await application.NotificationsOfAsync(agent.UserId));
        Assert.Equal(AgentState.Running, (await application.AgentAsync(agent.Id)).State);

        // The next plan that opens a position finds it, and then its user is told.
        await application.PlaceAsync(agent, GuardCases.OpenLongOnFutures);

        Assert.Equal(NotificationType.AttentionRequired, Assert.Single(await application.NotificationsOfAsync(agent.UserId)).Type);
        Assert.Equal(AgentState.AttentionRequired, (await application.AgentAsync(agent.Id)).State);
    }

    [Theory]
    [InlineData(ExchangeCallOutcome.Failed, false)]
    [InlineData(ExchangeCallOutcome.TimedOut, false)]
    [InlineData(ExchangeCallOutcome.RateLimited, false)]
    [InlineData(ExchangeCallOutcome.AuthenticationFailed, true)]
    public async Task An_account_that_cannot_be_read_gets_no_entry_and_nobody_is_told(ExchangeCallOutcome outcome, bool rechecksKey)
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync(market: Market.Futures);
        var checks = application.Application.Providers.Asked.Count;

        application.Application.Providers.FuturesAccount = (_, _) => ExchangeFuturesAccount.Without(outcome);

        await application.PlaceAsync(agent, GuardCases.OpenLongOnFutures);

        // It is not known that anything is to change: no entry (NFR-REL-2), and the agent runs on.
        Assert.Empty(application.Binance.Changes);
        Assert.Equal("AccountUnknown", Assert.Single(application.Application.Events(), said => said.Template == NotEntered).Text("NoEntryReason"));
        Assert.Empty(await application.NotificationsOfAsync(agent.UserId));
        Assert.Equal(AgentState.Running, (await application.AgentAsync(agent.Id)).State);
        Assert.Equal(rechecksKey ? checks + 1 : checks, application.Application.Providers.Asked.Count);
    }

    [Fact]
    public async Task An_agent_of_the_Spot_market_is_not_asked_about_an_account_of_Futures_and_sets_no_margin()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();
        application.Application.Providers.FuturesAccount = (_, _) => new ExchangeFuturesAccount(ExchangeCallOutcome.Succeeded, true, true, true);

        await application.PlaceAsync(agent, GuardCases.OpenLong);

        Assert.Empty(application.Application.Providers.FuturesAccountsAsked);
        Assert.DoesNotContain(application.Binance.Changes, path => path is MarginPath or LeveragePath);
        Assert.Single(await application.PositionsOfAsync(agent.Id));
    }

    [Fact]
    public async Task A_margin_that_Binance_refuses_gets_no_entry_and_its_user_is_told()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync(market: Market.Futures);

        // The account has an order of its user's own in the symbol, with which Binance does
        // not change the margin of the symbol.
        application.Binance.Instead = request => request.RequestUri!.AbsolutePath == MarginPath
            ? StandInSystem.Json(HttpStatusCode.BadRequest, """{ "code": -4047, "msg": "Margin type cannot be changed if there exists open orders." }""")
            : null;

        await application.PlaceAsync(agent, GuardCases.OpenLongOnFutures);

        // No leverage is set after it, and no entry is placed.
        Assert.Equal([MarginPath], application.Binance.Changes);
        Assert.Empty(await application.OrdersOfAsync(agent.Id));
        Assert.Equal("MarginRefused", Assert.Single(application.Application.Events(), said => said.Template == NotEntered).Text("NoEntryReason"));

        var notification = Assert.Single(await application.NotificationsOfAsync(agent.UserId));
        Assert.Equal(NotificationType.EntrySkipped, notification.Type);
        Assert.Equal(
            "An entry in BTCUSDT (Demo) was skipped: Binance did not set isolated margin at a leverage of 2 for BTCUSDT. It does not " +
            "change the margin of a symbol while the account has open orders or a position in it: cancel or close them at Binance. " +
            "This is said once in a day for an agent.",
            notification.Content);
        Assert.Equal(AgentState.Running, (await application.AgentAsync(agent.Id)).State);
    }

    [Theory]
    [InlineData(MarginPath, HttpStatusCode.InternalServerError, """{ "code": -1000, "msg": "An unknown error occurred." }""")]
    [InlineData(LeveragePath, HttpStatusCode.ServiceUnavailable, "")]
    [InlineData(LeveragePath, HttpStatusCode.OK, """{ "leverage": 20, "maxNotionalValue": "1000000", "symbol": "BTCUSDT" }""")]
    public async Task No_entry_is_placed_while_it_is_not_known_how_the_symbol_is_margined(string path, HttpStatusCode status, string body)
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync(market: Market.Futures);

        // Binance does not say that the margin is set, or answers with another leverage than
        // the one of the order: the liquidation buffer was checked against that one (FR-RSK-6).
        application.Binance.Instead = request => request.RequestUri!.AbsolutePath == path ? StandInSystem.Json(status, body) : null;

        await application.PlaceAsync(agent, GuardCases.OpenLongOnFutures);

        Assert.Empty(application.Binance.Asked);
        Assert.Empty(await application.OrdersOfAsync(agent.Id));
        Assert.Equal("MarginNotSet", Assert.Single(application.Application.Events(), said => said.Template == NotEntered).Text("NoEntryReason"));
        Assert.Empty(await application.NotificationsOfAsync(agent.UserId));
    }

    [Fact]
    public async Task No_entry_is_placed_on_the_Futures_market_in_a_symbol_that_takes_no_take_profit()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync(market: Market.Futures);

        // The symbol takes no take-profit that is placed at the market: the protection of a
        // position in it could not rest (FR-BIN-F3).
        application.Binance.Instead = request => request.RequestUri!.AbsolutePath == "/fapi/v1/exchangeInfo"
            ? StandInSystem.Json(HttpStatusCode.OK, BinanceCalls.FuturesSymbols.Replace(", \"TAKE_PROFIT_MARKET\"", string.Empty, StringComparison.Ordinal))
            : null;

        await application.PlaceAsync(agent, GuardCases.OpenLongOnFutures);

        Assert.Empty(application.Binance.Changes);
        Assert.Equal("NoTakeProfitOrders", Assert.Single(application.Application.Events(), said => said.Template == NotEntered).Text("NoEntryReason"));
    }

    [Fact]
    public async Task What_stands_in_the_way_of_an_agent_is_said_to_Agents_from_the_account_and_from_what_the_agent_holds()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync(market: Market.Futures);
        var exposure = Assert.Single(application.Application.Exposures);
        var providers = application.Application.Providers;

        // One-way mode, one asset, and nothing in the symbol: nothing stands in the way.
        Assert.Null(await exposure.AccountConflictAsync(agent, TestContext.Current.CancellationToken));

        providers.FuturesAccount = (_, _) => new ExchangeFuturesAccount(ExchangeCallOutcome.Succeeded, HedgeMode: true, MultiAssetsMargin: true, HoldsPosition: true);
        Assert.Equal(AccountConflict.HedgeMode, await exposure.AccountConflictAsync(agent, TestContext.Current.CancellationToken));

        providers.FuturesAccount = (_, _) => new ExchangeFuturesAccount(ExchangeCallOutcome.Succeeded, HedgeMode: false, MultiAssetsMargin: true, HoldsPosition: true);
        Assert.Equal(AccountConflict.MultiAssetsMode, await exposure.AccountConflictAsync(agent, TestContext.Current.CancellationToken));

        providers.FuturesAccount = (_, _) => new ExchangeFuturesAccount(ExchangeCallOutcome.Succeeded, HedgeMode: false, MultiAssetsMargin: false, HoldsPosition: true);
        Assert.Equal(AccountConflict.PositionInSymbol, await exposure.AccountConflictAsync(agent, TestContext.Current.CancellationToken));

        // An account that could not be asked about is not one that nothing stands in the way of.
        providers.FuturesAccount = (_, _) => ExchangeFuturesAccount.Without(ExchangeCallOutcome.TimedOut);
        Assert.Equal(AccountConflict.NotKnown, await exposure.AccountConflictAsync(agent, TestContext.Current.CancellationToken));

        // A position in the symbol is the agent's own once its records hold one: an agent that
        // was paused with its position open is resumed.
        providers.FuturesAccount = (_, _) => StandInProviders.OneWay;
        await application.PlaceAsync(agent, GuardCases.OpenLongOnFutures);

        providers.FuturesAccount = (_, _) => new ExchangeFuturesAccount(ExchangeCallOutcome.Succeeded, HedgeMode: false, MultiAssetsMargin: false, HoldsPosition: true);
        Assert.Null(await exposure.AccountConflictAsync(agent, TestContext.Current.CancellationToken));

        // It only reads: it sent Binance nothing but what the entry took.
        Assert.Equal(5, application.Binance.Changes.Count);
    }

    [Fact]
    public async Task Nothing_of_an_account_stands_in_the_way_of_an_agent_of_the_Spot_market()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();
        application.Application.Providers.FuturesAccount = (_, _) => new ExchangeFuturesAccount(ExchangeCallOutcome.Succeeded, true, true, true);

        Assert.Null(await Assert.Single(application.Application.Exposures).AccountConflictAsync(agent, TestContext.Current.CancellationToken));
        Assert.Empty(application.Application.Providers.FuturesAccountsAsked);
    }

    private static ExchangeBalances Holding(params ExchangeBalance[] balances) => new(ExchangeCallOutcome.Succeeded, balances);
}
