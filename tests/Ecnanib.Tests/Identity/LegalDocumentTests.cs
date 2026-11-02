using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Ecnanib.Modules.Identity.Contracts;
using Ecnanib.Tests.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Ecnanib.Tests.Identity;

/// <summary>NFR-LEG-01. The published versions are constants, so these change what the user has accepted instead.</summary>
public sealed partial class LegalDocumentTests(SharedAccountApp app) : IClassFixture<SharedAccountApp>
{
    private const string TermsOfUseDigest = "8459bd80b36a7192abb422dd61ca580d8f67346a0c8c7161c344eac96a65579b";
    private const string RiskDisclosureDigest = "3b17bcce295ca49a5c76170eb380e2feb55a3992f2ae8a593aa4f02c1d24eb8b";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>
    /// A material change needs a new version, and everyone then has to accept again. A typo doesn't. Only
    /// a person can tell which this is, so any edit stops here until someone decides. It reads what a
    /// reader sees, so editing the page, reordering it or dropping a paragraph counts, and not only the text.
    /// </summary>
    [Theory]
    [InlineData("/legal/terms", LegalDocuments.TermsOfUseVersion, TermsOfUseDigest)]
    [InlineData("/legal/risk-disclosure", LegalDocuments.RiskDisclosureVersion, RiskDisclosureDigest)]
    public async Task Each_document_is_pinned_to_its_version(string page, string version, string digest)
    {
        using var visitor = Visitor.To(app);

        var actual = Digest(await visitor.PageAsync(page));

        Assert.Equal("draft-1", version);
        Assert.True(
            actual == digest,
            $"{page} changed. If the change is material, change its version in LegalDocuments.cs. Either way, pin {actual}.");
    }

    [Theory]
    [InlineData("/legal/terms", "Terms of Use")]
    [InlineData("/legal/risk-disclosure", "Risk Disclosure")]
    public async Task A_visitor_can_read_each_document_before_agreeing(string page, string title)
    {
        using var visitor = Visitor.To(app);

        var html = await visitor.PageAsync(page);

        Assert.Contains(title, html, StringComparison.Ordinal);
        Assert.Contains("Version draft-1", html, StringComparison.Ordinal);
        Assert.Contains("Draft.", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task After_a_new_version_signing_in_leads_to_accepting_it()
    {
        using var visitor = Visitor.To(app);
        var email = await RegisterAsync(visitor);
        await AgeAcceptanceAsync(email);

        using var signedIn = await visitor.SignInAsync(email);
        using var account = await visitor.GetAsync("/account");

        Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, account.StatusCode);
        Assert.Equal("/legal/accept?returnUrl=%2Faccount", Visitor.Target(account));
    }

    [Fact]
    public async Task Accepting_records_the_new_versions_and_opens_the_way_back()
    {
        using var visitor = Visitor.To(app);
        var email = await RegisterAsync(visitor);
        await AgeAcceptanceAsync(email);
        (await visitor.SignInAsync(email)).Dispose();

        using var accepted = await AcceptAsync(visitor);

        Assert.Equal(HttpStatusCode.Redirect, accepted.StatusCode);
        Assert.Equal("/account", Visitor.Target(accepted));
        Assert.Contains(email, await visitor.PageAsync("/account"), StringComparison.Ordinal);

        var id = await AccountRows.IdAsync(app.ConnectionString, email, Cancellation);

        Assert.Equal(
            4,
            await Sql.ScalarAsync<long>(app.ConnectionString, $"SELECT count(*) FROM legal_acceptances WHERE user_id = '{id}'", Cancellation));
    }

    [Fact]
    public async Task Accepting_needs_both_documents()
    {
        using var visitor = Visitor.To(app);
        var email = await RegisterAsync(visitor);
        await AgeAcceptanceAsync(email);
        (await visitor.SignInAsync(email)).Dispose();

        using var response = await AcceptAsync(visitor, ("Input.AcceptRiskDisclosure", "false"));
        using var account = await visitor.GetAsync("/account");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, account.StatusCode);
    }

    [Fact]
    public async Task A_stale_form_records_nothing()
    {
        using var visitor = Visitor.To(app);
        var email = await RegisterAsync(visitor);
        await AgeAcceptanceAsync(email);
        (await visitor.SignInAsync(email)).Dispose();

        using var response = await AcceptAsync(visitor, ("Input.TermsOfUseVersion", "an older version"));
        var id = await AccountRows.IdAsync(app.ConnectionString, email, Cancellation);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("have changed", await response.Content.ReadAsStringAsync(Cancellation), StringComparison.Ordinal);
        Assert.Equal(
            2,
            await Sql.ScalarAsync<long>(app.ConnectionString, $"SELECT count(*) FROM legal_acceptances WHERE user_id = '{id}'", Cancellation));
    }

    /// <summary>The case a real new version creates: a session that was fine when it started.</summary>
    [Fact]
    public async Task A_session_from_before_a_new_version_has_to_accept_it()
    {
        using var client = await ClientWithSessionAsync(LegalDocuments: "an older pair");

        using var response = await client.GetAsync("/account", Cancellation);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/legal/accept?returnUrl=%2Faccount", Visitor.Target(response));
    }

    /// <summary>Otherwise the only way off the page would be to agree.</summary>
    [Fact]
    public async Task A_user_with_a_document_to_accept_can_still_sign_out()
    {
        using var visitor = Visitor.To(app);
        var email = await RegisterAsync(visitor);
        await AgeAcceptanceAsync(email);
        (await visitor.SignInAsync(email)).Dispose();

        using var signedOut = await visitor.SignOutAsync("/");
        using var account = await visitor.GetAsync("/account");

        Assert.Equal(HttpStatusCode.Redirect, signedOut.StatusCode);
        Assert.StartsWith("/sign-in", Visitor.Target(account), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_page_sends_a_visitor_to_sign_in_first()
    {
        using var visitor = Visitor.To(app);

        using var response = await visitor.GetAsync("/legal/accept");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/sign-in?returnUrl=%2Flegal%2Faccept", Visitor.Target(response));
    }

    private static Task<HttpResponseMessage> AcceptAsync(Visitor visitor, params (string Name, string Value)[] fields) =>
        visitor.SubmitAsync(
            "/legal/accept",
            Visitor.AcceptForm,
            [("Input.AcceptTermsOfUse", "true"), ("Input.AcceptRiskDisclosure", "true"), .. fields]);

    /// <summary>The words of the article as a reader meets them, without its markup.</summary>
    private static string Digest(string page)
    {
        var words = Spaces().Replace(WebUtility.HtmlDecode(Tag().Replace(Article().Match(page).Value, " ")), " ").Trim();

        Assert.NotEmpty(words);

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(words)));
    }

    private async Task<string> RegisterAsync(Visitor visitor)
    {
        var email = Visitor.NewEmail();

        (await visitor.RegisterAsync(email)).Dispose();

        return email;
    }

    /// <summary>What a new version does to a user's rows: what they accepted is no longer current.</summary>
    private async Task AgeAcceptanceAsync(string email)
    {
        var id = await AccountRows.IdAsync(app.ConnectionString, email, Cancellation);

        await Sql.ExecuteAsync(app.ConnectionString, $"UPDATE legal_acceptances SET version = 'older' WHERE user_id = '{id}'", Cancellation);
    }

    /// <summary>A session whose claim says what it says, made the way the cookie handler makes one.</summary>
    private async Task<HttpClient> ClientWithSessionAsync(string LegalDocuments)
    {
        const string Scheme = CookieAuthenticationDefaults.AuthenticationScheme;

        using var visitor = Visitor.To(app);
        var email = await visitor.SignUpAsync();
        var session = Assert.Single(await SessionRows.OfAsync(app.ConnectionString, email, Cancellation));
        var id = await AccountRows.IdAsync(app.ConnectionString, email, Cancellation);

        var options = app.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(Scheme);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, id.ToString()),
                new Claim(AccountClaims.Session, session.ToString()),
                new Claim(AccountClaims.LegalDocuments, LegalDocuments),
            ],
            Scheme));

        var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });

        client.DefaultRequestHeaders.Add(
            "Cookie",
            $"{options.Cookie.Name}={options.TicketDataFormat.Protect(new AuthenticationTicket(principal, Scheme))}");

        return client;
    }

    [GeneratedRegex("<article\\b.*</article>", RegexOptions.Singleline)]
    private static partial Regex Article();

    [GeneratedRegex("<[^>]*>")]
    private static partial Regex Tag();

    [GeneratedRegex("\\s+")]
    private static partial Regex Spaces();
}
