using System.Security.Cryptography;
using Ecnanib.Accounts;
using Ecnanib.Tests.BackgroundWork;

namespace Ecnanib.Tests.Accounts;

/// <summary>
/// A visitor can register with an email address and password, and is sent a verification email
/// (FR-ACC-1). An address that already has an account gets the same answer as a new one, and its
/// account is left as it is.
/// </summary>
public class RegistrationTests
{
    private const string Ada = "ada@example.com";

    private static readonly DateTimeOffset Morning = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_visitor_registers_and_is_sent_a_verification_link()
    {
        using var application = new AccountsApplication(clock: new TestClock(Morning));

        var result = await application.RegisterAsync(Ada);

        Assert.Equal(RegistrationOutcome.Accepted, result.Outcome);
        Assert.Empty(result.Problems);

        // The account is there, and its address is not verified yet.
        var user = Assert.Single(StoredUser.All(application.Database));
        Assert.Equal(Ada, user.Email);
        Assert.Equal(Morning, user.RegisteredAt);
        Assert.Null(user.EmailVerifiedAt);
        Assert.Equal(Morning + EmailVerification.LinkLifetime, user.TokenExpiresAt);

        // One message, to that address, with a link under the address users reach the service at.
        var message = Assert.Single(application.Email.Sent);
        Assert.Equal(Ada, message.To);
        Assert.Equal("Confirm your email address for Ecnanib Test", message.Subject);
        Assert.StartsWith($"{AccountsApplication.PublicUrl}/verify-email?token=", AccountsApplication.LinkIn(message).AbsoluteUri);
        Assert.Contains("The link works for 24 hours.", message.TextBody);
        Assert.DoesNotContain("registered before", message.TextBody);

        // The database holds the hash of the token in the link, and not the token.
        var token = Convert.FromBase64String(
            AccountsApplication.TokenIn(message).Replace('-', '+').Replace('_', '/') + "=");
        Assert.Equal(32, token.Length);
        Assert.Equal(SHA256.HashData(token), user.TokenHash);
    }

    [Fact]
    public async Task One_address_is_one_account_however_it_is_typed()
    {
        using var application = new AccountsApplication();

        await application.RegisterAsync("  Ada@Example.COM ");
        await application.RegisterAsync(Ada);

        var user = Assert.Single(StoredUser.All(application.Database));
        Assert.Equal(Ada, user.Email);
        Assert.All(application.Email.Sent, message => Assert.Equal(Ada, message.To));
    }

    public static TheoryData<string, string, RegistrationProblem[]> RequestsThatBreakARule => new()
    {
        { "not-an-address", AccountsApplication.Password, [RegistrationProblem.EmailInvalid] },
        { "ada@example", AccountsApplication.Password, [RegistrationProblem.EmailInvalid] },
        { "ada@@example.com", AccountsApplication.Password, [RegistrationProblem.EmailInvalid] },
        { "@example.com", AccountsApplication.Password, [RegistrationProblem.EmailInvalid] },
        { "ada lovelace@example.com", AccountsApplication.Password, [RegistrationProblem.EmailInvalid] },
        { "ada@example..com", AccountsApplication.Password, [RegistrationProblem.EmailInvalid] },
        { "", AccountsApplication.Password, [RegistrationProblem.EmailInvalid] },
        { Ada, "eleven-char", [RegistrationProblem.PasswordTooShort] },
        { Ada, "", [RegistrationProblem.PasswordTooShort] },

        // Eleven characters, though they take twenty-two units to store.
        { Ada, string.Concat(Enumerable.Repeat("🔐", 11)), [RegistrationProblem.PasswordTooShort] },
        { Ada, new string('x', 129), [RegistrationProblem.PasswordTooLong] },
        { "nobody", "short", [RegistrationProblem.EmailInvalid, RegistrationProblem.PasswordTooShort] },
    };

    [Theory]
    [MemberData(nameof(RequestsThatBreakARule))]
    public async Task A_request_that_breaks_a_rule_is_refused_and_nothing_is_stored_or_sent(
        string email, string password, RegistrationProblem[] problems)
    {
        using var application = new AccountsApplication();

        var result = await application.RegisterAsync(email, password);

        Assert.Equal(RegistrationOutcome.Invalid, result.Outcome);
        Assert.Equal(problems, result.Problems);
        Assert.Empty(StoredUser.All(application.Database));
        Assert.Empty(application.Email.Sent);
    }

    [Theory]
    [InlineData("twelve-chars")]
    [InlineData("🔐🔐🔐🔐🔐🔐🔐🔐🔐🔐🔐🔐")]
    public async Task A_password_of_twelve_characters_is_long_enough(string password)
    {
        using var application = new AccountsApplication();

        var result = await application.RegisterAsync(Ada, password);

        Assert.Equal(RegistrationOutcome.Accepted, result.Outcome);
        Assert.Single(StoredUser.All(application.Database));
    }

    [Fact]
    public async Task Registering_again_before_verifying_leaves_the_account_alone_and_sends_a_link_that_sets_the_password()
    {
        using var application = new AccountsApplication();

        await application.RegisterAsync(Ada, "the password chosen first");
        var first = StoredUser.Of(application.Database, Ada)!;
        var firstToken = AccountsApplication.TokenIn(application.Email.Sent[0]);

        var again = await application.RegisterAsync(Ada, "the password of someone else");

        // The same answer, and still one account, with the password it had.
        Assert.Equal(RegistrationOutcome.Accepted, again.Outcome);
        var user = Assert.Single(StoredUser.All(application.Database));
        Assert.Equal(first.Id, user.Id);
        Assert.Equal(first.PasswordHash, user.PasswordHash);
        Assert.True(user.LinkSetsPassword);
        Assert.Equal(3, StoredAcceptance.All(application.Database).Count);

        // A second message, which says that confirming it means choosing a password.
        Assert.Equal(2, application.Email.Sent.Count);
        var second = application.Email.Sent[1];
        var secondToken = AccountsApplication.TokenIn(second);
        Assert.Equal("Confirm your email address for Ecnanib Test", second.Subject);
        Assert.Contains("you choose a new password, and a password chosen earlier stops working.", second.TextBody);

        // The later link is the one that works, and it asks for a password.
        Assert.Equal(EmailVerificationOutcome.LinkNotValid, await application.ConfirmAsync(firstToken));
        Assert.Equal(EmailVerificationOutcome.PasswordNeeded, await application.ConfirmAsync(secondToken));
        Assert.Equal(EmailVerificationOutcome.PasswordTooShort, await application.ConfirmAsync(secondToken, "eleven-char"));
        Assert.Equal(
            EmailVerificationOutcome.PasswordTooLong, await application.ConfirmAsync(secondToken, new string('x', 129)));

        var unchanged = StoredUser.Of(application.Database, Ada)!;
        Assert.Null(unchanged.EmailVerifiedAt);
        Assert.Equal(first.PasswordHash, unchanged.PasswordHash);

        // Whoever reads the mail of the address chooses the password. Neither of the two that
        // were typed on the form is the account's any more.
        Assert.Equal(
            EmailVerificationOutcome.Verified, await application.ConfirmAsync(secondToken, "the password of the owner"));

        var verified = StoredUser.Of(application.Database, Ada)!;
        Assert.NotNull(verified.EmailVerifiedAt);
        Assert.False(verified.LinkSetsPassword);
        Assert.True(await application.Passwords.VerifyAsync("the password of the owner", verified.PasswordHash));
        Assert.False(await application.Passwords.VerifyAsync("the password chosen first", verified.PasswordHash));
        Assert.False(await application.Passwords.VerifyAsync("the password of someone else", verified.PasswordHash));

        // The link is spent.
        Assert.Equal(
            EmailVerificationOutcome.LinkNotValid, await application.ConfirmAsync(secondToken, "the password of the owner"));
    }

    [Fact]
    public async Task Confirming_an_address_that_was_registered_again_ends_the_sessions_of_whoever_registered_it_first()
    {
        using var application = new AccountsApplication();
        using var first = application.CreateClient();

        // Someone registers an address that is not theirs, and signs in to the account.
        await application.RegisterAsync(Ada, "the password of someone else");
        Assert.Equal("/", (await first.SignInAsync(Ada, "the password of someone else")).Path);
        Assert.Single(StoredSession.All(application.Database));

        // The owner of the address registers later, and confirms the link they are sent.
        await application.RegisterAsync(Ada, "what the owner typed on the form");
        Assert.True(await application.VerifyAsync(
            AccountsApplication.TokenIn(application.Email.Sent[1]), "the password of the owner"));

        // The first one is signed out, and their password is not the account's any more.
        Assert.Empty(StoredSession.All(application.Database));
        Assert.StartsWith(SessionAuthentication.SignInPath, (await first.OpenAsync("/account")).Address);
        Assert.Equal(SessionAuthentication.SignInPath, (await first.SignInAsync(Ada, "the password of someone else")).Path);

        using var owner = application.CreateClient();
        Assert.Equal("/", (await owner.SignInAsync(Ada, "the password of the owner")).Path);

        var chosen = Assert.Single(
            application.Events(),
            written => written.Template
                == "The email address of user {UserId} was verified with a new password, which ended {SessionCount} sessions");
        Assert.Equal(1, chosen["SessionCount"].GetInt32());
    }

    [Fact]
    public async Task An_address_with_a_verified_account_gets_the_same_answer_and_its_account_is_not_changed()
    {
        using var application = new AccountsApplication();
        var before = await application.RegisterVerifiedAsync(Ada);

        var again = await application.RegisterAsync(Ada, "the password of someone else");

        // What a new address gets: nothing says that this one has an account.
        Assert.Equal(RegistrationOutcome.Accepted, again.Outcome);
        Assert.Empty(again.Problems);

        var after = Assert.Single(StoredUser.All(application.Database));
        Assert.Equal(before.PasswordHash, after.PasswordHash);
        Assert.Equal(before.EmailVerifiedAt, after.EmailVerifiedAt);
        Assert.Null(after.TokenHash);
        Assert.Null(after.TokenExpiresAt);
        Assert.Equal(3, StoredAcceptance.All(application.Database).Count);

        // The owner is told, in a message without a link.
        Assert.Equal(2, application.Email.Sent.Count);
        var notice = application.Email.Sent[1];
        Assert.Equal(Ada, notice.To);
        Assert.Equal("You already have an account at Ecnanib Test", notice.Subject);
        Assert.Contains("Nothing about your account has changed.", notice.TextBody);
        Assert.DoesNotContain("http", notice.TextBody);
    }

    [Fact]
    public async Task When_the_email_cannot_be_sent_the_visitor_is_told_and_registering_again_sends_a_link()
    {
        using var application = new AccountsApplication();
        application.Email.Failure = new InvalidOperationException("The mail server is away.");

        var result = await application.RegisterAsync(Ada);

        Assert.Equal(RegistrationOutcome.EmailNotSent, result.Outcome);
        Assert.Empty(application.Email.Sent);

        // Logged once, with the cause, and without the address.
        var failure = Assert.Single(application.Events(), written => written.Level == "Error");
        Assert.Equal("Ecnanib.Accounts.Registration", failure.Area);
        Assert.Equal("The email of a registration could not be sent", failure.Template);
        Assert.Contains("The mail server is away.", failure.Exception);
        Assert.DoesNotContain(Ada, application.LogText());

        // The mail server is back.
        application.Email.Failure = null;
        var again = await application.RegisterAsync(Ada);

        Assert.Equal(RegistrationOutcome.Accepted, again.Outcome);
        Assert.Single(StoredUser.All(application.Database));
        // The account was there from the first try, so this link is of an address registered again.
        Assert.True(await application.VerifyAsync(
            AccountsApplication.TokenIn(Assert.Single(application.Email.Sent)), AccountsApplication.Password));
    }

    [Fact]
    public async Task Registrations_of_one_address_at_the_same_time_create_one_account()
    {
        using var application = new AccountsApplication();
        _ = application.Registration;

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() => application.RegisterAsync(Ada))));

        Assert.All(results, result => Assert.Equal(RegistrationOutcome.Accepted, result.Outcome));
        Assert.Single(StoredUser.All(application.Database));
        Assert.Equal(3, StoredAcceptance.All(application.Database).Count);
        Assert.Equal(6, application.Email.Sent.Count);

        // Whichever link went out last is the one in the database, and it works.
        var working = 0;
        foreach (var message in application.Email.Sent)
        {
            working += await application.VerifyAsync(AccountsApplication.TokenIn(message), AccountsApplication.Password) ? 1 : 0;
        }

        Assert.Equal(1, working);
    }

    [Fact]
    public async Task The_log_names_the_account_and_not_the_address()
    {
        using var application = new AccountsApplication();

        await application.RegisterVerifiedAsync(Ada);
        var user = StoredUser.Of(application.Database, Ada)!;

        var registered = Assert.Single(application.Events(), written => written.Template == "User {UserId} registered");
        Assert.Equal(user.Id.ToString(), registered.Text("UserId"));

        var verified = Assert.Single(
            application.Events(), written => written.Template == "The email address of user {UserId} was verified");
        Assert.Equal(user.Id.ToString(), verified.Text("UserId"));

        Assert.DoesNotContain(Ada, application.LogText());
    }
}
