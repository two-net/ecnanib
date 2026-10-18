using Ecnanib.Ai.Abstractions;
using Ecnanib.Ai.Providers;
using Ecnanib.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Ecnanib.Tests.Ai.DependencyInjection;

public class AddAiProvidersTests
{
    [Fact]
    public async Task ResolvesTheModuleWithoutContactingAProvider()
    {
        var handler = new FakeHttpMessageHandler();
        await using var services = AiTestHost.BuildServices(handler);
        var factory = services.GetRequiredService<IAiClientFactory>();

        foreach (var provider in Enum.GetValues<AiProvider>())
        {
            var client = factory.GetClient(provider);
            Assert.Equal(provider, client.Provider);
            Assert.Same(client, factory.GetClient(provider));
        }

        Assert.NotSame(factory.GetClient(AiProvider.Gpt), factory.GetClient(AiProvider.Grok));
        Assert.Throws<ArgumentOutOfRangeException>(() => factory.GetClient((AiProvider)42));
        Assert.Empty(services.GetServices<IHostedService>());
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task HttpClientTimeoutComesFromConfiguration()
    {
        await using var services = AiTestHost.BuildServices(new FakeHttpMessageHandler(), config: new() { ["Providers:CallTimeoutSeconds"] = "7" });

        var client = services.GetRequiredService<IHttpClientFactory>().CreateClient(AiProviderClientBase.HttpClientName);

        Assert.Equal(TimeSpan.FromSeconds(7), client.Timeout);
    }
}
