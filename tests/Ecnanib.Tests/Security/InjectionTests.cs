using Ecnanib.Accounts;
using Ecnanib.Tests.Accounts;

namespace Ecnanib.Tests.Security;

/// <summary>
/// The application defends against injection (NFR-SEC-4): what a request brings reaches the
/// database as a value and never as part of a statement. The rule that keeps it so is in
/// <c>ComponentRules</c>: no code sends SQL that was put together as text. These tests show it
/// for what a visitor can type.
/// </summary>
public class InjectionTests
{
    private const string Ada = "ada@example.com";

    // Each would change what a statement means, if it were ever part of one.
    public static TheoryData<string> AddressesWithSql =>
    [
        "'or''='@example.com",
        "ada'--@example.com",
        "x';delete/**/from/**/users;--@example.com",
        "\"or\"\"=\"@example.com",
        "%@example.com",
        "_da@example.com",
    ];

    [Theory]
    [MemberData(nameof(AddressesWithSql))]
    public async Task An_address_with_SQL_in_it_is_an_address_like_any_other(string address)
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClient();
        var ada = await application.RegisterVerifiedAsync(Ada);

        // It registers as what it is, beside the account that is there.
        var registered = await application.RegisterVerifiedAsync(address);

        Assert.Equal(address, registered.Email);
        Assert.Equal([Ada, address], StoredUser.All(application.Database).Select(user => user.Email));

        // And it signs in to its own account, and to no other.
        var (path, page) = await client.SignInAsync(address);

        Assert.Equal("/", path);
        Assert.Contains("Sign out", page);
        Assert.Equal(registered.Id, Assert.Single(StoredSession.All(application.Database)).UserId);
        Assert.NotEqual(ada.Id, registered.Id);
    }

    [Theory]
    [MemberData(nameof(AddressesWithSql))]
    public async Task An_address_with_SQL_in_it_finds_no_account_but_its_own(string address)
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        var stored = application.EverythingStored();

        // Signing in, with a password that would also be true of every row.
        var (path, page) = await client.SignInAsync(address, "' OR '1'='1' --");

        Assert.Equal(SessionAuthentication.SignInPath, path);
        Assert.Contains("The email address or the password is not right.", page);
        Assert.Empty(StoredSession.All(application.Database));

        // Asking for a reset link: the address has no account, and is told so. Nothing of the
        // account that is there has changed.
        Assert.Equal(PasswordResetRequestOutcome.Accepted, await application.RequestResetAsync(address));

        Assert.Equal(address, application.Email.Sent[^1].To);
        Assert.DoesNotContain(PasswordResetLink.Path, application.Email.Sent[^1].TextBody);
        Assert.Equal(stored, application.EverythingStored());
    }

    [Theory]
    [InlineData("' OR '1'='1")]
    [InlineData("' OR 1=1 --")]
    [InlineData("'; UPDATE users SET password_hash = ''; --")]
    public async Task SQL_in_a_password_is_a_wrong_password(string password)
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        var stored = application.EverythingStored();

        var (path, page) = await client.SignInAsync(Ada, password);

        Assert.Equal(SessionAuthentication.SignInPath, path);
        Assert.Contains("The email address or the password is not right.", page);
        Assert.Equal(stored, application.EverythingStored());
    }

    [Theory]
    [InlineData("' OR '1'='1")]
    [InlineData("x' OR email_verified_at IS NULL --")]
    public async Task SQL_in_the_token_of_a_link_is_a_link_that_does_not_work(string token)
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClient();
        await application.RegisterAsync(Ada);
        await application.RequestResetAsync(Ada);
        var stored = application.EverythingStored();

        var verify = $"{EmailVerification.Path}?token={Uri.EscapeDataString(token)}";
        var reset = $"{PasswordResetLink.Path}?token={Uri.EscapeDataString(token)}";

        var (_, verified) = await client.SubmitAsync(verify, "verify-email");
        var (_, chosen) = await client.SubmitAsync(reset, "reset-password", ("Password", "a new password of someone else"));

        Assert.Contains("This link does not work any more.", verified);
        Assert.Contains("This link does not work any more.", chosen);
        Assert.Equal(stored, application.EverythingStored());
    }
}
