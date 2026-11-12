using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using Ecnanib.Accounts;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.Logging;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Hosting;

namespace Ecnanib.Tests.Security;

/// <summary>
/// The application defends against cross-site scripting and clickjacking (NFR-SEC-4). Every
/// answer tells the browser to run no script and apply no style that is not the application's
/// own, and to show the application in no frame. The pages keep to that themselves, and write
/// what a person typed as text.
/// </summary>
public partial class BrowserDefenseTests
{
    private const string Ada = "ada@example.com";

    // What a deployment sends, with the nonce of the answer where {0} is.
    private const string Policy =
        "default-src 'self'; script-src 'self' 'nonce-{0}'; style-src 'self'; connect-src 'self'; " +
        "object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";

    [Theory]
    [InlineData("/", HttpStatusCode.OK)]
    [InlineData("/sign-in", HttpStatusCode.OK)]
    [InlineData("/account", HttpStatusCode.Redirect)]
    [InlineData("/api/health", HttpStatusCode.OK)]
    [InlineData("/app.css", HttpStatusCode.OK)]
    [InlineData("/no-such-page", HttpStatusCode.NotFound)]
    [InlineData("/fails", HttpStatusCode.InternalServerError)]
    [InlineData("/api/fails", HttpStatusCode.InternalServerError)]
    public async Task Every_answer_tells_the_browser_what_to_refuse(string path, HttpStatusCode status)
    {
        using var application = Deployed();
        using var client = application.CreateClientThatStays();

        using var answer = await client.GetAsync(path, TestContext.Current.CancellationToken);

        // A page, a page with a form, a redirect, the API, a file, a page that is not there, and
        // an error, as a page and under the API.
        Assert.Equal(status, answer.StatusCode);

        // Each header once, with one value: nothing else says something of its own beside it.
        Assert.Equal(string.Format(null, Policy, NonceOf(answer)), Assert.Single(answer.Headers.GetValues("Content-Security-Policy")));
        Assert.Equal("DENY", Assert.Single(answer.Headers.GetValues("X-Frame-Options")));
        Assert.Equal("nosniff", Assert.Single(answer.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("same-origin", Assert.Single(answer.Headers.GetValues("Referrer-Policy")));
    }

    [Fact]
    public async Task So_does_the_answer_that_sends_plain_HTTP_on_to_TLS()
    {
        using var application = Deployed();
        using var client = application.CreateClientThatStays(new Uri("http://ecnanib.example"));

        using var answer = await client.GetAsync("/", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.TemporaryRedirect, answer.StatusCode);
        Assert.Equal(string.Format(null, Policy, NonceOf(answer)), Assert.Single(answer.Headers.GetValues("Content-Security-Policy")));
        Assert.Equal("DENY", Assert.Single(answer.Headers.GetValues("X-Frame-Options")));
    }

    [Fact]
    public async Task No_site_may_show_the_application_in_a_frame_this_one_included()
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClientThatStays();

        // A page with a form: making its token against request forgery would otherwise say
        // that this site may frame the page.
        using var form = await client.GetAsync(SessionAuthentication.SignInPath, TestContext.Current.CancellationToken);
        Browser.AntiforgeryTokenIn(await form.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        Assert.Equal("DENY", Assert.Single(form.Headers.GetValues("X-Frame-Options")));
        Assert.Contains("frame-ancestors 'none'", Assert.Single(form.Headers.GetValues("Content-Security-Policy")));
    }

    [Fact]
    public async Task The_policy_allows_no_script_or_style_that_is_written_into_a_page_and_no_text_run_as_code()
    {
        using var application = Deployed();
        using var client = application.CreateClientThatStays();

        using var answer = await client.GetAsync("/", TestContext.Current.CancellationToken);
        var policy = Assert.Single(answer.Headers.GetValues("Content-Security-Policy"));

        Assert.DoesNotContain("'unsafe-inline'", policy);
        Assert.DoesNotContain("'unsafe-eval'", policy);
        Assert.DoesNotContain("'unsafe-hashes'", policy);
        Assert.DoesNotContain("data:", policy);
        Assert.DoesNotContain("*", policy);
    }

    [Fact]
    public async Task The_nonce_is_new_for_every_answer()
    {
        using var application = Deployed();
        using var client = application.CreateClientThatStays();

        var nonces = new List<string>();
        for (var request = 0; request < 3; request++)
        {
            using var answer = await client.GetAsync("/", TestContext.Current.CancellationToken);
            nonces.Add(NonceOf(answer));
        }

        // 16 random bytes, in letters that a page and a header write alike.
        Assert.All(nonces, nonce => Assert.Matches("^[A-Za-z0-9_-]{22}$", nonce));
        Assert.Equal(3, nonces.Distinct().Count());
    }

    [Fact]
    public async Task In_development_a_page_may_also_reach_the_tool_that_reloads_it()
    {
        // The environment of a host that is not told otherwise is Development.
        using var application = new LoggedApplication(LoggedApplication.Settings());
        using var client = application.CreateClientThatStays();

        using var answer = await client.GetAsync("/", TestContext.Current.CancellationToken);
        var policy = Assert.Single(answer.Headers.GetValues("Content-Security-Policy"));

        Assert.Contains("connect-src 'self' ws://localhost:* wss://localhost:*;", policy);
        Assert.Equal(
            string.Format(null, Policy, NonceOf(answer)),
            policy.Replace(" ws://localhost:* wss://localhost:*", string.Empty));
    }

    [Fact]
    public async Task Every_page_keeps_to_the_policy_for_a_visitor_and_for_a_user()
    {
        using var application = new AccountsApplication();
        using var visitor = application.CreateClient();
        using var user = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await user.SignInAsync(Ada);

        // Every page there is, so that a page which is added later is looked at too. A link
        // without its token shows the page that says so.
        var paths = Pages().ToList();
        Assert.Contains("/", paths);
        Assert.Contains(SessionAuthentication.SignInPath, paths);
        Assert.Contains(Browser.TwoFactorPage, paths);

        var inlineScripts = 0;
        foreach (var path in paths)
        {
            foreach (var client in new[] { visitor, user })
            {
                using var answer = await client.GetAsync(path, TestContext.Current.CancellationToken);
                var page = await answer.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

                inlineScripts += AssertKeepsToThePolicy(path, page, NonceOf(answer));
            }
        }

        // The framework writes one script into each page, which is why the policy has a nonce.
        // If it stops, the nonce has no use, and the policy can do without it.
        Assert.True(inlineScripts > 0, "No page carries a script of its own any more, so the policy needs no nonce.");

        // The page that draws the QR code was one of them, as its user sees it.
        Assert.Contains("<svg class=\"qr-code\"", await user.PageAsync(Browser.TwoFactorPage));
    }

    [Theory]
    [InlineData("<script src=\"https://elsewhere.example/x.js\"></script>", "a script from another site")]
    [InlineData("<script src=\"//elsewhere.example/x.js\"></script>", "a script from another site")]
    [InlineData("<script>alert(1)</script>", "a script without the nonce")]
    [InlineData("<script nonce=\"another\">alert(1)</script>", "a script without the nonce")]
    [InlineData("<style>p { color: red }</style>", "a style element")]
    [InlineData("<p style=\"color: red\">text</p>", "a style attribute")]
    [InlineData("<button onclick=\"alert(1)\">go</button>", "an event handler")]
    [InlineData("<a href=\"javascript:alert(1)\">go</a>", "a javascript: address")]
    [InlineData("<link rel=\"stylesheet\" href=\"https://elsewhere.example/x.css\" />", "a stylesheet from another site")]
    public void A_page_that_does_not_keep_to_the_policy_is_found(string markup, string what)
    {
        var failure = Record.Exception(() => AssertKeepsToThePolicy("/planted", $"<html><body>{markup}</body></html>", "the-nonce"));

        Assert.NotNull(failure);
        Assert.Contains(what, failure.Message);
    }

    [Fact]
    public void A_page_that_keeps_to_the_policy_is_left_alone()
    {
        var inline = AssertKeepsToThePolicy(
            "/planted",
            """
            <html><head><link rel="stylesheet" href="app.css" /><script type="importmap" nonce="the-nonce">{}</script></head>
            <body><p>The onset of style="nothing" is text.</p><script src="_framework/blazor.web.js"></script></body></html>
            """,
            "the-nonce");

        Assert.Equal(1, inline);
    }

    [Fact]
    public async Task What_a_visitor_typed_is_written_back_as_text()
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClient();

        // The form shows the address again when the sign-in fails.
        var (_, page) = await client.SignInAsync("\"><script>alert('typed')</script>", "MARKER <b>not the password</b>");

        Assert.Contains("The email address or the password is not right.", page);
        Assert.DoesNotContain("<script>alert(", page);
        Assert.Contains("&lt;script&gt;alert(", page);

        // And the password is not written back at all.
        Assert.DoesNotContain("MARKER", page);
    }

    [Fact]
    public async Task The_address_of_an_account_is_shown_as_text()
    {
        // Nothing keeps an address from holding markup: it has one @ and a domain.
        const string address = "<img/src=x/onerror=alert(1)>@example.com";

        using var application = new AccountsApplication();
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(address);

        var (path, start) = await client.SignInAsync(address);
        var account = await client.PageAsync("/account");

        // The bar at the top of every page names the user, and so does the account page.
        Assert.Equal("/", path);
        Assert.All(new[] { start, account }, page =>
        {
            Assert.DoesNotContain("<img/src", page);
            Assert.Contains("&lt;img/src=x/onerror=alert(1)&gt;@example.com", page);
        });
    }

    /// <summary>The application as deployed, with two paths that fail: one that is answered like a page, and one under the API.</summary>
    private static LoggedApplication Deployed() =>
        new(
            LoggedApplication.Settings(),
            host => host.UseEnvironment("Production").HandleAtTheEnd(context =>
                context.Request.Path.Value is "/api/fails" or "/fails"
                    ? throw new InvalidOperationException("An error of the test's own")
                    : Task.FromResult(false)));

    /// <summary>The path of every page of the application, as its route says it.</summary>
    private static IEnumerable<string> Pages() =>
        typeof(Program).Assembly.GetTypes()
            .SelectMany(component => component.GetCustomAttributes<RouteAttribute>())
            .Select(route => route.Template)
            .Distinct()
            .Order(StringComparer.Ordinal);

    /// <summary>The nonce that the policy of an answer names.</summary>
    private static string NonceOf(HttpResponseMessage answer)
    {
        var nonce = NonceInPolicy().Match(Assert.Single(answer.Headers.GetValues("Content-Security-Policy")));
        Assert.True(nonce.Success, "The policy of the answer names no nonce.");

        return nonce.Groups[1].Value;
    }

    /// <summary>
    /// Fails for a page that carries what the policy makes a browser refuse: such a page would
    /// be broken for its user, and a page that needs the policy loosened would show here first.
    /// </summary>
    /// <returns>How many scripts are written into the page, each with the nonce.</returns>
    private static int AssertKeepsToThePolicy(string path, string page, string nonce)
    {
        // Without the comments, which the framework fills with state that a browser does not run.
        var markup = Comment().Replace(page, string.Empty);
        var inline = 0;

        foreach (Match script in Script().Matches(markup))
        {
            var attributes = script.Groups["attributes"].Value;

            if (Source().Match(attributes) is { Success: true } source)
            {
                Assert.True(IsOfThisSite(source.Groups[1].Value), $"{path} loads a script from another site: {script.Value}");
            }
            else
            {
                Assert.True(
                    attributes.Contains($"nonce=\"{nonce}\"", StringComparison.Ordinal),
                    $"{path} carries a script without the nonce of its answer: {script.Value}");
                inline++;
            }
        }

        foreach (Match link in Stylesheet().Matches(markup))
        {
            Assert.True(
                Reference().Match(link.Value) is { Success: true } reference && IsOfThisSite(reference.Groups[1].Value),
                $"{path} loads a stylesheet from another site: {link.Value}");
        }

        Assert.False(StyleElement().IsMatch(markup), $"{path} carries a style element.");
        Assert.False(StyleAttribute().IsMatch(markup), $"{path} carries a style attribute: {StyleAttribute().Match(markup).Value}");
        Assert.False(EventHandler().IsMatch(markup), $"{path} carries an event handler: {EventHandler().Match(markup).Value}");
        Assert.False(markup.Contains("javascript:", StringComparison.OrdinalIgnoreCase), $"{path} carries a javascript: address.");

        return inline;
    }

    /// <summary>Whether an address is one of this site: a path, and not an address with a host.</summary>
    private static bool IsOfThisSite(string address) =>
        !address.StartsWith("//", StringComparison.Ordinal) && !address.Contains(':', StringComparison.Ordinal);

    [GeneratedRegex("'nonce-([^']+)'")]
    private static partial Regex NonceInPolicy();

    [GeneratedRegex("<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex Comment();

    [GeneratedRegex("<script\\b(?<attributes>[^>]*)>", RegexOptions.IgnoreCase)]
    private static partial Regex Script();

    [GeneratedRegex("\\bsrc\\s*=\\s*\"([^\"]*)\"", RegexOptions.IgnoreCase)]
    private static partial Regex Source();

    [GeneratedRegex("<link\\b[^>]*\\brel\\s*=\\s*\"stylesheet\"[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex Stylesheet();

    [GeneratedRegex("\\bhref\\s*=\\s*\"([^\"]*)\"", RegexOptions.IgnoreCase)]
    private static partial Regex Reference();

    [GeneratedRegex("<style\\b", RegexOptions.IgnoreCase)]
    private static partial Regex StyleElement();

    // Inside a tag, and as an attribute: after a space, or after the quote that ends another.
    [GeneratedRegex("<[^>]*[\\s\"']style\\s*=[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex StyleAttribute();

    [GeneratedRegex("<[^>]*[\\s\"']on[a-z]+\\s*=[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex EventHandler();
}
