using Ecnanib.Accounts;
using Ecnanib.Tests.BackgroundWork;

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

        Assert.True(await application.VerifyAsync(AccountsApplication.TokenIn(application.Email.Sent[1])));
        Assert.Equal(Morning.AddDays(3), StoredUser.Of(application.Database, Ada)!.EmailVerifiedAt);
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
