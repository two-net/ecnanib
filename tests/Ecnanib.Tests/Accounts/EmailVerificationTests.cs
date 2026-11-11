using System.Buffers.Text;
using System.Security.Cryptography;
using Ecnanib.Accounts;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.Tests.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Ecnanib.Tests.Accounts;

/// <summary>
/// A user verifies their email address with the link they were sent (FR-ACC-1). A link works
/// once, for 24 hours, and until a later link replaces it.
/// </summary>
public class EmailVerificationTests
{
    private const string Ada = "ada@example.com";

    private static readonly DateTimeOffset Morning = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task The_link_verifies_the_address_once()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        await application.RegisterAsync(Ada);
        var token = AccountsApplication.TokenIn(application.Email.Sent[0]);

        clock.Set(Morning.AddMinutes(5));
        var verified = await application.VerifyAsync(token);

        Assert.True(verified);
        var user = StoredUser.Of(application.Database, Ada)!;
        Assert.Equal(Morning.AddMinutes(5), user.EmailVerifiedAt);

        // The link is spent: nothing of it is left in the database.
        Assert.Null(user.TokenHash);
        Assert.Null(user.TokenExpiresAt);
        Assert.False(await application.VerifyAsync(token));
        Assert.Equal(Morning.AddMinutes(5), StoredUser.Of(application.Database, Ada)!.EmailVerifiedAt);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-token")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")] // Shaped like a token, and no account's.
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAB")] // As long as a token, with an end that no 32 bytes have.
    [InlineData("!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!")] // As long as a token, in characters that no token has.
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA==")]
    [InlineData("' OR 1=1 --")]
    public async Task What_is_not_the_token_of_a_link_verifies_nothing(string token)
    {
        using var application = new AccountsApplication();
        await application.RegisterAsync(Ada);

        Assert.False(await application.VerifyAsync(token));
        Assert.Null(StoredUser.Of(application.Database, Ada)!.EmailVerifiedAt);
    }

    [Fact]
    public async Task A_link_works_until_24_hours_have_passed()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        await application.RegisterAsync(Ada);
        await application.RegisterAsync("grace@example.com");

        clock.Set(Morning.AddHours(24).AddSeconds(-1));
        Assert.True(await application.VerifyAsync(AccountsApplication.TokenIn(application.Email.Sent[0])));

        clock.Set(Morning.AddHours(24));
        Assert.False(await application.VerifyAsync(AccountsApplication.TokenIn(application.Email.Sent[1])));
        Assert.Null(StoredUser.Of(application.Database, "grace@example.com")!.EmailVerifiedAt);
    }

    [Fact]
    public async Task Registering_again_after_the_link_expired_sends_one_that_works()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        await application.RegisterAsync(Ada);

        clock.Set(Morning.AddDays(3));
        Assert.False(await application.VerifyAsync(AccountsApplication.TokenIn(application.Email.Sent[0])));

        Assert.Equal(RegistrationOutcome.Accepted, (await application.RegisterAsync(Ada)).Outcome);
        Assert.Equal(
            Morning.AddDays(3) + EmailVerification.LinkLifetime, StoredUser.Of(application.Database, Ada)!.TokenExpiresAt);

        // The address was registered twice, so the link asks for the password of the account.
        Assert.True(await application.VerifyAsync(
            AccountsApplication.TokenIn(application.Email.Sent[1]), AccountsApplication.Password));
        Assert.Equal(Morning.AddDays(3), StoredUser.Of(application.Database, Ada)!.EmailVerifiedAt);
    }

    [Fact]
    public async Task A_first_link_takes_no_password()
    {
        using var application = new AccountsApplication();
        await application.RegisterAsync(Ada, "the password chosen first");
        var before = StoredUser.Of(application.Database, Ada)!;
        Assert.False(before.LinkSetsPassword);

        // A password that comes with it is not the account's: nothing asked for one.
        Assert.True(await application.VerifyAsync(
            AccountsApplication.TokenIn(application.Email.Sent[0]), "a password nobody asked for"));

        var after = StoredUser.Of(application.Database, Ada)!;
        Assert.NotNull(after.EmailVerifiedAt);
        Assert.Equal(before.PasswordHash, after.PasswordHash);
    }

    [Fact]
    public async Task A_link_that_was_out_before_repeat_links_set_the_password_asks_for_one()
    {
        // A database of the version before, in which a link does not say whether it is the first
        // link of its address or a later one, which then kept the first password.
        var database = PostgresServer.CreateDatabase();
        using (var before = database.Context())
        {
            before.GetService<IMigrator>().Migrate("20261111172937_AddAccounts");
        }

        var token = RandomNumberGenerator.GetBytes(32);
        using (var connection = database.Open())
        using (var insert = new NpgsqlCommand(
            """
            INSERT INTO users (id, email, password_hash, registered_at, email_verified_at, email_verification_token_hash, email_verification_expires_at)
            VALUES (@waiting, 'ada@example.com', 'not-a-hash', @now, NULL, @hash, @later),
                   (@verified, 'grace@example.com', 'not-a-hash', @now, @now, NULL, NULL)
            """,
            connection))
        {
            insert.Parameters.AddWithValue("waiting", Guid.CreateVersion7());
            insert.Parameters.AddWithValue("verified", Guid.CreateVersion7());
            insert.Parameters.AddWithValue("now", DateTimeOffset.UtcNow);
            insert.Parameters.AddWithValue("later", DateTimeOffset.UtcNow.AddHours(24));
            insert.Parameters.AddWithValue("hash", SHA256.HashData(token));
            insert.ExecuteNonQuery();
        }

        // Starting this version migrates the database.
        using var application = new AccountsApplication(database: database);
        _ = application.Registration;

        Assert.True(StoredUser.Of(database, Ada)!.LinkSetsPassword);
        Assert.False(StoredUser.Of(database, "grace@example.com")!.LinkSetsPassword);

        var link = Base64Url.EncodeToString(token);
        Assert.Equal(EmailVerificationOutcome.PasswordNeeded, await application.ConfirmAsync(link));
        Assert.Equal(EmailVerificationOutcome.Verified, await application.ConfirmAsync(link, "the password of the owner"));
        Assert.True(await application.Passwords.VerifyAsync("the password of the owner", StoredUser.Of(database, Ada)!.PasswordHash));
    }

    [Fact]
    public async Task A_link_verifies_only_the_account_it_was_sent_for()
    {
        using var application = new AccountsApplication();
        await application.RegisterAsync(Ada);
        await application.RegisterAsync("grace@example.com");

        Assert.True(await application.VerifyAsync(AccountsApplication.TokenIn(application.Email.Sent[0])));

        Assert.NotNull(StoredUser.Of(application.Database, Ada)!.EmailVerifiedAt);
        Assert.Null(StoredUser.Of(application.Database, "grace@example.com")!.EmailVerifiedAt);
    }
}
