using System.Security.Claims;
using Ecnanib.Accounts;
using Ecnanib.DecisionEngine;
using Ecnanib.ExchangeGateway;
using Ecnanib.Shared;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.Agents;
using Ecnanib.Tests.Experience;
using Ecnanib.Tests.Logging;
using Ecnanib.WebUi.Live;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Tests.LiveUpdates;

/// <summary>
/// Agent status, positions, prices, and cycle progress update in the UI within 2 seconds,
/// without a manual refresh (FR-NTF-2), and a user receives updates only for their own agents.
/// The feed of a page follows the signals of the user of its connection: it writes the parts of
/// the page that tick, has the page ask for itself again when something that it shows changed,
/// and stops when the session of the connection has ended. See docs/live-updates.md.
/// </summary>
/// <remarks>
/// The page is a stand-in, which keeps what its feed asked of it: no browser is needed to see
/// what a feed does. The clock is the test's, with the timers that run on it. A test waits for
/// a feed to have taken a signal by raising one more, of a step of a cycle, which the feed
/// answers by writing its parts: a feed does one thing after the other.
/// </remarks>
public class LiveFeedTests
{
    private const string Ada = "ada@example.com";
    private const string Grace = "grace@example.com";

    private static readonly LivePrice Bitcoin = new(Market.Spot, TradingMode.Demo, "BTCUSDT", ExchangePriceKind.LastTraded);
    private static readonly LivePrice BitcoinMark = new(Market.Futures, TradingMode.Demo, "BTCUSDT", ExchangePriceKind.Mark);

    [Fact]
    public async Task When_something_that_a_page_follows_changes_the_page_asks_for_itself_again_a_moment_later()
    {
        using var feeding = new Feeding();
        var ada = await feeding.SignedInAsync(Ada);
        var agent = Guid.NewGuid();
        var page = new StandInPage("/agents");

        // Two minutes after its user last did anything.
        feeding.Clock.Advance(TimeSpan.FromMinutes(2));

        await using var feed = feeding.Open(ada.Connection, page, agent, [LiveSubject.Agent, LiveSubject.Holdings], LiveShows.Cycle);
        feed.Follow();

        feeding.Signals.Raise(ada.User.Id, agent, LiveSubject.Agent);
        await TakenAsync(feeding, ada.User, agent, page);

        // Not at once: what else changes in that moment is on the same page.
        Assert.Equal(0, page.Asked);

        feeding.Clock.Advance(LiveFeed.AsksAfter - TimeSpan.FromMilliseconds(1));
        await TakenAsync(feeding, ada.User, agent, page);
        Assert.Equal(0, page.Asked);

        feeding.Clock.Advance(TimeSpan.FromMilliseconds(1));
        await Wait.UntilAsync(() => page.Asked == 1, "the page to ask for itself again");

        // Well within the two seconds, and with nothing that its user did.
        Assert.True(LiveFeed.AsksAfter < TimeSpan.FromSeconds(1));
        Assert.Equal(0, page.Left);
        Assert.Null(page.Failed);

        // The request that the page makes then is its own, and no activity of its user: the
        // feed said so to Accounts. The same request once more is one that nobody announced.
        await ada.Browser.AskAgainAsync(page.Address);
        Assert.Equal(Feeding.Morning, Assert.Single(StoredSession.All(feeding.Application.Database)).LastActiveAt);

        await ada.Browser.AskAgainAsync(page.Address);
        Assert.Equal(feeding.Clock.GetUtcNow(), Assert.Single(StoredSession.All(feeding.Application.Database)).LastActiveAt);
    }

    [Fact]
    public async Task A_burst_of_changes_is_one_request_and_a_page_asks_at_most_once_a_second()
    {
        using var feeding = new Feeding();
        var ada = await feeding.SignedInAsync(Ada);
        var agent = Guid.NewGuid();
        var page = new StandInPage();

        await using var feed = feeding.Open(ada.Connection, page, agent, [LiveSubject.Agent, LiveSubject.Holdings], LiveShows.Cycle);
        feed.Follow();

        // An order is executed: its row, its position, and the state of its agent change at once.
        for (var change = 0; change < 5; change++)
        {
            feeding.Signals.Raise(ada.User.Id, agent, change % 2 == 0 ? LiveSubject.Holdings : LiveSubject.Agent);
        }

        await TakenAsync(feeding, ada.User, agent, page);
        feeding.Clock.Advance(LiveFeed.AsksAfter);
        await Wait.UntilAsync(() => page.Asked == 1, "the page to ask for itself again, once");

        // What changes right after is not asked for at once: a page asks at most once a second.
        feeding.Signals.Raise(ada.User.Id, agent, LiveSubject.Holdings);
        feeding.Signals.Raise(ada.User.Id, agent, LiveSubject.Holdings);
        await TakenAsync(feeding, ada.User, agent, page);

        feeding.Clock.Advance(LiveFeed.AsksAtMostEvery - TimeSpan.FromMilliseconds(1));
        await TakenAsync(feeding, ada.User, agent, page);
        Assert.Equal(1, page.Asked);

        feeding.Clock.Advance(TimeSpan.FromMilliseconds(1));
        await Wait.UntilAsync(() => page.Asked == 2, "the page to ask for itself again, a second after it last did");

        // So a change shows within two seconds, however many came before it.
        Assert.True(LiveFeed.AsksAtMostEvery + LiveFeed.AsksAfter < TimeSpan.FromSeconds(2));

        // With nothing more to show, it asks for nothing more.
        feeding.Clock.Advance(TimeSpan.FromSeconds(30));
        await TakenAsync(feeding, ada.User, agent, page);
        Assert.Equal(2, page.Asked);
    }

    [Fact]
    public async Task A_page_is_told_nothing_of_another_user_of_another_agent_or_of_what_it_does_not_follow()
    {
        using var feeding = new Feeding();
        var ada = await feeding.SignedInAsync(Ada);
        var grace = await feeding.SignedInAsync(Grace);
        var (agent, another, hers) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var page = new StandInPage();
        var ofGrace = new StandInPage();

        feeding.Progress.Runs(grace.User.Id, hers, Feeding.Cycle(CycleStep.DataPackage));
        feeding.Progress.Runs(ada.User.Id, another, Feeding.Cycle(CycleStep.DataPackage));

        await using var feed = feeding.Open(ada.Connection, page, agent, [LiveSubject.Agent], LiveShows.Cycle);
        await using var hersFeed = feeding.Open(grace.Connection, ofGrace, hers, [LiveSubject.Agent], LiveShows.Cycle);
        feed.Follow();
        hersFeed.Follow();

        // An agent of Grace changes, and a cycle of it moves on: her page shows it.
        feeding.Signals.Raise(grace.User.Id, hers, LiveSubject.Agent);
        feeding.Signals.Raise(grace.User.Id, hers, LiveSubject.CycleStep);
        await Wait.UntilAsync(() => ofGrace.Shown == 1, "the page of Grace to show the cycle of her agent");

        // Another agent of Ada changes, and a cycle of it moves on. And something changes that
        // the page does not ask for itself again on.
        feeding.Signals.Raise(ada.User.Id, another, LiveSubject.Agent);
        feeding.Signals.Raise(ada.User.Id, another, LiveSubject.CycleStep);
        feeding.Signals.Raise(ada.User.Id, agent, LiveSubject.Holdings);
        feeding.Signals.Raise(ada.User.Id, agent, LiveSubject.Cycle);
        feeding.Signals.Raise(ada.User.Id, agentId: null, LiveSubject.Notification);

        // The page of Ada has taken all of it, and was asked for nothing: it wrote its parts
        // twice, for the two signals that this test waits by.
        await TakenAsync(feeding, ada.User, agent, page);
        feeding.Clock.Advance(TimeSpan.FromSeconds(5));
        await TakenAsync(feeding, ada.User, agent, page);

        Assert.Equal((2, 0), (page.Shown, page.Asked));
        Assert.Empty(feed.State.Cycles);

        // What it shows it asks for by owner: the cycle of the agent of Grace is on her page alone.
        Assert.Equal(CycleStep.DataPackage, Assert.Single(hersFeed.State.Cycles).Steps[^1].Step);

        feeding.Clock.Advance(LiveFeed.AsksAfter);
        await Wait.UntilAsync(() => ofGrace.Asked == 1, "the page of Grace to ask for itself again");
        Assert.Equal(0, page.Asked);

        // Its own agent changes: now it asks.
        feeding.Signals.Raise(ada.User.Id, agent, LiveSubject.Agent);
        await TakenAsync(feeding, ada.User, agent, page);
        feeding.Clock.Advance(LiveFeed.AsksAfter);
        await Wait.UntilAsync(() => page.Asked == 1, "the page of Ada to ask for itself again");
        Assert.Equal(1, ofGrace.Asked);
    }

    [Fact]
    public async Task A_page_of_every_agent_of_its_user_follows_each_of_them_and_a_change_that_names_no_agent()
    {
        using var feeding = new Feeding();
        var ada = await feeding.SignedInAsync(Ada);
        var page = new StandInPage();

        // The list of the agents: it is of no one agent.
        await using var feed = feeding.Open(ada.Connection, page, agentId: null, [LiveSubject.Agent, LiveSubject.Notification]);
        feed.Follow();

        feeding.Signals.Raise(ada.User.Id, Guid.NewGuid(), LiveSubject.Agent);
        await AsksAsync(feeding, page, 1);

        feeding.Clock.Advance(LiveFeed.AsksAtMostEvery);
        feeding.Signals.Raise(ada.User.Id, Guid.NewGuid(), LiveSubject.Agent);
        await AsksAsync(feeding, page, 2);

        // A notification about no agent, such as that of a key.
        feeding.Clock.Advance(LiveFeed.AsksAtMostEvery);
        feeding.Signals.Raise(ada.User.Id, agentId: null, LiveSubject.Notification);
        await AsksAsync(feeding, page, 3);
    }

    [Fact]
    public async Task The_steps_of_a_cycle_are_written_to_the_page_as_the_cycle_comes_to_them_and_the_page_asks_for_nothing()
    {
        using var feeding = new Feeding();
        var ada = await feeding.SignedInAsync(Ada);
        var agent = Guid.NewGuid();
        var page = new StandInPage("/agents/an-agent/test-cycle");

        // The page that runs a test cycle: its form waits, so it asks for itself again on nothing.
        await using var feed = feeding.Open(ada.Connection, page, agent, follows: [], LiveShows.Cycle);

        // As the page is written, no cycle of the agent runs.
        await feed.ReadAsync(TestContext.Current.CancellationToken);
        Assert.Empty(feed.State.Cycles);

        feed.Follow();

        var steps = new List<CycleStep>();

        foreach (var step in new[] { CycleStep.DataPackage, CycleStep.Analysts, CycleStep.Strategist, CycleStep.Recording })
        {
            steps.Add(step);
            feeding.Progress.Runs(ada.User.Id, agent, Feeding.Cycle([.. steps]));
            feeding.Signals.Raise(ada.User.Id, agent, LiveSubject.CycleStep);

            await Wait.UntilAsync(() => page.Shown == steps.Count, $"the page to show the step {step}");

            Assert.Equal(steps, Assert.Single(feed.State.Cycles).Steps.Select(reached => reached.Step));
        }

        // The cycle has ended, and is recorded: it runs no longer.
        feeding.Progress.Runs(ada.User.Id, agent);
        feeding.Signals.Raise(ada.User.Id, agent, LiveSubject.CycleStep);
        feeding.Signals.Raise(ada.User.Id, agent, LiveSubject.Cycle);

        await Wait.UntilAsync(() => page.Shown == 5, "the page to show that no cycle runs");
        Assert.Empty(feed.State.Cycles);

        // Each step reached the page over its connection, at once, and nothing was asked for.
        feeding.Clock.Advance(TimeSpan.FromSeconds(5));
        await TakenAsync(feeding, ada.User, agent, page);
        Assert.Equal(0, page.Asked);
    }

    [Fact]
    public async Task The_position_of_an_agent_and_its_price_are_written_to_the_page_as_they_change()
    {
        using var feeding = new Feeding();
        var application = feeding.Application;
        var ada = await feeding.SignedInAsync(Ada);
        var agent = await application.GiveAgentAsync(ada.User.Id, Feeding.Morning);
        var position = await application.GiveTradeAsync(ada.User.Id, agent, Feeding.Morning, closed: false);
        var page = new StandInPage();

        await feeding.Signals.ListensAsync();

        // A price that the watch has is on the page as it is written, with no read of Binance.
        feeding.Prices.Say(Bitcoin, new WatchedPrice(100_250m, Feeding.Morning));

        await using var feed = feeding.Open(ada.Connection, page, agent, [LiveSubject.Holdings], LiveShows.Position);
        await feed.ReadAsync(TestContext.Current.CancellationToken);

        Assert.Equal((0.01m, 100_000m, Bitcoin, 100_250m), (feed.State.Position!.Quantity, feed.State.Position.EntryPrice, feed.State.Followed, feed.State.Price!.Price));
        Assert.Empty(feeding.Prices.Followed);

        // Once the connection is made, the price is followed, and each one that is read is written.
        feed.Follow();
        await Wait.UntilAsync(() => feeding.Prices.Followed.Count == 1 && page.Shown == 1, "the feed to follow the price of the position");
        Assert.Equal([Bitcoin], feeding.Prices.Followed);

        var shown = page.Shown;

        for (var second = 1; second <= 3; second++)
        {
            feeding.Prices.Say(Bitcoin, new WatchedPrice(100_250m + (second * 10m), Feeding.Morning.AddSeconds(second)));
            await Wait.UntilAsync(() => page.Shown == shown + second, $"the page to show the price of second {second}");

            Assert.Equal(100_250m + (second * 10m), feed.State.Price!.Price);
            Assert.Equal(2.5m + (second * 0.1m), feed.State.Position!.UnrealizedAt(feed.State.Price.Price));
        }

        // The position is reduced, as the database says, whatever wrote it. The page shows what
        // the agent holds now, and asks for itself again, for what else of it follows from that.
        await using (var context = await application.Contexts.CreateDbContextAsync(TestContext.Current.CancellationToken))
        {
            await context.Set<Position>()
                .Where(held => held.Id == position)
                .ExecuteUpdateAsync(row => row.SetProperty(held => held.Quantity, 0.004m), TestContext.Current.CancellationToken);
        }

        await Wait.UntilAsync(() => feed.State.Position?.Quantity == 0.004m, "the page to show what the position holds now");
        await AsksAsync(feeding, page, 1);

        // It is closed. The agent holds nothing, and the price of its symbol is followed on.
        await using (var context = await application.Contexts.CreateDbContextAsync(TestContext.Current.CancellationToken))
        {
            await context.Set<Position>()
                .Where(held => held.Id == position)
                .ExecuteUpdateAsync(row => row.SetProperty(held => held.ClosedAt, (DateTimeOffset?)Feeding.Morning), TestContext.Current.CancellationToken);
        }

        await Wait.UntilAsync(() => feed.State.Position is null, "the page to show that the agent holds nothing");

        Assert.Equal((Bitcoin, 100_280m), (feed.State.Followed, feed.State.Price!.Price));
        Assert.Equal([Bitcoin], feeding.Prices.Followed);

        // A page that is gone follows nothing.
        await feed.DisposeAsync();
        Assert.Empty(feeding.Prices.Followed);
    }

    [Fact]
    public async Task A_page_that_values_what_its_user_holds_has_the_prices_of_that_followed_and_asks_for_itself_again_when_each_is_first_read()
    {
        using var feeding = new Feeding();
        var application = feeding.Application;
        var ada = await feeding.SignedInAsync(Ada);
        var grace = await feeding.SignedInAsync(Grace);
        var spot = await application.GiveAgentAsync(ada.User.Id, Feeding.Morning, name: "Spot");
        var futures = await application.GiveAgentAsync(ada.User.Id, Feeding.Morning, name: "Futures", market: Market.Futures);
        var hers = await application.GiveAgentAsync(grace.User.Id, Feeding.Morning, name: "Hers", symbol: "ETHUSDT");
        var page = new StandInPage("/dashboard");

        await application.GiveAgentAsync(ada.User.Id, Feeding.Morning, name: "Idle", symbol: "SOLUSDT");
        await application.GiveTradeAsync(ada.User.Id, spot, Feeding.Morning, closed: false);
        await application.GiveTradeAsync(ada.User.Id, futures, Feeding.Morning, closed: false);
        await application.GiveTradeAsync(grace.User.Id, hers, Feeding.Morning, closed: false);

        // The dashboard: a page of every agent of its user, which values what they hold.
        await using var feed = feeding.Open(ada.Connection, page, shows: LiveShows.Cycle, values: true);

        // As the page is written, nothing is followed: it is written with the prices that the
        // watch has, and asks Binance for none.
        await feed.ReadAsync(TestContext.Current.CancellationToken);
        Assert.Empty(feeding.Prices.Followed);

        // Once its connection is made, the prices of what its user holds are followed: the
        // price that each position is valued at, of the symbols of Ada alone. An agent that
        // holds nothing has no price here, and neither has an agent of Grace.
        feed.Follow();
        await Wait.UntilAsync(() => feeding.Prices.Followed.Count == 2, "the feed to follow the prices of what its user holds");

        Assert.Equal([Bitcoin, BitcoinMark], feeding.Prices.Followed.OrderBy(price => price.Kind));
        Assert.Equal(0, page.Asked);

        // The first read of a price: the page was written without it, and asks for itself again.
        feeding.Prices.Say(Bitcoin, new WatchedPrice(100_250m, Feeding.Morning));
        await AsksAsync(feeding, page, 1);

        // What is read of it after that asks for nothing: the figures of the page say the time
        // of their prices, and are worked out anew when something that the page shows changes.
        for (var second = 1; second <= 3; second++)
        {
            feeding.Prices.Say(Bitcoin, new WatchedPrice(100_250m + second, Feeding.Morning.AddSeconds(second)));
        }

        await TakenAsync(feeding, ada.User, spot, page);
        feeding.Clock.Advance(TimeSpan.FromSeconds(5));
        await TakenAsync(feeding, ada.User, spot, page);
        Assert.Equal(1, page.Asked);

        // The other price is read for the first time, and the page asks once more.
        feeding.Prices.Say(BitcoinMark, new WatchedPrice(99_400m, Feeding.Morning.AddSeconds(4)));
        await AsksAsync(feeding, page, 2);

        // Nothing is written over the connection for it, and nothing fails.
        Assert.Null(feed.State.Position);
        Assert.Null(page.Failed);

        // A page that is gone follows nothing.
        await feed.DisposeAsync();
        Assert.Empty(feeding.Prices.Followed);
    }

    [Fact]
    public async Task The_prices_that_are_followed_for_a_page_are_those_of_what_its_user_holds_now()
    {
        using var feeding = new Feeding();
        var application = feeding.Application;
        var ada = await feeding.SignedInAsync(Ada);
        var spot = await application.GiveAgentAsync(ada.User.Id, Feeding.Morning, name: "Spot");
        var futures = await application.GiveAgentAsync(ada.User.Id, Feeding.Morning, name: "Futures", market: Market.Futures);
        var page = new StandInPage("/dashboard");

        var held = await application.GiveTradeAsync(ada.User.Id, spot, Feeding.Morning, closed: false);

        await using var feed = feeding.Open(ada.Connection, page, shows: LiveShows.Cycle, values: true);
        feed.Follow();
        await Wait.UntilAsync(() => feeding.Prices.Followed.Count == 1, "the feed to follow the price of the position");
        Assert.Equal([Bitcoin], feeding.Prices.Followed);

        feeding.Prices.Say(Bitcoin, new WatchedPrice(100_250m, Feeding.Morning));
        await AsksAsync(feeding, page, 1);

        // Another agent opens a position, as the database says, whatever wrote it: its price is
        // followed as well, and the price that is followed already stays as it is.
        await application.GiveTradeAsync(ada.User.Id, futures, Feeding.Morning, closed: false);
        await Wait.UntilAsync(() => feeding.Prices.Followed.Count == 2, "the feed to follow the price of the new position");

        Assert.Equal([Bitcoin, BitcoinMark], feeding.Prices.Followed.OrderBy(price => price.Kind));

        // Its first read asks, and a read of the one that was read before does not.
        feeding.Prices.Say(Bitcoin, new WatchedPrice(100_300m, Feeding.Morning.AddSeconds(1)));
        await TakenAsync(feeding, ada.User, spot, page);
        feeding.Clock.Advance(TimeSpan.FromSeconds(5));
        await TakenAsync(feeding, ada.User, spot, page);
        Assert.Equal(1, page.Asked);

        feeding.Prices.Say(BitcoinMark, new WatchedPrice(99_400m, Feeding.Morning.AddSeconds(2)));
        await AsksAsync(feeding, page, 2);

        // The first position is closed: its price is let go of, and the other is followed on.
        await using (var context = await application.Contexts.CreateDbContextAsync(TestContext.Current.CancellationToken))
        {
            await context.Set<Position>()
                .Where(position => position.Id == held)
                .ExecuteUpdateAsync(row => row.SetProperty(position => position.ClosedAt, (DateTimeOffset?)Feeding.Morning), TestContext.Current.CancellationToken);
        }

        await Wait.UntilAsync(() => feeding.Prices.Followed.Count == 1, "the feed to let go of the price of the closed position");
        Assert.Equal([BitcoinMark], feeding.Prices.Followed);
    }

    [Fact]
    public async Task No_price_is_followed_for_a_page_that_values_nothing_or_whose_session_has_ended()
    {
        using var feeding = new Feeding();
        var application = feeding.Application;
        var ada = await feeding.SignedInAsync(Ada);
        var agent = await application.GiveAgentAsync(ada.User.Id, Feeding.Morning);
        var list = new StandInPage("/agents");
        var dashboard = new StandInPage("/dashboard");

        await application.GiveTradeAsync(ada.User.Id, agent, Feeding.Morning, closed: false);

        // The list of the agents follows what changes, and values nothing: no price is read for it.
        await using var plain = feeding.Open(ada.Connection, list, follows: [LiveSubject.Holdings], shows: LiveShows.Cycle);
        plain.Follow();
        await TakenAsync(feeding, ada.User, agent, list);

        Assert.Empty(feeding.Prices.Followed);

        feeding.Prices.Say(Bitcoin, new WatchedPrice(100_250m, Feeding.Morning));
        await TakenAsync(feeding, ada.User, agent, list);
        feeding.Clock.Advance(TimeSpan.FromSeconds(5));
        await TakenAsync(feeding, ada.User, agent, list);
        Assert.Equal(0, list.Asked);

        // The dashboard has it followed, until the session of its connection ends: then nothing
        // of its user is followed any more, and no price is read for a page that nobody may see.
        await using var valuing = feeding.Open(ada.Connection, dashboard, shows: LiveShows.Cycle, values: true);
        valuing.Follow();
        await Wait.UntilAsync(() => feeding.Prices.Followed.Count == 1, "the feed of the dashboard to follow the price");

        await ada.Browser.SignOutAsync();
        await Wait.UntilAsync(() => dashboard.Left == 1 && list.Left == 1, "the browser to be told to load the pages anew");

        Assert.Empty(feeding.Prices.Followed);
    }

    [Fact]
    public async Task The_position_of_an_agent_of_another_user_is_written_to_no_page()
    {
        using var feeding = new Feeding();
        var application = feeding.Application;
        var ada = await feeding.SignedInAsync(Ada);
        var grace = await feeding.SignedInAsync(Grace);
        var agent = await application.GiveAgentAsync(ada.User.Id, Feeding.Morning);
        await application.GiveTradeAsync(ada.User.Id, agent, Feeding.Morning, closed: false);
        var page = new StandInPage();

        // Whatever a page of Grace says that it follows: the agent that it names is not hers.
        await using var feed = feeding.Open(grace.Connection, page, agent, [LiveSubject.Holdings], LiveShows.Cycle | LiveShows.Position);
        feeding.Progress.Runs(ada.User.Id, agent, Feeding.Cycle(CycleStep.Analysts));

        await feed.ReadAsync(TestContext.Current.CancellationToken);
        feed.Follow();

        feeding.Signals.Raise(ada.User.Id, agent, LiveSubject.Holdings);
        feeding.Signals.Raise(ada.User.Id, agent, LiveSubject.CycleStep);
        feeding.Signals.Raise(grace.User.Id, agent, LiveSubject.CycleStep);
        await Wait.UntilAsync(() => page.Shown >= 2, "the page of Grace to have written its parts");

        Assert.Null(feed.State.Position);
        Assert.Empty(feed.State.Cycles);
        Assert.Equal(0, page.Asked);
    }

    [Fact]
    public async Task A_session_that_ended_stops_the_feed_and_has_the_browser_load_the_page_anew()
    {
        using var feeding = new Feeding();
        var ada = await feeding.SignedInAsync(Ada);
        var agent = Guid.NewGuid();
        var page = new StandInPage();

        await feeding.Signals.ListensAsync();
        feeding.Progress.Runs(ada.User.Id, agent, Feeding.Cycle(CycleStep.Analysts));

        await using var feed = feeding.Open(ada.Connection, page, agent, [LiveSubject.Agent], LiveShows.Cycle);
        await feed.ReadAsync(TestContext.Current.CancellationToken);
        feed.Follow();

        Assert.Single(feed.State.Cycles);

        // Its user signs out, on that browser. The database says that a session ended, and the
        // feed asks whether it was the one of its connection.
        await ada.Browser.SignOutAsync();
        await Wait.UntilAsync(() => page.Left == 1, "the browser to be told to load the page anew");

        // Nothing of its user is on the page any more, and nothing more is written or asked for.
        Assert.Empty(feed.State.Cycles);

        var shown = page.Shown;

        feeding.Signals.Raise(ada.User.Id, agent, LiveSubject.CycleStep);
        feeding.Signals.Raise(ada.User.Id, agent, LiveSubject.Agent);
        feeding.Clock.Advance(TimeSpan.FromMinutes(5));
        await Task.Delay(TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken);

        Assert.Equal((shown, 0, 1), (page.Shown, page.Asked, page.Left));
        Assert.Null(page.Failed);
    }

    [Fact]
    public async Task A_session_that_another_browser_of_its_user_ended_stops_no_page_of_this_one()
    {
        using var feeding = new Feeding();
        var ada = await feeding.SignedInAsync(Ada);
        var page = new StandInPage();

        // The same user, signed in on another browser as well.
        using var elsewhere = feeding.Application.CreateClient();
        await elsewhere.SignInAsync(Ada);

        await feeding.Signals.ListensAsync();

        await using var feed = feeding.Open(ada.Connection, page, agentId: null, [LiveSubject.Agent]);
        feed.Follow();

        await elsewhere.SignOutAsync();

        // The feed heard that a session ended, asked, and found its own standing: it goes on.
        feeding.Signals.Raise(ada.User.Id, Guid.NewGuid(), LiveSubject.Agent);
        await AsksAsync(feeding, page, 1);

        Assert.Equal(0, page.Left);
    }

    [Fact]
    public async Task A_session_that_was_left_alone_for_the_period_ends_for_a_page_that_follows_as_for_any_other()
    {
        using var feeding = new Feeding();
        var ada = await feeding.SignedInAsync(Ada);
        var agent = Guid.NewGuid();
        var page = new StandInPage();

        await using var feed = feeding.Open(ada.Connection, page, agent, [LiveSubject.Agent], LiveShows.Cycle);
        feed.Follow();
        await TakenAsync(feeding, ada.User, agent, page);

        // The page is open, and something of its user changes every few minutes: the page asks
        // for itself again each time, and its user does nothing. The file of the tests sets the
        // period of inactivity to 45 minutes.
        for (var minutes = 5; minutes < 45; minutes += 5)
        {
            feeding.Clock.Advance(TimeSpan.FromMinutes(5));
            feeding.Signals.Raise(ada.User.Id, agent, LiveSubject.Agent);
            await TakenAsync(feeding, ada.User, agent, page);
            feeding.Clock.Advance(LiveFeed.AsksAfter);

            var asks = minutes / 5;
            await Wait.UntilAsync(() => page.Asked == asks, $"the page to ask for itself again after {minutes} minutes");

            await ada.Browser.AskAgainAsync(page.Address);
        }

        // None of it was activity: the session is as its user left it.
        Assert.Equal(Feeding.Morning, Assert.Single(StoredSession.All(feeding.Application.Database)).LastActiveAt);
        Assert.Equal(0, page.Left);

        // The period is over. The feed asks about its session once a minute, and finds that it
        // no longer stands: the browser loads the page anew, which leads to the page that signs in.
        feeding.Clock.Advance(TimeSpan.FromMinutes(6));
        await Wait.UntilAsync(() => page.Left == 1, "the browser to be told to load the page anew");

        Assert.StartsWith(SessionAuthentication.SignInPath, (await ada.Browser.OpenAsync(page.Address)).Address);
    }

    [Fact]
    public async Task Nothing_is_asked_of_a_page_whose_connection_is_down_until_it_is_back()
    {
        using var feeding = new Feeding();
        var ada = await feeding.SignedInAsync(Ada);
        var agent = Guid.NewGuid();
        var page = new StandInPage();

        await feeding.Signals.ListensAsync();

        await using var feed = feeding.Open(ada.Connection, page, agent, [LiveSubject.Agent], LiveShows.Cycle);
        feed.Follow();

        // The browser sleeps. The framework keeps the page for it, and what changes meanwhile
        // is owed to it.
        page.IsConnected = false;

        feeding.Signals.Raise(ada.User.Id, agent, LiveSubject.Agent);
        await TakenAsync(feeding, ada.User, agent, page);
        feeding.Clock.Advance(TimeSpan.FromSeconds(30));
        await TakenAsync(feeding, ada.User, agent, page);

        Assert.Equal(0, page.Asked);

        // It wakes: the page asks for itself again, once.
        page.IsConnected = true;
        feed.Connected();
        await TakenAsync(feeding, ada.User, agent, page);
        feeding.Clock.Advance(LiveFeed.AsksAfter);
        await Wait.UntilAsync(() => page.Asked == 1, "the page to ask for itself again once its connection is back");

        // Its session ends while it sleeps again: the browser is told when it is back, and not before.
        page.IsConnected = false;
        await ada.Browser.SignOutAsync();
        await Task.Delay(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);

        Assert.Equal(0, page.Left);

        page.IsConnected = true;
        feed.Connected();
        await Wait.UntilAsync(() => page.Left == 1, "the browser to be told to load the page anew once its connection is back");

        Assert.Equal(1, page.Asked);
    }

    [Fact]
    public async Task A_connection_that_is_made_again_for_another_user_or_another_session_stops_the_feed()
    {
        using var feeding = new Feeding();
        var ada = await feeding.SignedInAsync(Ada);
        var grace = await feeding.SignedInAsync(Grace);
        var page = new StandInPage();
        var other = new StandInPage();
        var none = new StandInPage();

        await using var feed = feeding.Open(ada.Connection, page, agentId: null, [LiveSubject.Agent]);
        await using var ofOther = feeding.Open(ada.Connection, other, agentId: null, [LiveSubject.Agent]);
        await using var ofNone = feeding.Open(new ClaimsPrincipal(), none, agentId: null, [LiveSubject.Agent]);
        feed.Follow();
        ofOther.Follow();

        // The connection of a page is made again after it was lost, and is signed in again
        // then. The same user with the same session: the feed goes on.
        feed.UserIs(ada.Connection);
        feeding.Signals.Raise(ada.User.Id, Guid.NewGuid(), LiveSubject.Agent);
        await AsksAsync(feeding, page, 1);
        Assert.Equal(0, page.Left);

        // Another user signed in on that browser meanwhile: the feed is of the user that it
        // was opened for, and stops.
        ofOther.UserIs(grace.Connection);
        await Wait.UntilAsync(() => other.Left == 1, "the browser to be told to load the page anew");

        // The same user with another session: it stops as well.
        using var again = feeding.Application.CreateClient();
        await again.SignInAsync(Ada);
        var newer = StoredSession.All(feeding.Application.Database)
            .Single(session => session.UserId == ada.User.Id && session.Id != SignedInUser.SessionIdOf(ada.Connection));

        feed.UserIs(newer.ConnectionOf(ada.User));
        await Wait.UntilAsync(() => page.Left == 1, "the browser to be told to load the page anew");

        // And a connection on which nobody is signed in follows nothing from the start.
        ofNone.Follow();
        await Wait.UntilAsync(() => none.Left == 1, "the browser of nobody to be told to load the page anew");
    }

    [Fact]
    public async Task What_was_missed_has_the_page_ask_for_itself_again_and_its_parts_read_again()
    {
        using var feeding = new Feeding();
        var ada = await feeding.SignedInAsync(Ada);
        var agent = Guid.NewGuid();
        var page = new StandInPage();

        await feeding.Signals.ListensAsync();

        // The page was written before this instance listened: by another instance, or before
        // the connection of the listener was lost for a while.
        await using var feed = feeding.Open(ada.Connection, page, agent, [LiveSubject.Agent], LiveShows.Cycle, since: -1);
        await feed.ReadAsync(TestContext.Current.CancellationToken);

        feeding.Progress.Runs(ada.User.Id, agent, Feeding.Cycle(CycleStep.Strategist));
        feed.Follow();

        await Wait.UntilAsync(() => feed.State.Cycles.Count == 1, "the parts of the page to be read again");
        await AsksAsync(feeding, page, 1);
    }

    [Fact]
    public async Task What_was_heard_between_the_writing_of_a_page_and_its_connection_reaches_the_page()
    {
        using var feeding = new Feeding();
        var ada = await feeding.SignedInAsync(Ada);
        var agent = Guid.NewGuid();
        var page = new StandInPage();

        await feeding.Signals.ListensAsync();

        // The page is written, and its connection is made a moment later: meanwhile its agent changes.
        await using var feed = feeding.Open(ada.Connection, page, agent, [LiveSubject.Agent]);
        await feed.ReadAsync(TestContext.Current.CancellationToken);

        feeding.Signals.Raise(ada.User.Id, agent, LiveSubject.Agent);

        feed.Follow();
        await AsksAsync(feeding, page, 1);
    }

    [Fact]
    public async Task A_feed_that_fails_says_so_to_its_page_once_and_stops()
    {
        using var feeding = new Feeding();
        var ada = await feeding.SignedInAsync(Ada);
        var agent = Guid.NewGuid();
        var page = new StandInPage();

        await using var feed = feeding.Open(ada.Connection, page, agent, [LiveSubject.Agent], LiveShows.Cycle);
        feed.Follow();
        await TakenAsync(feeding, ada.User, agent, page);

        var failure = new InvalidOperationException("The cycles could not be asked for.");
        feeding.Progress.Fails = failure;

        feeding.Signals.Raise(ada.User.Id, agent, LiveSubject.CycleStep);
        await Wait.UntilAsync(() => page.Failed is not null, "the page to be told that its feed failed");

        Assert.Same(failure, page.Failed);

        // It follows nothing any more: what changes is neither written nor asked for.
        feeding.Progress.Fails = null;

        var shown = page.Shown;

        feeding.Signals.Raise(ada.User.Id, agent, LiveSubject.CycleStep);
        feeding.Signals.Raise(ada.User.Id, agent, LiveSubject.Agent);
        feeding.Clock.Advance(TimeSpan.FromSeconds(5));
        await Task.Delay(TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken);

        Assert.Equal((shown, 0, 0), (page.Shown, page.Asked, page.Left));
    }

    [Theory]
    [InlineData("GET", true)]
    [InlineData("POST", false)]
    public void A_page_that_answers_a_form_asks_for_itself_again_on_nothing(string method, bool follows)
    {
        var request = new DefaultHttpContext().Request;
        request.Method = method;
        request.Path = "/agents/0191a0c3-6b1c-7c44-9a36-0d4d6a1c2b10/state";
        request.QueryString = new QueryString("?page=2");

        var agent = Guid.NewGuid();
        var view = LiveView.Of(
            request, agent, [LiveSubject.Agent, LiveSubject.Holdings], LiveShows.Cycle | LiveShows.Position, Market.Futures, TradingMode.Live, "SOLUSDT", "USDT", since: 42);

        // What the form answered stays on the page: the page would lose it by asking again.
        Assert.Equal(follows ? [LiveSubject.Agent, LiveSubject.Holdings] : [], view.Follows);

        // A page says so when it values what its user holds, and no other page does. One that
        // answers a form values nothing either: a price that is read would have it ask again.
        Assert.False(view.Values);
        Assert.Equal(
            follows,
            LiveView.Of(request, agentId: null, [LiveSubject.Holdings], LiveShows.None, market: null, mode: null, symbol: null, quoteAsset: null, since: 42, values: true)
                .Values);

        // Its parts are written over its connection all the same, and it says where it is asked for.
        Assert.Equal(
            (agent, LiveShows.Cycle | LiveShows.Position, Market.Futures, TradingMode.Live, "SOLUSDT", "USDT", 42L),
            (view.AgentId!.Value, view.Shows, view.Market!.Value, view.Mode!.Value, view.Symbol, view.QuoteAsset, view.Since));
        Assert.Equal("/agents/0191a0c3-6b1c-7c44-9a36-0d4d6a1c2b10/state?page=2", view.Address);
    }

    /// <summary>
    /// Waits until a feed has taken every signal that was raised so far: one more is raised, of
    /// a step of a cycle of its agent, which a feed that shows the cycles answers by writing
    /// its parts.
    /// </summary>
    private static async Task TakenAsync(Feeding feeding, StoredUser user, Guid agentId, StandInPage page)
    {
        var shown = page.Shown;

        feeding.Signals.Raise(user.Id, agentId, LiveSubject.CycleStep);

        await Wait.UntilAsync(() => page.Shown > shown, "the feed to take the signals that were raised");
    }

    /// <summary>
    /// Waits until a page has asked for itself again so often, with the clock moved on by the
    /// moment that a feed waits for more signals, again and again: for a test that is not about
    /// when a page asks.
    /// </summary>
    private static Task AsksAsync(Feeding feeding, StandInPage page, int asked) =>
        Wait.UntilAsync(
            () =>
            {
                if (page.Asked < asked)
                {
                    feeding.Clock.Advance(LiveFeed.AsksAfter);
                }

                return page.Asked >= asked;
            },
            $"the page to ask for itself again {asked} times. It did {page.Asked} times");
}
