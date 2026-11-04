using Ecnanib.Modules.AiAdapters.Contracts;
using Ecnanib.Tests.Exchange;
using Ecnanib.Tests.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Ai;

/// <summary>EXT-AI-05, NFR-MNT-02, NFR-SEC-10: every provider is called the same way, and only where the configuration says.</summary>
public sealed class AiCallTests(PostgresFixture postgres)
{
    private const string Models = "/v1/models";
    private const string Complete = "/v1/complete";

    [Fact]
    public async Task Every_provider_with_an_adapter_is_listed()
    {
        await using var ai = new AiHarness(postgres);

        Assert.Equal(new[] { new AiProviderInfo("Test", true), new AiProviderInfo("Second", true) }, ai.Providers.All);
    }

    [Fact]
    public async Task A_request_goes_to_its_providers_configured_server_with_the_key_in_a_header()
    {
        await using var ai = new AiHarness(postgres);
        ai.Provider.On(HttpMethod.Post, Complete, _ => FakeAiProvider.Completion("""{"trend":"up"}"""));

        var first = await ai.CompleteAsync(AiHarness.Key("Test", apiKey: "first-key"));
        var second = await ai.CompleteAsync(AiHarness.Key("Second", apiKey: "second-key"));

        Assert.Equal(("""{"trend":"up"}""", """{"trend":"up"}"""), (first.Text, second.Text));
        Assert.Equal(
            new[] { (AiHarness.TestHost, Complete, "first-key"), (AiHarness.SecondHost, Complete, "second-key") },
            ai.Provider.Requests.Select(request => (request.Host, request.Path, request.Key!)));
        Assert.All(ai.Provider.Requests, request => Assert.Equal("https", request.Uri.Scheme));
    }

    [Fact]
    public async Task An_adapter_cant_send_a_request_anywhere_else()
    {
        await using var ai = new AiHarness(postgres);
        ai.Adapter.Escape = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => ai.CompleteAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => ai.Providers.ListModelsAsync(AiHarness.Key(), AiHarness.Cancellation));

        Assert.Empty(ai.Provider.Requests);
    }

    [Fact]
    public async Task A_model_list_follows_every_page()
    {
        await using var ai = new AiHarness(postgres);
        ai.Provider.On(HttpMethod.Get, Models, request => request.Page switch
        {
            null => FakeAiProvider.Models("p 2", "alpha", "beta"),
            "p 2" => FakeAiProvider.Models("p3", "gamma"),
            _ => FakeAiProvider.Models(null, "delta"),
        });

        var models = await ai.Providers.ListModelsAsync(AiHarness.Key(), AiHarness.Cancellation);

        Assert.Equal(new[] { "alpha", "beta", "gamma", "delta" }, models.Select(model => model.Id));
        Assert.Equal("Model alpha", models[0].DisplayName);
        Assert.Equal(new string?[] { null, "p 2", "p3" }, ai.Provider.Requests.Select(request => request.Page));
        Assert.All(ai.Provider.Requests, request => Assert.Equal(AiHarness.ApiKey, request.Key));
    }

    /// <summary>A partial list would make the catalog deprecate models that are still there (FR-CAT-04).</summary>
    [Fact]
    public async Task A_model_list_that_never_ends_is_abandoned()
    {
        await using var ai = new AiHarness(postgres);
        ai.Provider.On(HttpMethod.Get, Models, _ => FakeAiProvider.Models(Guid.NewGuid().ToString(), "alpha"));

        var exception = await Assert.ThrowsAsync<AiException>(() => ai.Providers.ListModelsAsync(AiHarness.Key(), AiHarness.Cancellation));

        Assert.Equal(AiFailure.Other, exception.Failure);
        Assert.Equal(20, ai.Provider.Requests.Count);
    }

    [Fact]
    public async Task A_model_list_that_repeats_a_page_is_abandoned()
    {
        await using var ai = new AiHarness(postgres);
        ai.Provider.On(HttpMethod.Get, Models, _ => FakeAiProvider.Models("same", "alpha"));

        var exception = await Assert.ThrowsAsync<AiException>(() => ai.Providers.ListModelsAsync(AiHarness.Key(), AiHarness.Cancellation));

        Assert.Equal(AiFailure.Other, exception.Failure);
        Assert.Equal(2, ai.Provider.Requests.Count);
    }

    /// <summary>Credentials (#20) reads an invalid key from this (FR-KEY-04).</summary>
    [Fact]
    public async Task An_invalid_key_fails_a_model_list_as_authentication()
    {
        await using var ai = new AiHarness(postgres);
        ai.Provider.On(HttpMethod.Get, Models, _ => FakeAiProvider.Error(System.Net.HttpStatusCode.Unauthorized, "authentication_error", "invalid x-api-key"));

        var exception = await Assert.ThrowsAsync<AiException>(() => ai.Providers.ListModelsAsync(AiHarness.Key(), AiHarness.Cancellation));

        Assert.Equal((AiFailure.Authentication, 1), (exception.Failure, exception.Attempts));
    }

    /// <summary>
    /// The pipeline's real handler doesn't follow a redirect, which could send a user's key to a server the
    /// configuration never named (NFR-SEC-10). A fake handler can't show this, so it runs against a socket.
    /// </summary>
    [Fact]
    public async Task A_redirect_is_not_followed_or_retried()
    {
        LoopbackServer? server = null;
        using var redirecting = server = new LoopbackServer(_ =>
            $"HTTP/1.1 302 Found\r\nLocation: {server!.BaseUrl}/elsewhere\r\nContent-Length: 0\r\n\r\n");
        await using var app = new AppFactory(
            postgres.NewConnectionString(),
            services => services.AddSingleton<IAiProviderAdapter>(new TestAdapter("Test")),
            new Dictionary<string, string?> { ["AiProviders:Test:BaseUrl"] = server.BaseUrl, ["AiProviders:Test:RetryBackoffMs"] = "0" });
        app.Start();

        var exception = await Assert.ThrowsAsync<AiException>(() => app.Services.GetRequiredService<IAiProviders>()
            .ListModelsAsync(AiHarness.Key(), AiHarness.Cancellation));

        Assert.Equal((AiFailure.Other, (int?)302, 1), (exception.Failure, exception.StatusCode, exception.Attempts));
        Assert.StartsWith("GET /v1/models HTTP/1.1\n", Assert.Single(server.Requests), StringComparison.Ordinal);
    }
}
