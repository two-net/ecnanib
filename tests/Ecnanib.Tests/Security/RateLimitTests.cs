using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Ecnanib.Tests.Logging;
using Ecnanib.Tests.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Ecnanib.Tests.Security;

/// <summary>
/// NFR-SEC-07. Authentication doesn't exist yet, so these use a stand-in principal and a stand-in
/// sign-in endpoint. #9 needs a test through the real ones: see docs/architecture.md.
/// </summary>
public sealed class RateLimitTests(PostgresFixture postgres)
{
    private const string RequestsPerMinute = "Security:RateLimits:RequestsPerMinute";
    private const string AuthenticationRequestsPerMinute = "Security:RateLimits:AuthenticationRequestsPerMinute";
    private const string Missing = "/nothing-here";
    private const string SignIn = "/sign-in-stand-in";
    private const string Negotiate = "/_blazor/negotiate?negotiateVersion=1";
    private const string ClientA = "203.0.113.1";
    private const string ClientB = "203.0.113.2";
    private const string TrustedProxy = "10.0.0.1";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>A 404 runs the pipeline again for the status page, so a limiter inside that would count it twice.</summary>
    [Fact]
    public async Task Each_request_costs_one_permit_and_the_next_is_told_when_to_retry()
    {
        await using var app = Limited(3);
        using var client = app.CreateClient();

        for (var request = 0; request < 3; request++)
        {
            using var allowed = await client.SendFromAsync(Missing, ClientA);

            Assert.Equal(HttpStatusCode.NotFound, allowed.StatusCode);
        }

        using var rejected = await client.SendFromAsync(Missing, ClientA);

        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.InRange(rejected.Headers.RetryAfter?.Delta?.TotalSeconds ?? 0, 1, 60);
    }

    /// <summary>With no body, a status page would otherwise render "Not found" under a 429.</summary>
    [Fact]
    public async Task A_rejected_request_doesnt_reach_the_status_page()
    {
        await using var app = Limited(1);
        using var client = app.CreateClient();

        (await client.SendFromAsync(Missing, ClientA)).Dispose();
        using var rejected = await client.SendFromAsync(Missing, ClientA);

        Assert.Empty(await rejected.Content.ReadAsByteArrayAsync(Cancellation));
    }

    [Fact]
    public async Task Each_address_has_its_own_limit()
    {
        await using var app = Limited(1);
        using var client = app.CreateClient();

        (await client.SendFromAsync(Missing, ClientA)).Dispose();

        using var other = await client.SendFromAsync(Missing, ClientB);

        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
    }

    [Theory]
    [InlineData("2001:db8:1:2::1", "2001:db8:1:2:ffff::9", true)]
    [InlineData("2001:db8:1:2::1", "2001:db8:1:3::1", false)]
    [InlineData("203.0.113.9", "::ffff:203.0.113.9", true)]
    public async Task One_subscriber_cant_rotate_addresses_to_get_more_permits(string first, string second, bool shared)
    {
        await using var app = Limited(1);
        using var client = app.CreateClient();

        (await client.SendFromAsync(Missing, first)).Dispose();

        using var response = await client.SendFromAsync(Missing, second);

        Assert.Equal(shared ? HttpStatusCode.TooManyRequests : HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>Otherwise every user behind a proxy shares its one address, and one abuser locks out all of them.</summary>
    [Fact]
    public async Task Behind_a_trusted_proxy_each_client_has_its_own_limit()
    {
        await using var app = Limited(1, new() { ["Security:TrustedProxies:0"] = "10.0.0.0/8" });
        using var client = app.CreateClient();

        (await client.SendFromAsync(Missing, TrustedProxy, forwardedFor: ClientA)).Dispose();

        using var same = await client.SendFromAsync(Missing, TrustedProxy, forwardedFor: ClientA);
        using var other = await client.SendFromAsync(Missing, TrustedProxy, forwardedFor: ClientB);

        Assert.Equal(HttpStatusCode.TooManyRequests, same.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
    }

    /// <summary>A client can send anything, so believing this would give it a fresh limit per request.</summary>
    [Fact]
    public async Task An_untrusted_peer_cant_choose_its_address()
    {
        await using var app = Limited(1);
        using var client = app.CreateClient();

        (await client.SendFromAsync(Missing, ClientA, forwardedFor: "192.0.2.1")).Dispose();

        using var response = await client.SendFromAsync(Missing, ClientA, forwardedFor: "192.0.2.2");

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
    }

    /// <summary>A client behind a chain of trusted proxies is the first address that isn't one of them.</summary>
    [Fact]
    public async Task A_chain_of_trusted_proxies_is_walked_back_to_the_client()
    {
        await using var app = Limited(1, new() { ["Security:TrustedProxies:0"] = "10.0.0.0/8" });
        using var client = app.CreateClient();

        (await client.SendFromAsync(Missing, TrustedProxy, forwardedFor: $"{ClientA}, 10.0.0.2")).Dispose();

        using var same = await client.SendFromAsync(Missing, TrustedProxy, forwardedFor: $"{ClientA}, 10.0.0.3");
        using var other = await client.SendFromAsync(Missing, TrustedProxy, forwardedFor: $"{ClientB}, 10.0.0.2");

        Assert.Equal(HttpStatusCode.TooManyRequests, same.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
    }

    [Fact]
    public async Task An_account_has_one_limit_across_addresses()
    {
        await using var app = Limited(1);
        using var client = app.CreateClient();

        (await client.SendFromAsync(Missing, ClientA, "account-a")).Dispose();

        using var same = await client.SendFromAsync(Missing, ClientB, "account-a");
        using var other = await client.SendFromAsync(Missing, "203.0.113.3", "account-b");

        Assert.Equal(HttpStatusCode.TooManyRequests, same.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
    }

    /// <summary>The account limit is on top of the address limit, so signing in can't escape it.</summary>
    [Fact]
    public async Task An_address_has_one_limit_across_accounts()
    {
        await using var app = Limited(1);
        using var client = app.CreateClient();

        (await client.SendFromAsync(Missing, ClientA, "account-a")).Dispose();

        using var response = await client.SendFromAsync(Missing, ClientA, "account-b");

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
    }

    /// <summary>A claim that didn't match would otherwise leave every account limited only by address, unnoticed.</summary>
    [Fact]
    public async Task A_signed_in_user_without_the_claim_fails_loudly()
    {
        await using var app = new AppFactory(
            postgres.NewConnectionString(),
            services => services.AddTransient<IStartupFilter>(_ => new OuterMiddleware(
                context => context.User = new ClaimsPrincipal(new ClaimsIdentity("Test")))));
        using var client = app.CreateClient();

        using var response = await client.GetAsync(Missing, Cancellation);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    /// <summary>The stand-in is an endpoint with the metadata RequireRateLimiting would give it.</summary>
    [Fact]
    public async Task The_authentication_policy_is_stricter_and_applies_per_address()
    {
        await using var app = new AppFactory(
            postgres.NewConnectionString(),
            services =>
            {
                services.AddTestNetwork();
                services.AddTransient<IStartupFilter>(_ => new OuterMiddleware(context =>
                {
                    if (context.Request.Path == SignIn)
                    {
                        context.SetEndpoint(StandInSignIn());
                    }
                }));
            },
            new Dictionary<string, string?> { [RequestsPerMinute] = "100", [AuthenticationRequestsPerMinute] = "2" });
        using var client = app.CreateClient();

        (await client.SendFromAsync(SignIn, ClientA)).Dispose();
        (await client.SendFromAsync(SignIn, ClientA)).Dispose();

        using var rejected = await client.SendFromAsync(SignIn, ClientA);
        using var elsewhere = await client.SendFromAsync(Missing, ClientA);
        using var other = await client.SendFromAsync(SignIn, ClientB);

        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, elsewhere.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, other.StatusCode);
    }

    /// <summary>
    /// The constraint #5 left for this issue: a limit must never fail a probe. Assets are exempt
    /// because one page load fetches several.
    /// </summary>
    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    [InlineData("/app.css")]
    public async Task Probes_and_assets_are_never_limited(string path)
    {
        await using var app = Limited(1);
        using var client = app.CreateClient();

        (await client.SendFromAsync(Missing, ClientA)).Dispose();

        for (var request = 0; request < 3; request++)
        {
            using var response = await client.SendFromAsync(path, ClientA);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [Fact]
    public async Task Negotiating_a_connection_is_limited()
    {
        await using var app = Limited(1);
        using var client = app.CreateClient();

        using var allowed = await client.PostAsync(Negotiate, null, Cancellation);
        using var rejected = await client.PostAsync(Negotiate, null, Cancellation);

        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
    }

    /// <summary>Long polling makes a request per message, so limiting these would end live sessions.</summary>
    [Fact]
    public async Task Long_polling_on_an_admitted_connection_is_not_limited()
    {
        await using var app = Limited(1);
        using var client = app.CreateClient();

        (await client.SendFromAsync(Missing)).Dispose();

        for (var request = 0; request < 3; request++)
        {
            using var response = await client.SendFromAsync("/_blazor?id=unknown-connection");

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    /// <summary>The request that failed was counted already, and a limit here would replace its page with an empty 429.</summary>
    [Fact]
    public async Task A_failed_request_at_the_limit_still_gets_its_error_page()
    {
        await using var app = new AppFactory(
            postgres.NewConnectionString(),
            services => services.AddTransient<IStartupFilter>(
                _ => new TerminalMiddleware(_ => throw new InvalidOperationException("Failing on purpose."))),
            new Dictionary<string, string?> { [RequestsPerMinute] = "1" },
            environment: Environments.Production);
        using var client = app.CreatePublicClient();

        using var response = await client.GetAsync(Missing, Cancellation);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("An error occurred", await response.Content.ReadAsStringAsync(Cancellation), StringComparison.Ordinal);
    }

    /// <summary>Request logging records a 5xx as an Error, so the default of 503 would put a flood in the database.</summary>
    [Fact]
    public async Task A_rejected_request_is_logged_as_information()
    {
        await using var app = new LoggedApp(
            postgres.NewConnectionString(),
            new() { [RequestsPerMinute] = "1", ["Logging:Overrides:Http"] = "Information" });
        using var client = app.App.CreateClient();

        (await client.GetAsync(Missing, Cancellation)).Dispose();
        using var rejected = await client.GetAsync(Missing, Cancellation);
        await app.StopAsync();

        var entry = Assert.Single(app.FileEvents(), IsRejection);

        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Equal("Information", entry.GetProperty("Level").GetString());
    }

    private static bool IsRejection(JsonElement entry) =>
        entry.GetProperty("Properties").TryGetProperty("StatusCode", out var status)
        && status.GetInt32() == StatusCodes.Status429TooManyRequests;

    private static Endpoint StandInSignIn() =>
        new(
            context =>
            {
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return Task.CompletedTask;
            },
            new EndpointMetadataCollection(new EnableRateLimitingAttribute("authentication")),
            "Stand-in sign-in");

    private AppFactory Limited(int requestsPerMinute, Dictionary<string, string?>? settings = null) =>
        new(
            postgres.NewConnectionString(),
            services => services.AddTestNetwork(),
            new Dictionary<string, string?>(settings ?? []) { [RequestsPerMinute] = $"{requestsPerMinute}" });
}
