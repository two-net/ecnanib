using System.Security.Cryptography;
using Ecnanib.Accounts;
using Ecnanib.Tests.BackgroundWork;

namespace Ecnanib.Tests.Accounts;

/// <summary>
/// A user can reset a forgotten password by email (FR-ACC-2). A link works once, for one hour,
/// and until a later link replaces it. Setting a password with it ends every session of the
/// account.
/// </summary>
public class PasswordResetTests
{
    private const string Ada = "ada@example.com";

    private const string NewPassword = "a brand new password";

    private static readonly DateTimeOffset Morning = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_user_who_asks_is_sent_a_link_that_sets_a_new_password()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        var before = await application.RegisterVerifiedAsync(Ada);

        clock.Set(Morning.AddMinutes(10));
        var outcome = await application.RequestResetAsync("  Ada@Example.COM ");

        Assert.Equal(PasswordResetRequestOutcome.Accepted, outcome);

        // One message, to the address of the account, with a link under the address users reach the service at.
        var message = application.Email.Sent[^1];
        Assert.Equal(2, application.Email.Sent.Count);
        Assert.Equal(Ada, message.To);
        Assert.Equal("Choose a new password for Ecnanib Test", message.Subject);
        Assert.StartsWith($"{AccountsApplication.PublicUrl}/reset-password?token=", AccountsApplication.LinkIn(message).AbsoluteUri);
        Assert.Contains("The link works for 60 minutes, and once.", message.TextBody);

        // The database holds the hash of the token in the link, and not the token. Nothing else changed.
        var user = StoredUser.Of(application.Database, Ada)!;
        var token = Convert.FromBase64String(
            AccountsApplication.TokenIn(message).Replace('-', '+').Replace('_', '/') + "=");
        Assert.Equal(32, token.Length);
        Assert.Equal(SHA256.HashData(token), user.ResetTokenHash);
        Assert.Equal(Morning.AddMinutes(10) + PasswordResetLink.Lifetime, user.ResetExpiresAt);
        Assert.Equal(before.PasswordHash, user.PasswordHash);

        // The log names the account and not the address.
        var sent = Assert.Single(
            application.Events(), written => written.Template == "A password reset link was sent to user {UserId}");
        Assert.Equal(user.Id.ToString(), sent.Text("UserId"));
        Assert.DoesNotContain(Ada, application.LogText());
    }

    [Fact]
    public async Task An_address_without_an_account_gets_the_same_answer_and_a_notice()
    {
        using var application = new AccountsApplication();
        await application.RegisterVerifiedAsync(Ada);

        var outcome = await application.RequestResetAsync("grace@example.com");

        // What an address with an account gets: nothing says that this one has none.
        Assert.Equal(PasswordResetRequestOutcome.Accepted, outcome);

        // Its owner is told, in a message without a link.
        var notice = application.Email.Sent[^1];
        Assert.Equal(2, application.Email.Sent.Count);
        Assert.Equal("grace@example.com", notice.To);
        Assert.Equal("No account at Ecnanib Test with this address", notice.Subject);
        Assert.DoesNotContain("http", notice.TextBody);

        // Nothing is stored for it, and the account that is there is left alone.
        var user = Assert.Single(StoredUser.All(application.Database));
        Assert.Null(user.ResetTokenHash);
        Assert.DoesNotContain("example.com", application.LogText());
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-address")]
    [InlineData("ada@example")]
    public async Task What_is_not_an_address_is_refused_and_nothing_is_sent(string email)
    {
        using var application = new AccountsApplication();

        Assert.Equal(PasswordResetRequestOutcome.Invalid, await application.RequestResetAsync(email));
        Assert.Empty(application.Email.Sent);
    }

    [Fact]
    public async Task The_link_sets_the_password_once_and_ends_every_session_of_the_account()
    {
        using var application = new AccountsApplication();
        using var laptop = application.CreateClient();
        using var phone = application.CreateClient();
        using var other = application.CreateClient();
        var before = await application.RegisterVerifiedAsync(Ada);
        await application.RegisterVerifiedAsync("grace@example.com");
        await laptop.SignInAsync(Ada);
        await phone.SignInAsync(Ada);
        await other.SignInAsync("grace@example.com");

        await application.RequestResetAsync(Ada);
        var token = AccountsApplication.TokenIn(application.Email.Sent[^1]);

        Assert.Equal(PasswordResetOutcome.Done, await application.ResetAsync(token, NewPassword));

        // The new password is the account's, and the earlier one is not.
        var user = StoredUser.Of(application.Database, Ada)!;
        Assert.True(await application.Passwords.VerifyAsync(NewPassword, user.PasswordHash));
        Assert.False(await application.Passwords.VerifyAsync(AccountsApplication.Password, user.PasswordHash));
        Assert.Equal(before.EmailVerifiedAt, user.EmailVerifiedAt);

        // The link is spent: nothing of it is left in the database.
        Assert.Null(user.ResetTokenHash);
        Assert.Null(user.ResetExpiresAt);
        Assert.Equal(PasswordResetOutcome.LinkNotValid, await application.ResetAsync(token, "yet another password"));
        Assert.True(await application.Passwords.VerifyAsync(NewPassword, StoredUser.Of(application.Database, Ada)!.PasswordHash));

        // Nobody is signed in to the account any more. Another account is not touched.
        Assert.StartsWith(SessionAuthentication.SignInPath, (await laptop.OpenAsync("/account")).Address);
        Assert.StartsWith(SessionAuthentication.SignInPath, (await phone.OpenAsync("/account")).Address);
        Assert.Contains("You are signed in as grace@example.com.", await other.PageAsync("/account"));
        Assert.NotEqual(user.Id, Assert.Single(StoredSession.All(application.Database)).UserId);

        // The owner is told, and the log names the account.
        var notice = application.Email.Sent[^1];
        Assert.Equal(Ada, notice.To);
        Assert.Equal("Your password at Ecnanib Test was changed", notice.Subject);
        Assert.DoesNotContain("http", notice.TextBody);

        var changed = Assert.Single(
            application.Events(),
            written => written.Template == "User {UserId} set a new password with a reset link, which ended {SessionCount} sessions");
        Assert.Equal(user.Id.ToString(), changed.Text("UserId"));
        Assert.Equal(2, changed["SessionCount"].GetInt32());
        Assert.DoesNotContain("example.com", application.LogText());
    }

    [Fact]
    public async Task A_link_works_until_one_hour_has_passed()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        await application.RegisterVerifiedAsync(Ada);
        var grace = await application.RegisterVerifiedAsync("grace@example.com");
        await application.RequestResetAsync(Ada);
        var adasToken = AccountsApplication.TokenIn(application.Email.Sent[^1]);
        await application.RequestResetAsync("grace@example.com");
        var gracesToken = AccountsApplication.TokenIn(application.Email.Sent[^1]);

        clock.Set(Morning.AddHours(1).AddSeconds(-1));
        Assert.Equal(PasswordResetOutcome.Done, await application.ResetAsync(adasToken, NewPassword));

        clock.Set(Morning.AddHours(1));
        Assert.Equal(PasswordResetOutcome.LinkNotValid, await application.ResetAsync(gracesToken, NewPassword));
        Assert.Equal(grace.PasswordHash, StoredUser.Of(application.Database, "grace@example.com")!.PasswordHash);
    }

    [Fact]
    public async Task A_later_link_ends_the_earlier_one()
    {
        using var application = new AccountsApplication();
        await application.RegisterVerifiedAsync(Ada);
        await application.RequestResetAsync(Ada);
        var first = AccountsApplication.TokenIn(application.Email.Sent[^1]);
        await application.RequestResetAsync(Ada);
        var second = AccountsApplication.TokenIn(application.Email.Sent[^1]);

        Assert.Equal(PasswordResetOutcome.LinkNotValid, await application.ResetAsync(first, NewPassword));
        Assert.Equal(PasswordResetOutcome.Done, await application.ResetAsync(second, NewPassword));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-token")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")] // Shaped like a token, and no account's.
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAB")] // As long as a token, with an end that no 32 bytes have.
    [InlineData("!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!")] // As long as a token, in characters that no token has.
    [InlineData("' OR 1=1 --")]
    public async Task What_is_not_the_token_of_a_link_sets_no_password(string token)
    {
        using var application = new AccountsApplication();
        var before = await application.RegisterVerifiedAsync(Ada);
        await application.RequestResetAsync(Ada);

        Assert.Equal(PasswordResetOutcome.LinkNotValid, await application.ResetAsync(token, NewPassword));
        Assert.Equal(before.PasswordHash, StoredUser.Of(application.Database, Ada)!.PasswordHash);
    }

    [Fact]
    public async Task The_link_of_one_account_sets_the_password_of_no_other()
    {
        using var application = new AccountsApplication();
        await application.RegisterVerifiedAsync(Ada);
        var grace = await application.RegisterVerifiedAsync("grace@example.com");
        await application.RequestResetAsync(Ada);

        Assert.Equal(
            PasswordResetOutcome.Done,
            await application.ResetAsync(AccountsApplication.TokenIn(application.Email.Sent[^1]), NewPassword));

        Assert.Equal(grace.PasswordHash, StoredUser.Of(application.Database, "grace@example.com")!.PasswordHash);
    }

    [Fact]
    public async Task A_new_password_that_breaks_a_rule_is_refused_and_the_link_still_works()
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClient();
        var before = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        await application.RequestResetAsync(Ada);
        var token = AccountsApplication.TokenIn(application.Email.Sent[^1]);

        Assert.Equal(PasswordResetOutcome.PasswordTooShort, await application.ResetAsync(token, "eleven-char"));
        Assert.Equal(PasswordResetOutcome.PasswordTooShort, await application.ResetAsync(token, string.Empty));
        Assert.Equal(PasswordResetOutcome.PasswordTooLong, await application.ResetAsync(token, new string('x', 129)));

        // Nothing changed, and nobody was signed out.
        Assert.Equal(before.PasswordHash, StoredUser.Of(application.Database, Ada)!.PasswordHash);
        Assert.Single(StoredSession.All(application.Database));

        Assert.Equal(PasswordResetOutcome.Done, await application.ResetAsync(token, "twelve-chars"));
    }

    [Fact]
    public async Task Two_resets_with_one_link_at_the_same_time_set_one_password()
    {
        using var application = new AccountsApplication();
        await application.RegisterVerifiedAsync(Ada);
        await application.RequestResetAsync(Ada);
        var token = AccountsApplication.TokenIn(application.Email.Sent[^1]);
        _ = application.PasswordReset;

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 6).Select(
            attempt => Task.Run(() => application.ResetAsync(token, $"the password of attempt {attempt}"))));

        Assert.Single(outcomes, outcome => outcome == PasswordResetOutcome.Done);
        Assert.Equal(5, outcomes.Count(outcome => outcome == PasswordResetOutcome.LinkNotValid));
    }

    [Fact]
    public async Task A_sign_in_with_the_earlier_password_does_not_outlast_a_reset_that_it_races()
    {
        using var application = new AccountsApplication();
        await application.RegisterVerifiedAsync(Ada);
        await application.RequestResetAsync(Ada);
        var token = AccountsApplication.TokenIn(application.Email.Sent[^1]);

        // Browsers that sign in with the earlier password, some before the reset is done and
        // some after, each with its form already open.
        var browsers = new List<(HttpClient Client, string Form)>();
        for (var browser = 0; browser < 12; browser++)
        {
            var client = application.CreateClient();
            browsers.Add((client, Browser.AntiforgeryTokenIn(await client.PageAsync(SessionAuthentication.SignInPath))));
        }

        try
        {
            var signIns = browsers.Select((browser, index) => Task.Run(async () =>
            {
                await Task.Delay(index * 5, TestContext.Current.CancellationToken);
                await browser.Client.PostAsync(
                    SessionAuthentication.SignInPath,
                    "sign-in",
                    browser.Form,
                    ("Email", Ada),
                    ("Password", AccountsApplication.Password));
            }));
            var reset = Task.Run(() => application.ResetAsync(token, NewPassword));

            await Task.WhenAll(signIns.Append(reset));

            Assert.Equal(PasswordResetOutcome.Done, await reset);
        }
        finally
        {
            browsers.ForEach(browser => browser.Client.Dispose());
        }

        // One that was first is ended by the reset, and one that was later is refused. Whichever
        // it was, nobody is signed in on the earlier password.
        Assert.Empty(StoredSession.All(application.Database));
    }

    [Fact]
    public async Task A_reset_does_not_confirm_the_address_of_the_account()
    {
        using var application = new AccountsApplication();
        await application.RegisterAsync(Ada);
        var verification = AccountsApplication.TokenIn(application.Email.Sent[0]);
        await application.RequestResetAsync(Ada);

        Assert.Equal(
            PasswordResetOutcome.Done,
            await application.ResetAsync(AccountsApplication.TokenIn(application.Email.Sent[^1]), NewPassword));

        // Only a link from the registration form confirms it, and that link still stands.
        Assert.Null(StoredUser.Of(application.Database, Ada)!.EmailVerifiedAt);
        Assert.True(await application.VerifyAsync(verification));
        Assert.True(await application.Passwords.VerifyAsync(NewPassword, StoredUser.Of(application.Database, Ada)!.PasswordHash));
    }

    [Fact]
    public async Task A_reset_ends_a_verification_link_that_would_set_the_password()
    {
        using var application = new AccountsApplication();
        await application.RegisterAsync(Ada, "the password chosen first");
        await application.RegisterAsync(Ada, "typed the second time");
        var verification = AccountsApplication.TokenIn(application.Email.Sent[1]);
        Assert.True(StoredUser.Of(application.Database, Ada)!.LinkSetsPassword);
        await application.RequestResetAsync(Ada);

        Assert.Equal(
            PasswordResetOutcome.Done,
            await application.ResetAsync(AccountsApplication.TokenIn(application.Email.Sent[^1]), NewPassword));

        // Whoever holds that link could otherwise replace the password that was just set.
        var user = StoredUser.Of(application.Database, Ada)!;
        Assert.Null(user.TokenHash);
        Assert.Null(user.TokenExpiresAt);
        Assert.False(user.LinkSetsPassword);
        Assert.Equal(
            EmailVerificationOutcome.LinkNotValid, await application.ConfirmAsync(verification, "the password of someone else"));
        Assert.True(await application.Passwords.VerifyAsync(NewPassword, StoredUser.Of(application.Database, Ada)!.PasswordHash));
    }

    [Fact]
    public async Task The_notice_is_sent_though_whoever_set_the_password_has_gone()
    {
        using var application = new AccountsApplication();
        await application.RegisterVerifiedAsync(Ada);
        await application.RequestResetAsync(Ada);
        var token = AccountsApplication.TokenIn(application.Email.Sent[^1]);

        // The connection is closed as soon as the password is set, which cancels the request.
        using var request = new CancellationTokenSource();
        application.Email.Sending = _ => request.Cancel();

        Assert.Equal(PasswordResetOutcome.Done, await application.PasswordReset.ResetAsync(token, NewPassword, AuditOrigin.Unknown, request.Token));

        Assert.Equal("Your password at Ecnanib Test was changed", application.Email.Sent[^1].Subject);
        Assert.True(await application.Passwords.VerifyAsync(NewPassword, StoredUser.Of(application.Database, Ada)!.PasswordHash));
    }

    [Fact]
    public async Task When_the_email_cannot_be_sent_the_user_is_told_and_asking_again_sends_a_link()
    {
        using var application = new AccountsApplication();
        await application.RegisterVerifiedAsync(Ada);
        application.Email.Failure = new InvalidOperationException("The mail server is away.");

        Assert.Equal(PasswordResetRequestOutcome.EmailNotSent, await application.RequestResetAsync(Ada));

        // An address without an account is told the same.
        Assert.Equal(PasswordResetRequestOutcome.EmailNotSent, await application.RequestResetAsync("grace@example.com"));

        // Logged with the cause, and without the address.
        var failures = application.Events().Where(written => written.Level == "Error").ToList();
        Assert.Equal(2, failures.Count);
        Assert.All(failures, failure =>
        {
            Assert.Equal("Ecnanib.Accounts.PasswordReset", failure.Area);
            Assert.Equal("The email of a password reset could not be sent", failure.Template);
            Assert.Contains("The mail server is away.", failure.Exception);
        });
        Assert.DoesNotContain("example.com", application.LogText());

        // The mail server is back.
        application.Email.Failure = null;
        Assert.Equal(PasswordResetRequestOutcome.Accepted, await application.RequestResetAsync(Ada));
        Assert.Equal(
            PasswordResetOutcome.Done,
            await application.ResetAsync(AccountsApplication.TokenIn(application.Email.Sent[^1]), NewPassword));
    }

    [Fact]
    public async Task When_the_notice_cannot_be_sent_the_password_is_set_all_the_same()
    {
        using var application = new AccountsApplication();
        await application.RegisterVerifiedAsync(Ada);
        await application.RequestResetAsync(Ada);
        var token = AccountsApplication.TokenIn(application.Email.Sent[^1]);
        application.Email.Failure = new InvalidOperationException("The mail server is away.");

        Assert.Equal(PasswordResetOutcome.Done, await application.ResetAsync(token, NewPassword));

        Assert.True(await application.Passwords.VerifyAsync(NewPassword, StoredUser.Of(application.Database, Ada)!.PasswordHash));
        var failure = Assert.Single(application.Events(), written => written.Level == "Error");
        Assert.Equal("The notice of a changed password could not be sent", failure.Template);
    }
}
