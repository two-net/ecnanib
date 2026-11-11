using Ecnanib.Accounts;
using Ecnanib.Tests.Persistence;

namespace Ecnanib.Tests.Accounts;

/// <summary>
/// Whether registration is open is read from the configuration file, and a closed registration
/// accepts no new accounts (FR-ACC-7).
/// </summary>
public class RegistrationSwitchTests
{
    private const string Ada = "ada@example.com";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Whether_registration_is_open_is_what_the_configuration_file_says(bool open)
    {
        using var application = new AccountsApplication(registrationOpen: open);

        Assert.Equal(open, application.Registration.IsOpen);
    }

    [Fact]
    public async Task A_closed_registration_accepts_no_new_accounts()
    {
        using var application = new AccountsApplication(registrationOpen: false);

        var result = await application.RegisterAsync(Ada);

        Assert.Equal(RegistrationOutcome.Closed, result.Outcome);
        Assert.Empty(StoredUser.All(application.Database));
        Assert.Empty(StoredAcceptance.All(application.Database));
        Assert.Empty(application.Email.Sent);
    }

    [Fact]
    public async Task The_page_of_a_closed_registration_has_no_form_and_a_post_to_it_creates_nothing()
    {
        using var application = new AccountsApplication(registrationOpen: false);
        using var client = application.CreateClient();

        var page = await client.PageAsync("/register");

        Assert.Contains("Registration is closed.", page);
        Assert.DoesNotContain("<form", page);
        Assert.DoesNotContain("href=\"/register\"", await client.PageAsync("/"));

        // The form as an open registration has it, posted by something that is not a browser,
        // with a token against request forgery from another page of the application.
        var token = Browser.AntiforgeryTokenIn(await client.PageAsync("/verify-email?token=x"));
        var (path, _) = await client.PostAsync(
            "/register",
            "register",
            token,
            ("Email", Ada),
            ("Password", AccountsApplication.Password),
            ("TermsOfService", LegalDocuments.CurrentVersionOf(LegalDocument.TermsOfService)),
            ("PrivacyPolicy", LegalDocuments.CurrentVersionOf(LegalDocument.PrivacyPolicy)),
            ("RiskDisclosure", LegalDocuments.CurrentVersionOf(LegalDocument.RiskDisclosure)));

        Assert.NotEqual("/register/sent", path);
        Assert.Empty(StoredUser.All(application.Database));
        Assert.Empty(application.Email.Sent);
    }

    [Fact]
    public async Task An_open_registration_is_linked_from_the_start_page()
    {
        using var application = new AccountsApplication(registrationOpen: true);
        using var client = application.CreateClient();

        Assert.Contains("href=\"/register\"", await client.PageAsync("/"));
        Assert.Contains("<form", await client.PageAsync("/register"));
    }

    [Fact]
    public async Task An_address_registered_while_registration_was_open_can_still_be_verified_after_it_closed()
    {
        string token;
        TestDatabase database;

        using (var open = new AccountsApplication(registrationOpen: true))
        {
            await open.RegisterAsync(Ada);
            token = AccountsApplication.TokenIn(open.Email.Sent[0]);
            database = open.Database;
        }

        // The same database, after a restart with the switch turned off.
        using var closed = new AccountsApplication(registrationOpen: false, database: database);

        Assert.True(await closed.VerifyAsync(token));
        Assert.NotNull(StoredUser.Of(database, Ada)!.EmailVerifiedAt);
    }
}
