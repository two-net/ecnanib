using System.Reflection;
using Ecnanib.Platform.Availability;
using Ecnanib.Tests.Accessibility;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.WebUi;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Reliability;

/// <summary>
/// A banner announces maintenance in advance (NFR-REL-6). It is under the bar of every page, for
/// a visitor as for a user, from the moment that the maintenance is announced until it is over:
/// it says from when to when the service may not answer, or until when once it has started, and
/// that no position is closed for it. It writes its times as the page does (NFR-USA-3), keeps to
/// the frame and to the rules of the markup (FR-UI-1, NFR-USA-1), and fails no page. See
/// "Announced maintenance" in docs/reliability.md.
/// </summary>
public class MaintenanceNoticeTests
{
    private const string Ada = "ada@example.com";
    private const string Banner = "<section class=\"site-notice\" aria-label=\"Maintenance\">";
    private const string NoPositionIsClosed = "Open positions keep their protective orders on the exchange, and no position is closed for it.";
    private const string NotRead = "The maintenance that is announced could not be read for the banner of a page";

    private static readonly DateTimeOffset Morning = new(2026, 11, 26, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Starts = new(2026, 11, 28, 2, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Ends = new(2026, 11, 28, 4, 0, 0, TimeSpan.Zero);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Every_page_tells_a_visitor_and_a_user_of_maintenance_that_is_announced_and_none_does_when_none_is()
    {
        using var application = new AccountsApplication(clock: new TestClock(Morning));
        using var visitor = application.CreateClient();
        using var user = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await user.SignInAsync(Ada);

        // Every page there is, so that a page which is added later is looked at too. A page that
        // is not for whoever asks answers with another page, which has the frame as well.
        var paths = Pages().ToList();

        Assert.Contains(WebUiModule.HomePath, paths);
        Assert.Contains(WebUiModule.DashboardPath, paths);
        Assert.Contains(WebUiModule.OperatorMaintenancePath, paths);

        foreach (var path in paths)
        {
            foreach (var client in new[] { visitor, user })
            {
                using var answer = await client.GetAsync(path, Cancellation);

                Assert.DoesNotContain("site-notice", await answer.Content.ReadAsStringAsync(Cancellation));
            }
        }

        StoredMaintenance.Give(application.Database, Starts, Ends, announcedAt: Morning);

        const string said =
            Banner + "<p role=\"status\">Maintenance is announced from 28 November 2026, 02:00 UTC to 28 November 2026, 04:00 UTC. " +
            "The service may not answer during it. " + NoPositionIsClosed + "</p></section>";

        var told = 0;

        foreach (var path in paths)
        {
            foreach (var client in new[] { visitor, user })
            {
                using var answer = await client.GetAsync(path, Cancellation);
                var page = await answer.Content.ReadAsStringAsync(Cancellation);

                // A page that is not for the user who asks is answered with the page that says
                // that there is no page, in place of it. The framework writes that page at once,
                // without what the frame asks the database for, so it is without the banner:
                // see "Not built yet" in docs/reliability.md.
                if (!answer.IsSuccessStatusCode)
                {
                    Assert.Contains("<h1>Page not found</h1>", page);
                    continue;
                }

                Assert.True(page.Contains(said, StringComparison.Ordinal), $"The page at {path} does not tell of the maintenance that is announced.");

                // Once, under the bar and before the content, which the link past the bar leads to.
                Assert.Equal(1, System.Text.RegularExpressions.Regex.Count(page, "site-notice"));
                Assert.True(
                    page.IndexOf("</header>", StringComparison.Ordinal) < page.IndexOf(Banner, StringComparison.Ordinal)
                    && page.IndexOf(Banner, StringComparison.Ordinal) < page.IndexOf("<main id=\"content\"", StringComparison.Ordinal),
                    $"The page at {path} does not have the banner between its bar and its content.");

                told++;
            }
        }

        // All of them but the pages of an operator, which are not for this user.
        Assert.True(told > paths.Count, $"Only {told} answers of {paths.Count} pages told of the maintenance.");
    }

    [Fact]
    public async Task The_banner_says_that_maintenance_is_under_way_once_it_has_started_and_is_gone_when_it_is_over()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        using var visitor = application.CreateClient();

        Assert.DoesNotContain(Banner, await visitor.PageAsync("/"));

        StoredMaintenance.Give(application.Database, Starts, Ends, announcedAt: Morning);

        // A minute before it starts it is still announced, and from its start on it is under way.
        clock.Set(Starts.AddMinutes(-1));

        Assert.Contains("Maintenance is announced from 28 November 2026, 02:00 UTC to 28 November 2026, 04:00 UTC.", await visitor.PageAsync("/"));

        clock.Set(Starts);

        Assert.Contains(
            Banner + "<p role=\"status\">Maintenance is under way until 28 November 2026, 04:00 UTC. The service may not answer for a " +
            "while. " + NoPositionIsClosed + "</p></section>",
            await visitor.PageAsync("/"));

        clock.Set(Ends.AddMinutes(-1));

        Assert.Contains("Maintenance is under way until 28 November 2026, 04:00 UTC.", await visitor.PageAsync("/"));

        // From its end on no page tells of it.
        clock.Set(Ends);

        Assert.DoesNotContain("site-notice", await visitor.PageAsync("/"));
    }

    [Fact]
    public async Task Of_several_the_banner_tells_of_what_is_under_way_and_else_of_what_starts_next()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        using var visitor = application.CreateClient();
        await visitor.PageAsync("/");

        // Two are announced, the later one first.
        StoredMaintenance.Give(application.Database, Starts.AddDays(7), Ends.AddDays(7), announcedAt: Morning);
        StoredMaintenance.Give(application.Database, Starts, Ends, announcedAt: Morning.AddMinutes(1));

        var next = await visitor.PageAsync("/");

        Assert.Contains("Maintenance is announced from 28 November 2026, 02:00 UTC", next);
        Assert.DoesNotContain("5 December 2026", next);

        clock.Set(Starts.AddHours(1));

        Assert.Contains("Maintenance is under way until 28 November 2026, 04:00 UTC.", await visitor.PageAsync("/"));

        // The first is over: the page tells of the one that is still to come.
        clock.Set(Ends.AddHours(1));

        Assert.Contains(
            "Maintenance is announced from 5 December 2026, 02:00 UTC to 5 December 2026, 04:00 UTC.", await visitor.PageAsync("/"));
    }

    [Fact]
    public async Task The_banner_writes_its_times_as_the_page_does()
    {
        using var application = new AccountsApplication(clock: new TestClock(Morning));
        using var visitor = application.CreateClient();
        using var user = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);

        // Her device says where it is as she signs in, and her pages are in its time zone.
        await user.SignInFromAsync(Ada, "Asia/Kolkata");

        StoredMaintenance.Give(application.Database, Starts, Ends, announcedAt: Morning);

        const string inKolkata =
            "Maintenance is announced from 28 November 2026, 07:30 UTC&#x2B;5:30 to 28 November 2026, 09:30 UTC&#x2B;5:30.";

        Assert.Contains(inKolkata, await user.PageAsync(WebUiModule.DashboardPath));

        // A visitor whose device has said none is told in UTC, and one whose device has is told
        // in its time zone.
        Assert.Contains("Maintenance is announced from 28 November 2026, 02:00 UTC to 28 November 2026, 04:00 UTC.", await visitor.PageAsync("/"));

        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.TryAddWithoutValidation("Cookie", "__Host-ecnanib-time-zone=" + Uri.EscapeDataString("Asia/Kolkata"));
        using var answer = await visitor.SendAsync(request, Cancellation);

        Assert.Contains(inKolkata, await answer.Content.ReadAsStringAsync(Cancellation));
    }

    [Fact]
    public async Task A_page_with_the_banner_keeps_to_the_rules_of_the_markup_and_the_banner_holds_nothing_to_act_on()
    {
        using var application = new AccountsApplication(clock: new TestClock(Morning));
        using var visitor = application.CreateClient();
        using var user = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await user.SignInAsync(Ada);

        StoredMaintenance.Give(application.Database, Starts, Ends, announcedAt: Morning);

        foreach (var (client, path) in new[] { (visitor, "/"), (visitor, "/sign-in"), (user, WebUiModule.DashboardPath), (user, "/account") })
        {
            var page = AccessibleMarkup.Read(await client.PageAsync(path));

            AccessibleMarkup.AssertIsAccessible(path, page);

            // It is a part of the page with a name, directly in the page, and holds one
            // sentence that says how things are: no link, no form, and nothing that takes the focus.
            var banner = Assert.Single(page.QuerySelectorAll("section.site-notice"));

            Assert.Equal("body", banner.ParentElement?.LocalName);
            Assert.Equal("Maintenance", banner.GetAttribute("aria-label"));
            Assert.Equal("status", Assert.Single(banner.Children).GetAttribute("role"));
            Assert.Empty(banner.QuerySelectorAll("a, form, button, input, [tabindex], [autofocus]"));
        }
    }

    [Fact]
    public async Task An_announcement_that_cannot_be_read_fails_no_page()
    {
        using var application = new AccountsApplication(
            clock: new TestClock(Morning),
            host: host => host.ConfigureServices(services => services.AddSingleton<IAnnouncedMaintenance>(new Unreadable())));
        using var visitor = application.CreateClient();

        var page = await visitor.PageAsync("/");

        // The page is there, without the banner, and the log says why.
        Assert.Contains("<main id=\"content\"", page);
        Assert.DoesNotContain("site-notice", page);

        var said = Assert.Single(application.Events(), written => written.Template == NotRead);

        Assert.Equal("Warning", said.Level);
    }

    /// <summary>The path of every page of the application that names nothing in its address.</summary>
    private static IEnumerable<string> Pages() =>
        typeof(Program).Assembly.GetTypes()
            .SelectMany(component => component.GetCustomAttributes<RouteAttribute>())
            .Select(route => route.Template)
            .Where(route => !route.Contains('{', StringComparison.Ordinal))
            .Distinct()
            .Order(StringComparer.Ordinal);

    /// <summary>What reads the maintenance that is announced, when the database does not answer it.</summary>
    private sealed class Unreadable : IAnnouncedMaintenance
    {
        public Task<AnnouncedMaintenance?> NextAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The database does not answer.");

        public Task<IReadOnlyList<AnnouncedMaintenance>> NotOverAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The database does not answer.");
    }
}
