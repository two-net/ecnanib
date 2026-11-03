using System.Net;
using Ecnanib.Modules.Identity.Contracts;
using Ecnanib.Tests.Jobs;
using Ecnanib.Tests.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Ecnanib.Tests.Audit;

namespace Ecnanib.Tests.Identity;

/// <summary>FR-AUTH-08, through the real form. CircuitAccessTests covers what it does to open circuits.</summary>
public sealed class PasswordChangeTests(SharedAccountApp app) : IClassFixture<SharedAccountApp>
{
    private const string NewPassword = "a new password of some length";
    private const string Wrong = "not the current password";
    private const string Canary = "canary-password-1234";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private IAccounts Accounts => app.Services.GetRequiredService<IAccounts>();

    [Fact]
    public async Task Changing_the_password_replaces_it()
    {
        using var visitor = Visitor.To(app);
        var email = await visitor.SignUpAsync();

        using var response = await visitor.ChangePasswordAsync(Visitor.Password, NewPassword);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/account?passwordChanged=true", Visitor.Target(response));
        Assert.Null(await Accounts.SignInAsync(email, Visitor.Password, Visitor.AnyClient, Cancellation));
        Assert.NotNull(await Accounts.SignInAsync(email, NewPassword, Visitor.AnyClient, Cancellation));
    }

    [Fact]
    public async Task The_new_password_is_hashed()
    {
        using var visitor = Visitor.To(app);
        var email = await visitor.SignUpAsync();

        (await visitor.ChangePasswordAsync(Visitor.Password, NewPassword)).Dispose();

        var id = await AccountRows.IdAsync(app.ConnectionString, email, Cancellation);

        Assert.StartsWith(
            "$argon2id$",
            await Sql.ScalarAsync<string>(app.ConnectionString, $"SELECT password_hash FROM users WHERE id = '{id}'", Cancellation),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_account_page_says_the_password_changed()
    {
        using var visitor = Visitor.To(app);
        await visitor.SignUpAsync();

        (await visitor.ChangePasswordAsync(Visitor.Password, NewPassword)).Dispose();

        Assert.Contains("Your password has been changed", await visitor.PageAsync("/account?passwordChanged=true"), StringComparison.Ordinal);
    }

    /// <summary>A visitor writes the query string, and a bool would make any other value a 500.</summary>
    [Theory]
    [InlineData("banana")]
    [InlineData("")]
    public async Task A_query_value_nobody_expected_is_ignored(string value)
    {
        using var visitor = Visitor.To(app);
        await visitor.SignUpAsync();

        Assert.DoesNotContain("Your password has been changed", await visitor.PageAsync($"/account?passwordChanged={value}"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Changing_the_password_revokes_every_other_session_and_keeps_this_one()
    {
        using var here = Visitor.To(app);
        using var there = Visitor.To(app);
        using var elsewhere = Visitor.To(app);
        var email = await here.SignUpAsync();

        (await there.SignInAsync(email)).Dispose();
        (await elsewhere.SignInAsync(email)).Dispose();

        (await here.ChangePasswordAsync(Visitor.Password, NewPassword)).Dispose();

        using var other = await there.GetAsync("/account");
        using var another = await elsewhere.GetAsync("/account");

        Assert.Equal(HttpStatusCode.Redirect, other.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, another.StatusCode);
        Assert.Contains(email, await here.PageAsync("/account"), StringComparison.Ordinal);
        Assert.Single(await SessionRows.OfAsync(app.ConnectionString, email, Cancellation));
    }

    /// <summary>Only this user's: the delete is by owner, so another account's sessions are safe.</summary>
    [Fact]
    public async Task Changing_a_password_leaves_other_users_alone()
    {
        using var mine = Visitor.To(app);
        using var theirs = Visitor.To(app);
        await mine.SignUpAsync();
        var email = await theirs.SignUpAsync();

        (await mine.ChangePasswordAsync(Visitor.Password, NewPassword)).Dispose();

        Assert.Contains(email, await theirs.PageAsync("/account"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_wrong_current_password_changes_nothing()
    {
        using var here = Visitor.To(app);
        using var there = Visitor.To(app);
        var email = await here.SignUpAsync();

        (await there.SignInAsync(email)).Dispose();

        using var response = await here.ChangePasswordAsync(Wrong, NewPassword);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("isn&#x27;t your current password", await response.Content.ReadAsStringAsync(Cancellation), StringComparison.Ordinal);
        Assert.Equal(2, (await SessionRows.OfAsync(app.ConnectionString, email, Cancellation)).Count);
        Assert.NotNull(await Accounts.SignInAsync(email, Visitor.Password, Visitor.AnyClient, Cancellation));
    }

    /// <summary>Someone holding a stolen session is who guesses here, so the guess has to cost what a sign-in does.</summary>
    [Fact]
    public async Task A_wrong_current_password_counts_towards_the_lockout()
    {
        using var visitor = Visitor.To(app);
        var email = await visitor.SignUpAsync();
        var id = await AccountRows.IdAsync(app.ConnectionString, email, Cancellation);

        (await visitor.ChangePasswordAsync(Wrong, NewPassword)).Dispose();

        Assert.Equal(1, await Sql.ScalarAsync<int>(app.ConnectionString, $"SELECT failed_sign_ins FROM users WHERE id = '{id}'", Cancellation));
    }

    [Fact]
    public async Task The_right_password_clears_the_count()
    {
        using var visitor = Visitor.To(app);
        var email = await visitor.SignUpAsync();
        var id = await AccountRows.IdAsync(app.ConnectionString, email, Cancellation);

        (await visitor.ChangePasswordAsync(Wrong, NewPassword)).Dispose();
        (await visitor.ChangePasswordAsync(Visitor.Password, NewPassword)).Dispose();

        Assert.Equal(0, await Sql.ScalarAsync<int>(app.ConnectionString, $"SELECT failed_sign_ins FROM users WHERE id = '{id}'", Cancellation));
    }

    [Fact]
    public async Task A_locked_account_is_refused_even_with_the_right_password()
    {
        using var visitor = Visitor.To(app);
        var email = await visitor.SignUpAsync();
        var id = await AccountRows.IdAsync(app.ConnectionString, email, Cancellation);

        await Sql.ExecuteAsync(app.ConnectionString, $"UPDATE users SET locked_until = now() + interval '1 hour' WHERE id = '{id}'", Cancellation);

        using var response = await visitor.ChangePasswordAsync(Visitor.Password, NewPassword);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("isn&#x27;t your current password", await response.Content.ReadAsStringAsync(Cancellation), StringComparison.Ordinal);

        await Sql.ExecuteAsync(app.ConnectionString, $"UPDATE users SET locked_until = NULL WHERE id = '{id}'", Cancellation);

        Assert.NotNull(await Accounts.SignInAsync(email, Visitor.Password, Visitor.AnyClient, Cancellation));
    }

    [Theory]
    [InlineData(11, "Use at least 12 characters")]
    [InlineData(257, "That password is too long")]
    public async Task A_new_password_of_the_wrong_length_changes_nothing(int length, string message)
    {
        using var visitor = Visitor.To(app);
        var email = await visitor.SignUpAsync();
        var id = await AccountRows.IdAsync(app.ConnectionString, email, Cancellation);

        using var response = await visitor.ChangePasswordAsync(Wrong, new string('a', length));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(message, await response.Content.ReadAsStringAsync(Cancellation), StringComparison.Ordinal);
        Assert.NotNull(await Accounts.SignInAsync(email, Visitor.Password, Visitor.AnyClient, Cancellation));

        // Refused before the current password was tried, so it cost no attempt and no hash.
        Assert.Equal(0, await Sql.ScalarAsync<int>(app.ConnectionString, $"SELECT failed_sign_ins FROM users WHERE id = '{id}'", Cancellation));
    }

    [Fact]
    public async Task Passwords_that_differ_change_nothing()
    {
        using var visitor = Visitor.To(app);
        var email = await visitor.SignUpAsync();

        using var response = await visitor.ChangePasswordAsync(Visitor.Password, NewPassword, "a different new password");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(await Accounts.SignInAsync(email, Visitor.Password, Visitor.AnyClient, Cancellation));
    }

    /// <summary>InputText writes its value into the page, and the framework's own sample echoes passwords this way.</summary>
    [Theory]
    [InlineData(Canary, NewPassword)]
    [InlineData(Wrong, Canary + " of some length")]
    public async Task A_refused_page_never_shows_a_password(string current, string next)
    {
        using var visitor = Visitor.To(app);
        await visitor.SignUpAsync();

        using var response = await visitor.ChangePasswordAsync(current, next, next + " but different");

        Assert.DoesNotContain(Canary, await response.Content.ReadAsStringAsync(Cancellation), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_page_is_for_signed_in_users()
    {
        using var visitor = Visitor.To(app);

        using var response = await visitor.GetAsync("/account/password");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/sign-in", response.Headers.Location?.AbsolutePath);
    }

    /// <summary>A static page, so a circuit never carries the password. See Forms that take a secret.</summary>
    [Fact]
    public async Task The_page_stays_static()
    {
        using var visitor = Visitor.To(app);
        await visitor.SignUpAsync();

        Assert.DoesNotContain("<!--Blazor:", await visitor.PageAsync("/account/password"), StringComparison.Ordinal);
    }

    /// <summary>CsrfTests posts as a visitor, who'd be sent to sign-in first, so this needs a session.</summary>
    [Fact]
    public async Task A_post_without_a_token_is_refused()
    {
        using var visitor = Visitor.To(app);
        await visitor.SignUpAsync();
        using var form = new FormUrlEncodedContent([new("Input.CurrentPassword", Visitor.Password), new("Input.NewPassword", NewPassword)]);

        using var response = await visitor.Client.PostAsync("/account/password", form, Cancellation);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task The_account_page_links_to_it()
    {
        using var visitor = Visitor.To(app);
        await visitor.SignUpAsync();

        Assert.Contains("href=\"/account/password\"", await visitor.PageAsync("/account"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Changing_the_password_is_audited_and_a_refusal_changes_nothing()
    {
        using var visitor = Visitor.To(app);
        var email = await visitor.SignUpAsync();

        (await visitor.ChangePasswordAsync(Wrong, NewPassword)).Dispose();
        (await visitor.ChangePasswordAsync(Visitor.Password, NewPassword)).Dispose();

        var userId = await AccountRows.IdAsync(app.ConnectionString, email, Cancellation);
        var rows = await AuditRows.OfUserAsync(app.ConnectionString, userId, Cancellation);

        Assert.Equal(["SignedIn", "PasswordChanged"], rows.Select(row => row.Action));
        Assert.Equal(("User", userId), (rows[1].ActorKind, rows[1].ActorId));
    }
}

/// <summary>What happens while a hash runs, which a fake hasher can hold open.</summary>
public sealed class PasswordChangeRaceTests(PostgresFixture postgres)
{
    private const string NewPassword = "a new password of some length";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>
    /// The hole this closes: someone with the old password is verifying when the owner changes it, and
    /// would otherwise end up with a session that the change never saw.
    /// </summary>
    [Fact]
    public async Task A_sign_in_that_was_verifying_during_a_change_doesnt_survive_it()
    {
        var hasher = new GatedHasher();
        await using var app = App(hasher);
        using var owner = Visitor.To(app);
        var email = await owner.SignUpAsync();
        var accounts = app.Services.GetRequiredService<IAccounts>();

        var gate = hasher.HoldNextVerification();
        var signIn = accounts.SignInAsync(email, Visitor.Password, Visitor.AnyClient, Cancellation);
        await gate.UntilReachedAsync();

        (await owner.ChangePasswordAsync(Visitor.Password, NewPassword)).Dispose();
        gate.Open();

        Assert.Null(await signIn);
        Assert.Single(await SessionRows.OfAsync(app.ConnectionString(), email, Cancellation));
    }

    /// <summary>Two changes verified against one hash: the second would otherwise undo the first without knowing it.</summary>
    [Fact]
    public async Task The_second_of_two_changes_at_once_is_refused()
    {
        var hasher = new GatedHasher();
        await using var app = App(hasher);
        using var visitor = Visitor.To(app);
        var email = await visitor.SignUpAsync();
        var accounts = app.Services.GetRequiredService<IAccounts>();
        var user = await accounts.SignInAsync(email, Visitor.Password, Visitor.AnyClient, Cancellation);

        var gate = hasher.HoldNextVerification();
        var slower = accounts.ChangePasswordAsync(user!, Visitor.Password, "the slower new password", Cancellation);
        await gate.UntilReachedAsync();

        Assert.Equal(PasswordChangeResult.Changed, await accounts.ChangePasswordAsync(user!, Visitor.Password, NewPassword, Cancellation));
        gate.Open();

        Assert.Equal(PasswordChangeResult.CurrentPasswordWrong, await slower);
        Assert.NotNull(await accounts.SignInAsync(email, NewPassword, Visitor.AnyClient, Cancellation));
    }

    private AppFactory App(GatedHasher hasher) =>
        AccountApp.Create(
            postgres,
            services: services =>
            {
                services.RemoveAll<IPasswordHasher>();
                services.AddSingleton<IPasswordHasher>(hasher);
            });

    private sealed class GatedHasher : IPasswordHasher
    {
        private Gate? _gate;

        /// <summary>Holds one verification once it has read the old hash, which is the moment that matters.</summary>
        public Gate HoldNextVerification()
        {
            var gate = new Gate();

            _gate = gate;

            return gate;
        }

        public Task<string> HashAsync(string password, CancellationToken cancellationToken) => Task.FromResult(password);

        public async Task<bool> VerifyAsync(string password, string hash, CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _gate, null) is { } gate)
            {
                await gate.PassAsync();
            }

            return password == hash;
        }

        public Task SpendAsync(string password, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
