using Ecnanib.AiProviderGateway;
using Ecnanib.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests;

/// <summary>
/// A provider adapter is added by registering one more <see cref="IAiProviderAdapter"/>. Callers
/// reach it through <see cref="IAiProviderGateway"/> by code, so nothing else changes (NFR-ARC-3).
/// </summary>
public class AiProviderRouterTests
{
    private static readonly AiCallRequest Request = new("some-model", "prompt", "{}");

    [Fact]
    public async Task A_new_provider_adapter_is_reached_through_the_gateway_with_no_other_change()
    {
        var adapter = new FakeAdapter("a-fifth-provider");
        await using var services = new ServiceCollection()
            .AddLogging()
            .AddAiProviderGateway()
            .AddSingleton<IAiProviderAdapter>(adapter)
            .BuildServiceProvider();
        var gateway = services.GetRequiredService<IAiProviderGateway>();

        var result = await gateway.CompleteAsync(
            new AiProviderCode("A-Fifth-Provider"), Request, "key", TestContext.Current.CancellationToken);

        Assert.Same(adapter.Result, result);
        Assert.Same(Request, adapter.Received);
        Assert.Equal([adapter.Provider], gateway.Providers);
    }

    [Fact]
    public async Task Each_call_goes_to_the_adapter_of_its_provider()
    {
        var first = new FakeAdapter("first");
        var second = new FakeAdapter("second");
        await using var services = new ServiceCollection()
            .AddLogging()
            .AddAiProviderGateway()
            .AddSingleton<IAiProviderAdapter>(first)
            .AddSingleton<IAiProviderAdapter>(second)
            .BuildServiceProvider();
        var gateway = services.GetRequiredService<IAiProviderGateway>();

        var result = await gateway.CompleteAsync(second.Provider, Request, "key", TestContext.Current.CancellationToken);

        Assert.Same(second.Result, result);
        Assert.Null(first.Received);
    }

    [Fact]
    public async Task A_provider_without_an_adapter_is_refused_by_name()
    {
        await using var services = new ServiceCollection().AddLogging().AddAiProviderGateway().BuildServiceProvider();
        var gateway = services.GetRequiredService<IAiProviderGateway>();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => gateway.CompleteAsync(
            new AiProviderCode("unknown"), Request, "key", TestContext.Current.CancellationToken));

        Assert.Contains("'unknown'", error.Message);
    }

    [Fact]
    public async Task Two_adapters_for_one_provider_are_refused()
    {
        await using var services = new ServiceCollection()
            .AddLogging()
            .AddAiProviderGateway()
            .AddSingleton<IAiProviderAdapter>(new FakeAdapter("same"))
            .AddSingleton<IAiProviderAdapter>(new FakeAdapter("same"))
            .BuildServiceProvider();

        var error = Assert.Throws<InvalidOperationException>(services.GetRequiredService<IAiProviderGateway>);

        Assert.Contains("'same'", error.Message);
    }

    private sealed class FakeAdapter(string provider) : IAiProviderAdapter
    {
        public AiProviderCode Provider { get; } = new(provider);

        public AiCallResult Result { get; } = new("{}", new AiTokenUsage(1, 2), TimeSpan.FromMilliseconds(3));

        public AiCallRequest? Received { get; private set; }

        public Task<AiCallResult> CompleteAsync(AiCallRequest request, string apiKey, CancellationToken cancellationToken)
        {
            Received = request;
            return Task.FromResult(Result);
        }
    }
}
