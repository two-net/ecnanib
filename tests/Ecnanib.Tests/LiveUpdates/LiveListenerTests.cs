using Ecnanib.Agents;
using Ecnanib.Notifications;
using Ecnanib.Platform.LiveUpdates;
using Ecnanib.Platform.Logging;
using Ecnanib.Shared;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.Agents;
using Ecnanib.Tests.DecisionEngine;
using Ecnanib.Tests.Logging;
using Ecnanib.Tests.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.LiveUpdates;

/// <summary>
/// The database says that a row of a user changed, whatever wrote it, when the change is
/// committed, and the application hands that to what follows the owner of the row, and to
/// nobody else (FR-NTF-2). These tests go the whole way: a write, the trigger, the listener,
/// and the follower. See docs/live-updates.md.
/// </summary>
public class LiveListenerTests
{
    private const string Ada = "ada@example.com";
    private const string Grace = "grace@example.com";

    private static readonly DateTimeOffset Morning = new(2026, 11, 24, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_change_of_an_agent_reaches_what_follows_its_user_and_nobody_else()
    {
        using var application = new AccountsApplication();
        var ada = await application.RegisterVerifiedAsync(Ada);
        var grace = await application.RegisterVerifiedAsync(Grace);
        var ofAda = await application.GiveAgentAsync(ada.Id, Morning, team: false);
        var ofGrace = await application.GiveAgentAsync(grace.Id, Morning, team: false);

        await application.LiveSignals.ListensAsync();
        using var adaFollows = new Following(application.LiveSignals, ada.Id);
        using var graceFollows = new Following(application.LiveSignals, grace.Id);

        await MoveAsync(application, ofAda, AgentState.Paused);
        await adaFollows.HearsAsync(LiveSubject.Agent, ofAda);

        // The listener hears in the order that the database says things in. So once Grace has
        // heard a change of her own, she would have heard that of Ada before it.
        await MoveAsync(application, ofGrace, AgentState.Stopped);
        await graceFollows.HearsAsync(LiveSubject.Agent, ofGrace);

        Assert.Equal([new(LiveSubject.Agent, ofAda)], adaFollows.Heard);
        Assert.Equal([new(LiveSubject.Agent, ofGrace)], graceFollows.Heard);
    }

    [Fact]
    public async Task The_equity_of_an_agent_that_is_recorded_reaches_what_follows_its_user_with_its_agent()
    {
        using var application = new AccountsApplication();
        var ada = await application.RegisterVerifiedAsync(Ada);
        var grace = await application.RegisterVerifiedAsync(Grace);
        var ofAda = await application.GiveAgentAsync(ada.Id, Morning, team: false);
        var ofGrace = await application.GiveAgentAsync(grace.Id, Morning, team: false);

        await application.LiveSignals.ListensAsync();
        using var adaFollows = new Following(application.LiveSignals, ada.Id);
        using var graceFollows = new Following(application.LiveSignals, grace.Id);

        // Two recordings, written at once: the screen of the agent shows its equity over time (FR-RPT-2).
        await application.GiveEquityAsync(ada.Id, ofAda, (Morning, 1010m), (Morning.AddMinutes(15), 1012m));
        await adaFollows.HearsAsync(LiveSubject.Equity, ofAda);

        await application.GiveEquityAsync(grace.Id, ofGrace, (Morning, 990m));
        await graceFollows.HearsAsync(LiveSubject.Equity, ofGrace);

        // Each hears of their own agent, and the two rows of one transaction say the same, which the database says once.
        Assert.Equal([new(LiveSubject.Equity, ofAda)], adaFollows.Heard);
        Assert.Equal([new(LiveSubject.Equity, ofGrace)], graceFollows.Heard);
    }

    [Fact]
    public async Task A_change_is_said_when_it_is_committed_and_one_that_is_rolled_back_is_said_to_nobody()
    {
        using var application = new AccountsApplication();
        var ada = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(ada.Id, Morning, team: false);
        var other = await application.GiveAgentAsync(ada.Id, Morning, name: "Another", team: false);

        await application.LiveSignals.ListensAsync();
        using var follows = new Following(application.LiveSignals, ada.Id);

        await using (var context = await application.Contexts.CreateDbContextAsync(TestContext.Current.CancellationToken))
        {
            await using var transaction = await context.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);

            await context.Set<Agent>()
                .Where(stored => stored.Id == agent)
                .ExecuteUpdateAsync(row => row.SetProperty(stored => stored.State, AgentState.Paused), TestContext.Current.CancellationToken);

            await transaction.RollbackAsync(TestContext.Current.CancellationToken);
        }

        await using (var context = await application.Contexts.CreateDbContextAsync(TestContext.Current.CancellationToken))
        {
            await using var transaction = await context.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);

            await context.Set<Agent>()
                .Where(stored => stored.Id == other)
                .ExecuteUpdateAsync(row => row.SetProperty(stored => stored.State, AgentState.Paused), TestContext.Current.CancellationToken);

            // Written, and not committed: whoever asked now would read the row as it was.
            await Task.Delay(TimeSpan.FromMilliseconds(300), TestContext.Current.CancellationToken);
            Assert.Empty(follows.Heard);

            await transaction.CommitAsync(TestContext.Current.CancellationToken);
        }

        await follows.HearsAsync(LiveSubject.Agent, other);

        Assert.Equal([new(LiveSubject.Agent, other)], follows.Heard);
    }

    [Fact]
    public async Task Every_kind_of_row_that_a_page_shows_says_its_subject_and_its_agent()
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClient();
        var ada = await application.RegisterVerifiedAsync(Ada);

        await application.LiveSignals.ListensAsync();
        using var follows = new Following(application.LiveSignals, ada.Id);

        // An agent that is created, with its team: the roles of a team are on no page that follows.
        var agent = await application.GiveAgentAsync(ada.Id, Morning);
        await follows.HearsAsync(LiveSubject.Agent, agent);

        // A cycle that is recorded, and the orders and the position of it.
        var cycle = await application.GiveCycleAsync(ada.Id, agent, Morning, orders: false);
        await follows.HearsAsync(LiveSubject.Cycle, agent);

        await application.GiveOrdersAsync(ada.Id, agent, cycle, Morning);
        await follows.HearsAsync(LiveSubject.Holdings, agent);

        // A notification about the agent, and one about none, such as that of a key.
        await application.GiveNotificationAsync(ada.Id, agent, Morning);
        await follows.HearsAsync(LiveSubject.Notification, agent);

        await application.GiveNotificationAsync(ada.Id, agentId: null, Morning, type: NotificationType.KeyInvalid);
        await follows.HearsAsync(LiveSubject.Notification, agentId: null);

        // A session that ends, here because its user signs out. One that begins is said to nobody.
        await client.SignInAsync(Ada);
        follows.Forget();

        await client.SignOutAsync();
        await follows.HearsAsync(LiveSubject.Session, agentId: null);

        Assert.Equal([new(LiveSubject.Session, null)], follows.Heard);
    }

    [Fact]
    public async Task An_agent_that_is_deleted_says_so_with_everything_of_it_that_goes()
    {
        using var application = new AccountsApplication();
        var ada = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(ada.Id, Morning);
        var cycle = await application.GiveCycleAsync(ada.Id, agent, Morning, orders: false);
        await application.GiveOrdersAsync(ada.Id, agent, cycle, Morning);

        await application.LiveSignals.ListensAsync();
        using var follows = new Following(application.LiveSignals, ada.Id);

        await using (var context = await application.Contexts.CreateDbContextAsync(TestContext.Current.CancellationToken))
        {
            await context.Set<Agent>().Where(stored => stored.Id == agent).ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        }

        await follows.HearsAsync(LiveSubject.Agent, agent);
        await follows.HearsAsync(LiveSubject.Holdings, agent);
        await follows.HearsAsync(LiveSubject.Cycle, agent);

        // Its two orders and its position say the same, which the database says once.
        Assert.Equal(3, follows.Heard.Count);
    }

    [Fact]
    public async Task What_is_written_again_and_again_with_nothing_to_show_for_it_is_said_to_nobody()
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClient();
        var ada = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(ada.Id, Morning);
        var cycle = await application.GiveCycleAsync(ada.Id, agent, Morning, orders: false);
        await application.GiveOrdersAsync(ada.Id, agent, cycle, Morning);
        var notification = await application.GiveNotificationAsync(ada.Id, agent, Morning);
        await client.SignInAsync(Ada);

        await application.LiveSignals.ListensAsync();
        using var follows = new Following(application.LiveSignals, ada.Id);

        // When the service last gave a role another model, when an order was last asked about,
        // that the user of a position was told of it, when its funding is read next, what
        // became of the email of a notification, and when a session was last used.
        application.Database.Execute("UPDATE agents SET roles_reassigned_at = now()");
        application.Database.Execute("UPDATE orders SET checked_at = now()");
        application.Database.Execute("UPDATE positions SET alerted_at = now(), funding_due_at = now()");
        application.Database.Execute("UPDATE notifications SET email_pending = true, emailed_at = now()");
        application.Database.Execute("UPDATE sessions SET last_active_at = now()");

        // And a statement that writes a row as it is.
        application.Database.Execute("UPDATE agents SET state = state");
        application.Database.Execute("UPDATE orders SET status = status, executed_quantity = executed_quantity");

        // What shows on a page is said: each of these after the others, so nothing was said before it.
        application.Database.Execute($"UPDATE notifications SET is_read = true WHERE id = '{notification}'");
        await follows.HearsAsync(LiveSubject.Notification, agent);

        Assert.Equal([new(LiveSubject.Notification, agent)], follows.Heard);

        application.Database.Execute("UPDATE orders SET executed_quantity = executed_quantity / 2");
        application.Database.Execute("UPDATE positions SET stop_loss = stop_loss + 1");
        await follows.HearsAsync(LiveSubject.Holdings, agent);

        application.Database.Execute("UPDATE agents SET allocated_capital = allocated_capital + 1");
        await follows.HearsAsync(LiveSubject.Agent, agent);

        // The two orders say the same in one transaction, which the database says once. The
        // position says it again, because its statement is a transaction of its own.
        Assert.Equal(
            [new(LiveSubject.Notification, agent), new(LiveSubject.Holdings, agent), new(LiveSubject.Holdings, agent), new(LiveSubject.Agent, agent)],
            follows.Heard);
    }

    [Fact]
    public async Task What_the_listener_logs_is_of_the_instance_and_of_no_request_and_no_user()
    {
        using var application = new LoggedApplication(LoggedApplication.Settings());
        var signals = application.Services.GetRequiredService<ILiveSignals>();

        // The instance begins to listen with the first page that follows, in the request of
        // whoever asked for that page.
        using (application.Logger().BeginCorrelation(new Correlation(RequestId: "of-a-page", UserId: "of-a-user")))
        {
            signals.Mark();
        }

        await signals.ListensAsync();

        // It listens for every user, and long after that request: what it logs says nothing of them.
        var said = Assert.Single(application.Events(), written => written.Area == "Ecnanib.Platform.LiveUpdates.LiveListener");
        Assert.False(said.Has("RequestId"), said.ToString());
        Assert.False(said.Has("UserId"), said.ToString());
    }

    private static async Task MoveAsync(AccountsApplication application, Guid agentId, AgentState state)
    {
        await using var context = await application.Contexts.CreateDbContextAsync(TestContext.Current.CancellationToken);

        await context.Set<Agent>()
            .Where(agent => agent.Id == agentId)
            .ExecuteUpdateAsync(row => row.SetProperty(agent => agent.State, state), TestContext.Current.CancellationToken);
    }
}
