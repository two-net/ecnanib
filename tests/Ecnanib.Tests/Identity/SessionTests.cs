using System.Data.Common;
using System.Net;
using System.Security.Claims;
using Ecnanib.Modules.Identity.Contracts;
using Ecnanib.Platform.Contracts;
using Ecnanib.Tests.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Ecnanib.Tests.Audit;

namespace Ecnanib.Tests.Identity;

/// <summary>FR-AUTH-07 over HTTP. CircuitAccessTests covers what HTTP can't reach.</summary>
public sealed class SessionTests(SharedAccountApp app) : IClassFixture<SharedAccountApp>
{
    private const string SessionCookie = "ecnanib.session";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private IAccounts Accounts => app.Services.GetRequiredService<IAccounts>();

    private ISessions Sessions => app.Services.GetRequiredService<ISessions>();

    [Fact]
    public async Task A_user_can_see_this_session_on_their_account()
    {
        using var visitor = Visitor.To(app);
        visitor.Client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Canary Browser/1.0");

        await visitor.SignUpAsync();

        var page = await visitor.PageAsync("/account");

        Assert.Contains("Canary Browser/1.0", page, StringComparison.Ordinal);
        Assert.Contains("This session", page, StringComparison.Ordinal);
        Assert.DoesNotContain(">Revoke<", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Another_session_shows_with_a_way_to_revoke_it()
    {
        using var here = Visitor.To(app);
        using var there = Visitor.To(app);
        var email = await here.SignUpAsync();

        (await there.SignInAsync(email)).Dispose();

        var page = await here.PageAsync("/account");

        Assert.Single(Occurrences(page, "This session"));
        Assert.Contains("Revoke", page, StringComparison.Ordinal);
    }

    /// <summary>Anyone can write this header, and the victim of a stolen session is who reads it.</summary>
    [Fact]
    public async Task A_user_agent_is_only_ever_text()
    {
        using var visitor = Visitor.To(app);
        visitor.Client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "<script>alert(1)</script>");

        await visitor.SignUpAsync();

        Assert.DoesNotContain("<script>alert(1)", await visitor.PageAsync("/account"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_session_shows_an_address_as_a_person_would_read_it()
    {
        var email = await RegisterAsync();

        var session = await SignInAsync(email, new ClientDetails(IPAddress.Parse("::ffff:203.0.113.7"), "Canary"));

        Assert.Equal("203.0.113.7", session.IpAddress);
        Assert.Equal("Canary", session.UserAgent);
    }

    [Fact]
    public async Task A_host_that_reports_nothing_still_starts_a_session()
    {
        var email = await RegisterAsync();

        var session = await SignInAsync(email, Visitor.AnyClient);

        Assert.Null(session.IpAddress);
        Assert.Null(session.UserAgent);
    }

    /// <summary>
    /// A client writes this header. PostgreSQL refuses a NUL, and half a surrogate pair can't be encoded,
    /// so either would be a 500 and a database row that a visitor chose to write.
    /// </summary>
    [Theory]
    [InlineData("Canary\0Browser", "CanaryBrowser")]
    [InlineData("Canary\r\n\tBrowser\u007F", "CanaryBrowser")]
    [InlineData("   ", null)]
    [InlineData("\0", null)]
    public async Task A_user_agent_is_stored_clean(string sent, string? stored)
    {
        var email = await RegisterAsync();

        var session = await SignInAsync(email, new ClientDetails(null, sent));

        Assert.Equal(stored, session.UserAgent);
    }

    [Fact]
    public async Task A_long_user_agent_is_cut_to_fit()
    {
        var email = await RegisterAsync();

        var session = await SignInAsync(email, new ClientDetails(null, new string('a', 2000)));

        Assert.Equal(512, session.UserAgent?.Length);
    }

    [Fact]
    public async Task Cutting_a_user_agent_never_splits_a_character()
    {
        var email = await RegisterAsync();

        var session = await SignInAsync(email, new ClientDetails(null, new string('a', 511) + "\U0001F600 and more"));

        Assert.Equal(new string('a', 511), session.UserAgent);
    }

    [Fact]
    public async Task A_session_ends_when_its_idle_timeout_passes()
    {
        using var visitor = Visitor.To(app);
        var session = await SignUpAsync(visitor);

        await SessionRows.SetLastSeenAsync(app.ConnectionString, session, "61 minutes", Cancellation);

        await AssertEndedAsync(visitor);
    }

    [Fact]
    public async Task A_session_ends_at_its_absolute_limit_however_active_it_is()
    {
        using var visitor = Visitor.To(app);
        var session = await SignUpAsync(visitor);

        await SessionRows.SetExpiredAsync(app.ConnectionString, session, Cancellation);
        await SessionRows.SetLastSeenAsync(app.ConnectionString, session, "0 seconds", Cancellation);

        await AssertEndedAsync(visitor);
    }

    /// <summary>The cookie is deleted, so a browser stops presenting it.</summary>
    [Fact]
    public async Task An_ended_session_clears_its_cookie()
    {
        using var visitor = Visitor.To(app);
        var session = await SignUpAsync(visitor);

        await SessionRows.SetExpiredAsync(app.ConnectionString, session, Cancellation);

        using var response = await visitor.GetAsync("/account");

        Assert.Contains(
            response.Headers.GetValues("Set-Cookie"),
            cookie => cookie.StartsWith($"{SessionCookie}=;", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_request_counts_as_activity()
    {
        using var visitor = Visitor.To(app);
        var session = await SignUpAsync(visitor);

        await SessionRows.SetLastSeenAsync(app.ConnectionString, session, "30 minutes", Cancellation);

        (await visitor.GetAsync("/account")).Dispose();

        Assert.True(await SessionRows.WasJustSeenAsync(app.ConnectionString, session, Cancellation));
    }

    /// <summary>A page load is several requests, and this is what stops each one writing a row.</summary>
    [Fact]
    public async Task A_recently_seen_session_isnt_written_again()
    {
        using var visitor = Visitor.To(app);
        var session = await SignUpAsync(visitor);

        await SessionRows.SetLastSeenAsync(app.ConnectionString, session, "30 seconds", Cancellation);

        (await visitor.GetAsync("/account")).Dispose();

        Assert.InRange(await SessionRows.SecondsSinceSeenAsync(app.ConnectionString, session, Cancellation), 29, 40);
    }

    /// <summary>
    /// The row decides, so a reissued cookie would only be a way for a ticket to outlive it. A sliding
    /// cookie renews past half its life, so this one has to be old for the test to mean anything.
    /// </summary>
    [Fact]
    public async Task The_cookie_isnt_reissued()
    {
        const string Scheme = CookieAuthenticationDefaults.AuthenticationScheme;

        using var visitor = Visitor.To(app);
        var email = await visitor.SignUpAsync();
        var session = Assert.Single(await SessionRows.OfAsync(app.ConnectionString, email, Cancellation));
        var options = app.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(Scheme);
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, (await UserIdAsync(email)).ToString()),
                new Claim(AccountClaims.Session, session.ToString()),
                new Claim(AccountClaims.LegalDocuments, LegalDocuments.Current),
            ],
            Scheme);
        var now = DateTimeOffset.UtcNow;
        var properties = new AuthenticationProperties { IssuedUtc = now.AddHours(-23), ExpiresUtc = now.AddHours(1) };
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), properties, Scheme);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/account");
        request.Headers.Add("Cookie", $"{SessionCookie}={options.TicketDataFormat.Protect(ticket)}");

        using var anotherBrowser = Visitor.To(app);
        using var response = await anotherBrowser.Client.SendAsync(request, Cancellation);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(
            response.Headers.TryGetValues("Set-Cookie", out var cookies)
                && cookies.Any(cookie => cookie.StartsWith(SessionCookie, StringComparison.Ordinal)));
    }

    /// <summary>A cookie from before sessions existed has nothing to look up, and it has to fail closed.</summary>
    [Fact]
    public async Task A_cookie_with_no_session_is_refused()
    {
        var options = app.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(CookieAuthenticationDefaults.AuthenticationScheme);
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())], CookieAuthenticationDefaults.AuthenticationScheme);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), CookieAuthenticationDefaults.AuthenticationScheme);

        using var visitor = Visitor.To(app);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/account");
        request.Headers.Add("Cookie", $"{SessionCookie}={options.TicketDataFormat.Protect(ticket)}");

        using var response = await visitor.Client.SendAsync(request, Cancellation);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/sign-in", response.Headers.Location?.AbsolutePath);
    }

    [Fact]
    public async Task A_list_holds_only_that_users_sessions()
    {
        var mine = await RegisterAsync();
        var theirs = await RegisterAsync();

        var session = await SignInAsync(mine, Visitor.AnyClient);
        await SignInAsync(theirs, Visitor.AnyClient);

        var listed = Assert.Single(await Sessions.ListAsync(await UserIdAsync(mine), Cancellation));

        Assert.Equal(session.Id, listed.Id);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_list_leaves_out_an_expired_session(bool idle)
    {
        var email = await RegisterAsync();
        var session = await SignInAsync(email, Visitor.AnyClient);

        await (idle
            ? SessionRows.SetLastSeenAsync(app.ConnectionString, session.Id, "61 minutes", Cancellation)
            : SessionRows.SetExpiredAsync(app.ConnectionString, session.Id, Cancellation));

        Assert.Empty(await Sessions.ListAsync(await UserIdAsync(email), Cancellation));
    }

    [Fact]
    public async Task A_list_puts_the_most_recently_used_first()
    {
        var email = await RegisterAsync();
        var older = await SignInAsync(email, new ClientDetails(null, "older"));
        var newer = await SignInAsync(email, new ClientDetails(null, "newer"));

        await SessionRows.SetLastSeenAsync(app.ConnectionString, older.Id, "10 minutes", Cancellation);

        var listed = await Sessions.ListAsync(await UserIdAsync(email), Cancellation);

        Assert.Equal([newer.Id, older.Id], listed.Select(session => session.Id));
    }

    [Fact]
    public async Task Revoking_a_session_ends_it_on_the_next_request()
    {
        using var here = Visitor.To(app);
        using var there = Visitor.To(app);
        var email = await here.SignUpAsync();

        (await there.SignInAsync(email)).Dispose();

        var sessions = await SessionRows.OfAsync(app.ConnectionString, email, Cancellation);

        Assert.True(await Sessions.RevokeAsync(await UserIdAsync(email), sessions[0], Cancellation));

        await AssertEndedAsync(here);
        Assert.Contains(email, await there.PageAsync("/account"), StringComparison.Ordinal);
    }

    /// <summary>NFR-SEC-04: the ID alone proves nothing, because the check that matters is whose it is.</summary>
    [Fact]
    public async Task A_user_cant_revoke_someone_elses_session()
    {
        using var victim = Visitor.To(app);
        var victimEmail = await victim.SignUpAsync();
        var session = Assert.Single(await SessionRows.OfAsync(app.ConnectionString, victimEmail, Cancellation));
        var attacker = await UserIdAsync(await RegisterAsync());

        Assert.False(await Sessions.RevokeAsync(attacker, session, Cancellation));

        Assert.Contains(victimEmail, await victim.PageAsync("/account"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Revoking_a_session_that_has_gone_changes_nothing()
    {
        var email = await RegisterAsync();

        Assert.False(await Sessions.RevokeAsync(await UserIdAsync(email), Guid.NewGuid(), Cancellation));
    }

    [Fact]
    public async Task Signing_out_deletes_the_session()
    {
        using var visitor = Visitor.To(app);
        var email = await visitor.SignUpAsync();

        (await visitor.SignOutAsync()).Dispose();

        Assert.Empty(await SessionRows.OfAsync(app.ConnectionString, email, Cancellation));
    }

    /// <summary>Sign-out ends a session too, but the user did something different, so the trail says so.</summary>
    [Fact]
    public async Task Revoking_and_signing_out_are_audited_apart()
    {
        using var here = Visitor.To(app);
        using var there = Visitor.To(app);
        var email = await here.SignUpAsync();

        (await there.SignInAsync(email)).Dispose();

        var sessions = await SessionRows.OfAsync(app.ConnectionString, email, Cancellation);
        var userId = await UserIdAsync(email);

        Assert.True(await Sessions.RevokeAsync(userId, sessions[0], Cancellation));
        Assert.False(await Sessions.RevokeAsync(userId, sessions[0], Cancellation));
        (await there.SignOutAsync()).Dispose();

        var rows = await AuditRows.OfUserAsync(app.ConnectionString, userId, Cancellation);

        Assert.Equal(
            [("SignedIn", sessions[0].ToString()), ("SignedIn", sessions[1].ToString()), ("SessionRevoked", sessions[0].ToString()), ("SignedOut", sessions[1].ToString())],
            rows.Select(row => (row.Action, row.TargetId!)));
    }

    /// <summary>The cookie is replaced here, and a principal without the claim would sign the user out.</summary>
    [Fact]
    public async Task Accepting_the_documents_keeps_the_session()
    {
        using var visitor = Visitor.To(app);
        var email = await visitor.SignUpAsync();
        var session = Assert.Single(await SessionRows.OfAsync(app.ConnectionString, email, Cancellation));
        var id = await UserIdAsync(email);

        await Sql.ExecuteAsync(app.ConnectionString, $"UPDATE legal_acceptances SET version = 'older' WHERE user_id = '{id}'", Cancellation);
        (await visitor.SignOutAsync()).Dispose();
        (await visitor.SignInAsync(email)).Dispose();

        session = Assert.Single(await SessionRows.OfAsync(app.ConnectionString, email, Cancellation));

        using var accepted = await visitor.SubmitAsync(
            "/legal/accept",
            Visitor.AcceptForm,
            ("Input.AcceptTermsOfUse", "true"),
            ("Input.AcceptRiskDisclosure", "true"),
            ("Input.TermsOfUseVersion", LegalDocuments.TermsOfUseVersion),
            ("Input.RiskDisclosureVersion", LegalDocuments.RiskDisclosureVersion));

        Assert.Equal(HttpStatusCode.Redirect, accepted.StatusCode);
        Assert.Contains(email, await visitor.PageAsync("/account"), StringComparison.Ordinal);
        Assert.Equal(session, Assert.Single(await SessionRows.OfAsync(app.ConnectionString, email, Cancellation)));
    }

    private static List<int> Occurrences(string text, string value)
    {
        var found = new List<int>();

        for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + 1, StringComparison.Ordinal))
        {
            found.Add(index);
        }

        return found;
    }

    private static async Task AssertEndedAsync(Visitor visitor)
    {
        using var response = await visitor.GetAsync("/account");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/sign-in", response.Headers.Location?.AbsolutePath);
    }

    private async Task<Guid> SignUpAsync(Visitor visitor) =>
        Assert.Single(await SessionRows.OfAsync(app.ConnectionString, await visitor.SignUpAsync(), Cancellation));

    private async Task<string> RegisterAsync()
    {
        using var visitor = Visitor.To(app);
        var email = Visitor.NewEmail();

        (await visitor.RegisterAsync(email)).Dispose();

        return email;
    }

    private async Task<SessionRecord> SignInAsync(string email, ClientDetails client)
    {
        var principal = (await Accounts.SignInAsync(email, Visitor.Password, client, Cancellation)).Principal;
        var sessionId = AccountClaims.SessionId(principal!);

        return (await Sessions.ListAsync(await UserIdAsync(email), Cancellation)).Single(session => session.Id == sessionId);
    }

    private Task<Guid> UserIdAsync(string email) => AccountRows.IdAsync(app.ConnectionString, email, Cancellation);
}

/// <summary>The settings, and the failure path, which each need an application of their own.</summary>
public sealed class SessionHostingTests(PostgresFixture postgres)
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_absolute_limit_comes_from_configuration()
    {
        await using var app = AccountApp.Create(postgres, new() { ["Security:SessionAbsoluteTimeoutHours"] = "3" });
        using var visitor = Visitor.To(app);

        await visitor.SignUpAsync();

        var hours = await Sql.ScalarAsync<double>(
            app.ConnectionString(),
            "SELECT (extract(epoch FROM expires_at - created_at) / 3600)::float8 FROM sessions",
            Cancellation);

        Assert.Equal(3, hours, precision: 3);
    }

    [Fact]
    public async Task The_absolute_limit_defaults_to_a_day()
    {
        await using var app = AccountApp.Create(postgres);
        using var visitor = Visitor.To(app);

        await visitor.SignUpAsync();

        var hours = await Sql.ScalarAsync<double>(
            app.ConnectionString(),
            "SELECT (extract(epoch FROM expires_at - created_at) / 3600)::float8 FROM sessions",
            Cancellation);

        Assert.Equal(24, hours, precision: 3);
    }

    [Fact]
    public async Task The_idle_timeout_comes_from_configuration()
    {
        await using var app = AccountApp.Create(postgres, new() { ["Security:SessionIdleTimeoutMinutes"] = "5" });
        using var visitor = Visitor.To(app);
        var connectionString = app.ConnectionString();
        var email = await visitor.SignUpAsync();
        var session = Assert.Single(await SessionRows.OfAsync(connectionString, email, Cancellation));

        await SessionRows.SetLastSeenAsync(connectionString, session, "6 minutes", Cancellation);

        using var response = await visitor.GetAsync("/account");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    /// <summary>
    /// The exception handler runs authentication again to render the error page, so a lookup that fails
    /// twice would leave an empty 500. The session may be fine, so the cookie has to survive it too.
    /// </summary>
    [Fact]
    public async Task A_failed_session_lookup_shows_the_error_page_and_keeps_the_cookie()
    {
        var failure = new FailingSessions();

        await using var app = AccountApp.Create(
            postgres,
            services: services => services.ConfigureDbContext<EcnanibDbContext>(options => options.AddInterceptors(failure), ServiceLifetime.Singleton),
            environment: Environments.Production);
        using var visitor = Visitor.ToPublic(app);
        var email = await visitor.SignUpAsync();

        failure.Failing = true;
        using var response = await visitor.GetAsync("/account");
        failure.Failing = false;

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("<h1", await response.Content.ReadAsStringAsync(Cancellation), StringComparison.Ordinal);
        Assert.Contains(email, await visitor.PageAsync("/account"), StringComparison.Ordinal);
    }

    /// <summary>
    /// An accepted limit: a tab can sit past its session until the next expiry run, and its token is bound
    /// to a user the request no longer has. DisableAntiforgery is banned, so this stays. What matters is
    /// that the cookie still goes. See Sessions in docs/architecture.md. In Production, because Development
    /// rethrows a bad request as a 500.
    /// </summary>
    [Fact]
    public async Task Signing_out_of_a_dead_session_is_refused_but_clears_the_cookie()
    {
        await using var app = AccountApp.Create(postgres, environment: Environments.Production);
        using var visitor = Visitor.ToPublic(app);
        var email = await visitor.SignUpAsync();
        var session = Assert.Single(await SessionRows.OfAsync(app.ConnectionString(), email, Cancellation));
        var form = await visitor.SignOutFormAsync();

        await SessionRows.SetExpiredAsync(app.ConnectionString(), session, Cancellation);

        using var response = await visitor.PostSignOutAsync(form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(
            response.Headers.GetValues("Set-Cookie"),
            cookie => cookie.StartsWith("ecnanib.session=;", StringComparison.Ordinal));
    }

    private sealed class FailingSessions : DbCommandInterceptor
    {
        public bool Failing { get; set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default) =>
            Failing && command.CommandText.Contains("FROM sessions", StringComparison.Ordinal)
                ? throw new InvalidOperationException("The session lookup failed.")
                : ValueTask.FromResult(result);
    }
}
