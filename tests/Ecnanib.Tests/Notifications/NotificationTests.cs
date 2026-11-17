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
/// alerted when the stop-loss of its position could not be placed (FR-RSK-7). A notification
/// belongs to one user, and holds no secret. See docs/notifications.md.
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
    public async Task The_page_of_the_state_of_an_agent_says_what_its_user_has_not_read_about_it_latest_first()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        var ada = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(ada.Id, Morning, name: "Swing");
        var other = await application.GiveAgentAsync(ada.Id, Morning, name: "Other", symbol: "SOLUSDT");
        using var client = application.CreateClient();
        await client.SignInAsync(Ada);

        // With nothing to say, the page has no such heading.
        Assert.DoesNotContain("Notifications", AgentBrowser.Read(await client.PageAsync(AgentBrowser.AgentStatePage(agent))));

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
