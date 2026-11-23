using System.Net;
using Ecnanib.Notifications;
using Ecnanib.Platform.Persistence;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.Agents;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.Tests.Persistence;
using Ecnanib.WebUi.Pages;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Tests.Notifications;

/// <summary>
/// In-app notifications cover an order or protection failure (FR-NTF-1): the user of an agent is
/// alerted when the stop-loss of its position could not be placed (FR-RSK-7), and notified when
/// an entry is skipped because the quote balance is below what the plan needs (FR-BIN-S3). A
/// notification belongs to one user, and holds no secret. See docs/notifications.md.
/// </summary>
public class NotificationTests
{
    private const string Ada = "ada@example.com";
    private const string Grace = "grace@example.com";

    private static readonly DateTimeOffset Morning = new(2026, 11, 17, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_notification_is_stored_for_its_user_about_an_agent_as_one_that_is_not_read()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        var ada = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(ada.Id, Morning);

        await application.Notifications.RaiseAsync(
            ada.Id, NotificationType.ProtectionFailure, agent, StoredNotificationRows.Content, TestContext.Current.CancellationToken);

        await using var context = await application.Contexts.CreateDbContextAsync(TestContext.Current.CancellationToken);
        var stored = await context.Set<Notification>().AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            (ada.Id, NotificationType.ProtectionFailure, (Guid?)agent, StoredNotificationRows.Content, false, Morning),
            (stored.UserId, stored.Type, stored.AgentId, stored.Content, stored.IsRead, stored.CreatedAt));

        // The log names what it is about, and does not repeat what it says.
        var raised = Assert.Single(application.Events(), said => said.Area == "Ecnanib.Notifications.StoredNotifications");
        Assert.Equal(ada.Id.ToString(), raised.Text("UserId"));
        Assert.Equal(agent.ToString(), raised.Text("AgentId"));
        Assert.Equal("ProtectionFailure", raised.Text("NotificationType"));
        Assert.DoesNotContain(StoredNotificationRows.Content, application.LogText());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_notification_says_something(string content)
    {
        using var application = new AccountsApplication(clock: new TestClock(Morning));
        var ada = await application.RegisterVerifiedAsync(Ada);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => application.Notifications.RaiseAsync(
            ada.Id, NotificationType.ProtectionFailure, agentId: null, content, TestContext.Current.CancellationToken));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => application.Notifications.RaiseAsync(
            ada.Id,
            NotificationType.ProtectionFailure,
            agentId: null,
            new string('x', Notification.ContentLength + 1),
            TestContext.Current.CancellationToken));

        Assert.Equal(0, application.Database.Scalar<long>("SELECT count(*) FROM notifications"));
    }

    [Fact]
    public async Task What_is_raised_inside_a_transaction_is_stored_when_the_transaction_is_and_not_when_it_is_not()
    {
        using var application = new AccountsApplication(clock: new TestClock(Morning));
        var ada = await application.RegisterVerifiedAsync(Ada);

        await using var context = await application.Contexts.CreateDbContextAsync(TestContext.Current.CancellationToken);

        Task RaiseInAsync(string content) => application.Notifications.RaiseInAsync(
            context, ada.Id, NotificationType.KeyInvalid, agentId: null, content, TestContext.Current.CancellationToken);

        // A context that holds no transaction is refused: the notification would stand when
        // what it tells of fails.
        await Assert.ThrowsAsync<InvalidOperationException>(() => RaiseInAsync("Raised with no transaction."));

        // What it tells of is undone, and so is the notification.
        await using (var transaction = await context.Database.BeginTransactionAsync(TestContext.Current.CancellationToken))
        {
            await RaiseInAsync("A key that was not marked after all.");
            await transaction.RollbackAsync(TestContext.Current.CancellationToken);
        }

        Assert.Empty(await application.ToldAsync(ada.Id));

        // What it tells of is stored, and so is the notification.
        await using (var transaction = await context.Database.BeginTransactionAsync(TestContext.Current.CancellationToken))
        {
            await RaiseInAsync("A key that was marked.");
            await transaction.CommitAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal([(NotificationType.KeyInvalid, (Guid?)null, "A key that was marked.")], await application.ToldAsync(ada.Id));

        // It says something, in what a notification holds, as one that is raised on its own.
        await using (await context.Database.BeginTransactionAsync(TestContext.Current.CancellationToken))
        {
            await Assert.ThrowsAnyAsync<ArgumentException>(() => RaiseInAsync(" "));
            await Assert.ThrowsAnyAsync<ArgumentException>(() => RaiseInAsync(new string('x', Notification.ContentLength + 1)));
        }
    }

    [Fact]
    public async Task What_is_raised_once_in_a_time_is_not_raised_again_about_the_same_agent_within_it()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        var ada = await application.RegisterVerifiedAsync(Ada);
        var grace = await application.RegisterVerifiedAsync(Grace);
        var agent = await application.GiveAgentAsync(ada.Id, Morning, name: "Swing");
        var other = await application.GiveAgentAsync(ada.Id, Morning, name: "Other", symbol: "SOLUSDT");
        var day = TimeSpan.FromDays(1);

        Task<bool> RaiseOnceAsync(Guid userId, NotificationType type, Guid about) =>
            application.Notifications.RaiseOnceAsync(userId, type, about, "An entry was skipped.", day, TestContext.Current.CancellationToken);

        // The first is raised. What happens again while nothing changes is not said again.
        Assert.True(await RaiseOnceAsync(ada.Id, NotificationType.EntrySkipped, agent));

        clock.Set(Morning.AddHours(23));
        Assert.False(await RaiseOnceAsync(ada.Id, NotificationType.EntrySkipped, agent));

        // It is said once for each agent, for each thing that it is about, and for each user:
        // what is asked is asked by owner, so the notification of another user stands in no way.
        Assert.True(await RaiseOnceAsync(ada.Id, NotificationType.EntrySkipped, other));
        Assert.True(await RaiseOnceAsync(ada.Id, NotificationType.ProtectionFailure, agent));
        Assert.True(await RaiseOnceAsync(grace.Id, NotificationType.EntrySkipped, agent));

        // And again once the time has passed.
        clock.Set(Morning.AddDays(1));
        Assert.True(await RaiseOnceAsync(ada.Id, NotificationType.EntrySkipped, agent));

        Assert.Equal(5, application.Database.Scalar<long>("SELECT count(*) FROM notifications"));
        Assert.Equal(5, application.Events().Count(said => said.Area == "Ecnanib.Notifications.StoredNotifications"));
    }

    [Fact]
    public async Task The_page_says_what_each_kind_of_notification_is_about()
    {
        using var application = new AccountsApplication(clock: new TestClock(Morning));
        var ada = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(ada.Id, Morning, name: "Swing");
        using var client = application.CreateClient();
        await client.SignInAsync(Ada);

        // One of every kind: a kind that the page has no words for would fail the page.
        foreach (var type in Enum.GetValues<NotificationType>())
        {
            await application.Notifications.RaiseAsync(ada.Id, type, agent, $"Something of the kind {type}.", TestContext.Current.CancellationToken);
        }

        var page = AgentBrowser.Read(await client.PageAsync(AgentBrowser.AgentStatePage(agent)));

        Assert.Contains("<strong>Order or protection failure</strong>", page);
        Assert.Contains("<strong>Entry skipped</strong>", page);
        Assert.All(Enum.GetValues<NotificationType>(), type => Assert.Contains($"Something of the kind {type}.", page));
    }

    [Fact]
    public async Task The_page_of_the_state_of_an_agent_says_what_its_user_has_not_read_about_it_latest_first()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        var ada = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(ada.Id, Morning, name: "Swing");
        var other = await application.GiveAgentAsync(ada.Id, Morning, name: "Other", symbol: "SOLUSDT");
        using var client = application.CreateClient();
        await client.SignInAsync(Ada);

        // With nothing to say, the page has no such heading. The bar at its top has the link to
        // the page of the notifications all the same.
        Assert.DoesNotContain("notifications-heading", await client.PageAsync(AgentBrowser.AgentStatePage(agent)));

        await application.GiveNotificationAsync(ada.Id, agent, Morning.AddMinutes(1), "The first thing that happened.");
        await application.GiveNotificationAsync(ada.Id, agent, Morning.AddMinutes(2), "What happened <after> that & more.");
        await application.GiveNotificationAsync(ada.Id, other, Morning.AddMinutes(3), "About the other agent.");
        var read = await application.GiveNotificationAsync(ada.Id, agent, Morning.AddMinutes(4), "Read already.");

        await using (var context = await application.Contexts.CreateDbContextAsync(TestContext.Current.CancellationToken))
        {
            await context.Set<Notification>()
                .Where(notification => notification.Id == read)
                .ExecuteUpdateAsync(row => row.SetProperty(notification => notification.IsRead, true), TestContext.Current.CancellationToken);
        }

        var markup = await client.PageAsync(AgentBrowser.AgentStatePage(agent));
        var page = AgentBrowser.Read(markup);

        // What is about this agent and is not read, with what it is about and when, the
        // latest first. What it says is written as text, and never as markup.
        Assert.Contains("<h2 id=\"notifications-heading\">Notifications</h2>", markup);
        Assert.Contains(
            $"<strong>Order or protection failure</strong>, {PageTimes.At(Morning.AddMinutes(2))}. What happened <after> that & more.", page);
        Assert.Contains($"<strong>Order or protection failure</strong>, {PageTimes.At(Morning.AddMinutes(1))}. The first thing that happened.", page);
        Assert.True(page.IndexOf("What happened", StringComparison.Ordinal) < page.IndexOf("The first thing", StringComparison.Ordinal));
        Assert.Contains("What happened &lt;after&gt; that &amp; more.", markup);
        Assert.DoesNotContain("About the other agent.", page);
        Assert.DoesNotContain("Read already.", page);

        // The other agent has its own.
        Assert.Contains("About the other agent.", AgentBrowser.Read(await client.PageAsync(AgentBrowser.AgentStatePage(other))));
    }

    [Fact]
    [ProvesOwnershipOf("notifications")]
    public async Task A_notification_is_its_users_alone()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        var grace = await application.RegisterVerifiedAsync(Grace);
        var graces = await application.GiveAgentAsync(grace.Id, Morning, name: "Graces");
        await application.GiveNotificationAsync(grace.Id, graces, Morning, "Something about the agent of Grace.");
        var before = application.RowsOf(grace.Id);

        var ada = await application.RegisterVerifiedAsync(Ada);
        var adas = await application.GiveAgentAsync(ada.Id, Morning, name: "Adas");
        using var client = application.CreateClient();
        await client.SignInAsync(Ada);

        // Ada's pages say nothing of it: not the page of her own agent, and the agent of Grace
        // is not found for her, like one that does not exist.
        Assert.DoesNotContain("Something about the agent of Grace.", AgentBrowser.Read(await client.PageAsync(AgentBrowser.AgentStatePage(adas))));
        using var answer = await client.GetAsync(AgentBrowser.AgentStatePage(graces), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, answer.StatusCode);
        Assert.DoesNotContain(
            "Something about the agent of Grace.", await answer.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        // A notification that names the agent of Grace and is stored for Ada is Ada's, and
        // shows nothing of Grace's: what is read is asked for by owner.
        await application.GiveNotificationAsync(ada.Id, adas, Morning, "Something about the agent of Ada.");

        await using var context = await application.Contexts.CreateDbContextAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            ["Something about the agent of Ada."],
            await context.Set<Notification>().OwnedBy(ada.Id).Select(notification => notification.Content).ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(
            ["Something about the agent of Grace."],
            await context.Set<Notification>().OwnedBy(grace.Id).Select(notification => notification.Content).ToListAsync(TestContext.Current.CancellationToken));

        // Her export holds her own, and not Grace's.
        var export = await client.PageAsync("/account/export");
        Assert.Contains("Something about the agent of Ada.", export);
        Assert.DoesNotContain("Something about the agent of Grace.", export);

        Assert.Equal(before, application.RowsOf(grace.Id));
    }
}
