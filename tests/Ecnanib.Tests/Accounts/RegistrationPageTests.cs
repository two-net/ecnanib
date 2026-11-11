using System.Text.RegularExpressions;
using Ecnanib.Accounts;
using Ecnanib.WebUi.Pages.Legal;

namespace Ecnanib.Tests.Accounts;

/// <summary>
/// The pages a visitor registers with: the form, the page a verification link opens, and the
/// legal documents the form asks them to accept (FR-ACC-1, FR-ACC-8).
/// </summary>
public class RegistrationPageTests
{
    private const string Ada = "ada@example.com";

    private static readonly (string Name, string Value)[] EveryDocumentAccepted =
    [
        ("TermsOfService", LegalDocuments.CurrentVersionOf(LegalDocument.TermsOfService)),
        ("PrivacyPolicy", LegalDocuments.CurrentVersionOf(LegalDocument.PrivacyPolicy)),
        ("RiskDisclosure", LegalDocuments.CurrentVersionOf(LegalDocument.RiskDisclosure)),
    ];

    [Fact]
    public async Task The_form_asks_for_an_address_a_password_and_each_document_in_its_version()
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClient();

        var page = await client.PageAsync("/register");

        Assert.Contains("name=\"Email\"", page);
        Assert.Contains("name=\"Password\"", page);
        Assert.Contains("type=\"password\"", page);

        foreach (var document in LegalDocuments.All)
        {
            // A box whose value is the version in force, next to a link to the document.
            Assert.Contains($"name=\"{document}\" type=\"checkbox\" required value=\"{LegalDocuments.CurrentVersionOf(document)}\"", page);
            Assert.Contains($"href=\"{LegalPages.PathOf(document)}\"", page);
            Assert.Contains($">{LegalPages.NameOf(document)}</a>", page);
        }
    }

    [Fact]
    public async Task A_visitor_registers_on_the_form_and_confirms_the_address_with_the_link()
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClient();

        var (path, sent) = await client.SubmitAsync(
            "/register", "register", [("Email", Ada), ("Password", AccountsApplication.Password), .. EveryDocumentAccepted]);

        Assert.Equal("/register/sent", path);
        Assert.Contains("<h1>Check your email</h1>", sent);
        Assert.Null(StoredUser.Of(application.Database, Ada)!.EmailVerifiedAt);
        Assert.Equal(3, StoredAcceptance.All(application.Database).Count);

        // Opening the link verifies nothing: it shows the button that does.
        var link = AccountsApplication.LinkIn(Assert.Single(application.Email.Sent));
        var opened = await client.PageAsync(link.PathAndQuery);
        Assert.Contains("Confirm my email address", opened);
        Assert.Contains("<meta name=\"referrer\" content=\"no-referrer\"", opened);
        Assert.Null(StoredUser.Of(application.Database, Ada)!.EmailVerifiedAt);

        var (_, confirmed) = await client.SubmitAsync(link.PathAndQuery, "verify-email");

        Assert.Contains("Your email address is confirmed.", confirmed);
        Assert.NotNull(StoredUser.Of(application.Database, Ada)!.EmailVerifiedAt);

        // The link is spent: confirming it a second time finds it used.
        var (_, again) = await client.SubmitAsync(link.PathAndQuery, "verify-email");
        Assert.Contains("This link does not work any more.", again);
    }

    [Fact]
    public async Task The_form_says_what_is_wrong_and_does_not_show_the_password_again()
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClient();

        var (path, page) = await client.SubmitAsync(
            "/register",
            "register",
            ("Email", "ada@nowhere"),
            ("Password", "SHORT-MARKER"[..11]),
            ("TermsOfService", LegalDocuments.CurrentVersionOf(LegalDocument.TermsOfService)),
            ("RiskDisclosure", "0.0"));

        Assert.Equal("/register", path);
        Assert.Contains("Enter an email address, such as name@example.com.", page);
        Assert.Contains("Choose a password of at least 12 characters.", page);
        Assert.Contains("Read and accept the current Privacy Policy to register.", page);
        Assert.Contains("Read and accept the current risk disclosure to register.", page);
        Assert.DoesNotContain("Read and accept the current Terms of Service to register.", page);

        // What was typed is still there, except the password, and what was ticked is still ticked.
        Assert.Contains("value=\"ada@nowhere\"", page);
        Assert.DoesNotContain("SHORT-MARKE", page);
        Assert.Matches("name=\"TermsOfService\"[^>]* checked", page);
        Assert.DoesNotMatch("name=\"PrivacyPolicy\"[^>]* checked", page);
        Assert.DoesNotMatch("name=\"RiskDisclosure\"[^>]* checked", page);

        Assert.Empty(StoredUser.All(application.Database));
        Assert.Empty(application.Email.Sent);
    }

    [Fact]
    public async Task The_form_answers_an_address_that_has_an_account_as_it_answers_a_new_one()
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);

        var known = await client.SubmitAsync(
            "/register", "register", [("Email", Ada), ("Password", AccountsApplication.Password), .. EveryDocumentAccepted]);
        var unknown = await client.SubmitAsync(
            "/register", "register", [("Email", "grace@example.com"), ("Password", AccountsApplication.Password), .. EveryDocumentAccepted]);

        Assert.Equal("/register/sent", known.Path);
        Assert.Equal(unknown.Path, known.Path);
        Assert.Equal(WithoutComments(unknown.Page), WithoutComments(known.Page));
    }

    [Fact]
    public async Task The_form_says_so_when_the_email_could_not_be_sent()
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClient();
        application.Email.Failure = new InvalidOperationException("The mail server is away.");

        var (path, page) = await client.SubmitAsync(
            "/register", "register", [("Email", Ada), ("Password", AccountsApplication.Password), .. EveryDocumentAccepted]);

        Assert.Equal("/register", path);
        Assert.Contains("We could not send the email. Try again in a few minutes.", page);
    }

    [Fact]
    public async Task A_post_without_the_token_against_request_forgery_registers_nothing()
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClient();

        var (path, _) = await client.PostAsync(
            "/register", "register", "not-the-token", [("Email", Ada), ("Password", AccountsApplication.Password), .. EveryDocumentAccepted]);

        Assert.NotEqual("/register/sent", path);
        Assert.Empty(StoredUser.All(application.Database));
        Assert.Empty(application.Email.Sent);
    }

    [Theory]
    [InlineData("/verify-email")]
    [InlineData("/verify-email?token=")]
    public async Task A_link_without_a_token_is_said_not_to_work(string path)
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClient();

        var page = await client.PageAsync(path);

        Assert.Contains("This link does not work any more.", page);
        Assert.DoesNotContain("<form", page);
    }

    [Fact]
    public async Task A_link_with_a_token_that_is_no_accounts_is_said_not_to_work_once_confirmed()
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClient();
        await application.RegisterAsync(Ada);

        var (_, page) = await client.SubmitAsync("/verify-email?token=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", "verify-email");

        Assert.Contains("This link does not work any more.", page);
        Assert.Contains("href=\"/register\"", page);
        Assert.Null(StoredUser.Of(application.Database, Ada)!.EmailVerifiedAt);
    }

    /// <summary>
    /// A page as a visitor sees it. The framework adds a comment to each page that holds state
    /// for the browser, encrypted, and so different every time.
    /// </summary>
    private static string WithoutComments(string page) => Regex.Replace(page, "<!--.*?-->", string.Empty, RegexOptions.Singleline);

    [Theory]
    [InlineData(LegalDocument.TermsOfService, "/legal/terms", "Terms of Service")]
    [InlineData(LegalDocument.PrivacyPolicy, "/legal/privacy", "Privacy Policy")]
    [InlineData(LegalDocument.RiskDisclosure, "/legal/risk-disclosure", "Risk disclosure")]
    public async Task Each_document_has_a_page_that_shows_the_version_in_force(LegalDocument document, string path, string title)
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClient();

        Assert.Equal(path, LegalPages.PathOf(document));

        var page = await client.PageAsync(path);

        Assert.Contains($"<h1>{title}</h1>", page);
        Assert.Contains($"Version {LegalDocuments.CurrentVersionOf(document)}", page);
    }
}
