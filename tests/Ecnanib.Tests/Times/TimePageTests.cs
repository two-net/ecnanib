using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ecnanib.Accounts;
using Ecnanib.Agents;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.Agents;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.Tests.ExchangeGateway;
using Ecnanib.Tests.Persistence;
using Ecnanib.WebUi;
using Ecnanib.WebUi.Pages.Agents;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Ecnanib.Tests.Times;

/// <summary>
/// Times are shown in the user's local time zone, with UTC available (NFR-USA-3). The device of
/// a user says its time zone when the user signs in, and the server keeps it on the device, in
/// a cookie that scripts cannot read. The account says whether its pages are in the time zone of
/// the device, which a new account has, or in UTC. The server writes every time, in the time
/// zone of the page, and each time says how far that is from UTC. See docs/times.md.
/// </summary>
/// <remarks>
/// None of these tests drives a browser. What is held here is what a browser is sent: the times
/// of a page, the cookie of a device, and the row of an account. What a browser says its time
/// zone is, the script writes into the form: see <see cref="TimeScriptTests"/>. A test says it
/// in the place of the script. How a time is written is held without an application by
/// <see cref="PageTimesTests"/>, and that no page writes a time in another time zone than its
/// own by <see cref="TimeZoneCoverageTests"/>.
/// </remarks>
public partial class TimePageTests
{
    private const string Ada = "ada@example.com";
    private const string Grace = "grace@example.com";

    private const string OfAccount = WebUiModule.AppearancePath;
    private const string Form = "times";
    private const string Field = "Times";

    private const string Cookie = "__Host-ecnanib-time-zone";

    // Time zones that are as far from UTC all year, so that a test says the same in summer.
    private const string Kolkata = "Asia/Kolkata";
    private const string Tokyo = "Asia/Tokyo";

    private static readonly DateTimeOffset Morning = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public void The_choices_are_the_device_and_utc_and_are_stored_by_these_names()
    {
        // A member is stored by its name, so a member that is renamed needs a migration.
        Assert.Equal(["Device", "Utc"], Enum.GetNames<TimeDisplay>());

        // The one of a new user is the one that nothing needs to say.
        Assert.Equal(TimeDisplay.Device, default(TimeDisplay));
    }

    [Fact]
    public async Task A_user_reads_a_time_in_the_time_zone_that_their_device_said_when_they_signed_in()
    {
        using var application = new AccountsApplication(clock: new TestClock(Morning));
        using var client = application.CreateClientThatStays();
        await application.RegisterVerifiedAsync(Ada);

        // The form of the sign-in has a field for what the device says, which the script fills.
        var form = await client.PageAsync(SessionAuthentication.SignInPath);

        Assert.Contains("<input type=\"hidden\" name=\"TimeZone\" data-time-zone />", form);

        using var answer = await client.SendFormAsync(
            SessionAuthentication.SignInPath,
            "sign-in",
            Browser.AntiforgeryTokenIn(form),
            ("Email", Ada),
            ("Password", AccountsApplication.Password),
            ("TimeZone", Kolkata));

        // The server keeps it on the device: for the whole host, over TLS alone, hidden from
        // scripts, and for a year. It says nothing of who the user is.
        var kept = Assert.Single(Browser.CookiesSetBy(answer), cookie => cookie.StartsWith(Cookie, StringComparison.Ordinal));

        Assert.Equal(
            $"{Cookie}=Asia%2FKolkata; max-age=31536000; path=/; secure; samesite=lax; httponly",
            kept, ignoreCase: true);

        // A new account reads the time zone of its device, and every time says how far that is
        // from UTC: the session ends at a quarter past three in the afternoon there.
        Assert.Equal("Device", DisplayOf(application, Ada));
        Assert.Contains(", at 15:15 UTC+5:30, ", await SaidOfTheSessionAsync(client));

        // The page of the appearance says which time zone the device said, and what is chosen.
        var appearance = Shown(await client.PageAsync(OfAccount));

        Assert.Equal(["device", "utc"], ChoicesOn(appearance));
        Assert.Equal("device", ChosenOn(appearance));
        Assert.Contains("<label for=\"times-device\">This device</label>", appearance);
        Assert.Contains("<label for=\"times-utc\">UTC</label>", appearance);
        Assert.Contains("<p>This device has said that its time zone is Asia/Kolkata, which is UTC+5:30 now.</p>", appearance);

        // And the page of the account names the choice that is stored with it.
        Assert.Contains(
            "<p>Time zone: This device. Your choice is stored with your account, and is in a download.</p>",
            await client.PageAsync(WebUiModule.AccountPath));
    }

    [Fact]
    public async Task A_device_that_has_said_no_time_zone_reads_utc_and_the_page_says_so()
    {
        using var application = new AccountsApplication(clock: new TestClock(Morning));
        using var client = application.CreateClientThatStays();
        await application.RegisterVerifiedAsync(Ada);

        // A browser without the script sends the field empty, or not at all.
        var form = await client.PageAsync(SessionAuthentication.SignInPath);
        using var answer = await client.SendFormAsync(
            SessionAuthentication.SignInPath,
            "sign-in",
            Browser.AntiforgeryTokenIn(form),
            ("Email", Ada),
            ("Password", AccountsApplication.Password),
            ("TimeZone", string.Empty));

        // Nothing is kept for it, and nothing is taken back: no word of the cookie at all.
        Assert.DoesNotContain(Browser.CookiesSetBy(answer), cookie => cookie.StartsWith(Cookie, StringComparison.Ordinal));

        // The times are in UTC, as they always were, and say so.
        Assert.Contains(", at 09:45 UTC, ", await SaidOfTheSessionAsync(client));

        // The page says that the device has said none, and how it comes to say one.
        var appearance = await client.PageAsync(OfAccount);

        Assert.Equal("device", ChosenOn(appearance));
        Assert.Contains(
            "<p>This device has said no time zone yet, so its times are in UTC. It says one when you save this choice here, or sign in again.</p>",
            appearance);
    }

    [Fact]
    public async Task An_account_from_before_the_choice_reads_the_time_zone_of_its_device()
    {
        // A database of the version before, in which an account has no such choice.
        var database = PostgresServer.CreateDatabase();
        using (var before = database.Context())
        {
            before.GetService<IMigrator>().Migrate("20261125161032_AddSetupMarks");
        }

        database.Execute(
            $"""
            INSERT INTO users (id, email, password_hash, registered_at, email_verification_sets_password, two_factor_failed_codes)
            VALUES ('{Guid.CreateVersion7()}', '{Ada}', 'not-a-hash', now(), FALSE, 0)
            """);

        // Starting this version migrates the database.
        using var application = new AccountsApplication(database: database);
        _ = application.Registration;

        Assert.Equal("Device", DisplayOf(application, Ada));
    }

    [Theory]
    [InlineData("No/Such")]
    [InlineData("Mars")]
    [InlineData("/etc/localtime")]
    [InlineData("../../etc/passwd")]
    [InlineData("Europe/Berlin/../../etc/passwd")]
    [InlineData("Asia/Kolkata ")]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("Asia/Kolkata\r\nSet-Cookie: x=y")]
    [InlineData("AaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaAaaaaaaaaaaaaaaa")]
    public async Task What_names_no_time_zone_is_not_kept_and_the_pages_are_in_utc(string said)
    {
        using var application = new AccountsApplication(clock: new TestClock(Morning));
        using var client = application.CreateClientThatStays();
        await application.RegisterVerifiedAsync(Ada);

        var form = await client.PageAsync(SessionAuthentication.SignInPath);
        using var answer = await client.SendFormAsync(
            SessionAuthentication.SignInPath,
            "sign-in",
            Browser.AntiforgeryTokenIn(form),
            ("Email", Ada),
            ("Password", AccountsApplication.Password),
            ("TimeZone", said));

        // The user is signed in all the same. What the form said is kept nowhere: not on the
        // device, and in no page.
        Assert.Equal(HttpStatusCode.Redirect, answer.StatusCode);
        Assert.DoesNotContain(Browser.CookiesSetBy(answer), cookie => cookie.StartsWith(Cookie, StringComparison.Ordinal));
        Assert.Contains(", at 09:45 UTC, ", await SaidOfTheSessionAsync(client));
        Assert.DoesNotContain(said.Trim(), await client.PageAsync(OfAccount), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("No/Such")]
    [InlineData("../../etc/passwd")]
    [InlineData("Asia/Kolkata\"><script>")]
    [InlineData("")]
    public async Task A_cookie_that_names_no_time_zone_is_none(string value)
    {
        using var application = new AccountsApplication(clock: new TestClock(Morning));
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        // A cookie is what a browser sends, and a browser sends what it likes.
        using var request = new HttpRequestMessage(HttpMethod.Get, OfAccount);
        request.Headers.TryAddWithoutValidation("Cookie", $"{Cookie}={Uri.EscapeDataString(value)}");

        using var answer = await client.SendAsync(request, Cancellation);
        var page = await answer.Content.ReadAsStringAsync(Cancellation);

        // What it says is written into no page: the device has said no time zone.
        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        Assert.Contains("This device has said no time zone yet", page);
        Assert.DoesNotContain("<script>\"", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_wrong_password_keeps_nothing_of_what_the_device_said()
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClientThatStays();
        await application.RegisterVerifiedAsync(Ada);

        var form = await client.PageAsync(SessionAuthentication.SignInPath);
        using var answer = await client.SendFormAsync(
            SessionAuthentication.SignInPath,
            "sign-in",
            Browser.AntiforgeryTokenIn(form),
            ("Email", Ada),
            ("Password", "not the password"),
            ("TimeZone", Kolkata));

        // Only the device of whoever knew the password is kept a time zone for.
        Assert.DoesNotContain(Browser.CookiesSetBy(answer), cookie => cookie.StartsWith(Cookie, StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_account_with_two_factor_authentication_has_its_time_zone_kept_when_the_password_was_right()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        using var owner = application.CreateClient();
        using var client = application.CreateClientThatStays();
        await application.RegisterVerifiedAsync(Ada);
        await owner.SignInAsync(Ada);
        var (app, _) = await owner.TurnOnTwoFactorAsync(clock);

        var form = await client.PageAsync(SessionAuthentication.SignInPath);
        using var answer = await client.SendFormAsync(
            SessionAuthentication.SignInPath,
            "sign-in",
            Browser.AntiforgeryTokenIn(form),
            ("Email", Ada),
            ("Password", AccountsApplication.Password),
            ("TimeZone", Tokyo));

        // The page that takes the code has no field for the time zone, so what the device said
        // is kept with the answer to the password: beside the cookie of the sign-in that waits.
        Assert.Equal(HttpStatusCode.Redirect, answer.StatusCode);
        Assert.Equal(
            ["__Host-ecnanib-sign-in", Cookie],
            Browser.CookiesSetBy(answer).Select(cookie => cookie.Split('=')[0]).Order(StringComparer.Ordinal));

        // With the code the user is signed in, and reads the time zone of the device. The day
        // that two-factor authentication was turned on is the day of that time zone too.
        clock.Set(Morning.AddMinutes(1));
        await client.EnterCodeAsync(app.CodeAt(clock.GetUtcNow()));

        Assert.Contains(" UTC+9, ", await SaidOfTheSessionAsync(client));
        Assert.Contains("Two-factor authentication is on, since 12 November 2026.", await client.PageAsync(Browser.TwoFactorPage));
    }

    [Fact]
    public async Task The_day_of_a_moment_is_the_day_of_the_time_zone_of_the_page()
    {
        // Half past ten in the evening, UTC: the next day in Tokyo.
        var clock = new TestClock(new DateTimeOffset(2026, 11, 12, 22, 30, 0, TimeSpan.Zero));
        using var application = new AccountsApplication(clock: clock);
        using var inTokyo = application.CreateClient();
        using var nowhere = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await inTokyo.SignInFromAsync(Ada, Tokyo);
        var (app, _) = await inTokyo.TurnOnTwoFactorAsync(clock);

        Assert.Contains("Two-factor authentication is on, since 13 November 2026.", await inTokyo.PageAsync(Browser.TwoFactorPage));

        // A device that has said no time zone reads the day of UTC, which is still the twelfth.
        // It signs in a minute later: a code of the app is taken once.
        clock.Set(clock.GetUtcNow().AddMinutes(1));
        await nowhere.SignInAsync(Ada);
        await nowhere.EnterCodeAsync(app.CodeAt(clock.GetUtcNow()));

        Assert.Contains("Two-factor authentication is on, since 12 November 2026.", await nowhere.PageAsync(Browser.TwoFactorPage));
    }

    [Fact]
    public async Task A_user_who_chose_utc_reads_utc_on_every_device_and_comes_back_to_the_device()
    {
        using var application = new AccountsApplication(clock: new TestClock(Morning));
        using var desk = application.CreateClientThatStays();
        using var phone = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await desk.SignInFromAsync(Ada, Kolkata);
        await phone.SignInFromAsync(Ada, Tokyo);

        // Each device reads its own time zone: the choice of the account is the device.
        Assert.Contains(" UTC+5:30, ", await SaidOfTheSessionAsync(desk));
        Assert.Contains(" UTC+9, ", await SaidOfTheSessionAsync(phone));

        // UTC is chosen on one device, and holds on both. What the device said is left as it is.
        var page = await desk.PageAsync(OfAccount);
        using var saved = await desk.SendFormAsync(OfAccount, Form, Browser.AntiforgeryTokenIn(page), (Field, "utc"), ("TimeZone", Kolkata));

        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        Assert.Equal(OfAccount, new Uri(desk.BaseAddress!, saved.Headers.Location!).AbsolutePath);
        Assert.DoesNotContain(Browser.CookiesSetBy(saved), cookie => cookie.StartsWith(Cookie, StringComparison.Ordinal));
        Assert.Equal("Utc", DisplayOf(application, Ada));

        Assert.Contains(", at 09:45 UTC, ", await SaidOfTheSessionAsync(desk));
        Assert.Contains(" UTC, ", await SaidOfTheSessionAsync(phone));

        var asked = await desk.PageAsync(OfAccount);
        var chosen = Shown(asked);

        Assert.Equal("utc", ChosenOn(chosen));
        Assert.Contains("Times are written in UTC, whatever your device says", chosen);

        // The page still says what the device said, which is what "This device" would go by.
        Assert.Contains("This device has said that its time zone is Asia/Kolkata, which is UTC+5:30 now.", chosen);
        Assert.Contains(
            "<p>Time zone: UTC. Your choice is stored with your account, and is in a download.</p>",
            await desk.PageAsync(WebUiModule.AccountPath));

        // Back to the device, with what the device says now: it has moved, and need not be
        // signed in on again for that.
        using var back = await desk.SendFormAsync(
            OfAccount, Form, Browser.AntiforgeryTokenIn(asked), (Field, "device"), ("TimeZone", Tokyo));

        Assert.StartsWith($"{Cookie}=Asia%2FTokyo;", Assert.Single(Browser.CookiesSetBy(back), cookie => cookie.StartsWith(Cookie, StringComparison.Ordinal)));
        Assert.Equal("Device", DisplayOf(application, Ada));
        Assert.Contains(" UTC+9, ", await SaidOfTheSessionAsync(desk));
    }

    [Fact]
    public async Task The_choice_of_one_user_is_not_the_choice_of_another()
    {
        using var application = new AccountsApplication(clock: new TestClock(Morning));
        using var ada = application.CreateClient();
        using var grace = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await application.RegisterVerifiedAsync(Grace);
        await ada.SignInFromAsync(Ada, Kolkata);
        await grace.SignInFromAsync(Grace, Kolkata);

        await ada.SubmitAsync(OfAccount, Form, (Field, "utc"));

        Assert.Equal("Utc", DisplayOf(application, Ada));
        Assert.Equal("Device", DisplayOf(application, Grace));
        Assert.Contains(" UTC+5:30, ", await SaidOfTheSessionAsync(grace));
    }

    [Fact]
    public async Task A_form_that_names_no_choice_saves_nothing_and_one_without_its_token_is_refused()
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClientThatStays();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInFromAsync(Ada, Kolkata);

        foreach (var word in new[] { "local", "UTC", "Asia/Tokyo", string.Empty })
        {
            var page = await client.PageAsync(OfAccount);
            using var answer = await client.SendFormAsync(OfAccount, Form, Browser.AntiforgeryTokenIn(page), (Field, word), ("TimeZone", Tokyo));
            var said = await answer.Content.ReadAsStringAsync(Cancellation);

            // The page comes back as it was, and says what to choose. Nothing is stored, and
            // nothing is kept on the device.
            Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
            Assert.Contains("<p class=\"form-error\" role=\"alert\">Choose This device or UTC.</p>", said);
            Assert.DoesNotContain(Browser.CookiesSetBy(answer), cookie => cookie.StartsWith(Cookie, StringComparison.Ordinal));
            Assert.Equal("Device", DisplayOf(application, Ada));
        }

        // A form that another site posts for a user has no token of a page of this browser.
        using var forged = await client.SendFormAsync(OfAccount, Form, "not-the-token", (Field, "utc"));

        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
        Assert.Equal("Device", DisplayOf(application, Ada));
    }

    [Fact]
    public async Task What_a_device_said_stays_on_it_when_its_user_signs_out()
    {
        using var application = new AccountsApplication(clock: new TestClock(Morning));
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInFromAsync(Ada, Kolkata);
        await client.SignOutAsync();

        // The time zone is of the device, and not of whoever was signed in: a browser without
        // the script that signs in next on it reads the same time zone still.
        await client.SignInAsync(Ada);

        Assert.Contains(" UTC+5:30, ", await SaidOfTheSessionAsync(client));
    }

    [Fact]
    public async Task The_pages_of_an_agent_say_their_times_in_the_time_zone_of_the_page()
    {
        using var application = AgentsApplication.With(new StandInMarketData(), new TestClock(Morning));
        using var inKolkata = application.CreateClient();
        using var inUtc = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await inKolkata.SignInFromAsync(Ada, Kolkata);
        await inUtc.SignInAsync(Ada);
        var agent = await application.GiveAgentAsync(user.Id, Morning, name: "Swing", state: AgentState.Draft);

        // Since when the agent is in its state, and when its state changed: the same moment,
        // in the time zone of each page, with how far that is from UTC.
        var local = AgentBrowser.Read(await inKolkata.PageAsync(AgentBrowser.AgentStatePage(agent)));
        var utc = AgentBrowser.Read(await inUtc.PageAsync(AgentBrowser.AgentStatePage(agent)));

        Assert.Contains("<strong>Draft</strong>, since 12 November 2026, 14:30 UTC+5:30. ", local);
        Assert.Contains("<th scope=\"row\">12 November 2026, 14:30 UTC+5:30</th>", local);
        Assert.Contains("<strong>Draft</strong>, since 12 November 2026, 09:00 UTC. ", utc);
        Assert.Contains("<th scope=\"row\">12 November 2026, 09:00 UTC</th>", utc);

        // No time of the page is left in UTC: each says the time zone of the page.
        Assert.DoesNotMatch(@"\d\d:\d\d(:\d\d)? UTC(?!\+5:30)", local);
    }

    /// <summary>What the notice before a session ends says, which names the time of day that the session ends at.</summary>
    private static async Task<string> SaidOfTheSessionAsync(HttpClient client)
    {
        using var answer = await client.GetAsync(SessionAuthentication.SessionLeftPath, Cancellation);
        using var said = JsonDocument.Parse(await answer.Content.ReadAsStringAsync(Cancellation));

        return said.RootElement.GetProperty("says").GetString() ?? string.Empty;
    }

    /// <summary>
    /// A page as a browser shows it. A page writes the sign of a time zone that is ahead of UTC
    /// as an entity, as it writes every sign that markup could be made of.
    /// </summary>
    private static string Shown(string page) => WebUtility.HtmlDecode(page);

    /// <summary>What the row of an account says about the time zone of its pages.</summary>
    private static string DisplayOf(AccountsApplication application, string email) =>
        application.Database.Scalar<string>($"SELECT time_display FROM users WHERE email = '{email}'");

    /// <summary>The choices that a page offers, in their order.</summary>
    private static string[] ChoicesOn(string page) => [.. Choice().Matches(page).Select(choice => choice.Groups[1].Value)];

    /// <summary>The choice that a page has ticked.</summary>
    private static string ChosenOn(string page) =>
        Assert.Single(Choice().Matches(page), choice => choice.Value.Contains(" checked", StringComparison.Ordinal)).Groups[1].Value;

    [GeneratedRegex("<input id=\"times-[a-z]+\" type=\"radio\" name=\"Times\" value=\"([a-z]+)\"[^>]*>")]
    private static partial Regex Choice();
}
