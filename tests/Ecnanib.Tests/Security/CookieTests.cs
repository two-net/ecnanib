using Ecnanib.Tests.Logging;
using Ecnanib.Tests.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Ecnanib.Tests.Security;

/// <summary>
/// NFR-SEC-06. The session cookie arrives with #9, so these set cookies of their own. The policy
/// applies to every cookie, which is what protects that one too.
/// </summary>
public sealed class CookieTests(PostgresFixture postgres)
{
    private const string Plain = "plain";
    private const string Strict = "strict";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_cookie_nobody_configured_is_still_secure()
    {
        await using var app = CookieApp(Environments.Production);
        using var client = app.CreatePublicClient();

        using var response = await client.GetAsync("/set-cookies", Cancellation);

        var cookie = Cookie(response, Plain);

        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_stricter_cookie_stays_strict()
    {
        await using var app = CookieApp(Environments.Production);
        using var client = app.CreatePublicClient();

        using var response = await client.GetAsync("/set-cookies", Cancellation);

        Assert.Contains("samesite=strict", Cookie(response, Strict), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A browser on plain http drops a Secure cookie, which Safari does on localhost too.</summary>
    [Fact]
    public async Task Development_on_plain_http_keeps_cookies_usable()
    {
        await using var app = CookieApp(environment: null);
        using var client = app.CreateClient();

        using var response = await client.GetAsync("/set-cookies", Cancellation);

        var cookie = Cookie(response, Plain);

        Assert.DoesNotContain("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Antiforgery is the one framework cookie in use. It stays Strict, and it gets Secure from the policy.</summary>
    [Fact]
    public async Task The_antiforgery_cookie_gets_the_same_flags()
    {
        await using var app = new AppFactory(postgres.NewConnectionString(), environment: Environments.Production);
        using var client = app.CreatePublicClient();

        using var response = await client.GetAsync("/", Cancellation);

        var cookie = Cookie(response, ".AspNetCore.Antiforgery");

        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Setting Cookie.SecurePolicy on antiforgery instead would throw on any request that isn't HTTPS.
    /// A page catches that during rendering and still answers 200, so only the log shows it.
    /// </summary>
    [Fact]
    public async Task A_page_over_plain_http_logs_no_error()
    {
        await using var app = new LoggedApp(postgres.NewConnectionString());
        using var client = app.App.CreateClient();

        using var response = await client.GetAsync("/", Cancellation);
        await app.StopAsync();

        Assert.True(response.IsSuccessStatusCode, $"Got {response.StatusCode}");
        Assert.DoesNotContain(app.FileEvents(), entry => entry.GetProperty("Level").GetString() == "Error");
    }

    private static string Cookie(HttpResponseMessage response, string name) =>
        Assert.Single(response.Headers.GetValues("Set-Cookie"), cookie => cookie.StartsWith(name, StringComparison.Ordinal));

    private AppFactory CookieApp(string? environment) =>
        new(
            postgres.NewConnectionString(),
            services => services.AddTransient<IStartupFilter>(_ => new TerminalMiddleware(context =>
            {
                context.Response.Cookies.Append(Plain, "1");
                context.Response.Cookies.Append(Strict, "1", new CookieOptions { SameSite = SameSiteMode.Strict });
                return Task.CompletedTask;
            })),
            environment: environment);
}
