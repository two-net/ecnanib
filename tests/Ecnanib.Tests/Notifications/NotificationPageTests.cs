using System.Net;
using System.Text.RegularExpressions;
using Ecnanib.Accounts;
using Ecnanib.Notifications;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.Agents;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.WebUi.Pages;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Tests.Notifications;

/// <summary>
/// A notification belongs to one user and can be marked as read (FR-NTF-1): the page of the
/// notifications lists what the service told the user who is signed in, the newest first, a page
/// of them at a time, and its forms mark one as read, or all of them. The bar at the top of
/// every page links to it, with how many are not read, and the page of an agent marks what is
/// about the agent as read as well. See "The pages" in docs/notifications.md.
/// </summary>
public class NotificationPageTests
{
    private const string Ada = "ada@example.com";
    private const string Grace = "grace@example.com";

    private const string Page = "/notifications";
    private const string MarkAllForm = "mark-all-read";

    private static readonly DateTimeOffset Morning = new(2026, 11, 17, 9, 20, 0, TimeSpan.Zero);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_page_lists_what_the_service_told_its_user_newest_first_and_says_what_is_read()
    {
        using var application = new AccountsApplication(clock: new TestClock(Morning));
        var ada = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(ada.Id, Morning, name: "Swing");
        using var client = application.CreateClient();
        await client.SignInAsync(Ada);

        // With nothing to say, the page says so, and has no form.
        var empty = await client.PageAsync(Page);

        Assert.Contains("<title>Notifications</title>", empty);
        Assert.Contains("<h1 id=\"notifications-heading\">Notifications</h1>", empty);
        Assert.Contains("The service has told you nothing yet.", empty);
        Assert.Empty(FormsOn(empty));

        await application.GiveNotificationAsync(ada.Id, agent, Morning.AddMinutes(1), "The first thing that happened.");
        await application.GiveNotificationAsync(
            ada.Id, agentId: null, Morning.AddMinutes(2), "What happened <after> that & more.", NotificationType.KeyInvalid);
        var read = await application.GiveNotificationAsync(
            ada.Id, agent, Morning.AddMinutes(3), "The agent was halted.", NotificationType.AgentHalted);
        await MarkAsync(application, read);

        var markup = await client.PageAsync(Page);
        var page = AgentBrowser.Read(markup);

        // How many there are, and how many of them are not read.
        Assert.Contains("3 notifications, 2 not read, newest first. Page 1 of 1.", page);

        // Each with what it is about, when it was raised, whether it is read, and what it says:
        // the newest first.
        Assert.Contains($"<strong>Agent halted</strong>, {PageTimes.Utc.At(Morning.AddMinutes(3))}. Read.", page);
        Assert.Contains($"<strong>Key invalid</strong>, {PageTimes.Utc.At(Morning.AddMinutes(2))}. Not read.", page);
        Assert.Contains($"<strong>Order or protection failure</strong>, {PageTimes.Utc.At(Morning.AddMinutes(1))}. Not read.", page);
        Assert.True(page.IndexOf("The agent was halted.", StringComparison.Ordinal) < page.IndexOf("What happened", StringComparison.Ordinal));
        Assert.True(page.IndexOf("What happened", StringComparison.Ordinal) < page.IndexOf("The first thing", StringComparison.Ordinal));

        // What a notification says is written as text, and never as markup (NFR-SEC-4).
        Assert.Contains("What happened &lt;after&gt; that &amp; more.", markup);

        // One that is about an agent leads to it, by the name that its user gave it. One that is
        // about none leads nowhere.
        Assert.Equal(2, Regex.Count(markup, Regex.Escape($"<a href=\"/agents/{agent}/state\">State of the agent Swing</a>")));

        // A form for each that is not read, and one for all of them.
        Assert.Equal(3, FormsOn(markup).Count);
        Assert.DoesNotContain($"mark-read-{read}", markup);
    }

    [Fact]
    public async Task A_user_marks_a_notification_as_read()
    {
        using var application = new AccountsApplication(clock: new TestClock(Morning));
        var ada = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(ada.Id, Morning, name: "Swing");
        var first = await application.GiveNotificationAsync(ada.Id, agent, Morning.AddMinutes(1), "The first thing that happened.");
        var second = await application.GiveNotificationAsync(ada.Id, agent, Morning.AddMinutes(2), "The second thing that happened.");
        using var client = application.CreateClient();
        await client.SignInAsync(Ada);

        using (var marked = await SendAsync(client, Page, $"mark-read-{first}"))
        {
            // The page as it is now, by a request that changes nothing.
            Assert.Equal(HttpStatusCode.OK, marked.StatusCode);
            Assert.Equal(Page, marked.RequestMessage!.RequestUri!.PathAndQuery);
        }

        Assert.Equal([(first, true), (second, false)], await ReadAsync(application, ada.Id));

        var page = AgentBrowser.Read(await client.PageAsync(Page));

        Assert.Contains("2 notifications, 1 not read, newest first.", page);
        Assert.Contains($"<strong>Order or protection failure</strong>, {PageTimes.Utc.At(Morning.AddMinutes(1))}. Read.", page);

        // It stays on the page, and has no form any more: sent again, its form is the form of no page.
        Assert.Contains("The first thing that happened.", page);

        using (var again = await SendAsync(client, Page, $"mark-read-{first}"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        }

        Assert.Equal([(first, true), (second, false)], await ReadAsync(application, ada.Id));
    }

    [Fact]
    public async Task A_user_marks_all_as_read_and_not_what_was_raised_after_the_page_was_written()
    {
        using var application = new AccountsApplication(clock: new TestClock(Morning));
        var ada = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(ada.Id, Morning, name: "Swing");
        var first = await application.GiveNotificationAsync(ada.Id, agent, Morning.AddMinutes(1), "The first thing that happened.");
        var second = await application.GiveNotificationAsync(ada.Id, agent, Morning.AddMinutes(2), "The second thing that happened.");
        using var client = application.CreateClient();
        await client.SignInAsync(Ada);

        // The page as Ada reads it: its form names the newest that it shows.
        var shown = await client.PageAsync(Page);

        Assert.Equal(second.ToString(), ThroughOn(shown));

        // Something happens before she presses the button, which her page does not show.
        var since = await application.GiveNotificationAsync(ada.Id, agent, Morning.AddMinutes(3), "What happened since.");

        using (var marked = await client.SendFormAsync(Page, MarkAllForm, Browser.AntiforgeryTokenIn(shown), ("Through", ThroughOn(shown))))
        {
            Assert.Equal(HttpStatusCode.OK, marked.StatusCode);
            Assert.Equal(Page, marked.RequestMessage!.RequestUri!.PathAndQuery);
        }

        // What she was shown is read. What she was not shown is not.
        Assert.Equal([(first, true), (second, true), (since, false)], await ReadAsync(application, ada.Id));
        Assert.Contains("3 notifications, 1 not read, newest first.", AgentBrowser.Read(await client.PageAsync(Page)));

        // A form that names no notification of hers marks nothing.
        var now = await client.PageAsync(Page);

        foreach (var through in new[] { "not-an-id", Guid.NewGuid().ToString(), string.Empty })
        {
            using var nothing = await client.SendFormAsync(Page, MarkAllForm, Browser.AntiforgeryTokenIn(now), ("Through", through));

            Assert.Equal(HttpStatusCode.OK, nothing.StatusCode);
        }

        Assert.Equal([(first, true), (second, true), (since, false)], await ReadAsync(application, ada.Id));
    }

    [Fact]
    public async Task The_notifications_come_a_page_at_a_time()
    {
        using var application = new AccountsApplication(clock: new TestClock(Morning));
        var ada = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(ada.Id, Morning, name: "Swing");

        // One more than a page holds, each a minute after the one before.
        for (var number = 1; number <= NotificationList.PageSize + 1; number++)
        {
            await application.GiveNotificationAsync(ada.Id, agent, Morning.AddMinutes(number), $"What happened as number {number}.");
        }

        using var client = application.CreateClient();
        await client.SignInAsync(Ada);

        var first = AgentBrowser.Read(await client.PageAsync(Page));

        // The newest 25, and the way to the older ones.
        Assert.Contains("26 notifications, 26 not read, newest first. Page 1 of 2.", first);
        Assert.Contains("What happened as number 26.", first);
        Assert.Contains("What happened as number 2.", first);
        Assert.DoesNotContain("What happened as number 1.", first);
        Assert.Contains("<a href=\"/notifications?page=2\">Older notifications</a>", first);
        Assert.DoesNotContain("Newer notifications", first);

        var second = AgentBrowser.Read(await client.PageAsync($"{Page}?page=2"));

        Assert.Contains("Page 2 of 2.", second);
        Assert.Contains("What happened as number 1.", second);
        Assert.DoesNotContain("What happened as number 2.", second);
        Assert.Contains("<a href=\"/notifications\">Newer notifications</a>", second);
        Assert.DoesNotContain("Older notifications", second);

        // A form of the second page leads back to the second page.
        var oldest = (await ReadAsync(application, ada.Id))[0].Id;

        using var marked = await SendAsync(client, $"{Page}?page=2", $"mark-read-{oldest}");

        Assert.Equal($"{Page}?page=2", marked.RequestMessage!.RequestUri!.PathAndQuery);
    }

    [Theory]
    [InlineData("?page=0")]
    [InlineData("?page=01")]
    [InlineData("?page=two")]
    [InlineData("?page=-1")]
    [InlineData("?page=2")]
    [InlineData("?page=1&page=1")]
    public async Task An_address_that_names_a_page_which_there_is_not_is_not_found(string query)
    {
        using var application = new AccountsApplication(clock: new TestClock(Morning));
        var ada = await application.RegisterVerifiedAsync(Ada);
        await application.GiveNotificationAsync(ada.Id, agentId: null, Morning, "Something that happened.");
        using var client = application.CreateClient();
        await client.SignInAsync(Ada);

        using var answer = await client.GetAsync($"{Page}{query}", Cancellation);

        // Nothing else is shown in its place.
        Assert.Equal(HttpStatusCode.NotFound, answer.StatusCode);
        Assert.DoesNotContain("Something that happened.", await answer.Content.ReadAsStringAsync(Cancellation));
    }

    [Fact]
    public async Task A_user_neither_reads_nor_marks_the_notifications_of_another()
    {
        using var application = new AccountsApplication(clock: new TestClock(Morning));
        var grace = await application.RegisterVerifiedAsync(Grace);
        var graces = await application.GiveAgentAsync(grace.Id, Morning, name: "Graces");
        var hers = await application.GiveNotificationAsync(grace.Id, graces, Morning.AddMinutes(1), "Something about the agent of Grace.");
        var before = application.RowsOf(grace.Id);

        var ada = await application.RegisterVerifiedAsync(Ada);
        var own = await application.GiveNotificationAsync(ada.Id, agentId: null, Morning.AddMinutes(2), "Something for Ada.");
        using var client = application.CreateClient();
        await client.SignInAsync(Ada);

        // Ada's page says nothing of it, and counts her own alone.
        var shown = await client.PageAsync(Page);
        var page = AgentBrowser.Read(shown);

        Assert.Contains("1 notification, 1 not read, newest first.", page);
        Assert.DoesNotContain("Something about the agent of Grace.", page);
        Assert.DoesNotContain(hers.ToString(), shown);

        // The link of the bar says that its page is open, so it carries more than its address.
        Assert.Matches("<a href=\"/notifications\" aria-current=\"page\">Notifications \\(1\\)</a>", shown);

        // The form of Grace's notification is the form of no page of Ada's.
        using (var marked = await SendAsync(client, Page, $"mark-read-{hers}"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, marked.StatusCode);
        }

        // And the form that marks all, sent with Grace's notification as the newest that was
        // shown, marks nothing: not Grace's, and not Ada's own.
        using (var all = await client.SendFormAsync(Page, MarkAllForm, Browser.AntiforgeryTokenIn(shown), ("Through", hers.ToString())))
        {
            Assert.Equal(HttpStatusCode.OK, all.StatusCode);
        }

        Assert.Equal([(own, false)], await ReadAsync(application, ada.Id));
        Assert.Equal(before, application.RowsOf(grace.Id));
    }

    [Fact]
    public async Task The_bar_of_every_page_links_to_the_notifications_with_how_many_are_not_read()
    {
        using var application = new AccountsApplication(clock: new TestClock(Morning));
        var ada = await application.RegisterVerifiedAsync(Ada);
        using var client = application.CreateClient();

        // A visitor has no notifications, and no link to them.
        Assert.DoesNotContain("href=\"/notifications\"", await client.PageAsync("/"));

        await client.SignInAsync(Ada);

        // With nothing that is not read, the link says no number.
        Assert.Contains("<a href=\"/notifications\">Notifications</a>", await client.PageAsync("/"));

        var first = await application.GiveNotificationAsync(ada.Id, agentId: null, Morning.AddMinutes(1), "The first thing that happened.");
        await application.GiveNotificationAsync(ada.Id, agentId: null, Morning.AddMinutes(2), "The second thing that happened.");

        Assert.Contains("<a href=\"/notifications\">Notifications (2)</a>", await client.PageAsync("/"));
        Assert.Contains("<a href=\"/notifications\">Notifications (2)</a>", await client.PageAsync("/account"));

        // What is read is not counted.
        await MarkAsync(application, first);

        Assert.Contains("<a href=\"/notifications\">Notifications (1)</a>", await client.PageAsync("/"));
    }

    [Fact]
    public async Task The_page_of_an_agent_marks_what_is_about_the_agent_as_read()
    {
        using var application = new AccountsApplication(clock: new TestClock(Morning));
        var ada = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(ada.Id, Morning, name: "Swing");
        var first = await application.GiveNotificationAsync(ada.Id, agent, Morning.AddMinutes(1), "The first thing that happened.");
        var second = await application.GiveNotificationAsync(ada.Id, agent, Morning.AddMinutes(2), "The second thing that happened.");
        using var client = application.CreateClient();
        await client.SignInAsync(Ada);

        var state = AgentBrowser.AgentStatePage(agent);

        // The page of the agent leads to every notification of its user.
        Assert.Contains("<a href=\"/notifications\">All your notifications</a>", await client.PageAsync(state));

        using (var marked = await SendAsync(client, state, $"mark-read-{first}"))
        {
            // The page of the agent as it is now, by a request that changes nothing.
            Assert.Equal(HttpStatusCode.OK, marked.StatusCode);
            Assert.Equal(state, marked.RequestMessage!.RequestUri!.PathAndQuery);
        }

        Assert.Equal([(first, true), (second, false)], await ReadAsync(application, ada.Id));

        // It is off the page of the agent, which says what is not read, and stays on the page
        // of the notifications.
        var page = AgentBrowser.Read(await client.PageAsync(state));

        Assert.DoesNotContain("The first thing that happened.", page);
        Assert.Contains("The second thing that happened.", page);
        Assert.Contains("The first thing that happened.", AgentBrowser.Read(await client.PageAsync(Page)));
    }

    [Fact]
    public async Task A_notification_stays_when_its_agent_is_deleted_and_then_names_none()
    {
        using var application = new AccountsApplication(clock: new TestClock(Morning));
        var ada = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(ada.Id, Morning, name: "Swing");
        await application.GiveNotificationAsync(ada.Id, agent, Morning.AddMinutes(1), "Something about the agent.");
        using var client = application.CreateClient();
        await client.SignInAsync(Ada);

        Assert.Contains("State of the agent Swing", await client.PageAsync(Page));

        await using (var context = await application.Contexts.CreateDbContextAsync(Cancellation))
        {
            await context.Database.ExecuteSqlAsync($"DELETE FROM agents WHERE id = {agent}", Cancellation);
        }

        var page = await client.PageAsync(Page);

        // It belongs to the account, and not to the agent: it is still said, with no link to a
        // page that is not there.
        Assert.Contains("Something about the agent.", page);
        Assert.DoesNotContain("State of the agent", page);
        Assert.DoesNotContain($"/agents/{agent}", page);
    }

    [Theory]
    [InlineData("/notifications")]
    [InlineData("/notifications?page=2")]
    public async Task A_visitor_who_opens_the_notifications_is_sent_to_sign_in(string page)
    {
        using var application = new AccountsApplication(clock: new TestClock(Morning));
        using var client = application.CreateClient();

        var (address, shown) = await client.OpenAsync(page);

        Assert.Equal($"{SessionAuthentication.SignInPath}?returnUrl={Uri.EscapeDataString(page)}", address);
        Assert.Contains("<h1>Sign in</h1>", shown);
    }

    [Fact]
    public async Task A_form_without_the_token_of_its_page_changes_nothing()
    {
        using var application = new AccountsApplication(clock: new TestClock(Morning));
        var ada = await application.RegisterVerifiedAsync(Ada);
        var only = await application.GiveNotificationAsync(ada.Id, agentId: null, Morning, "Something that happened.");
        using var client = application.CreateClient();
        await client.SignInAsync(Ada);

        // Something that is not the page sends the forms, with a token that the page did not give.
        using (var forged = await client.SendFormAsync(Page, $"mark-read-{only}", "not-the-token"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
        }

        using (var forged = await client.SendFormAsync(Page, MarkAllForm, "not-the-token", ("Through", only.ToString())))
        {
            Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
        }

        Assert.Equal([(only, false)], await ReadAsync(application, ada.Id));
    }

    /// <summary>Posts a form of a page with the token that the page carries, and gives the answer itself.</summary>
    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string path, string form) =>
        await client.SendFormAsync(path, form, Browser.AntiforgeryTokenIn(await client.PageAsync(path)));

    /// <summary>The names of the forms that a page has, in the order that they are on it.</summary>
    private static IReadOnlyList<string> FormsOn(string page) =>
        [.. Regex.Matches(page, "name=\"_handler\" value=\"(mark-[^\"]+)\"").Select(form => form.Groups[1].Value)];

    /// <summary>The newest notification that a page says it shows, in the form that marks all as read.</summary>
    private static string ThroughOn(string page) =>
        Regex.Match(page, "name=\"Through\" value=\"([^\"]+)\"").Groups[1].Value;

    /// <summary>The notifications of a user in the order that they were raised in, each with whether it is read.</summary>
    private static async Task<IReadOnlyList<(Guid Id, bool IsRead)>> ReadAsync(AccountsApplication application, Guid userId)
    {
        await using var context = await application.Contexts.CreateDbContextAsync(Cancellation);

        var stored = await context.Set<Notification>()
            .AsNoTracking()
            .Where(notification => notification.UserId == userId)
            .OrderBy(notification => notification.CreatedAt)
            .Select(notification => new { notification.Id, notification.IsRead })
            .ToListAsync(Cancellation);

        return [.. stored.Select(notification => (notification.Id, notification.IsRead))];
    }

    /// <summary>Marks a notification as read, as a row: for a test that is about what a page says of one.</summary>
    private static async Task MarkAsync(AccountsApplication application, Guid notificationId)
    {
        await using var context = await application.Contexts.CreateDbContextAsync(Cancellation);

        await context.Set<Notification>()
            .Where(notification => notification.Id == notificationId)
            .ExecuteUpdateAsync(row => row.SetProperty(notification => notification.IsRead, true), Cancellation);
    }
}
