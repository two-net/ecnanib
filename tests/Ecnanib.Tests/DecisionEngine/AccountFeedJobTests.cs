using Ecnanib.DecisionEngine;
using Ecnanib.ExchangeGateway;
using Ecnanib.Platform.BackgroundWork;
using Ecnanib.Shared;
using Ecnanib.Tests.Credentials;
using Ecnanib.Tests.RiskGuard;

namespace Ecnanib.Tests.DecisionEngine;

/// <summary>
/// Order and position changes are tracked through the exchange's account event feed where
/// available (FR-BIN-7): a job has the gateway follow the accounts in which an agent holds
/// something, and reconciles an agent at once when the feed says that something changed in its
/// account. An event is a signal: what the records say is what the exchange answers when it is
/// asked. See "The account event feed" in docs/orders.md, and docs/background-work.md.
/// </summary>
public class AccountFeedJobTests
{
    private const string Area = "Ecnanib.DecisionEngine.AccountFeedJob";

    [Fact]
    public async Task The_feed_is_followed_every_minute_and_as_the_work_starts_when_there_is_an_account_to_follow()
    {
        var feed = new StandInFeed();
        using var application = new OrdersApplication(feed: feed);
        var job = application.Application.AccountFeed;
        var atStart = Assert.IsAssignableFrom<IDueAtStart>(job);
        var started = OrdersApplication.Start;

        Assert.Equal("account-feed", job.Name);
        Assert.Equal("every 00:01:00", job.Schedule.ToString());
        Assert.Equal(started.AddMinutes(1), job.Schedule.NextRun(started, started));

        // An account in which no agent holds anything is not followed: nothing in it could
        // differ from the records.
        var agent = await application.GiveAgentAsync();
        Assert.False(await atStart.IsDueAtStartAsync(TestContext.Current.CancellationToken));

        await application.PlaceAsync(agent, GuardCases.OpenLong);
        Assert.True(await atStart.IsDueAtStartAsync(TestContext.Current.CancellationToken));

        // Where the exchange has no feed there is nothing to follow.
        feed.Available.Clear();
        Assert.False(await atStart.IsDueAtStartAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_run_follows_the_accounts_in_which_an_agent_holds_something_with_the_key_of_their_user()
    {
        var feed = new StandInFeed();
        using var application = new OrdersApplication(feed: feed);
        var ada = await application.GiveAgentAsync("ada@example.com", Market.Futures);
        var grace = await application.GiveAgentAsync("grace@example.com");
        await application.GiveAgentAsync("lin@example.com");
        await application.PlaceAsync(ada, GuardCases.OpenLongOnFutures);
        await application.PlaceAsync(grace, GuardCases.OpenLong);

        await application.FollowFeedAsync();

        // Each account is followed with the credential of its own user for the environment of
        // the agent, which is handed over for that call. The account of a user whose agent
        // holds nothing is not followed.
        Assert.Equal(
            new[]
            {
                new AskedFeed(ada.UserId, Market.Futures, TradingMode.Demo, Keys.Of(ada.UserId.ToString("N"), "demo"), Keys.Of(ada.UserId.ToString("N"), "seal")),
                new AskedFeed(grace.UserId, Market.Spot, TradingMode.Demo, Keys.Of(grace.UserId.ToString("N"), "demo"), Keys.Of(grace.UserId.ToString("N"), "seal")),
            }.OrderBy(asked => asked.UserId),
            feed.Followed.OrderBy(asked => asked.UserId));

        // The next run says that both are still wanted, and takes no key for an account that is followed.
        await application.FollowFeedAsync();

        Assert.Equal(2, feed.Followed.Count);
        Assert.Equal(2, feed.Kept.Count(kept => kept == (ada.UserId, Market.Futures, TradingMode.Demo)));
        Assert.Equal(2, feed.Kept.Count(kept => kept == (grace.UserId, Market.Spot, TradingMode.Demo)));

        // A feed whose renewal is due, and one that has ended, take the key again.
        feed.Stands(ada.UserId, Market.Futures, ExchangeFeedState.RenewalDue);
        feed.Stands(grace.UserId, Market.Spot, ExchangeFeedState.NotFollowed);

        await application.FollowFeedAsync();

        Assert.Equal(4, feed.Followed.Count);
    }

    [Fact]
    public async Task A_change_reconciles_the_agent_that_holds_something_in_that_symbol_at_once()
    {
        var feed = new StandInFeed();
        using var application = new OrdersApplication(feed: feed);
        var ada = await application.GiveAgentAsync("ada@example.com", Market.Futures);
        var grace = await application.GiveAgentAsync("grace@example.com");
        await application.PlaceAsync(ada, GuardCases.OpenLongOnFutures);
        await application.PlaceAsync(grace, GuardCases.OpenLong);
        application.Clock.Set(application.Clock.GetUtcNow().AddMinutes(1));
        var asked = application.Binance.AskedAbout.Count;

        // Ada closes her position by hand, at Binance, and the feed of her account says that
        // something changed in the symbol: of an order, and of what the account holds.
        application.Binance.HoldByHand(Market.Futures, 0m);
        feed.Say(ada.UserId, Market.Futures, "BTCUSDT");
        feed.Say(ada.UserId, Market.Futures, "BTCUSDT");

        await application.FollowFeedAsync();

        // Her agent was reconciled in that run, and not at the next time between
        // reconciliations: the exchange was asked, and the records say what it answered.
        Assert.Equal(ExitReason.ClosedAtExchange, Assert.Single(await application.PositionsOfAsync(ada.Id)).ExitReason);

        // Nothing of an event was written: the orders of her agent were asked about, and the
        // account, as any reconciliation asks. The agent of Grace was asked nothing.
        var hers = (await application.OrdersOfAsync(ada.Id)).Select(order => order.ClientOrderId).ToList();
        Assert.All(application.Binance.AskedAbout.Skip(asked), identifier => Assert.Contains(identifier, hers));
        Assert.NotEmpty(application.Application.Providers.FuturesPositionsAsked);
        Assert.Null(Assert.Single(await application.PositionsOfAsync(grace.Id)).ClosedAt);

        var said = Assert.Single(application.Application.Events(), written => written.Area == Area);
        Assert.Equal((1, 1), (said["Changes"].GetInt32(), said["Reconciliations"].GetInt32()));
    }

    [Fact]
    public async Task A_change_of_everything_in_an_account_reconciles_every_agent_that_holds_something_there()
    {
        var feed = new StandInFeed();
        using var application = new OrdersApplication(feed: feed);
        var agent = await application.GiveAgentAsync(market: Market.Futures);
        await application.PlaceAsync(agent, GuardCases.OpenLongOnFutures);
        application.Clock.Set(application.Clock.GetUtcNow().AddMinutes(1));

        // The feed of the account ended, or began: what happened meanwhile was not heard.
        application.Binance.HoldByHand(Market.Futures, 0.005m);
        feed.Say(agent.UserId, Market.Futures, symbol: null);

        await application.FollowFeedAsync();

        Assert.Equal(0.005m, Assert.Single(await application.PositionsOfAsync(agent.Id)).Quantity);
    }

    [Fact]
    public async Task A_change_in_another_symbol_or_in_another_account_reconciles_nobody()
    {
        var feed = new StandInFeed();
        using var application = new OrdersApplication(feed: feed);
        var agent = await application.GiveAgentAsync(market: Market.Futures);
        await application.PlaceAsync(agent, GuardCases.OpenLongOnFutures);
        application.Clock.Set(application.Clock.GetUtcNow().AddMinutes(1));
        var calls = application.Binance.System.Calls.Count;

        // Its user trades another symbol by hand, and on the other market, and another user trades this one.
        feed.Say(agent.UserId, Market.Futures, "SOLUSDT");
        feed.Say(agent.UserId, Market.Spot, "BTCUSDT");
        feed.Say(Guid.NewGuid(), Market.Futures, "BTCUSDT");

        await application.FollowFeedAsync();

        Assert.Equal(calls, application.Binance.System.Calls.Count);
        Assert.Empty(application.Application.Providers.FuturesPositionsAsked);
    }

    [Fact]
    public async Task A_run_listens_for_most_of_its_minute_and_one_with_nothing_to_follow_is_over_at_once()
    {
        var feed = new StandInFeed();
        using var application = new OrdersApplication(feed: feed);
        var agent = await application.GiveAgentAsync();

        // Nothing to follow: the run ends without a moment of the clock passing, and asks for
        // what the feeds said once, without waiting.
        await application.Application.FollowFeedAsync();

        Assert.Empty(feed.Followed);
        Assert.Equal(1, feed.Taken);

        // With an account to follow it listens until its time is up, and takes what the feeds
        // say again and again meanwhile.
        await application.PlaceAsync(agent, GuardCases.OpenLong);
        var began = application.Clock.GetUtcNow();

        await application.FollowFeedAsync();

        Assert.True(application.Clock.GetUtcNow() - began >= TimeSpan.FromSeconds(40));
        Assert.True(application.Clock.GetUtcNow() - began < TimeSpan.FromSeconds(60));
        Assert.True(feed.Taken > 2);
    }

    [Fact]
    public async Task A_feed_that_is_slow_to_open_keeps_nothing_that_another_said_from_being_taken()
    {
        var feed = new StandInFeed();
        using var application = new OrdersApplication(feed: feed);
        var agent = await application.GiveAgentAsync(market: Market.Futures);
        await application.PlaceAsync(agent, GuardCases.OpenLongOnFutures);
        application.Clock.Set(application.Clock.GetUtcNow().AddMinutes(1));

        // Following the account takes longer than a run listens for, as when the address of a
        // feed does not answer.
        feed.Outcome = _ =>
        {
            application.Clock.Set(application.Clock.GetUtcNow().AddSeconds(45));

            return ExchangeFeedOutcome.Following;
        };

        application.Binance.HoldByHand(Market.Futures, 0m);
        feed.Say(agent.UserId, Market.Futures, "BTCUSDT");

        await application.FollowFeedAsync();

        // What the feeds said was taken all the same, once, and its agent reconciled.
        Assert.Equal(1, feed.Taken);
        Assert.Equal(ExitReason.ClosedAtExchange, Assert.Single(await application.PositionsOfAsync(agent.Id)).ExitReason);
    }

    [Fact]
    public async Task An_account_that_cannot_be_followed_keeps_no_other_waiting()
    {
        var feed = new StandInFeed();
        using var application = new OrdersApplication(feed: feed);
        var ada = await application.GiveAgentAsync("ada@example.com", Market.Futures);
        var grace = await application.GiveAgentAsync("grace@example.com");
        await application.PlaceAsync(ada, GuardCases.OpenLongOnFutures);
        await application.PlaceAsync(grace, GuardCases.OpenLong);

        // Following one account ends with an error of the application.
        feed.Outcome = asked => asked.UserId == ada.UserId ? throw new InvalidOperationException("Something of the test's went wrong.") : ExchangeFeedOutcome.Following;

        await application.FollowFeedAsync();

        // The other account is followed, and the error is said once, with the account.
        Assert.Equal(ExchangeFeedState.Followed, feed.StateOf(grace.UserId, Market.Spot, TradingMode.Demo));
        Assert.Equal(ExchangeFeedState.NotFollowed, feed.StateOf(ada.UserId, Market.Futures, TradingMode.Demo));

        var said = Assert.Single(
            application.Application.Events(), written => written.Area == "Ecnanib.DecisionEngine.AccountFeed" && written.Level == "Error");
        Assert.Equal(ada.UserId.ToString(), said.Text("UserId"));
    }

    [Fact]
    public async Task Where_the_exchange_has_no_feed_nothing_is_followed()
    {
        var feed = new StandInFeed();
        feed.Available.Remove((Market.Spot, TradingMode.Demo));
        using var application = new OrdersApplication(feed: feed);
        var agent = await application.GiveAgentAsync();
        await application.PlaceAsync(agent, GuardCases.OpenLong);

        await application.Application.FollowFeedAsync();

        // The account is reconciled on the schedule alone.
        Assert.Empty(feed.Followed);
        Assert.Empty(feed.Kept);
    }

    [Fact]
    public async Task A_credential_that_the_exchange_refuses_is_checked_again()
    {
        var feed = new StandInFeed { Outcome = _ => ExchangeFeedOutcome.KeyRefused };
        using var application = new OrdersApplication(feed: feed);
        var agent = await application.GiveAgentAsync();
        await application.PlaceAsync(agent, GuardCases.OpenLong);
        var checks = application.Application.Providers.Asked.Count;

        await application.Application.FollowFeedAsync();

        // The check decides whether the key is invalid (FR-KEY-3). Nothing is followed, so the run is over.
        Assert.Equal(checks + 1, application.Application.Providers.Asked.Count);
        Assert.Equal(TradingMode.Demo.ToString(), application.Application.Providers.Asked[^1].About);
    }

    [Fact]
    public async Task When_the_gateway_opens_no_more_feeds_for_now_the_rest_wait_for_the_next_run()
    {
        var feed = new StandInFeed { Outcome = _ => ExchangeFeedOutcome.NotNow };
        using var application = new OrdersApplication(feed: feed);

        foreach (var email in new[] { "ada@example.com", "grace@example.com", "lin@example.com" })
        {
            var agent = await application.GiveAgentAsync(email);
            await application.PlaceAsync(agent, GuardCases.OpenLong);
        }

        await application.Application.FollowFeedAsync();

        // The limit on requests has no room, or enough feeds were opened in this minute: no key
        // is handed over for an account that would not be followed with it now.
        Assert.Single(feed.Followed);

        feed.Outcome = null;
        await application.FollowFeedAsync();

        Assert.Equal(4, feed.Followed.Count);
    }
}
