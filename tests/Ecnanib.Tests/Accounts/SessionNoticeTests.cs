using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ecnanib.Accounts;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.WebUi;
using Ecnanib.WebUi.Layout;
using Microsoft.AspNetCore.Components;

namespace Ecnanib.Tests.Accounts;

/// <summary>
/// The notice before a session ends (NFR-USA-1, NFR-SEC-1): a session ends after a period of
/// inactivity, and a page of a user says so shortly before, with a button that says they are
/// still there. Here is what the server does for it: what a page is told, what it is answered
/// when it asks how long is left, which is no activity, and what "Stay signed in" does, which
/// is. What the script does with it is seen in a browser: see docs/accessibility.md. The file
/// of the tests sets the period to 45 minutes.
/// </summary>
public partial class SessionNoticeTests
{
    private const string Ada = "ada@example.com";

    private static readonly DateTimeOffset Morning = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan Idle = TimeSpan.FromMinutes(45);

    [Fact]
    public async Task A_page_of_a_user_says_how_long_its_session_has_left_and_a_page_of_a_visitor_says_nothing()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        using var visitor = application.CreateClient();
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        // The whole period, two minutes of notice, and where to ask: in whole seconds.
        Assert.Equal((2700, 120, SessionAuthentication.SessionLeftPath), NoticeOn(await client.PageAsync("/account")));

        // Half a minute on, the time that is stored for the session is the same, so less is left.
        clock.Set(Morning.AddSeconds(30));
        Assert.Equal(2670, NoticeOn(await client.PageAsync("/account")).Left);

        // A request that is written down starts the period anew.
        clock.Set(Morning.AddMinutes(10));
        Assert.Equal(2700, NoticeOn(await client.PageAsync("/account")).Left);

        // A visitor has no session, and so no notice, and no dialog.
        foreach (var path in new[] { "/", SessionAuthentication.SignInPath, "/legal/terms" })
        {
            var page = await visitor.PageAsync(path);

            Assert.DoesNotContain("session-notice", page);
            Assert.DoesNotContain("session-warning", page);
        }
    }

    [Fact]
    public async Task The_notice_is_a_dialog_with_a_name_and_two_buttons_and_adds_no_form_and_no_heading()
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        var page = await client.PageAsync("/account");
        var notice = Notice().Match(page);
        Assert.True(notice.Success, "The page of a user has no notice before its session ends.");

        var dialog = notice.Groups["dialog"].Value;

        // A dialog that takes an answer, named by what it says first, and described by the rest.
        Assert.Contains(
            "<dialog id=\"session-warning\" role=\"alertdialog\" data-nosnippet aria-labelledby=\"session-warning-title\" aria-describedby=\"session-warning-text\">",
            dialog);
        Assert.Contains($"<p id=\"session-warning-title\" class=\"session-title\">{SessionPages.Title}</p>", dialog);
        Assert.Contains($"<p id=\"session-warning-text\">{SessionPages.Soon}</p>", dialog);

        // What it says when the server gave no answer is there, and not shown.
        Assert.Contains($"<p id=\"session-warning-failed\" role=\"alert\" hidden>{SessionPages.NotReached}</p>", dialog);

        // Two buttons, which send the form of the bar: the first to where staying is said.
        Assert.Contains(
            $"<button type=\"submit\" form=\"{Screens.SignOutFormId}\" formaction=\"{WebUiModule.StaySignedInPath}\">{SessionPages.Stay}</button>",
            dialog);
        Assert.Contains($"<button type=\"submit\" form=\"{Screens.SignOutFormId}\">{SessionPages.SignOutNow}</button>", dialog);
        Assert.Equal(2, Regex.Count(dialog, "<button\\b"));
        Assert.Contains($"<form id=\"{Screens.SignOutFormId}\" method=\"post\" action=\"{WebUiModule.SignOutPath}\"", page);

        // It has no form of its own, and no heading: the page keeps its forms and its outline.
        // Nothing in it asks for the focus as the page opens, which the heading of the page has.
        Assert.DoesNotContain("<form", dialog);
        Assert.DoesNotMatch("<h[1-6]\\b", dialog);
        Assert.DoesNotContain("autofocus", dialog);

        // The part around it is kept by a page that asks for itself again, so an open dialog stays open.
        Assert.Contains("<div id=\"session-notice\" data-permanent data-left=", page);
    }

    [Fact]
    public async Task Asking_how_long_is_left_is_no_activity_and_says_what_another_window_did()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        // Five minutes before the end, asked more than once: the session is as it was.
        clock.Set(Morning.AddMinutes(40));

        for (var asked = 0; asked < 3; asked++)
        {
            var (left, says) = await LeftAsync(client);

            Assert.Equal(300, left);
            Assert.Equal(SessionPages.At(TimeSpan.FromMinutes(5), Morning + Idle), says);
        }

        Assert.Equal(Morning, Assert.Single(StoredSession.All(application.Database)).LastActiveAt);

        // Its user does something in another window of the same browser, and the answer says so.
        await client.PageAsync("/account");
        Assert.Equal(Morning.AddMinutes(40), Assert.Single(StoredSession.All(application.Database)).LastActiveAt);

        Assert.Equal(2700, (await LeftAsync(client)).Left);

        // Left alone from then on, it ends when it was to, however often the page asked.
        clock.Set(Morning.AddMinutes(40) + Idle - TimeSpan.FromSeconds(1));
        Assert.Equal(1, (await LeftAsync(client)).Left);

        clock.Set(Morning.AddMinutes(40) + Idle);
        Assert.Equal((0, null), await LeftAsync(client));
        Assert.Empty(StoredSession.All(application.Database));
    }

    [Fact]
    public async Task Staying_signed_in_is_activity_and_the_session_lasts_the_whole_period_again()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        // The page that its user sits at, with the form whose token the button sends.
        var token = Browser.AntiforgeryTokenIn(await client.PageAsync("/account"));

        // A minute before the end, they say that they are still there.
        clock.Set(Morning.AddMinutes(44));

        using var answer = await client.SendFormAsync(WebUiModule.StaySignedInPath, "sign-out", token);
        var (left, says) = await ReadAsync(answer);

        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        Assert.Equal(2700, left);
        Assert.Equal(SessionPages.At(Idle, Morning.AddMinutes(44) + Idle), says);
        Assert.Equal(Morning.AddMinutes(44), Assert.Single(StoredSession.All(application.Database)).LastActiveAt);

        // So the session is there after the moment that it would have ended at, and for the
        // whole period from when they said so, and no longer.
        clock.Set(Morning.AddMinutes(44) + Idle - TimeSpan.FromSeconds(1));
        Assert.Equal(1, (await LeftAsync(client)).Left);

        clock.Set(Morning.AddMinutes(44) + Idle);
        Assert.StartsWith(SessionAuthentication.SignInPath, (await client.OpenAsync("/account")).Address);
    }

    [Fact]
    public async Task Staying_signed_in_without_the_token_is_refused()
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        using var forged = await client.SendFormAsync(WebUiModule.StaySignedInPath, "sign-out", "not-the-token");

        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);

        // And it is no page: a browser that asks for it gets nothing.
        using var asked = await client.GetAsync(WebUiModule.StaySignedInPath, TestContext.Current.CancellationToken);

        Assert.False(asked.IsSuccessStatusCode);
    }

    [Fact]
    public async Task After_the_session_has_ended_both_say_so_and_start_none()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        using var visitor = application.CreateClient();
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        var token = Browser.AntiforgeryTokenIn(await client.PageAsync("/account"));

        clock.Set(Morning + Idle);

        // The button of a page that is older than its session: its token is of a user that the
        // request no longer has, so it is not looked at, and the answer says that nothing is left.
        using var stayed = await client.SendFormAsync(WebUiModule.StaySignedInPath, "sign-out", token);

        Assert.Equal(HttpStatusCode.OK, stayed.StatusCode);
        Assert.Equal((0, null), await ReadAsync(stayed));
        Assert.Empty(StoredSession.All(application.Database));

        // The question is answered the same, and a visitor is answered as a session that ended:
        // neither is sent to the page to sign in on, which a script could not read.
        Assert.Equal((0, null), await LeftAsync(client));
        Assert.Equal((0, null), await LeftAsync(visitor));

        using var nobody = await visitor.SendFormAsync(WebUiModule.StaySignedInPath, "sign-out", "no-token-at-all");

        Assert.Equal((0, null), await ReadAsync(nobody));
        Assert.Empty(StoredSession.All(application.Database));
    }

    [Fact]
    public async Task Neither_answer_is_kept_by_the_browser()
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        var token = Browser.AntiforgeryTokenIn(await client.PageAsync("/account"));

        using var asked = await client.GetAsync(SessionAuthentication.SessionLeftPath, TestContext.Current.CancellationToken);
        using var stayed = await client.SendFormAsync(WebUiModule.StaySignedInPath, "sign-out", token);

        Assert.All(new[] { asked, stayed }, answer =>
        {
            Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
            Assert.Equal("application/json", answer.Content.Headers.ContentType?.MediaType);
            Assert.Equal("no-store", answer.Headers.CacheControl?.ToString());
        });
    }

    [Theory]
    [InlineData(1, 30)]
    [InlineData(2, 30)]
    [InlineData(4, 60)]
    [InlineData(6, 90)]
    [InlineData(8, 120)]
    [InlineData(45, 120)]
    [InlineData(60, 120)]
    [InlineData(1440, 120)]
    public void The_notice_comes_a_quarter_of_the_period_before_the_end_and_between_half_a_minute_and_two(int idleMinutes, int seconds)
    {
        // Never less than half a minute, which is more than the 20 seconds that WCAG 2.1 asks
        // for (2.2.1), also with the shortest period that the configuration file allows.
        Assert.Equal(TimeSpan.FromSeconds(seconds), SessionAuthentication.WarnsBefore(TimeSpan.FromMinutes(idleMinutes)));
    }

    [Theory]
    [InlineData(118, "in about 2 minutes")]
    [InlineData(95, "in about 2 minutes")]
    [InlineData(80, "in about a minute")]
    [InlineData(31, "in about a minute")]
    [InlineData(29, "in less than a minute")]
    [InlineData(1, "in less than a minute")]
    public void What_the_notice_says_names_the_minutes_left_and_the_time_in_UTC(int seconds, string left)
    {
        var at = new DateTimeOffset(2026, 11, 12, 14, 32, 10, TimeSpan.FromHours(2));

        // The time of day in UTC, as every page writes a time, and said once: nothing counts down.
        Assert.Equal(
            $"Nothing was done here for a while. To keep your account safe, you will be signed out {left}, at 12:32 UTC, and this page will then ask you to sign in again.",
            SessionPages.At(TimeSpan.FromSeconds(seconds), at));
    }

    [Fact]
    public async Task A_page_that_asks_for_itself_again_has_no_more_time_left()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        using var client = application.CreateClient();
        var ada = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        var connection = Assert.Single(StoredSession.All(application.Database)).ConnectionOf(ada);

        // A page that follows what changes asks for itself again, having said that it would.
        // That is no activity, so the page that comes says less is left, as often as it comes.
        foreach (var minutes in new[] { 10, 30, 44 })
        {
            clock.Set(Morning.AddMinutes(minutes));
            application.ConnectedSession.ExpectsPageAgain(connection, "/account");

            Assert.Equal((45 - minutes) * 60, NoticeOn(await client.AskAgainAsync("/account")).Left);
        }

        Assert.Equal(Morning, Assert.Single(StoredSession.All(application.Database)).LastActiveAt);
    }

    [Fact]
    public async Task Every_page_with_the_notice_has_the_form_that_its_buttons_post()
    {
        using var application = new AccountsApplication();
        using var visitor = application.CreateClient();
        using var user = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await user.SignInAsync(Ada);

        // Every page that has no parameter in its route, so that a page which is added later is
        // looked at too: a page that is answered for a user has the notice, with the form.
        var paths = typeof(Program).Assembly.GetTypes()
            .SelectMany(component => component.GetCustomAttributes<RouteAttribute>())
            .Select(route => route.Template)
            .Where(route => !route.Contains('{', StringComparison.Ordinal))
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();
        var withNotice = 0;

        Assert.Contains("/account", paths);

        foreach (var path in paths)
        {
            using var answer = await user.GetAsync(path, TestContext.Current.CancellationToken);
            var page = await answer.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            var hasForm = page.Contains($"<form id=\"{Screens.SignOutFormId}\"", StringComparison.Ordinal);

            Assert.True(
                page.Contains("id=\"session-warning\"", StringComparison.Ordinal) == hasForm,
                $"The page at {path} has the notice before a session ends without the form that signs out, or the form without the notice. Its buttons send that form.");

            withNotice += hasForm ? 1 : 0;

            using var toVisitor = await visitor.GetAsync(path, TestContext.Current.CancellationToken);

            Assert.DoesNotContain("id=\"session-warning\"", await toVisitor.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }

        Assert.True(withNotice > 10, "Hardly any page of a user has the notice before its session ends.");
    }

    /// <summary>What a page says of its session: the seconds that are left, the seconds of notice, and where to ask.</summary>
    private static (int Left, int Warn, string Ask) NoticeOn(string page)
    {
        var notice = Notice().Match(page);
        Assert.True(notice.Success, "The page has no notice before its session ends.");

        return (
            int.Parse(notice.Groups["left"].Value, System.Globalization.CultureInfo.InvariantCulture),
            int.Parse(notice.Groups["warn"].Value, System.Globalization.CultureInfo.InvariantCulture),
            notice.Groups["ask"].Value);
    }

    /// <summary>Asks how long the session of a browser has left, as its page does.</summary>
    private static async Task<(int Left, string? Says)> LeftAsync(HttpClient client)
    {
        using var answer = await client.GetAsync(SessionAuthentication.SessionLeftPath, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);

        return await ReadAsync(answer);
    }

    /// <summary>What an answer says: the seconds that are left, and the sentence of the notice where there is one.</summary>
    private static async Task<(int Left, string? Says)> ReadAsync(HttpResponseMessage answer)
    {
        using var said = JsonDocument.Parse(await answer.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        return (
            said.RootElement.GetProperty("left").GetInt32(),
            said.RootElement.TryGetProperty("says", out var says) ? says.GetString() : null);
    }

    [GeneratedRegex(
        "<div id=\"session-notice\" data-permanent data-left=\"(?<left>\\d+)\" data-warn=\"(?<warn>\\d+)\" data-ask=\"(?<ask>[^\"]+)\">(?<dialog>.*?</dialog>)\\s*</div>",
        RegexOptions.Singleline)]
    private static partial Regex Notice();
}
