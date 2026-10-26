using Ecnanib.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Ecnanib.Tests.Configuration;

public class ServerOptionsValidationTests
{
    [Fact]
    public void DefaultsAreValid()
    {
        var options = Resolve([]);

        Assert.Equal("http://localhost:5207", options.PublicUrl);
        Assert.False(options.IsHttps);
        Assert.True(options.IsLoopbackPublicHost);
        Assert.Empty(options.TrustedProxies);
    }

    [Fact]
    public void OverridesAreHonoured()
    {
        var options = Resolve(new() { ["Server:PublicUrl"] = "https://ecnanib.example.com", ["Server:TrustedProxies:0"] = "10.0.0.0/8" });

        Assert.True(options.IsHttps);
        Assert.False(options.IsLoopbackPublicHost);
        Assert.Equal("ecnanib.example.com", options.PublicUri!.Host);
        Assert.Equal(["10.0.0.0/8"], options.TrustedProxies);
    }

    /// <summary>NFR-SEC-02: https anywhere; plain http only where nobody but this machine can reach it.</summary>
    [Theory]
    [InlineData("https://ecnanib.example.com/")]
    [InlineData("HTTPS://Ecnanib.Example.com:8443")]
    [InlineData("https://192.168.1.10:5207")]
    [InlineData("http://localhost:5207")]
    [InlineData("http://127.0.0.1:5207")]
    [InlineData("http://[::1]:5207")]
    public void PublicUrlVariantsAreAccepted(string publicUrl)
    {
        var options = Resolve(new() { ["Server:PublicUrl"] = publicUrl });

        Assert.Equal(publicUrl, options.PublicUrl);
    }

    [Theory]
    [InlineData("10.0.0.0/8")]
    [InlineData("192.168.1.10")]
    [InlineData("fd00::/8")]
    [InlineData("::1")]
    public void TrustedProxiesAcceptAddressesAndNetworks(string entry)
    {
        var options = Resolve(new() { ["Server:TrustedProxies:0"] = entry });

        Assert.Equal([entry], options.TrustedProxies);
        Assert.True(ServerOptions.TryParseTrustedProxy(entry, out var address, out var network));
        Assert.True((address is null) != (network is null));
    }

    [Theory]
    [InlineData("proxy.example")]
    [InlineData("10.0.0.0/33")]
    [InlineData("")]
    public void TrustedProxiesRefuseAnythingElse(string entry)
    {
        var ex = Assert.Throws<OptionsValidationException>(() => Resolve(new() { ["Server:TrustedProxies:0"] = entry }));

        Assert.Contains("Server:TrustedProxies[0] must be an IP address or a network in CIDR notation", ex.Message);
    }

    [Theory]
    [InlineData("", "Server:PublicUrl must be an absolute http or https URL")]
    [InlineData("ftp://ecnanib.example.com", "Server:PublicUrl must be an absolute http or https URL")]
    [InlineData("localhost:5207", "Server:PublicUrl must be an absolute http or https URL")]
    [InlineData("ecnanib.example.com", "Server:PublicUrl must be an absolute http or https URL")]
    [InlineData("http://ecnanib.example.com/app", "Server:PublicUrl must be an absolute http or https URL")]
    [InlineData("http://ecnanib.example.com/?q=1", "Server:PublicUrl must be an absolute http or https URL")]
    [InlineData("http://ecnanib.example.com/#top", "Server:PublicUrl must be an absolute http or https URL")]
    [InlineData("http://user:secret@ecnanib.example.com", "Server:PublicUrl must be an absolute http or https URL")]
    [InlineData("http://192.168.1.10:5207", "Server:PublicUrl must be https unless its host is localhost or a loopback address")]
    [InlineData("http://ecnanib.example.com", "served over HTTPS in production (NFR-SEC-02)")]
    public void InvalidPublicUrlsFailWithPropertyPath(string value, string expectedFragment)
    {
        var ex = Assert.Throws<OptionsValidationException>(() => Resolve(new() { ["Server:PublicUrl"] = value }));

        Assert.Contains(expectedFragment, ex.Message);
    }

    private static ServerOptions Resolve(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddServer(configuration);
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<ServerOptions>>().Value;
    }
}
