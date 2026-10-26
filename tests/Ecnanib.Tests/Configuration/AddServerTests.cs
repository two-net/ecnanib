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
    public void RegistersTheOptionsAndTheValidatorAndLeavesTheEndpointsToKestrel()
    {
        var services = Build(new() { ["Server:PublicUrl"] = "https://ecnanib.example.com" });
        using var provider = services.BuildServiceProvider();

        Assert.Equal("https://ecnanib.example.com", provider.GetRequiredService<IOptions<ServerOptions>>().Value.PublicUrl);
        Assert.Contains(provider.GetServices<IValidateOptions<ServerOptions>>(), v => v is ServerOptionsValidator);
        // Where Kestrel listens is the framework's Kestrel section, read by Kestrel itself: nothing here configures the server.
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IConfigureOptions<KestrelServerOptions>));
    }

    [Fact]
    public void AnInvalidSectionFailsAtStartupValidation()
    {
        using var provider = Build(new() { ["Server:PublicUrl"] = "ftp://ecnanib.example.com" }).BuildServiceProvider();

        // One failing section is rethrown as is; several are wrapped in an AggregateException (both handled by Program).
        var ex = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate());

        Assert.Contains("Server:PublicUrl must be an absolute http or https URL", ex.Message);
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
