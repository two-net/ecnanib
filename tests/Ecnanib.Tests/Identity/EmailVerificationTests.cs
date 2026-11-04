using System.Net;
using Ecnanib.Modules.Identity.Contracts;
using Ecnanib.Tests.Audit;
using Ecnanib.Tests.Email;
using Ecnanib.Tests.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Identity;

/// <summary>
/// FR-AUTH-03 and EXT-EML-01, through the real forms. A link verifies only for someone signed in to its
/// account, so a stranger who registered an address first can't have its owner verify it for them.
/// </summary>
public sealed class EmailVerificationTests(SharedEmailApp app) : IClassFixture<SharedEmailApp>
{
    private const string Invalid = "This link has expired, has already been used, or belongs to another account";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private IEmailVerification Verification => app.Services.GetRequiredService<IEmailVerification>();

    [Fact]
    public async Task Registering_emails_a_link_from_the_public_address()
    {
        using var visitor = Visitor.To(app);
        var email = Visitor.NewEmail();

        (await visitor.RegisterAsync(email)).Dispose();

        var message = Assert.Single(app.Email.To(email));

        Assert.Contains("Verify", message.Subject, StringComparison.Ordinal);
        Assert.StartsWith($"{AccountApp.PublicBaseUrl}/account/verify-email?token=", CapturedEmail.LinkIn(message).AbsoluteUri, StringComparison.Ordinal);
    }

    /// <summary>The answer and the time it takes can't say whether the address was taken, so neither can the mail.</summary>
    [Fact]
    public async Task Registering_a_taken_address_sends_nothing_and_answers_the_same()
    {
        using var visitor = Visitor.To(app);
        var email = Visitor.NewEmail();

        using var first = await visitor.RegisterAsync(email);
        using var second = await visitor.RegisterAsync(email, "a different password entirely");

        Assert.Equal(Visitor.Target(first), Visitor.Target(second));
        Assert.Single(app.Email.To(email));
    }

    [Fact]
    public async Task The_sign_in_page_says_a_link_was_sent()
    {
        using var visitor = Visitor.To(app);

        Assert.Contains("we&#x27;ve emailed it a link", await visitor.PageAsync("/sign-in?registered=true"), StringComparison.Ordinal);
    }

    /// <summary>A mail scanner that opens the link must not spend it.</summary>
    [Fact]
    public async Task Opening_the_link_changes_nothing_until_it_is_confirmed()
    {
        using var visitor = Visitor.To(app);
        var email = await visitor.SignUpAsync();
        var link = app.Email.LastPathTo(email);

        Assert.Contains("Verify the address", await visitor.PageAsync(link), StringComparison.Ordinal);
        Assert.False(await AccountRows.IsVerifiedAsync(app.ConnectionString, email, Cancellation));

        using var response = await visitor.VerifyEmailAsync(link);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/account?emailVerified=true", Visitor.Target(response));
        Assert.True(await AccountRows.IsVerifiedAsync(app.ConnectionString, email, Cancellation));
        Assert.Contains("Your email address is verified.", await visitor.PageAsync("/account?emailVerified=true"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Verifying_is_audited()
    {
        using var visitor = Visitor.To(app);
        var email = await visitor.SignUpAsync();

        (await visitor.VerifyEmailAsync(app.Email.LastPathTo(email))).Dispose();

        var userId = await AccountRows.IdAsync(app.ConnectionString, email, Cancellation);
        var rows = await AuditRows.OfUserAsync(app.ConnectionString, userId, Cancellation);

        Assert.Equal(["SignedIn", "EmailVerified"], rows.Select(row => row.Action));
        Assert.Equal(("User", userId), (rows[1].ActorKind, rows[1].ActorId));
    }

    [Fact]
    public async Task A_link_works_once()
    {
        using var visitor = Visitor.To(app);
        var email = await visitor.SignUpAsync();
        var link = app.Email.LastPathTo(email);

        (await visitor.VerifyEmailAsync(link)).Dispose();
        using var again = await visitor.VerifyEmailAsync(link);

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Contains(Invalid, await again.Content.ReadAsStringAsync(Cancellation), StringComparison.Ordinal);
    }

    /// <summary>
    /// The case #9 left: a stranger registered the address first. Its owner gets the link but is signed in
    /// as someone else, or not at all, so the stranger's account stays unverified.
    /// </summary>
    [Fact]
    public async Task A_link_for_another_account_is_refused_and_still_works_for_its_own()
    {
        using var owner = Visitor.To(app);
        using var someoneElse = Visitor.To(app);
        var email = await owner.SignUpAsync();
        await someoneElse.SignUpAsync();
        var link = app.Email.LastPathTo(email);

        using var refused = await someoneElse.VerifyEmailAsync(link);

        Assert.Contains(Invalid, await refused.Content.ReadAsStringAsync(Cancellation), StringComparison.Ordinal);
        Assert.False(await AccountRows.IsVerifiedAsync(app.ConnectionString, email, Cancellation));

        (await owner.VerifyEmailAsync(link)).Dispose();

        Assert.True(await AccountRows.IsVerifiedAsync(app.ConnectionString, email, Cancellation));
    }

    [Fact]
    public async Task A_visitor_signs_in_first_and_comes_back_to_the_link()
    {
        using var registrant = Visitor.To(app);
        var email = Visitor.NewEmail();
        (await registrant.RegisterAsync(email)).Dispose();
        var link = app.Email.LastPathTo(email);

        using var visitor = Visitor.To(app);
        using var challenge = await visitor.GetAsync(link);
        var signIn = Visitor.Target(challenge)!;

        Assert.Equal(HttpStatusCode.Redirect, challenge.StatusCode);
        Assert.StartsWith("/sign-in?returnUrl=", signIn, StringComparison.Ordinal);

        using var signedIn = await visitor.SignInAsync(email, page: signIn);

        Assert.Equal(link, Visitor.Target(signedIn));

        (await visitor.VerifyEmailAsync(link)).Dispose();

        Assert.True(await AccountRows.IsVerifiedAsync(app.ConnectionString, email, Cancellation));
    }

    [Fact]
    public async Task An_expired_link_is_refused()
    {
        using var visitor = Visitor.To(app);
        var email = await visitor.SignUpAsync();
        await TokenRows.ExpireAsync(app.ConnectionString, email, TokenRows.Verification, Cancellation);

        using var response = await visitor.VerifyEmailAsync(app.Email.LastPathTo(email));

        Assert.Contains(Invalid, await response.Content.ReadAsStringAsync(Cancellation), StringComparison.Ordinal);
        Assert.False(await AccountRows.IsVerifiedAsync(app.ConnectionString, email, Cancellation));
    }

    [Theory]
    [InlineData("")]
    [InlineData("?token=")]
    [InlineData("?token=not-a-token")]
    [InlineData("?token=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task A_made_up_link_is_refused(string query)
    {
        using var visitor = Visitor.To(app);
        await visitor.SignUpAsync();

        using var response = await visitor.VerifyEmailAsync($"/account/verify-email{query}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(Invalid, await response.Content.ReadAsStringAsync(Cancellation), StringComparison.Ordinal);
    }

    /// <summary>The limiter sees only the sender's address, so this is what keeps anyone from filling an inbox.</summary>
    [Fact]
    public async Task Asking_again_within_minutes_sends_nothing_and_keeps_the_first_link()
    {
        using var visitor = Visitor.To(app);
        var email = await visitor.SignUpAsync();
        var userId = await AccountRows.IdAsync(app.ConnectionString, email, Cancellation);
        var first = app.Email.LastPathTo(email);

        Assert.Equal(VerificationEmailResult.TooSoon, await Verification.SendAsync(userId, Cancellation));
        Assert.Single(app.Email.To(email));

        (await visitor.VerifyEmailAsync(first)).Dispose();

        Assert.True(await AccountRows.IsVerifiedAsync(app.ConnectionString, email, Cancellation));
    }

    [Fact]
    public async Task A_new_link_replaces_the_old_one_once_the_throttle_has_passed()
    {
        using var visitor = Visitor.To(app);
        var email = await visitor.SignUpAsync();
        var userId = await AccountRows.IdAsync(app.ConnectionString, email, Cancellation);
        var first = app.Email.LastPathTo(email);

        await TokenRows.AgeAsync(app.ConnectionString, email, TokenRows.Verification, "6 minutes", Cancellation);

        Assert.Equal(VerificationEmailResult.Sent, await Verification.SendAsync(userId, Cancellation));
        Assert.Equal(2, app.Email.To(email).Count);

        using var old = await visitor.VerifyEmailAsync(first);

        Assert.Contains(Invalid, await old.Content.ReadAsStringAsync(Cancellation), StringComparison.Ordinal);

        (await visitor.VerifyEmailAsync(app.Email.LastPathTo(email))).Dispose();

        Assert.True(await AccountRows.IsVerifiedAsync(app.ConnectionString, email, Cancellation));
    }

    [Fact]
    public async Task A_verified_address_gets_no_more_links()
    {
        using var visitor = Visitor.To(app);
        var email = await visitor.SignUpAsync();
        var userId = await AccountRows.IdAsync(app.ConnectionString, email, Cancellation);

        (await visitor.VerifyEmailAsync(app.Email.LastPathTo(email))).Dispose();

        Assert.Equal(VerificationEmailResult.AlreadyVerified, await Verification.SendAsync(userId, Cancellation));
        Assert.Single(app.Email.To(email));
    }

    [Fact]
    public async Task The_account_page_shows_whether_the_address_is_verified()
    {
        using var visitor = Visitor.To(app);
        var email = await visitor.SignUpAsync();

        var before = await visitor.PageAsync("/account");

        Assert.Contains("<dd>Not yet</dd>", before, StringComparison.Ordinal);
        Assert.Contains("Email me a verification link", before, StringComparison.Ordinal);

        (await visitor.VerifyEmailAsync(app.Email.LastPathTo(email))).Dispose();

        var after = await visitor.PageAsync("/account");

        Assert.Contains("<dd>Yes</dd>", after, StringComparison.Ordinal);
        Assert.DoesNotContain("Email me a verification link", after, StringComparison.Ordinal);
    }

    /// <summary>The requirement #39 checks before switching an agent to Live.</summary>
    [Fact]
    public async Task Live_needs_a_verified_address_when_email_is_on()
    {
        using var visitor = Visitor.To(app);
        var email = await visitor.SignUpAsync();
        var userId = await AccountRows.IdAsync(app.ConnectionString, email, Cancellation);

        Assert.True(Verification.IsEnabled);
        Assert.False(await Verification.IsSatisfiedAsync(userId, Cancellation));

        (await visitor.VerifyEmailAsync(app.Email.LastPathTo(email))).Dispose();

        Assert.True(await Verification.IsSatisfiedAsync(userId, Cancellation));
        Assert.False(await Verification.IsSatisfiedAsync(Guid.NewGuid(), Cancellation));
    }

    /// <summary>Signed in, because a visitor is turned away before the token is looked at.</summary>
    [Fact]
    public async Task A_post_without_an_antiforgery_token_is_refused()
    {
        using var visitor = Visitor.To(app);
        var email = await visitor.SignUpAsync();
        using var form = new FormUrlEncodedContent([new("_handler", Visitor.VerifyEmailForm)]);

        using var response = await visitor.Client.PostAsync(app.Email.LastPathTo(email), form, Cancellation);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(await AccountRows.IsVerifiedAsync(app.ConnectionString, email, Cancellation));
    }
}

/// <summary>What verification does with a listed address, and with email off.</summary>
public sealed class EmailVerificationSettingsTests(PostgresFixture postgres)
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>FR-AUTH-10: with email on, the link is what proves a listed address.</summary>
    [Fact]
    public async Task A_listed_address_is_an_administrator_once_verified()
    {
        var email = Visitor.NewEmail();
        var captured = new CapturedEmail();
        await using var app = AccountApp.CreateWithEmail(postgres, captured, new() { ["Admin:AdminEmails:0"] = email });
        using var visitor = Visitor.To(app);
        await visitor.SignUpAsync(email);
        var administrators = app.Services.GetRequiredService<IAdministrators>();
        var userId = await AccountRows.IdAsync(app.ConnectionString(), email, Cancellation);

        Assert.False(await administrators.IsAdministratorAsync(userId, Cancellation));

        (await visitor.VerifyEmailAsync(captured.LastPathTo(email))).Dispose();

        Assert.True(await administrators.IsAdministratorAsync(userId, Cancellation));
    }

    /// <summary>EXT-EML-01: with email off, nothing is verified, and nothing needs to be.</summary>
    [Fact]
    public async Task With_email_off_nothing_is_sent_or_needed()
    {
        var captured = new CapturedEmail();
        await using var app = AccountApp.Create(postgres, services: captured.ReplaceSender);
        using var visitor = Visitor.To(app);
        var email = await visitor.SignUpAsync();
        var userId = await AccountRows.IdAsync(app.ConnectionString(), email, Cancellation);
        var verification = app.Services.GetRequiredService<IEmailVerification>();

        Assert.Empty(captured.Sent);
        Assert.False(verification.IsEnabled);
        Assert.True(await verification.IsSatisfiedAsync(userId, Cancellation));
        Assert.Equal(VerificationEmailResult.Disabled, await verification.SendAsync(userId, Cancellation));
        Assert.DoesNotContain("Address verified", await visitor.PageAsync("/account"), StringComparison.Ordinal);
        Assert.Contains("Registration received. Sign in to continue.", await visitor.PageAsync("/sign-in?registered=true"), StringComparison.Ordinal);
    }
}
