using System.Net;
using Ecnanib.Accounts;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.Logging;
using Microsoft.AspNetCore.Hosting;

namespace Ecnanib.Tests.Security;

/// <summary>
/// All traffic uses TLS, and the cookies the application sets are sent only over TLS and are
/// hidden from scripts (NFR-SEC-1). The file of the tests names <c>https://ecnanib.example</c>
/// as the address users reach the application at.
/// </summary>
public class TransportTests
{
    [Theory]
    [InlineData("/")]
    [InlineData("/register?from=a-link")]
    [InlineData("/account")]
    [InlineData("/api/health")]
    [InlineData("/app.css")]
    public async Task A_request_over_plain_HTTP_is_sent_on_to_TLS_and_is_not_served(string path)
    {
        using var application = new LoggedApplication(LoggedApplication.Settings());
        using var client = application.CreateClientThatStays(new Uri("http://ecnanib.example"));

        using var answer = await client.GetAsync(path, TestContext.Current.CancellationToken);

        // The same address over TLS, at the port of the address users reach the application at.
        Assert.Equal(HttpStatusCode.TemporaryRedirect, answer.StatusCode);
        Assert.Equal($"https://ecnanib.example{path}", answer.Headers.Location!.OriginalString);
        Assert.Empty(await answer.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Empty(Browser.CookiesSetBy(answer));
    }

    [Fact]
    public async Task A_form_posted_over_plain_HTTP_is_not_taken()
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClientThatStays();
        using var plain = new HttpRequestMessage(HttpMethod.Post, "http://localhost/sign-in")
        {
            Content = new FormUrlEncodedContent([new("Email", "ada@example.com"), new("Password", AccountsApplication.Password)]),
        };

        using var answer = await client.SendAsync(plain, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.TemporaryRedirect, answer.StatusCode);
        Assert.Equal("https://localhost/sign-in", answer.Headers.Location!.OriginalString);
        Assert.DoesNotContain(
            application.Events(), written => written.Template.StartsWith("A sign-in failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Over_TLS_a_browser_is_told_to_use_nothing_else_for_a_year()
    {
        // Not in development, where a browser would remember it for every project on the machine.
        using var application = new LoggedApplication(
            LoggedApplication.Settings(), host => host.UseEnvironment("Production"));
        using var client = application.CreateClientThatStays(new Uri("https://ecnanib.example"));

        using var page = await client.GetAsync("/", TestContext.Current.CancellationToken);
        using var api = await client.GetAsync("/api/health", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal(["max-age=31536000"], page.Headers.GetValues("Strict-Transport-Security"));
        Assert.Equal(["max-age=31536000"], api.Headers.GetValues("Strict-Transport-Security"));
    }

    [Fact]
    public async Task Every_cookie_is_sent_only_over_TLS_and_is_hidden_from_scripts()
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClientThatStays();
        await application.RegisterVerifiedAsync("ada@example.com");

        // A page with a form sets the cookie of the token against request forgery.
        using var form = await client.GetAsync(SessionAuthentication.SignInPath, TestContext.Current.CancellationToken);
        var token = Browser.AntiforgeryTokenIn(await form.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        // Signing in sets the cookie of the session.
        using var signedIn = await client.SendFormAsync(
            SessionAuthentication.SignInPath,
            "sign-in",
            token,
            ("Email", "ada@example.com"),
            ("Password", AccountsApplication.Password));

        var cookies = Browser.CookiesSetBy(form).Concat(Browser.CookiesSetBy(signedIn)).ToList();

        Assert.Equal(
            ["__Host-ecnanib-antiforgery", "__Host-ecnanib-session"],
            cookies.Select(cookie => cookie.Split('=')[0]).Order(StringComparer.Ordinal));
        Assert.All(cookies, cookie =>
        {
            var attributes = cookie.Split("; ")[1..];

            Assert.Contains("secure", attributes);
            Assert.Contains("httponly", attributes);
            Assert.Contains("path=/", attributes);
            Assert.Contains(attributes, attribute => attribute is "samesite=lax" or "samesite=strict");
            Assert.DoesNotContain(attributes, attribute => attribute.StartsWith("domain=", StringComparison.Ordinal));
        });
    }
}
