using System.Security.Claims;
using Ecnanib.Accounts;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.Tests.Logging;
using Microsoft.AspNetCore.Http;

namespace Ecnanib.Tests.Accounts;

/// <summary>
/// A page that follows what changes keeps a connection open, whose user is decided when it is
/// made (FR-NTF-2). So such a page asks whether the session of its connection still stands, and
/// what it does by itself is no activity of its user: a session ends after the configured
/// period of inactivity (NFR-SEC-1), whether a page follows or not. See "The session of a
/// connection" in docs/accounts.md. The file of the tests sets the period to 45 minutes.
/// </summary>
public class ConnectedSessionTests
{
    private const string Ada = "ada@example.com";
    private const string Grace = "grace@example.com";

    private const string WhichSession = "/which-session";

    private static readonly DateTimeOffset Morning = new(2026, 11, 24, 9, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan Idle = TimeSpan.FromMinutes(45);

    [Fact]
    public async Task The_user_of_a_request_names_the_session_that_it_is_signed_in_with_and_a_visitor_names_none()
    {
        using var application = new AccountsApplication(host: host => host.HandleAtTheEnd(WhichSessionAsync));
        using var client = application.CreateClient();
        using var visitor = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        var session = Assert.Single(StoredSession.All(application.Database));

        // The id of the row, which is not the token of the cookie: it proves nothing.
        Assert.Equal(session.Id.ToString(), await client.PageAsync(WhichSession));
        Assert.Equal("none", await visitor.PageAsync(WhichSession));

        // An identity that nothing vouched for names none, whatever it claims.
        var claimed = new Claim(SessionAuthentication.SessionClaimType, session.Id.ToString());
        Assert.Null(SignedInUser.SessionIdOf(new ClaimsPrincipal(new ClaimsIdentity([claimed]))));
        Assert.Null(SignedInUser.SessionIdOf(new ClaimsPrincipal()));
    }

    [Fact]
    public async Task The_session_of_a_connection_stands_until_it_was_left_alone_for_the_period_and_asking_is_no_activity()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        using var client = application.CreateClient();
        var ada = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        var connection = Assert.Single(StoredSession.All(application.Database)).ConnectionOf(ada);

        Assert.True(await application.ConnectedSession.StandsAsync(connection, TestContext.Current.CancellationToken));

        // Asked again and again, as a page does for as long as it is open.
        for (var asked = TimeSpan.FromMinutes(5); asked < Idle; asked += TimeSpan.FromMinutes(5))
        {
            clock.Set(Morning + asked);
            Assert.True(await application.ConnectedSession.StandsAsync(connection, TestContext.Current.CancellationToken));
        }

        // None of it was use of the session, so the period counts from the last request of its user.
        Assert.Equal(Morning, Assert.Single(StoredSession.All(application.Database)).LastActiveAt);

        clock.Set(Morning + Idle - TimeSpan.FromSeconds(1));
        Assert.True(await application.ConnectedSession.StandsAsync(connection, TestContext.Current.CancellationToken));

        clock.Set(Morning + Idle);
        Assert.False(await application.ConnectedSession.StandsAsync(connection, TestContext.Current.CancellationToken));

        // Asking ends nothing. The session ends when a request next carries its token.
        Assert.Single(StoredSession.All(application.Database));
        Assert.StartsWith(SessionAuthentication.SignInPath, (await client.OpenAsync("/account")).Address);
        Assert.Empty(StoredSession.All(application.Database));
    }

    [Fact]
    public async Task The_session_of_a_connection_no_longer_stands_once_its_user_signed_out_or_set_a_new_password()
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClient();
        using var elsewhere = application.CreateClient();
        var ada = await application.RegisterVerifiedAsync(Ada);

        await client.SignInAsync(Ada);
        var first = Assert.Single(StoredSession.All(application.Database)).ConnectionOf(ada);
        Assert.True(await application.ConnectedSession.StandsAsync(first, TestContext.Current.CancellationToken));

        await client.SignOutAsync();
        Assert.False(await application.ConnectedSession.StandsAsync(first, TestContext.Current.CancellationToken));

        // On two browsers. A new password ends every session of the account.
        await client.SignInAsync(Ada);
        await elsewhere.SignInAsync(Ada);
        var connections = StoredSession.All(application.Database).Select(session => session.ConnectionOf(ada)).ToList();
        Assert.Equal(2, connections.Count);

        await application.RequestResetAsync(Ada);
        await application.ResetAsync(AccountsApplication.TokenIn(application.Email.Sent[^1]), "another password entirely");

        foreach (var connection in connections)
        {
            Assert.False(await application.ConnectedSession.StandsAsync(connection, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task A_session_stands_for_its_own_user_alone_and_for_nobody_who_names_none()
    {
        using var application = new AccountsApplication();
        using var adas = application.CreateClient();
        var ada = await application.RegisterVerifiedAsync(Ada);
        var grace = await application.RegisterVerifiedAsync(Grace);
        await adas.SignInAsync(Ada);
        var session = Assert.Single(StoredSession.All(application.Database));

        Assert.True(await application.ConnectedSession.StandsAsync(session.ConnectionOf(ada), TestContext.Current.CancellationToken));

        // The session of another user is not found, like one that does not exist.
        Assert.False(await application.ConnectedSession.StandsAsync(session.ConnectionOf(grace), TestContext.Current.CancellationToken));

        // A user who names no session, one whom nothing vouched for, and nobody at all.
        var id = new Claim(ClaimTypes.NameIdentifier, ada.Id.ToString());
        var named = new Claim(SessionAuthentication.SessionClaimType, session.Id.ToString());

        foreach (var nobody in new[]
        {
            new ClaimsPrincipal(new ClaimsIdentity([id], SessionAuthentication.Scheme)),
            new ClaimsPrincipal(new ClaimsIdentity([id, named])),
            new ClaimsPrincipal(),
        })
        {
            Assert.False(await application.ConnectedSession.StandsAsync(nobody, TestContext.Current.CancellationToken));

            // And such a one expects nothing: what it says is not kept.
            application.ConnectedSession.ExpectsPageAgain(nobody, "/account");
        }
    }

    [Fact]
    public async Task The_connection_of_a_page_is_no_activity_and_a_session_that_was_left_alone_ends_with_it_all_the_same()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        // The page connects, and connects again whenever its connection was lost, for as long
        // as it is open. Whatever the framework answers, the session was not used.
        foreach (var minutes in new[] { 2, 10, 30, 44 })
        {
            clock.Set(Morning.AddMinutes(minutes));

            using var connected = await client.PostAsync(
                $"{SessionAuthentication.ConnectionPath}/negotiate?negotiateVersion=1", content: null, TestContext.Current.CancellationToken);

            Assert.Equal(Morning, Assert.Single(StoredSession.All(application.Database)).LastActiveAt);
        }

        // What its user does is activity, as before.
        Assert.Contains("You are signed in", await client.PageAsync("/account"));
        Assert.Equal(Morning.AddMinutes(44), Assert.Single(StoredSession.All(application.Database)).LastActiveAt);

        // Left alone for the whole period, the session ends with whatever carries its token next.
        clock.Set(Morning.AddMinutes(44) + Idle);

        using var late = await client.PostAsync(
            $"{SessionAuthentication.ConnectionPath}/negotiate?negotiateVersion=1", content: null, TestContext.Current.CancellationToken);

        Assert.Empty(StoredSession.All(application.Database));
    }

    [Fact]
    public async Task A_page_that_asks_for_itself_again_is_no_activity_when_it_said_that_it_would_and_only_then()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        using var client = application.CreateClient();
        var ada = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        var connection = Assert.Single(StoredSession.All(application.Database)).ConnectionOf(ada);

        // The page said so, and asks at once.
        clock.Set(Morning.AddMinutes(2));
        application.ConnectedSession.ExpectsPageAgain(connection, "/account");

        Assert.Contains("You are signed in", await client.AskAgainAsync("/account"));
        Assert.Equal(Morning, Assert.Single(StoredSession.All(application.Database)).LastActiveAt);

        // Its word is good for one request. The same request once more is its user's, as far as anyone knows.
        Assert.Contains("You are signed in", await client.AskAgainAsync("/account"));
        Assert.Equal(Morning.AddMinutes(2), Assert.Single(StoredSession.All(application.Database)).LastActiveAt);
    }

    [Fact]
    public async Task Each_page_that_says_it_asks_again_is_expected_once_so_two_pages_of_one_address_keep_no_session_alive()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        using var client = application.CreateClient();
        var ada = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        var connection = Assert.Single(StoredSession.All(application.Database)).ConnectionOf(ada);

        // Two tabs of one browser show the same page, and both hear the same change.
        clock.Set(Morning.AddMinutes(2));
        application.ConnectedSession.ExpectsPageAgain(connection, "/notifications?page=2");
        application.ConnectedSession.ExpectsPageAgain(connection, "/notifications?page=2");

        await client.AskAgainAsync("/notifications?page=2", found: false);
        await client.AskAgainAsync("/notifications?page=2", found: false);
        Assert.Equal(Morning, Assert.Single(StoredSession.All(application.Database)).LastActiveAt);

        // A third request was said by neither.
        await client.AskAgainAsync("/notifications?page=2", found: false);
        Assert.Equal(Morning.AddMinutes(2), Assert.Single(StoredSession.All(application.Database)).LastActiveAt);
    }

    [Theory]
    [InlineData("another address")]
    [InlineData("an ordinary request")]
    [InlineData("too late")]
    [InlineData("another session")]
    public async Task What_a_page_did_not_say_it_would_ask_is_activity(string what)
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        using var client = application.CreateClient();
        using var elsewhere = application.CreateClient();
        var ada = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        var session = Assert.Single(StoredSession.All(application.Database));

        clock.Set(Morning.AddMinutes(2));
        await elsewhere.SignInAsync(Ada);
        var other = StoredSession.All(application.Database).Single(stored => stored.Id != session.Id);

        application.ConnectedSession.ExpectsPageAgain((what == "another session" ? other : session).ConnectionOf(ada), "/account");

        switch (what)
        {
            case "another address":
                // With its query, an address is another one.
                await client.AskAgainAsync("/account?again=1");
                break;

            case "an ordinary request":
                // Its user reloads the page, or follows a link to it.
                await client.PageAsync("/account");
                break;

            case "too late":
                clock.Set(clock.GetUtcNow() + TimeSpan.FromSeconds(10));
                await client.AskAgainAsync("/account");
                break;

            default:
                await client.AskAgainAsync("/account");
                break;
        }

        Assert.Equal(clock.GetUtcNow(), StoredSession.All(application.Database).Single(stored => stored.Id == session.Id).LastActiveAt);
    }

    /// <summary>Stands for a page that follows: it says which session its request is signed in with.</summary>
    private static async Task<bool> WhichSessionAsync(HttpContext request)
    {
        if (!request.Request.Path.StartsWithSegments(WhichSession))
        {
            return false;
        }

        await request.Response.WriteAsync(SignedInUser.SessionIdOf(request.User)?.ToString() ?? "none");

        return true;
    }
}
