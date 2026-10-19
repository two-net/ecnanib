using Ecnanib.Configuration;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Ecnanib.Tests.Configuration;

public class AddServerTests
{
    [Fact]
    public void RegistersTheOptionsTheValidatorAndTheKestrelEndpoint()
    {
        var services = Build(new() { ["Server:Port"] = "6001" });
        using var provider = services.BuildServiceProvider();

        Assert.Equal(6001, provider.GetRequiredService<IOptions<ServerOptions>>().Value.Port);
        Assert.Contains(provider.GetServices<IValidateOptions<ServerOptions>>(), v => v is ServerOptionsValidator);
        Assert.Contains(services, d => d.ServiceType == typeof(IConfigureOptions<KestrelServerOptions>));
        // Listen(IPEndPoint) needs no server; resolving the options proves the endpoint configures without error.
        _ = provider.GetRequiredService<IOptions<KestrelServerOptions>>().Value;
    }

    [Fact]
    public void AnInvalidSectionFailsAtStartupValidation()
    {
        using var provider = Build(new() { ["Server:Port"] = "0" }).BuildServiceProvider();

        // One failing section is rethrown as is; several are wrapped in an AggregateException (both handled by Program).
        var ex = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate());

        Assert.Contains("Server:Port must be between 1 and 65535.", ex.Message);
    }

    private static ServiceCollection Build(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddServer(configuration);
        return services;
    }
}
