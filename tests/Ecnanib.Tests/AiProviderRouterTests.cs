using Ecnanib.AiProviderGateway;
using Ecnanib.Shared;
using Ecnanib.Tests.Logging;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests;

/// <summary>
/// A provider adapter is added by registering one more <see cref="IAiProviderAdapter"/>. Callers
/// reach it through <see cref="IAiProviderGateway"/> by code, so nothing else changes: not the
/// adapters that are there, and not what calls the gateway (NFR-ARC-3).
/// </summary>
public class AiProviderRouterTests
{
    private static readonly AiCallRequest Request = new("some-model", "prompt", "{}");

    [Fact]
    public async Task A_new_provider_adapter_is_reached_through_the_gateway_with_no_other_change()
    {
        var adapter = new FakeAdapter("a-fifth-provider");
        using var application = With(adapter);
        var gateway = application.Services.GetRequiredService<IAiProviderGateway>();

        var result = await gateway.CompleteAsync(
            new AiProviderCode("A-Fifth-Provider"), Request, "key", TestContext.Current.CancellationToken);

        Assert.Same(adapter.Result, result);
        Assert.Same(Request, adapter.Received);

        // It stands beside the four of the application, which are as they were.
        Assert.Equal(
            ["a-fifth-provider", "claude", "gemini", "gpt", "grok"],
            gateway.Providers.Select(provider => provider.Value).Order());
    }

    [Fact]
    public async Task Each_call_goes_to_the_adapter_of_its_provider()
    {
        var first = new FakeAdapter("first");
        var second = new FakeAdapter("second");
        using var application = With(first, second);
        var gateway = application.Services.GetRequiredService<IAiProviderGateway>();

        var result = await gateway.CompleteAsync(second.Provider, Request, "key", TestContext.Current.CancellationToken);

        Assert.Same(second.Result, result);
        Assert.Null(first.Received);
    }

    [Fact]
    public async Task A_provider_without_an_adapter_is_refused_by_name()
    {
        using var application = With();
        var gateway = application.Services.GetRequiredService<IAiProviderGateway>();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => gateway.CompleteAsync(
            new AiProviderCode("unknown"), Request, "key", TestContext.Current.CancellationToken));

        Assert.Contains("'unknown'", error.Message);
    }

    [Fact]
    public void Two_adapters_for_one_provider_are_refused()
    {
        // One more for a provider that the application has an adapter for.
        using var application = With(new FakeAdapter("claude"));

        var error = Assert.Throws<InvalidOperationException>(application.Services.GetRequiredService<IAiProviderGateway>);

        Assert.Contains("'claude'", error.Message);
    }

    /// <summary>The application with adapters of the test's own, registered as a provider's adapter is.</summary>
    private static LoggedApplication With(params IAiProviderAdapter[] adapters) =>
        new(
            LoggedApplication.Settings(),
            host => host.ConfigureServices(services =>
            {
                foreach (var adapter in adapters)
                {
                    services.AddSingleton(adapter);
                }
            }));

    private sealed class FakeAdapter(string provider) : IAiProviderAdapter
    {
        public AiProviderCode Provider { get; } = new(provider);

        public AiCallResult Result { get; } = new(AiCallOutcome.Succeeded, "{}", new AiTokenUsage(1, 2), TimeSpan.FromMilliseconds(3));

        public AiCallRequest? Received { get; private set; }

        public Task<AiCallResult> CompleteAsync(AiCallRequest request, string apiKey, CancellationToken cancellationToken)
        {
            Received = request;
            return Task.FromResult(Result);
        }
    }
}
