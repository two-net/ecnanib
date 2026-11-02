using System.Net;
using System.Text.RegularExpressions;
using Ecnanib.Tests.Logging;
using Ecnanib.Tests.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Ecnanib.Tests.Security;

/// <summary>
/// NFR-SEC-03, NFR-SEC-06. A browser is what enforces these, so the page tests stop at "the HTML is
/// consistent with the policy". See docs/architecture.md for what was checked in a real browser.
/// </summary>
public sealed partial class SecurityHeaderTests(PostgresFixture postgres)
{
    private const string Missing = "/nothing-here";

    private static readonly Dictionary<string, string?> OneRequestPerMinute = new()
    {
        ["Security:RateLimits:RequestsPerMinute"] = "1",
    };

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("/", HttpStatusCode.OK)]
    [InlineData("/app.css", HttpStatusCode.OK)]
    [InlineData("/health/live", HttpStatusCode.OK)]
    [InlineData(Missing, HttpStatusCode.NotFound)]
    public async Task Every_kind_of_response_carries_the_headers(string path, HttpStatusCode status)
    {
        await using var app = new AppFactory(postgres.NewConnectionString(), environment: Environments.Production);
        using var client = app.CreatePublicClient();

        using var response = await client.GetAsync(path, Cancellation);

        Assert.Equal(status, response.StatusCode);
        AssertHeaders(response);
    }

    /// <summary>The exception handler clears the headers, and a failure page still renders content.</summary>
    [Fact]
    public async Task The_error_page_carries_the_headers()
    {
        await using var app = FailingApp();
        using var client = app.CreatePublicClient();

        using var response = await client.GetAsync(Missing, Cancellation);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        AssertHeaders(response);
    }

    [Fact]
    public async Task A_rejected_request_carries_the_headers()
    {
        await using var app = new AppFactory(
            postgres.NewConnectionString(),
            settings: OneRequestPerMinute,
            environment: Environments.Production);
        using var client = app.CreatePublicClient();

        (await client.GetAsync(Missing, Cancellation)).Dispose();
        using var response = await client.GetAsync(Missing, Cancellation);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        AssertHeaders(response);
    }

    [Fact]
    public async Task A_redirect_carries_the_headers()
    {
        await using var app = new AppFactory(postgres.NewConnectionString(), environment: Environments.Production);
        using var client = app.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://ecnanib.test"),
            AllowAutoRedirect = false,
        });

        using var response = await client.GetAsync("/", Cancellation);

        Assert.Equal(HttpStatusCode.TemporaryRedirect, response.StatusCode);
        AssertHeaders(response);
    }

    /// <summary>dotnet watch refreshes the browser over a WebSocket on a port of its own.</summary>
    [Fact]
    public async Task Development_also_allows_the_refresh_socket()
    {
        await using var app = new AppFactory(postgres.NewConnectionString());
        using var client = app.CreateClient();

        using var response = await client.GetAsync("/", Cancellation);

        Assert.Contains("connect-src 'self' ws://localhost:* wss://localhost:*;", PolicyOf(response), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Each_response_has_a_nonce_of_its_own()
    {
        await using var app = new AppFactory(postgres.NewConnectionString());
        using var client = app.CreateClient();

        using var first = await client.GetAsync("/", Cancellation);
        using var second = await client.GetAsync("/", Cancellation);

        Assert.NotEqual(NonceOf(first), NonceOf(second));
    }

    [Theory]
    [InlineData("/")]
    [InlineData(Missing)]
    public async Task A_page_has_nothing_the_policy_would_block(string path)
    {
        await using var app = new AppFactory(postgres.NewConnectionString(), environment: Environments.Production);
        using var client = app.CreatePublicClient();

        using var response = await client.GetAsync(path, Cancellation);

        await AssertPageMatchesPolicyAsync(response);
    }

    /// <summary>The error page runs in a new scope, so a nonce kept in a scoped service would differ here.</summary>
    [Fact]
    public async Task The_error_page_has_nothing_the_policy_would_block()
    {
        await using var app = FailingApp();
        using var client = app.CreatePublicClient();

        using var response = await client.GetAsync(Missing, Cancellation);

        await AssertPageMatchesPolicyAsync(response);
    }

    private static async Task AssertPageMatchesPolicyAsync(HttpResponseMessage response)
    {
        var html = Comment().Replace(await response.Content.ReadAsStringAsync(Cancellation), string.Empty);
        var nonce = NonceOf(response);
        var scripts = Script().Matches(html).Select(match => match.Groups["attributes"].Value).ToList();

        // Without this the checks below could pass on a page with nothing to check.
        Assert.Contains(scripts, attributes => attributes.Contains("type=\"importmap\"", StringComparison.Ordinal));

        foreach (var inline in scripts.Where(attributes => !attributes.Contains("src=", StringComparison.Ordinal)))
        {
            Assert.Contains($"nonce=\"{nonce}\"", inline, StringComparison.Ordinal);
        }

        Assert.DoesNotMatch(External(), html);
        Assert.DoesNotMatch(InlineStyle(), html);
        Assert.DoesNotMatch(InlineHandler(), html);
    }

    private static void AssertHeaders(HttpResponseMessage response)
    {
        Assert.Equal(
            $"default-src 'self'; script-src 'self' 'nonce-{NonceOf(response)}'; style-src 'self'; img-src 'self' data:; "
                + "connect-src 'self'; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'",
            PolicyOf(response));
        Assert.Equal("DENY", Assert.Single(response.Headers.GetValues("X-Frame-Options")));
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("same-origin", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
    }

    /// <summary>Interactive rendering adds its own policy, and browsers would enforce both.</summary>
    private static string PolicyOf(HttpResponseMessage response) =>
        Assert.Single(response.Headers.GetValues("Content-Security-Policy"));

    private static string NonceOf(HttpResponseMessage response) =>
        Nonce().Match(PolicyOf(response)).Groups["nonce"].Value;

    private AppFactory FailingApp() =>
        new(
            postgres.NewConnectionString(),
            services => services.AddTransient<IStartupFilter>(
                _ => new TerminalMiddleware(_ => throw new InvalidOperationException("Failing on purpose."))),
            environment: Environments.Production);

    [GeneratedRegex("'nonce-(?<nonce>[0-9a-f]{32})'")]
    private static partial Regex Nonce();

    [GeneratedRegex("<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex Comment();

    [GeneratedRegex("<script\\b(?<attributes>[^>]*)>")]
    private static partial Regex Script();

    [GeneratedRegex("""<(?:script|link|img)\b[^>]*\b(?:src|href)="(?:https?:)?//""")]
    private static partial Regex External();

    [GeneratedRegex("""<style\b|<[a-z][^>]*\sstyle\s*=""", RegexOptions.IgnoreCase)]
    private static partial Regex InlineStyle();

    [GeneratedRegex("""<[a-z][^>]*\son[a-z]+\s*=""", RegexOptions.IgnoreCase)]
    private static partial Regex InlineHandler();
}
