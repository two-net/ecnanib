using System.Net;
using Ecnanib.Accounts;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.Tests.Logging;
using Ecnanib.Tests.Persistence;
using Ecnanib.Tests.Settings;
using Ecnanib.WebUi;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Reliability;

/// <summary>
/// A banner announces maintenance in advance (NFR-REL-6). An operator announces it, on a page of
/// their own: from when to when the service may not answer. They cancel it before it starts, and
/// end it while it is under way. What is announced is stored in the database, each of the three
/// is recorded in the audit trail, the page shows nothing of any user (FR-OPS-5), and a user who
/// is not an operator reaches none of it (NFR-SEC-3). Maintenance that is announced later than
/// the configuration file asks for is told of all the same, and is not left out of the
/// availability (NFR-REL-1). See "Announced maintenance" in docs/reliability.md.
/// </summary>
public class MaintenanceAnnouncementTests
{
    private const string Ada = "ada@example.com";
    private const string Olive = "olive@example.com";

    private const string Page = WebUiModule.OperatorMaintenancePath;
    private const string AnnounceForm = "announce-maintenance";
    private const string ChangeForm = "change-maintenance";

    private const string NoneAnnounced = "No maintenance is announced.";
    private const string Done = "<p class=\"form-done\" role=\"status\">The maintenance is announced. Every page tells of it from now on.</p>";
    private const string Banner = "<section class=\"site-notice\" aria-label=\"Maintenance\">";

    private const string AnnouncedTemplate =
        "User {UserId} announced maintenance {MaintenanceId} from {StartsAt} to {EndsAt}. Announced in advance: {InAdvance}. It acts on no agent";
    private const string CancelledTemplate = "User {UserId} cancelled maintenance {MaintenanceId} before it started: no page tells of it any more";
    private const string EndedTemplate = "User {UserId} ended maintenance {MaintenanceId}, which was under way: it is over from now";
    private const string RefusedTemplate = "User {UserId} was refused announcing, cancelling, or ending maintenance, which is for an operator";
    private const string RefusedPageTemplate = "User {UserId} was refused {Path}, which is not for them";

    // A Thursday morning. The file of the tests asks for 12 hours of notice, and lets
    // maintenance last 6 hours.
    private static readonly DateTimeOffset Morning = new(2026, 11, 26, 9, 0, 0, TimeSpan.Zero);

    // Two days later, in the night, for two hours.
    private static readonly DateTimeOffset Starts = new(2026, 11, 28, 2, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Ends = new(2026, 11, 28, 4, 0, 0, TimeSpan.Zero);

    private static readonly (string Name, string Value)[] TwoNightsOn = [("Starts", "2026-11-28 02:00"), ("Ends", "2026-11-28 04:00")];

    [Fact]
    [ShowsAuditOf(AuditAction.MaintenanceAnnounced)]
    public async Task An_operator_announces_maintenance_and_it_is_stored_recorded_and_told_of()
    {
        var clock = new TestClock(Morning);
        using var application = Application(clock, host: host => host.From("203.0.113.7"));
        using var client = application.CreateClient();
        var olive = await application.RegisterVerifiedAsync(Olive);
        await client.SignInAsync(Olive);

        // The screen of an operator leads to the page, which says what an announcement does and
        // what it does not, that none is announced, and has the form.
        Assert.Matches($"<a href=\"{Page}\"[^>]*>Maintenance</a>", await client.PageAsync(WebUiModule.OperatorStatusPath));

        var before = await client.PageAsync(Page);

        Assert.Contains("<h1>Maintenance</h1>", before);
        Assert.Contains("An announcement pauses no agent, takes no order off the exchange, and closes no position.", before);
        Assert.Contains("announced at least 12 hours before it starts", before);
        Assert.Contains(NoneAnnounced, before);
        Assert.Contains("<button type=\"submit\">Announce maintenance</button>", before);
        Assert.DoesNotContain(Banner, before);
        Assert.Empty(StoredMaintenance.All(application.Database));

        clock.Set(Morning.AddMinutes(5));
        var (path, page) = await client.SubmitAsync(Page, AnnounceForm, TwoNightsOn);

        // She is back on the page, which says that it is announced, and shows it with the
        // button that cancels it. The banner is on this page as on every other.
        Assert.Equal(Page, path);
        Assert.Contains(Done, page);
        Assert.DoesNotContain(NoneAnnounced, page);
        Assert.Matches(
            "<th scope=\"row\" id=\"maintenance-1\">28 November 2026, 02:00 UTC</th>\\s*<td>28 November 2026, 04:00 UTC</td>\\s*" +
            "<td>Announced</td>\\s*<td>Yes</td>",
            page);
        Assert.Contains(">Cancel</button>", page);
        Assert.DoesNotContain(">End now</button>", page);
        Assert.Contains(
            Banner + "<p role=\"status\">Maintenance is announced from 28 November 2026, 02:00 UTC to 28 November 2026, 04:00 UTC. " +
            "The service may not answer during it. Open positions keep their protective orders on the exchange, and no position is " +
            "closed for it.</p></section>",
            page);

        // It is a row of the database, with when it was announced, and that this was in advance:
        // more than the 12 hours that the file asks for.
        var stored = Assert.Single(StoredMaintenance.All(application.Database));

        Assert.Equal((Starts, Ends, Morning.AddMinutes(5), true), (stored.StartsAt, stored.EndsAt, stored.AnnouncedAt, stored.InAdvance));

        // And an event of the audit trail: who, what, when, and from where (FR-OPS-5).
        var recorded = Assert.Single(StoredAuditEvent.All(application.Database), audited => audited.Target == $"maintenance:{stored.Id}");

        Assert.Equal(
            (olive.Id, "MaintenanceAnnounced", Morning.AddMinutes(5), "203.0.113.7"),
            (recorded.UserId, recorded.Action, recorded.OccurredAt, recorded.Origin));

        // The log says it by the id of the account, and no more of her.
        var said = Assert.Single(application.Events(), written => written.Template == AnnouncedTemplate);

        Assert.Equal((olive.Id.ToString(), stored.Id.ToString()), (said.Text("UserId"), said.Text("MaintenanceId")));

        // The row says nothing of who announced it, and nothing stored says who is an operator.
        Assert.DoesNotContain("operator", application.EverythingStored(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Maintenance_that_is_announced_late_is_told_of_and_the_page_says_that_it_counts()
    {
        var clock = new TestClock(Morning);
        using var application = Application(clock);
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Olive);
        await client.SignInAsync(Olive);

        // In three hours, where the file asks for twelve.
        var (path, page) = await client.SubmitAsync(Page, AnnounceForm, ("Starts", "2026-11-26 12:00"), ("Ends", "2026-11-26 13:00"));

        Assert.Equal(Page, path);
        Assert.Contains(
            "<p class=\"form-done\" role=\"status\">The maintenance is announced. Every page tells of it from now on. It was announced " +
            "later than 12 hours before it starts, so what the service is down for during it counts.</p>",
            page);
        Assert.Matches("<td>Announced</td>\\s*<td>No: it was announced late</td>", page);
        Assert.Contains(Banner + "<p role=\"status\">Maintenance is announced from 26 November 2026, 12:00 UTC", page);

        Assert.False(Assert.Single(StoredMaintenance.All(application.Database)).InAdvance);
        Assert.False(Assert.Single(application.Events(), written => written.Template == AnnouncedTemplate)["InAdvance"].GetBoolean());
    }

    [Fact]
    [ShowsAuditOf(AuditAction.MaintenanceCancelled)]
    public async Task An_operator_cancels_maintenance_that_has_not_started_and_no_page_tells_of_it_any_more()
    {
        var clock = new TestClock(Morning);
        using var application = Application(clock, host: host => host.From("203.0.113.7"));
        using var client = application.CreateClient();
        var olive = await application.RegisterVerifiedAsync(Olive);
        await client.SignInAsync(Olive);

        await client.SubmitAsync(Page, AnnounceForm, TwoNightsOn);
        var announced = Assert.Single(StoredMaintenance.All(application.Database));

        clock.Set(Morning.AddMinutes(30));
        var (path, page) = await client.SubmitAsync(Page, ChangeForm, ("Cancel", announced.Id.ToString()));

        Assert.Equal(Page, path);
        Assert.Contains(NoneAnnounced, page);
        Assert.DoesNotContain(Banner, page);
        Assert.DoesNotContain(Done, page);

        // The row is gone, and the trail says what became of it.
        Assert.Empty(StoredMaintenance.All(application.Database));
        Assert.Equal(
            [
                (olive.Id, "MaintenanceAnnounced", Morning, "203.0.113.7"),
                (olive.Id, "MaintenanceCancelled", Morning.AddMinutes(30), "203.0.113.7"),
            ],
            StoredAuditEvent.All(application.Database)
                .Where(audited => audited.Target == $"maintenance:{announced.Id}")
                .Select(audited => (audited.UserId, audited.Action, audited.OccurredAt, audited.Origin!)));

        var said = Assert.Single(application.Events(), written => written.Template == CancelledTemplate);

        Assert.Equal((olive.Id.ToString(), announced.Id.ToString()), (said.Text("UserId"), said.Text("MaintenanceId")));
    }

    [Fact]
    [ShowsAuditOf(AuditAction.MaintenanceEnded)]
    public async Task An_operator_ends_maintenance_that_is_under_way_and_it_is_over_from_then_on()
    {
        var clock = new TestClock(Morning);
        using var application = Application(clock, host: host => host.From("203.0.113.7"));
        using var client = application.CreateClient();
        var olive = await application.RegisterVerifiedAsync(Olive);
        await client.SignInAsync(Olive);

        await client.SubmitAsync(Page, AnnounceForm, TwoNightsOn);
        var announced = Assert.Single(StoredMaintenance.All(application.Database));

        // Half an hour into it, every page says that it is under way, and until when. The page
        // of the operator has the button that ends it, and none that cancels it.
        await LaterAsync(clock, client, Starts.AddMinutes(30));

        var underWay = await client.PageAsync(Page);

        Assert.Contains(
            Banner + "<p role=\"status\">Maintenance is under way until 28 November 2026, 04:00 UTC. The service may not answer for a " +
            "while. Open positions keep their protective orders on the exchange, and no position is closed for it.</p></section>",
            underWay);
        Assert.Matches("<td>Under way</td>\\s*<td>Yes</td>", underWay);
        Assert.Contains(">End now</button>", underWay);
        Assert.DoesNotContain(">Cancel</button>", underWay);

        var (path, page) = await client.SubmitAsync(Page, ChangeForm, ("End", announced.Id.ToString()));

        Assert.Equal(Page, path);
        Assert.Contains(NoneAnnounced, page);
        Assert.DoesNotContain(Banner, page);

        // The row stays, with its end where it was ended: what was left out of the availability
        // until then stays left out, and nothing after it is.
        Assert.Equal(
            [announced with { EndsAt = Starts.AddMinutes(30) }],
            StoredMaintenance.All(application.Database));

        var recorded = StoredAuditEvent.All(application.Database).Last(audited => audited.Target == $"maintenance:{announced.Id}");

        Assert.Equal(
            (olive.Id, "MaintenanceEnded", Starts.AddMinutes(30), "203.0.113.7"),
            (recorded.UserId, recorded.Action, recorded.OccurredAt, recorded.Origin));
        Assert.Single(application.Events(), written => written.Template == EndedTemplate);
    }

    [Theory]

    // No date with a time of day: words, nothing, a date alone, and a day that there is none of.
    [InlineData("soon", "2026-11-28 04:00", "maintenance-starts", "Enter a date and a time of day, as in 2026-11-28 02:00.")]
    [InlineData("2026-11-28 02:00", "", "maintenance-ends", "Enter a date and a time of day, as in 2026-11-28 02:00.")]
    [InlineData("2026-11-28", "2026-11-28 04:00", "maintenance-starts", "Enter a date and a time of day, as in 2026-11-28 02:00.")]
    [InlineData("2026-11-28 02:00", "2026-11-31 04:00", "maintenance-ends", "Enter a date and a time of day, as in 2026-11-28 02:00.")]

    // Now, and earlier than now: maintenance is announced before it starts.
    [InlineData("2026-11-26 09:00", "2026-11-26 10:00", "maintenance-starts", "Maintenance is announced before it starts. Enter a start that is later than now.")]
    [InlineData("2026-11-25 02:00", "2026-11-25 04:00", "maintenance-starts", "Maintenance is announced before it starts. Enter a start that is later than now.")]

    // An end that is the start, or before it.
    [InlineData("2026-11-28 02:00", "2026-11-28 02:00", "maintenance-ends", "Enter an end that is later than the start.")]
    [InlineData("2026-11-28 02:00", "2026-11-28 01:00", "maintenance-ends", "Enter an end that is later than the start.")]

    // A minute longer than the six hours of the file.
    [InlineData("2026-11-28 02:00", "2026-11-28 08:01", "maintenance-ends", "Maintenance lasts 6 hours at the most. Enter an earlier end.")]
    public async Task Maintenance_that_cannot_be_announced_as_typed_is_refused_at_its_field_and_nothing_is_stored(
        string starts, string ends, string field, string says)
    {
        using var application = Application(new TestClock(Morning));
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Olive);
        await client.SignInAsync(Olive);

        var (path, page) = await client.SubmitAsync(Page, AnnounceForm, ("Starts", starts), ("Ends", ends));

        // The page again, with what is wrong said at the field, which is marked as wrong and
        // names what says so. What was typed is kept.
        Assert.Equal(Page, path);
        Assert.Contains($"<p id=\"{field}-error\" class=\"field-error\">{says}</p>", page);
        Assert.Matches($"<input id=\"{field}\"[^>]* aria-invalid=\"true\" aria-describedby=\"{field}-error {field}-hint\"", page);
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Count(page, "aria-invalid=\"true\""));
        Assert.Contains($"value=\"{starts}\"", page);
        Assert.DoesNotContain(Done, page);
        Assert.DoesNotContain(Banner, page);

        Assert.Empty(StoredMaintenance.All(application.Database));
        Assert.DoesNotContain(StoredAuditEvent.All(application.Database), audited => audited.Action.StartsWith("Maintenance", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Maintenance_of_the_longest_that_the_file_lets_it_last_is_announced()
    {
        using var application = Application(new TestClock(Morning));
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Olive);
        await client.SignInAsync(Olive);

        // Typed with blanks around it, and with the letter that a moment has in a file or a log.
        var (_, page) = await client.SubmitAsync(Page, AnnounceForm, ("Starts", " 2026-11-28 02:00 "), ("Ends", "2026-11-28T08:00"));

        Assert.Contains(Done, page);

        var stored = Assert.Single(StoredMaintenance.All(application.Database));

        Assert.Equal(TimeSpan.FromHours(6), stored.EndsAt - stored.StartsAt);
    }

    [Fact]
    public async Task A_form_that_is_sent_twice_or_for_what_is_not_to_be_changed_any_more_changes_and_records_nothing()
    {
        var clock = new TestClock(Morning);
        using var application = Application(clock);
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Olive);
        await client.SignInAsync(Olive);

        await client.SubmitAsync(Page, AnnounceForm, TwoNightsOn);
        var announced = Assert.Single(StoredMaintenance.All(application.Database));
        var token = Browser.AntiforgeryTokenIn(await client.PageAsync(Page));

        // Ended before it has started: it is not under way, so nothing ends.
        var (path, _) = await client.PostAsync(Page, ChangeForm, token, ("End", announced.Id.ToString()));

        Assert.Equal(Page, path);
        Assert.Equal([announced], StoredMaintenance.All(application.Database));

        // Cancelled twice, as a button that is pressed twice sends its form: the first one
        // cancels, and the second finds nothing to cancel. So does one for an id that there is none of.
        await client.PostAsync(Page, ChangeForm, token, ("Cancel", announced.Id.ToString()));
        (path, var page) = await client.PostAsync(Page, ChangeForm, token, ("Cancel", announced.Id.ToString()));
        await client.PostAsync(Page, ChangeForm, token, ("Cancel", Guid.NewGuid().ToString()));

        Assert.Equal(Page, path);
        Assert.Contains(NoneAnnounced, page);
        Assert.Equal(
            ["MaintenanceAnnounced", "MaintenanceCancelled"],
            StoredAuditEvent.All(application.Database)
                .Where(audited => audited.Action.StartsWith("Maintenance", StringComparison.Ordinal))
                .Select(audited => audited.Action));
        Assert.Single(application.Events(), written => written.Template == CancelledTemplate);

        // Maintenance that has started is not cancelled: it is ended. And one that is over is neither.
        await client.SubmitAsync(Page, AnnounceForm, TwoNightsOn);
        var second = Assert.Single(StoredMaintenance.All(application.Database));

        await LaterAsync(clock, client, Starts.AddMinutes(10));
        token = Browser.AntiforgeryTokenIn(await client.PageAsync(Page));
        await client.PostAsync(Page, ChangeForm, token, ("Cancel", second.Id.ToString()));

        Assert.Equal([second], StoredMaintenance.All(application.Database));

        await LaterAsync(clock, client, Ends.AddMinutes(1));
        token = Browser.AntiforgeryTokenIn(await client.PageAsync(Page));
        (path, _) = await client.PostAsync(Page, ChangeForm, token, ("End", second.Id.ToString()));

        Assert.Equal(Page, path);

        Assert.Equal([second], StoredMaintenance.All(application.Database));
        Assert.DoesNotContain(application.Events(), written => written.Template == EndedTemplate);
    }

    [Fact]
    public async Task A_user_who_is_not_an_operator_reaches_neither_the_page_nor_its_forms()
    {
        using var application = Application(new TestClock(Morning));
        using var client = application.CreateClient();
        var ada = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        // The page is refused, and she is told no more than that there is no page for her.
        using (var refused = await client.GetAsync(Page, TestContext.Current.CancellationToken))
        {
            var said = await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Contains("<h1>Page not found</h1>", said);
            Assert.DoesNotContain("Announce maintenance", said);
            Assert.DoesNotContain(AnnounceForm, said);
        }

        // So is a form that she posts there, with a token of a page of her own.
        var token = Browser.AntiforgeryTokenIn(await client.PageAsync("/account"));

        using (var refused = await client.SendFormAsync(Page, AnnounceForm, token, TwoNightsOn))
        {
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        }

        using (var refused = await client.SendFormAsync(Page, ChangeForm, token, ("Cancel", Guid.NewGuid().ToString())))
        {
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        }

        Assert.Empty(StoredMaintenance.All(application.Database));

        var refusals = application.Events().Where(written => written.Template == RefusedPageTemplate).ToList();

        Assert.Equal(3, refusals.Count);
        Assert.All(refusals, written => Assert.Equal((ada.Id.ToString(), Page), (written.Text("UserId"), written.Text("Path"))));
        Assert.Contains("Sign out", await client.PageAsync("/account"));
    }

    [Fact]
    public async Task A_visitor_is_sent_to_sign_in_and_announces_nothing()
    {
        using var application = Application(new TestClock(Morning));
        using var visitor = application.CreateClient();

        Assert.Equal(SessionAuthentication.PageWith(SessionAuthentication.SignInPath, Page), (await visitor.OpenAsync(Page)).Address);

        var (path, _) = await visitor.PostAsync(
            Page, AnnounceForm, Browser.AntiforgeryTokenIn(await visitor.PageAsync(SessionAuthentication.SignInPath)), TwoNightsOn);

        Assert.Equal(SessionAuthentication.SignInPath, path);
        Assert.Empty(StoredMaintenance.All(application.Database));
    }

    [Fact]
    public async Task A_form_without_the_token_of_its_page_announces_nothing()
    {
        using var application = Application(new TestClock(Morning));
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Olive);
        await client.SignInAsync(Olive);

        // From a page of another site, which has no token of this one (NFR-SEC-5).
        using var answer = await client.SendFormAsync(Page, AnnounceForm, "not-the-token", TwoNightsOn);

        Assert.Equal(HttpStatusCode.BadRequest, answer.StatusCode);
        Assert.Empty(StoredMaintenance.All(application.Database));
    }

    [Fact]
    public async Task What_announces_maintenance_asks_again_whether_the_user_of_its_request_is_an_operator()
    {
        // Something that reaches what announces, cancels, and ends maintenance past its page,
        // as nothing of the application does, for whoever sent the request.
        const string direct = "/announces-maintenance-directly";

        var clock = new TestClock(Morning);
        using var application = Application(clock, host: host => host.HandleAtTheEnd(async request =>
        {
            if (!request.Request.Path.StartsWithSegments(direct))
            {
                return false;
            }

            var announcements = request.RequestServices.GetRequiredService<IMaintenanceAnnouncements>();
            var id = Guid.TryParse(request.Request.Query["id"], out var named) ? named : Guid.Empty;

            var done = request.Request.Query["do"].ToString() switch
            {
                "cancel" => await announcements.CancelAsync(request, id),
                "end" => await announcements.EndAsync(request, id),
                _ => (await announcements.AnnounceAsync(request, Starts, Ends)).Announced is not null,
            };

            await request.Response.WriteAsync(done ? "Done" : "Refused");

            return true;
        }));
        using var visitor = application.CreateClient();
        using var user = application.CreateClient();
        using var anOperator = application.CreateClient();
        var ada = await application.RegisterVerifiedAsync(Ada);
        var olive = await application.RegisterVerifiedAsync(Olive);
        await user.SignInAsync(Ada);
        await anOperator.SignInAsync(Olive);

        // Nobody who is not an operator announces anything, signed in or not (NFR-SEC-3).
        Assert.Equal("Refused", await visitor.PageAsync(direct));
        Assert.Equal("Refused", await user.PageAsync(direct));
        Assert.Empty(StoredMaintenance.All(application.Database));

        // An operator does, and it is recorded where it is done, whatever asked for it.
        Assert.Equal("Done", await anOperator.PageAsync(direct));

        var announced = Assert.Single(StoredMaintenance.All(application.Database));

        Assert.Equal(
            (olive.Id, "MaintenanceAnnounced"),
            Assert.Single(
                StoredAuditEvent.All(application.Database).Select(audited => (audited.UserId, audited.Action)),
                audited => audited.Action.StartsWith("Maintenance", StringComparison.Ordinal)));

        // Nor does anybody else cancel it, or end it once it is under way.
        Assert.Equal("Refused", await visitor.PageAsync($"{direct}?do=cancel&id={announced.Id}"));
        Assert.Equal("Refused", await user.PageAsync($"{direct}?do=cancel&id={announced.Id}"));

        await LaterAsync(clock, user, Starts.AddMinutes(1), Ada);
        await anOperator.SignInAsync(Olive);

        Assert.Equal("Refused", await user.PageAsync($"{direct}?do=end&id={announced.Id}"));
        Assert.Equal([announced], StoredMaintenance.All(application.Database));

        var refusals = application.Events().Where(written => written.Template == RefusedTemplate).ToList();

        Assert.Equal(5, refusals.Count);
        Assert.All(refusals, written => Assert.Equal("Warning", written.Level));
        Assert.Equal(ada.Id.ToString(), refusals[1].Text("UserId"));

        Assert.Equal("Done", await anOperator.PageAsync($"{direct}?do=end&id={announced.Id}"));
        Assert.Equal(Starts.AddMinutes(1), Assert.Single(StoredMaintenance.All(application.Database)).EndsAt);
    }

    [Fact]
    public async Task What_is_announced_stays_when_the_account_of_whoever_announced_it_is_deleted()
    {
        var clock = new TestClock(Morning);
        TestDatabase database;
        Guid oliveId;

        using (var first = Application(clock))
        {
            using var client = first.CreateClient();
            oliveId = (await first.RegisterVerifiedAsync(Olive)).Id;
            await client.SignInAsync(Olive);
            await client.SubmitAsync(Page, AnnounceForm, TwoNightsOn);

            database = first.Database;
        }

        // Her address is taken off the list of the file, and the application starts again: she
        // is a user like any other now, and deletes her account as anyone does.
        using var application = Application(clock, database, operators: false);
        using var again = application.CreateClient();
        await again.SignInAsync(Olive);
        await again.ConfirmIdentityAsync();

        Assert.Equal(WebUiModule.AccountDeletedPath, (await again.DeleteAccountAsync()).Path);

        // The maintenance is of the service, and does not end with one account. Its row never
        // held anything of hers, and nothing else in the database does any more (FR-ACC-9).
        var stored = Assert.Single(StoredMaintenance.All(database));

        Assert.Equal((Starts, Ends), (stored.StartsAt, stored.EndsAt));
        Assert.DoesNotContain(oliveId.ToString(), application.EverythingStored());
        Assert.DoesNotContain(Olive, application.EverythingStored());

        using var visitor = application.CreateClient();

        Assert.Contains(Banner, await visitor.PageAsync("/"));
    }

    [Fact]
    public async Task The_page_shows_nothing_of_any_user()
    {
        var clock = new TestClock(Morning);
        using var application = Application(clock);
        using var client = application.CreateClient();

        var (ada, _, _) = await application.RegisterWithTwoFactorAsync(Ada, clock);
        var olive = await application.RegisterVerifiedAsync(Olive);
        await client.SignInAsync(Olive);

        var seen = new List<string>
        {
            await client.PageAsync(Page),
            (await client.SubmitAsync(Page, AnnounceForm, TwoNightsOn)).Page,
            (await client.SubmitAsync(Page, AnnounceForm, ("Starts", "never"), ("Ends", "never"))).Page,
        };

        var announced = Assert.Single(StoredMaintenance.All(application.Database));

        seen.Add((await client.SubmitAsync(Page, ChangeForm, ("Cancel", announced.Id.ToString()))).Page);

        // Each is the page, and none says who announced what: not the id of the operator
        // herself, and nothing of the other user.
        Assert.All(seen, page => Assert.Contains("<h1>Maintenance</h1>", page));
        Assert.All(seen, page =>
        {
            Assert.DoesNotContain(Ada, page);
            Assert.DoesNotContain(ada.Id.ToString(), page);
            Assert.DoesNotContain(olive.Id.ToString(), page);
            Assert.DoesNotContain("••••", page);
        });
    }

    /// <summary>
    /// Moves the clock a long way, and signs a user in again: a session does not outlast being
    /// idle for as long as lies between an announcement and its maintenance.
    /// </summary>
    private static async Task LaterAsync(TestClock clock, HttpClient client, DateTimeOffset at, string email = Olive)
    {
        clock.Set(at);
        await client.SignInAsync(email);
    }

    /// <summary>The application with one operator in its configuration file, or with none.</summary>
    private static AccountsApplication Application(
        TestClock? clock = null, TestDatabase? database = null, bool operators = true, Action<IWebHostBuilder>? host = null) =>
        new(clock: clock, database: database, settings: operators ? Operators : NoOperators, host: host);

    private static SettingsJson Operators(SettingsJson file) =>
        file.WithJson("Application:Operators", $"[ \"{Olive}\" ]");

    private static SettingsJson NoOperators(SettingsJson file) =>
        file.WithJson("Application:Operators", "[]");
}
