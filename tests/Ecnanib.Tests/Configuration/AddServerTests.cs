using System.Net;
using Ecnanib.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.HttpsPolicy;
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

    /// <summary>NFR-SEC-01/02: the forwarded headers read behind the proxy, from loopback and the configured proxies only.</summary>
    [Fact]
    public void ForwardedHeadersComeFromLoopbackAndTheTrustedProxies()
    {
        using var provider = Build(new() { ["Server:TrustedProxies:0"] = "10.0.0.0/8", ["Server:TrustedProxies:1"] = "203.0.113.9" }).BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;

        Assert.Equal(ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto, options.ForwardedHeaders);
        Assert.Equal(1, options.ForwardLimit);
        Assert.False(options.RequireHeaderSymmetry);
        Assert.Contains(options.KnownIPNetworks, n => n.Contains(IPAddress.Parse("127.0.0.1")));
        Assert.Contains(options.KnownIPNetworks, n => n.Contains(IPAddress.Parse("10.20.30.40")));
        Assert.Contains(IPAddress.IPv6Loopback, options.KnownProxies);
        Assert.Contains(IPAddress.Parse("203.0.113.9"), options.KnownProxies);
        Assert.DoesNotContain(options.KnownIPNetworks, n => n.Contains(IPAddress.Parse("192.168.1.1")));
    }

    [Fact]
    public void WithoutTrustedProxiesOnlyLoopbackIsKnown()
    {
        using var provider = Build([]).BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;

        Assert.Single(options.KnownProxies);
        Assert.Single(options.KnownIPNetworks);
    }

    /// <summary>NFR-SEC-02: HSTS is a year, this host only; the redirection is the pipeline's own, from Server:PublicUrl.</summary>
    [Fact]
    public void HstsIsAYearForThisHostOnly()
    {
        using var provider = Build(new() { ["Server:PublicUrl"] = "https://ecnanib.example.com" }).BuildServiceProvider();

        var hsts = provider.GetRequiredService<IOptions<HstsOptions>>().Value;
        Assert.Equal(TimeSpan.FromDays(365), hsts.MaxAge);
        Assert.False(hsts.IncludeSubDomains);
        Assert.False(hsts.Preload);
    }

    private static ServiceCollection Build(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddServer(configuration);
        return services;
    }
}
