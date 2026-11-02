using System.Net;
using Ecnanib.Tests.Logging;
using Ecnanib.Tests.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Ecnanib.Tests.Security;

/// <summary>NFR-SEC-01.</summary>
public sealed class HttpsTests(PostgresFixture postgres)
{
    private const string Hsts = "Strict-Transport-Security";
    private const string OneYear = "max-age=31536000";
    private const string Page = "/";
    private const string Missing = "/nothing-here";
    private const string TrustedPeer = "10.0.0.1";
    private const string UntrustedPeer = "198.51.100.1";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(Page, HttpStatusCode.OK)]
    [InlineData(Missing, HttpStatusCode.NotFound)]
    public async Task Https_responses_carry_hsts(string path, HttpStatusCode status)
    {
        await using var app = new AppFactory(postgres.NewConnectionString(), environment: Environments.Production);
        using var client = app.CreatePublicClient();

        using var response = await client.GetAsync(path, Cancellation);

        Assert.Equal(status, response.StatusCode);
        Assert.Equal(OneYear, Assert.Single(response.Headers.GetValues(Hsts)));
    }

    /// <summary>HSTS is set directly and the exception handler clears it, so moving it before the handler loses it here.</summary>
    [Fact]
    public async Task The_error_page_carries_hsts()
    {
        await using var app = new AppFactory(
            postgres.NewConnectionString(),
            services => services.AddTransient<IStartupFilter>(
                _ => new TerminalMiddleware(_ => throw new InvalidOperationException("Failing on purpose."))),
            environment: Environments.Production);
        using var client = app.CreatePublicClient();

        using var response = await client.GetAsync(Missing, Cancellation);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(OneYear, Assert.Single(response.Headers.GetValues(Hsts)));
    }

    [Fact]
    public async Task A_rejected_request_carries_hsts()
    {
        await using var app = new AppFactory(
            postgres.NewConnectionString(),
            settings: new Dictionary<string, string?> { ["Security:RateLimits:RequestsPerMinute"] = "1" },
            environment: Environments.Production);
        using var client = app.CreatePublicClient();

        (await client.GetAsync(Missing, Cancellation)).Dispose();
        using var response = await client.GetAsync(Missing, Cancellation);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.True(response.Headers.Contains(Hsts));
    }

    [Fact]
    public async Task Development_sends_no_hsts()
    {
        await using var app = new AppFactory(postgres.NewConnectionString());
        using var client = app.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri(AppFactory.PublicBaseUrl) });

        using var response = await client.GetAsync(Page, Cancellation);

        Assert.False(response.Headers.Contains(Hsts));
    }

    /// <summary>Without a port the framework would serve this over plain HTTP and only log a warning.</summary>
    [Fact]
    public async Task Plain_http_redirects_to_the_public_port()
    {
        await using var app = new AppFactory(
            postgres.NewConnectionString(),
            settings: new Dictionary<string, string?> { ["App:PublicBaseUrl"] = "https://ecnanib.test:8443" },
            environment: Environments.Production);
        using var client = PlainClient(app);

        using var response = await client.GetAsync("/page?a=1", Cancellation);

        Assert.Equal(HttpStatusCode.TemporaryRedirect, response.StatusCode);
        Assert.Equal("https://ecnanib.test:8443/page?a=1", response.Headers.Location?.ToString());
        Assert.False(response.Headers.Contains(Hsts));
    }

    /// <summary>A probe sent straight to the application would otherwise read a 307 as success.</summary>
    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task Health_answers_a_probe_on_plain_http(string path)
    {
        await using var app = new AppFactory(postgres.NewConnectionString(), environment: Environments.Production);
        using var client = PlainClient(app);

        using var response = await client.GetAsync(path, Cancellation);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData(TrustedPeer, true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData(UntrustedPeer, false)]
    public async Task Only_a_trusted_proxy_can_say_the_request_was_https(string peer, bool trusted)
    {
        await using var app = new AppFactory(
            postgres.NewConnectionString(),
            services => services.AddTestNetwork(),
            new Dictionary<string, string?> { ["Security:TrustedProxies:0"] = "10.0.0.0/8" },
            environment: Environments.Production);
        using var client = PlainClient(app);

        using var response = await client.SendFromAsync(Page, peer, forwardedFor: "203.0.113.7", forwardedProto: "https");

        Assert.Equal(trusted ? HttpStatusCode.OK : HttpStatusCode.TemporaryRedirect, response.StatusCode);
        Assert.Equal(trusted, response.Headers.Contains(Hsts));
    }

    [Fact]
    public async Task A_single_address_can_be_trusted_without_a_network()
    {
        await using var app = new AppFactory(
            postgres.NewConnectionString(),
            services => services.AddTestNetwork(),
            new Dictionary<string, string?> { ["Security:TrustedProxies:0"] = TrustedPeer },
            environment: Environments.Production);
        using var client = PlainClient(app);

        using var trusted = await client.SendFromAsync(Page, TrustedPeer, forwardedFor: "203.0.113.7", forwardedProto: "https");
        using var neighbour = await client.SendFromAsync(Page, "10.0.0.2", forwardedFor: "203.0.113.7", forwardedProto: "https");

        Assert.Equal(HttpStatusCode.OK, trusted.StatusCode);
        Assert.Equal(HttpStatusCode.TemporaryRedirect, neighbour.StatusCode);
    }

    private static HttpClient PlainClient(AppFactory app) =>
        app.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://ecnanib.test"),
            AllowAutoRedirect = false,
        });
}
