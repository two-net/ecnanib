using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Ecnanib.Accounts;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.Tests.Logging;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ecnanib.Tests.Accounts;

/// <summary>
/// A session is a row of the database that the browser names with a cookie. The cookie is sent
/// only over TLS and is hidden from scripts, and the session ends after the configured period of
/// inactivity (NFR-SEC-1). The file of the tests sets that period to 45 minutes.
/// </summary>
public partial class SessionTests
{
    private const string Ada = "ada@example.com";

    private const string Cookie = "__Host-ecnanib-session";

    private static readonly DateTimeOffset Morning = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan Idle = TimeSpan.FromMinutes(45);

    [Fact]
    public async Task A_session_ends_after_the_configured_period_of_inactivity_and_not_before()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        // Used just before the period is over, and again just before the period after that use.
        clock.Set(Morning + Idle - TimeSpan.FromSeconds(1));
        Assert.Contains("You are signed in", await client.PageAsync("/account"));

        clock.Set(clock.GetUtcNow() + Idle - TimeSpan.FromSeconds(1));
        Assert.Contains("You are signed in", await client.PageAsync("/account"));

        // Left alone for the whole period.
        clock.Set(clock.GetUtcNow() + Idle);
        var (asked, page) = await client.OpenAsync("/account");

        Assert.Equal($"{SessionAuthentication.SignInPath}?returnUrl=%2Faccount", asked);
        Assert.DoesNotContain("You are signed in", page);
        Assert.DoesNotContain("Sign out", await client.PageAsync("/"));

        // The session is gone from the database, and the log says why.
        Assert.Empty(StoredSession.All(application.Database));
        var ended = Assert.Single(
            application.Events(), written => written.Template == "A session of user {UserId} ended after inactivity");
        Assert.Equal(user.Id.ToString(), ended.Text("UserId"));
    }

    [Fact]
    public async Task A_session_never_lasts_longer_than_the_period_though_not_every_request_is_written_down()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        Assert.Equal(Morning, Assert.Single(StoredSession.All(application.Database)).LastActiveAt);

        // A request this soon after the stored time leaves the row as it is.
        clock.Set(Morning.AddSeconds(30));
        await client.PageAsync("/account");
        Assert.Equal(Morning, Assert.Single(StoredSession.All(application.Database)).LastActiveAt);

        // A later one is written down.
        clock.Set(Morning.AddMinutes(1));
        await client.PageAsync("/account");
        Assert.Equal(Morning.AddMinutes(1), Assert.Single(StoredSession.All(application.Database)).LastActiveAt);

        // The period counts from what is written, which is never later than the last request.
        clock.Set(Morning.AddMinutes(1) + Idle);
        Assert.StartsWith(SessionAuthentication.SignInPath, (await client.OpenAsync("/account")).Address);
    }

    [Fact]
    public async Task The_cookie_is_sent_only_over_TLS_is_hidden_from_scripts_and_ends_with_the_browser()
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClientThatStays();
        await application.RegisterVerifiedAsync(Ada);

        using var signedIn = await SignInAndStayAsync(client);
        var set = Assert.Single(Browser.CookiesSetBy(signedIn), cookie => cookie.StartsWith($"{Cookie}=", StringComparison.Ordinal));

        // The prefix makes a browser refuse the cookie unless it is Secure, for the whole host,
        // and without a domain. No expiry: it ends with the browser.
        Assert.Matches(SetCookie(), set);

        // Taken away in the same form, or a browser would ignore the removal.
        var token = Browser.AntiforgeryTokenIn(await client.PageAsync("/"));
        using var signedOut = await client.SendFormAsync(WebUi.WebUiModule.SignOutPath, "sign-out", token);
        var cleared = Assert.Single(Browser.CookiesSetBy(signedOut), cookie => cookie.StartsWith($"{Cookie}=", StringComparison.Ordinal));

        Assert.Equal($"{Cookie}=; expires=Thu, 01 Jan 1970 00:00:00 GMT; path=/; secure; samesite=lax; httponly", cleared);
    }

    [Fact]
    public async Task The_cookie_of_a_session_that_has_ended_is_taken_from_the_browser()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        using var client = application.CreateClientThatStays();
        await application.RegisterVerifiedAsync(Ada);
        (await SignInAndStayAsync(client)).Dispose();

        clock.Set(Morning + Idle);
        using var answer = await client.GetAsync("/", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        Assert.Contains(
            $"{Cookie}=; expires=Thu, 01 Jan 1970 00:00:00 GMT; path=/; secure; samesite=lax; httponly",
            Browser.CookiesSetBy(answer));
    }

    [Fact]
    public async Task Signing_out_ends_the_session_on_the_server_and_not_only_in_the_browser()
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClientThatStays();
        await application.RegisterVerifiedAsync(Ada);

        using var signedIn = await SignInAndStayAsync(client);
        var token = TokenIn(signedIn);

        // The cookie works, also from somewhere that is not the browser it was given to.
        using var copied = application.CreateClientThatStays();
        Assert.Equal(HttpStatusCode.OK, (await GetWithCookieAsync(copied, "/account", token)).StatusCode);

        (await client.SendFormAsync(
            WebUi.WebUiModule.SignOutPath, "sign-out", Browser.AntiforgeryTokenIn(await client.PageAsync("/")))).Dispose();

        // The copy names a session that is not there any more.
        using var refused = await GetWithCookieAsync(copied, "/account", token);
        Assert.Equal(HttpStatusCode.Redirect, refused.StatusCode);
        Assert.StartsWith(SessionAuthentication.SignInPath, refused.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Signing_in_again_gives_the_browser_a_new_session_in_place_of_the_one_it_had()
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);

        await client.SignInAsync(Ada);
        var first = Assert.Single(StoredSession.All(application.Database));

        await client.SignInAsync(Ada);
        var second = Assert.Single(StoredSession.All(application.Database));

        Assert.NotEqual(first.Id, second.Id);
        Assert.NotEqual(first.TokenHash, second.TokenHash);
        Assert.Contains("You are signed in", await client.PageAsync("/account"));
    }

    [Fact]
    public async Task Each_browser_has_a_session_of_its_own()
    {
        using var application = new AccountsApplication();
        using var laptop = application.CreateClient();
        using var phone = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);

        await laptop.SignInAsync(Ada);
        await phone.SignInAsync(Ada);
        Assert.Equal(2, StoredSession.All(application.Database).Count);

        // Signing out on one leaves the other signed in.
        await laptop.SignOutAsync();

        Assert.Single(StoredSession.All(application.Database));
        Assert.StartsWith(SessionAuthentication.SignInPath, (await laptop.OpenAsync("/account")).Address);
        Assert.Contains("You are signed in", await phone.PageAsync("/account"));
    }

    [Fact]
    public async Task Signing_in_removes_the_sessions_of_the_user_that_ended_on_browsers_that_never_came_back()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        using var gone = application.CreateClient();
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await gone.SignInAsync(Ada);

        clock.Set(Morning + Idle);
        await client.SignInAsync(Ada);

        Assert.Equal(Morning + Idle, Assert.Single(StoredSession.All(application.Database)).CreatedAt);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-token")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")] // Shaped like a token, and no session's.
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAB")] // As long as a token, with an end that no 32 bytes have.
    [InlineData("!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!")] // As long as a token, in characters that no token has.
    [InlineData("' OR 1=1 --")]
    public async Task What_is_not_the_token_of_a_session_signs_nobody_in(string token)
    {
        using var application = new AccountsApplication();
        using var signedIn = application.CreateClient();
        using var client = application.CreateClientThatStays();
        await application.RegisterVerifiedAsync(Ada);
        await signedIn.SignInAsync(Ada);

        using var answer = await GetWithCookieAsync(client, "/account", token);

        Assert.Equal(HttpStatusCode.Redirect, answer.StatusCode);
        Assert.Single(StoredSession.All(application.Database));
    }

    [Fact]
    public async Task The_database_holds_the_hash_of_the_token_and_the_log_holds_neither_token_nor_password()
    {
        // With the host's own events about each request turned on, which name its address.
        using var application = new AccountsApplication(
            settings: file => file.WithJson("Logging:Overrides", """{ "Microsoft.AspNetCore": "Information" }"""));
        using var client = application.CreateClientThatStays();
        await application.RegisterAsync(Ada, "PASSWORD-MARKER a long phrase 7d1f");

        using var signedIn = await SignInAndStayAsync(client, "PASSWORD-MARKER a long phrase 7d1f");
        var token = TokenIn(signedIn);
        await client.PageAsync("/account");

        // 32 random bytes, of which the database has the hash.
        var bytes = Convert.FromBase64String(token.Replace('-', '+').Replace('_', '/') + "=");
        Assert.Equal(32, bytes.Length);
        Assert.Equal(SHA256.HashData(bytes), Assert.Single(StoredSession.All(application.Database)).TokenHash);

        var everything = string.Join(
            Environment.NewLine,
            application.Database.Tables().SelectMany(table =>
                application.Database.Column($"""SELECT row_to_json(stored)::text FROM "{table}" stored""")));
        Assert.DoesNotContain(token, everything);
        Assert.DoesNotContain("PASSWORD-MARKER", everything);

        var log = application.LogText();
        Assert.Contains(SessionAuthentication.SignInPath, log);
        Assert.DoesNotContain(token, log);
        Assert.DoesNotContain("PASSWORD-MARKER", log);
    }

    [Fact]
    public async Task When_the_session_cannot_be_looked_up_nobody_is_signed_out_and_a_page_that_needs_the_user_fails()
    {
        // Not in development, where the host shows its own page for an error.
        using var application = new AccountsApplication(host: host => host.UseEnvironment("Production"));
        using var client = application.CreateClientThatStays();
        await application.RegisterVerifiedAsync(Ada);
        (await SignInAndStayAsync(client)).Dispose();

        // The table is away, as the whole database is when it cannot be reached.
        application.Database.Execute("ALTER TABLE sessions RENAME TO sessions_away");

        // A page for everyone is served, as to a visitor, and the browser keeps its cookie.
        using var start = await client.GetAsync("/", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        Assert.DoesNotContain("Sign out", await start.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.DoesNotContain(Browser.CookiesSetBy(start), cookie => cookie.StartsWith($"{Cookie}=", StringComparison.Ordinal));

        // A page for signed-in users does not ask a user who is signed in to sign in. It fails,
        // with the page and the reference of any other error.
        using var account = await client.GetAsync("/account", TestContext.Current.CancellationToken);
        var page = await account.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.InternalServerError, account.StatusCode);
        Assert.Contains("<h1>Something went wrong.</h1>", page);

        var reference = Regex.Match(page, "<code>(?<reference>[A-Z0-9]+)</code>").Groups["reference"].Value;
        var error = Assert.Single(application.Events(), written => written.Level == "Error");
        Assert.Equal(reference, error.Text("ReferenceId"));
        Assert.Contains("sessions", error.Exception);
        Assert.False(string.IsNullOrEmpty(error.Text("RequestId")));

        // Each request said that it went on as not signed in, under its own request id.
        var warnings = application.Events()
            .Where(written => written.Area == "Ecnanib.Accounts.SessionAuthenticationHandler" && written.Level == "Warning")
            .ToList();
        Assert.Equal(2, warnings.Count);
        Assert.All(warnings, warning => Assert.False(string.IsNullOrEmpty(warning.Text("RequestId"))));
        Assert.Equal(error.Text("RequestId"), warnings[1].Text("RequestId"));

        // The database is back, and so is the user.
        application.Database.Execute("ALTER TABLE sessions_away RENAME TO sessions");
        Assert.Contains("You are signed in", await client.PageAsync("/account"));
    }

    [Fact]
    public async Task What_a_signed_in_request_logs_names_the_user()
    {
        using var application = new AccountsApplication(host: host => host.HandleAtTheEnd(LogSomething));
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);

        (await client.GetAsync("/logs-something", TestContext.Current.CancellationToken)).Dispose();
        await client.SignInAsync(Ada);
        (await client.GetAsync("/logs-something", TestContext.Current.CancellationToken)).Dispose();

        var written = application.Events().Where(logged => logged.Text("Where") == "in a request").ToList();
        Assert.Equal(2, written.Count);
        Assert.False(written[0].Has("UserId"));
        Assert.Equal(user.Id.ToString(), written[1].Text("UserId"));
        Assert.All(written, logged => Assert.False(string.IsNullOrEmpty(logged.Text("RequestId"))));
    }

    /// <summary>Signs in and returns the answer itself, which sets the cookie.</summary>
    private static async Task<HttpResponseMessage> SignInAndStayAsync(HttpClient client, string password = AccountsApplication.Password)
    {
        var form = await client.PageAsync(SessionAuthentication.SignInPath);
        var answer = await client.SendFormAsync(
            SessionAuthentication.SignInPath, "sign-in", Browser.AntiforgeryTokenIn(form), ("Email", Ada), ("Password", password));

        Assert.Equal(HttpStatusCode.Redirect, answer.StatusCode);

        return answer;
    }

    /// <summary>The token of the session cookie that an answer sets.</summary>
    private static string TokenIn(HttpResponseMessage answer) =>
        SetCookie().Match(Browser.CookiesSetBy(answer).Single(cookie => cookie.StartsWith($"{Cookie}=", StringComparison.Ordinal)))
            .Groups["token"].Value;

    /// <summary>Asks for a path with a session cookie that the client was not given.</summary>
    private static async Task<HttpResponseMessage> GetWithCookieAsync(HttpClient client, string path, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Cookie", $"{Cookie}={token}");

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static Task<bool> LogSomething(HttpContext context)
    {
        if (context.Request.Path != "/logs-something")
        {
            return Task.FromResult(false);
        }

        context.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("Ecnanib.Tests.SomeComponent")
            .LogInformation("Written {Where}", "in a request");
        context.Response.StatusCode = StatusCodes.Status204NoContent;

        return Task.FromResult(true);
    }

    [GeneratedRegex("^__Host-ecnanib-session=(?<token>[A-Za-z0-9_-]{43}); path=/; secure; samesite=lax; httponly$")]
    private static partial Regex SetCookie();
}
