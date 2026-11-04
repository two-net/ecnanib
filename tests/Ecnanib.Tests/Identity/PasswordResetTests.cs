using System.Net;
using Ecnanib.Modules.Identity.Contracts;
using Ecnanib.Platform.Contracts;
using Ecnanib.Tests.Audit;
using Ecnanib.Tests.Email;
using Ecnanib.Tests.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ecnanib.Tests.Identity;

/// <summary>FR-AUTH-06 with email on, through the real forms.</summary>
public sealed class PasswordResetTests(SharedEmailApp app) : IClassFixture<SharedEmailApp>
{
    private const string NewPassword = "a new password of some length";
    private const string Sent = "If an account uses that address";
    private const string Invalid = "This link has expired or has already been used.";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private IAccounts Accounts => app.Services.GetRequiredService<IAccounts>();

    private IPasswordResets Resets => app.Services.GetRequiredService<IPasswordResets>();

    [Fact]
    public async Task The_sign_in_page_links_to_it()
    {
        using var visitor = Visitor.To(app);

        Assert.Contains("href=\"/forgot-password\"", await visitor.PageAsync("/sign-in"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Asking_for_a_known_address_emails_a_link_from_the_public_address()
    {
        var email = await RegisterAsync();
        using var visitor = Visitor.To(app);

        using var response = await visitor.ForgotPasswordAsync(email);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(Sent, await response.Content.ReadAsStringAsync(Cancellation), StringComparison.Ordinal);

        var message = Assert.Single(ResetsTo(email));

        Assert.StartsWith($"{AccountApp.PublicBaseUrl}/reset-password?token=", CapturedEmail.LinkIn(message).AbsoluteUri, StringComparison.Ordinal);
    }

    /// <summary>The answer can't say whether the address has an account.</summary>
    [Fact]
    public async Task An_unknown_address_gets_the_same_answer_and_nothing_is_sent()
    {
        var email = Visitor.NewEmail();
        using var visitor = Visitor.To(app);

        using var response = await visitor.ForgotPasswordAsync(email);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(Sent, await response.Content.ReadAsStringAsync(Cancellation), StringComparison.Ordinal);
        Assert.Empty(app.Email.To(email));
    }

    [Theory]
    [InlineData("not an address")]
    [InlineData("Sam <sam@example.test>")]
    [InlineData("")]
    public async Task A_malformed_address_gets_the_same_answer(string email)
    {
        using var visitor = Visitor.To(app);

        using var response = await visitor.ForgotPasswordAsync(email);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(Sent, await response.Content.ReadAsStringAsync(Cancellation), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Asking_again_within_minutes_sends_nothing_and_keeps_the_first_link()
    {
        var email = await RegisterAsync();
        using var visitor = Visitor.To(app);

        (await visitor.ForgotPasswordAsync(email)).Dispose();
        var first = app.Email.LastPathTo(email);
        (await visitor.ForgotPasswordAsync(email.ToUpperInvariant())).Dispose();

        Assert.Single(ResetsTo(email));

        (await visitor.ResetPasswordAsync(first, NewPassword)).Dispose();

        Assert.NotNull((await Accounts.SignInAsync(email, NewPassword, Visitor.AnyClient, Cancellation)).Principal);
    }

    /// <summary>A mail scanner that opens the link must not spend it.</summary>
    [Fact]
    public async Task Opening_the_link_shows_the_form_and_uses_nothing()
    {
        var email = await RegisterAsync();
        var link = await RequestAsync(email);
        using var visitor = Visitor.To(app);

        Assert.Contains("Set the password", await visitor.PageAsync(link), StringComparison.Ordinal);
        Assert.Contains("Set the password", await visitor.PageAsync(link), StringComparison.Ordinal);
        Assert.Equal(1, await TokenRows.CountAsync(app.ConnectionString, email, TokenRows.Reset, Cancellation));
    }

    [Fact]
    public async Task Resetting_replaces_the_password_and_ends_every_session()
    {
        using var here = Visitor.To(app);
        using var there = Visitor.To(app);
        var email = await here.SignUpAsync();
        (await there.SignInAsync(email)).Dispose();
        var link = await RequestAsync(email);
        using var visitor = Visitor.To(app);

        using var response = await visitor.ResetPasswordAsync(link, NewPassword);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/sign-in?passwordReset=true", Visitor.Target(response));
        Assert.Empty(await SessionRows.OfAsync(app.ConnectionString, email, Cancellation));

        using var signedOutHere = await here.GetAsync("/account");
        using var signedOutThere = await there.GetAsync("/account");

        Assert.Equal(HttpStatusCode.Redirect, signedOutHere.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, signedOutThere.StatusCode);
        Assert.Null((await Accounts.SignInAsync(email, Visitor.Password, Visitor.AnyClient, Cancellation)).Principal);
        Assert.NotNull((await Accounts.SignInAsync(email, NewPassword, Visitor.AnyClient, Cancellation)).Principal);
        Assert.Contains("Your password has been reset", await visitor.PageAsync("/sign-in?passwordReset=true"), StringComparison.Ordinal);
    }

    /// <summary>Only the address's reader could have used the link, which is what verifying needs.</summary>
    [Fact]
    public async Task An_emailed_link_verifies_the_address()
    {
        var email = await RegisterAsync();
        using var visitor = Visitor.To(app);

        (await visitor.ResetPasswordAsync(await RequestAsync(email), NewPassword)).Dispose();

        Assert.True(await AccountRows.IsVerifiedAsync(app.ConnectionString, email, Cancellation));
    }

    [Fact]
    public async Task Resetting_is_audited()
    {
        var email = await RegisterAsync();
        using var visitor = Visitor.To(app);

        (await visitor.ResetPasswordAsync(await RequestAsync(email), NewPassword)).Dispose();

        var userId = await AccountRows.IdAsync(app.ConnectionString, email, Cancellation);
        var rows = await AuditRows.OfUserAsync(app.ConnectionString, userId, Cancellation);

        Assert.Equal(["PasswordResetRequested", "EmailVerified", "PasswordReset"], rows.Select(row => row.Action));
        Assert.Equal(("Visitor", (Guid?)null), (rows[0].ActorKind, rows[0].ActorId));
        Assert.Equal(("User", userId), (rows[2].ActorKind, rows[2].ActorId));
    }

    [Fact]
    public async Task Resetting_ends_a_lockout()
    {
        var email = await RegisterAsync();

        for (var attempt = 0; attempt < 5; attempt++)
        {
            Assert.Null((await Accounts.SignInAsync(email, "a wrong password altogether", Visitor.AnyClient, Cancellation)).Principal);
        }

        Assert.Null((await Accounts.SignInAsync(email, Visitor.Password, Visitor.AnyClient, Cancellation)).Principal);

        using var visitor = Visitor.To(app);
        (await visitor.ResetPasswordAsync(await RequestAsync(email), NewPassword)).Dispose();

        Assert.NotNull((await Accounts.SignInAsync(email, NewPassword, Visitor.AnyClient, Cancellation)).Principal);
    }

    [Fact]
    public async Task A_link_works_once()
    {
        var email = await RegisterAsync();
        var link = await RequestAsync(email);
        using var visitor = Visitor.To(app);

        (await visitor.ResetPasswordAsync(link, NewPassword)).Dispose();

        Assert.Contains(Invalid, await visitor.PageAsync(link), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_expired_link_is_refused()
    {
        var email = await RegisterAsync();
        var link = await RequestAsync(email);
        using var visitor = Visitor.To(app);
        await TokenRows.ExpireAsync(app.ConnectionString, email, TokenRows.Reset, Cancellation);

        Assert.Contains(Invalid, await visitor.PageAsync(link), StringComparison.Ordinal);
        Assert.Equal(PasswordResetResult.InvalidLink, await Resets.ResetAsync(CapturedEmail.TokenIn(link), NewPassword, Cancellation));
        Assert.NotNull((await Accounts.SignInAsync(email, Visitor.Password, Visitor.AnyClient, Cancellation)).Principal);
    }

    /// <summary>A link asked for before the change would otherwise undo it.</summary>
    [Fact]
    public async Task Changing_the_password_voids_an_outstanding_link()
    {
        using var visitor = Visitor.To(app);
        var email = await visitor.SignUpAsync();
        var link = await RequestAsync(email);

        (await visitor.ChangePasswordAsync(Visitor.Password, NewPassword)).Dispose();

        Assert.Contains(Invalid, await visitor.PageAsync(link), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("short", "Use at least 12 characters.")]
    [InlineData(NewPassword, "The passwords don&#x27;t match.")]
    public async Task A_refused_password_keeps_the_link_and_isnt_written_back(string password, string error)
    {
        var email = await RegisterAsync();
        var link = await RequestAsync(email);
        using var visitor = Visitor.To(app);

        using var response = await visitor.ResetPasswordAsync(link, password, confirmation: password == NewPassword ? "something else" : null);
        var page = await response.Content.ReadAsStringAsync(Cancellation);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(error, page, StringComparison.Ordinal);
        Assert.DoesNotContain($"value=\"{password}\"", page, StringComparison.Ordinal);
        Assert.Equal(1, await TokenRows.CountAsync(app.ConnectionString, email, TokenRows.Reset, Cancellation));
    }

    [Theory]
    [InlineData("")]
    [InlineData("?token=")]
    [InlineData("?token=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("?token=far-too-long-to-be-a-token-far-too-long-to-be-a-token-far-too-long-to-be-a-token")]
    public async Task A_made_up_link_is_refused(string query)
    {
        using var visitor = Visitor.To(app);

        Assert.Contains(Invalid, await visitor.PageAsync($"/reset-password{query}"), StringComparison.Ordinal);
    }

    /// <summary>
    /// The case #9 left, from the other side: a stranger registered the owner's address and can sign in
    /// to it. The owner's reset takes it back, signs the stranger out, and verifies the address.
    /// </summary>
    [Fact]
    public async Task The_owner_of_an_address_takes_it_back_from_a_stranger_who_registered_it()
    {
        using var stranger = Visitor.To(app);
        var email = await stranger.SignUpAsync();
        var link = await RequestAsync(email);
        using var owner = Visitor.To(app);

        (await owner.ResetPasswordAsync(link, NewPassword)).Dispose();

        using var strangerPage = await stranger.GetAsync("/account");

        Assert.Equal(HttpStatusCode.Redirect, strangerPage.StatusCode);
        Assert.Null((await Accounts.SignInAsync(email, Visitor.Password, Visitor.AnyClient, Cancellation)).Principal);
        Assert.True(await AccountRows.IsVerifiedAsync(app.ConnectionString, email, Cancellation));
    }

    /// <summary>Anonymous, so no session or redirect stands in front of the token check.</summary>
    [Fact]
    public async Task A_post_without_an_antiforgery_token_is_refused()
    {
        var email = await RegisterAsync();
        var link = await RequestAsync(email);
        using var visitor = Visitor.To(app);
        using var form = new FormUrlEncodedContent([new("Input.NewPassword", NewPassword), new("Input.ConfirmPassword", NewPassword)]);

        using var response = await visitor.Client.PostAsync(link, form, Cancellation);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotNull((await Accounts.SignInAsync(email, Visitor.Password, Visitor.AnyClient, Cancellation)).Principal);
    }

    private IEnumerable<EmailMessage> ResetsTo(string email) =>
        app.Email.To(email).Where(message => message.TextBody.Contains("/reset-password?token=", StringComparison.Ordinal));

    /// <summary>An account that hasn't signed in, so its only session would be one a test makes.</summary>
    private async Task<string> RegisterAsync()
    {
        using var visitor = Visitor.To(app);
        var email = Visitor.NewEmail();

        (await visitor.RegisterAsync(email)).Dispose();

        return email;
    }

    /// <summary>Asks through the contract, which the form calls, and returns the link as a path.</summary>
    private async Task<string> RequestAsync(string email)
    {
        await Resets.RequestAsync(email, Cancellation);

        return app.Email.LastPathTo(email);
    }
}

/// <summary>FR-AUTH-06 at its edges: with email off, and racing a sign-in.</summary>
public sealed class PasswordResetSettingsTests(PostgresFixture postgres)
{
    private const string NewPassword = "a new password of some length";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>EXT-EML-01: with email off, an administrator issues resets, and the page says so.</summary>
    [Fact]
    public async Task With_email_off_the_page_sends_users_to_an_administrator()
    {
        var captured = new CapturedEmail();
        await using var app = AccountApp.Create(postgres, services: captured.ReplaceSender);
        using var visitor = Visitor.To(app);
        var email = await visitor.SignUpAsync();
        var resets = app.Services.GetRequiredService<IPasswordResets>();

        var page = await visitor.PageAsync("/forgot-password");

        await resets.RequestAsync(email, Cancellation);

        Assert.Equal(PasswordResetMode.ByAdministrator, resets.Mode);
        Assert.Contains("so an administrator resets passwords", page, StringComparison.Ordinal);
        Assert.DoesNotContain("Send the link", page, StringComparison.Ordinal);
        Assert.Empty(captured.Sent);
    }

    /// <summary>
    /// Someone with the old password is verifying when the owner resets it, and would otherwise end up
    /// with a session the reset never saw.
    /// </summary>
    [Fact]
    public async Task A_sign_in_that_was_verifying_during_a_reset_doesnt_survive_it()
    {
        var hasher = new GatedHasher();
        var captured = new CapturedEmail();
        await using var app = AccountApp.Create(
            postgres,
            AccountApp.EmailOn(),
            services =>
            {
                captured.ReplaceSender(services);
                services.RemoveAll<IPasswordHasher>();
                services.AddSingleton<IPasswordHasher>(hasher);
            });
        using var visitor = Visitor.To(app);
        var email = Visitor.NewEmail();
        (await visitor.RegisterAsync(email)).Dispose();
        var accounts = app.Services.GetRequiredService<IAccounts>();
        var resets = app.Services.GetRequiredService<IPasswordResets>();

        await resets.RequestAsync(email, Cancellation);
        var token = CapturedEmail.TokenIn(captured.LastPathTo(email));

        var gate = hasher.HoldNextVerification();
        var signIn = accounts.SignInAsync(email, Visitor.Password, Visitor.AnyClient, Cancellation);
        await gate.UntilReachedAsync();

        Assert.Equal(PasswordResetResult.Reset, await resets.ResetAsync(token, NewPassword, Cancellation));
        gate.Open();

        Assert.Null((await signIn).Principal);
        Assert.Empty(await SessionRows.OfAsync(app.ConnectionString(), email, Cancellation));
    }
}
