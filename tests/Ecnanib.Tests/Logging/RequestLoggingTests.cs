using System.Net;
using System.Text.Json;
using Ecnanib.Tests.Persistence;
using Ecnanib.Tests.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpsPolicy;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Logging;

/// <summary>FR-LOG-06.</summary>
public sealed class RequestLoggingTests(PostgresFixture postgres)
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_request_records_its_method_route_status_and_duration()
    {
        await using var app = new LoggedApp(postgres.NewConnectionString());
        using var client = app.App.CreateClient();

        using var response = await client.GetAsync("/", Cancellation);
        await app.StopAsync();

        var properties = Single(app, "GET").GetProperty("Properties");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("/", properties.GetProperty("Route").GetString());
        Assert.Equal(200, properties.GetProperty("StatusCode").GetInt32());
        Assert.True(properties.GetProperty("DurationMs").GetDouble() >= 0);
    }

    /// <summary>
    /// No authentication endpoint exists yet to test directly. This pins down everything a request
    /// event can hold instead, so a body, header or raw path can't be added to one unnoticed.
    /// </summary>
    [Fact]
    public async Task A_request_event_holds_only_the_expected_properties()
    {
        await using var app = new LoggedApp(postgres.NewConnectionString());
        using var client = app.App.CreateClient();

        using var response = await client.GetAsync("/", Cancellation);
        await app.StopAsync();

        var names = Single(app, "GET").GetProperty("Properties").EnumerateObject().Select(property => property.Name);

        Assert.Equal(
            [
                "ApplicationVersion", "CorrelationId", "DurationMs", "MachineName", "Method", "ParentId", "RequestId",
                "Route", "SourceContext", "SpanId", "StatusCode", "TraceId",
            ],
            names.Order(StringComparer.Ordinal));
    }

    /// <summary>Redirecting is a case of its own: a test host has no HTTPS port, so it would otherwise never run.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Nothing_from_the_request_itself_reaches_a_sink_even_at_the_most_verbose_level(bool redirected)
    {
        var canary = CanaryCases.NewCanary();

        await using var app = new LoggedApp(
            postgres.NewConnectionString(),
            new()
            {
                ["Logging:MinimumLevel"] = "Verbose",
                ["Logging:Overrides:Microsoft"] = "Verbose",
            },
            services =>
            {
                // Forwarded headers are only parsed from a trusted peer, and a test host has no peer at all.
                services.AddTransient<IStartupFilter>(
                    _ => new OuterMiddleware(context => context.Connection.RemoteIpAddress = IPAddress.Loopback));

                if (redirected)
                {
                    services.Configure<HttpsRedirectionOptions>(options => options.HttpsPort = 443);
                }
            });
        using var client = app.App.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/reset-password/{canary}?reset={canary}")
        {
            Content = new StringContent($$"""{"password":"{{canary}}"}""", System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {canary}");
        request.Headers.TryAddWithoutValidation("X-Api-Key", canary);
        request.Headers.TryAddWithoutValidation("Cookie", $"session={canary}");
        request.Headers.TryAddWithoutValidation("X-Innocent", canary);
        request.Headers.TryAddWithoutValidation("X-Correlation-ID", canary);
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", canary);

        using var response = await client.SendAsync(request, Cancellation);
        await app.StopAsync();

        var route = Single(app, "POST").GetProperty("Properties").GetProperty("Route").GetString();

        Assert.Equal(redirected, response.StatusCode == HttpStatusCode.TemporaryRedirect);
        Assert.DoesNotContain("reset-password", route, StringComparison.Ordinal);
        Assert.DoesNotContain(canary, app.FileText(), StringComparison.Ordinal);
        Assert.DoesNotContain(canary, await app.TableTextAsync(Cancellation), StringComparison.Ordinal);
    }

    /// <summary>
    /// A 503 with no body sends the request through the status page, which replaces the endpoint. The
    /// logged route must still be this request's own, not the status page's.
    /// </summary>
    [Fact]
    public async Task A_server_error_logs_as_an_error_against_its_own_route()
    {
        await using var app = new LoggedApp(
            postgres.NewConnectionString(),
            configureServices: services => services.AddTransient<IStartupFilter>(
                _ => new TerminalMiddleware(context =>
                {
                    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    return Task.CompletedTask;
                })));
        using var client = app.App.CreateClient();

        using var response = await client.GetAsync("/nothing-here", Cancellation);
        await app.StopAsync();

        var logged = Single(app, "GET");

        Assert.Equal("Error", logged.GetProperty("Level").GetString());
        Assert.Equal(503, logged.GetProperty("Properties").GetProperty("StatusCode").GetInt32());
        Assert.Equal("(unmatched)", logged.GetProperty("Properties").GetProperty("Route").GetString());
    }

    /// <summary>The framework's own exception page sits outside this middleware, so the exception passes through it.</summary>
    [Fact]
    public async Task An_unhandled_exception_logs_as_a_500()
    {
        var canary = CanaryCases.NewCanary();

        await using var app = new LoggedApp(
            postgres.NewConnectionString(),
            configureServices: services => services.AddTransient<IStartupFilter>(
                _ => new TerminalMiddleware(_ => throw new UnhandledFailure($"Failed with token={canary}"))));
        using var client = app.App.CreateClient();

        using var response = await client.GetAsync("/nothing-here", Cancellation);
        await app.StopAsync();

        var logged = Single(app, "GET");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("Error", logged.GetProperty("Level").GetString());
        Assert.Equal(500, logged.GetProperty("Properties").GetProperty("StatusCode").GetInt32());
        Assert.Contains("Failed with token=***", logged.GetProperty("Exception").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain(canary, app.FileText(), StringComparison.Ordinal);
    }

    private static JsonElement Single(LoggedApp app, string method) =>
        Assert.Single(
            app.FileEvents(),
            entry => entry.GetProperty("Properties").TryGetProperty("SourceContext", out var category)
                && category.GetString() == "Http"
                && entry.GetProperty("Properties").GetProperty("Method").GetString() == method);

    private sealed class UnhandledFailure(string message) : Exception(message);
}
