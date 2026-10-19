using System.Net;
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

        Assert.Equal("127.0.0.1", options.BindAddress);
        Assert.Equal(5207, options.Port);
        Assert.Equal("http://localhost:5207", options.PublicUrl);
        Assert.Equal(new IPEndPoint(IPAddress.Loopback, 5207), options.ToEndPoint());
        Assert.False(options.IsHttps);
    }

    [Fact]
    public void OverridesAreHonoured()
    {
        var options = Resolve(new() { ["Server:BindAddress"] = "::", ["Server:Port"] = "8080", ["Server:PublicUrl"] = "https://ecnanib.example.com" });

        Assert.Equal(new IPEndPoint(IPAddress.IPv6Any, 8080), options.ToEndPoint());
        Assert.True(options.IsHttps);
        Assert.Equal("ecnanib.example.com", options.PublicUri!.Host);
    }

    [Theory]
    [InlineData("https://ecnanib.example.com/")]
    [InlineData("HTTPS://Ecnanib.Example.com:8443")]
    [InlineData("http://192.168.1.10:5207")]
    public void PublicUrlVariantsAreAccepted(string publicUrl)
    {
        var options = Resolve(new() { ["Server:PublicUrl"] = publicUrl });

        Assert.Equal(publicUrl, options.PublicUrl);
    }

    [Theory]
    [InlineData("Server:BindAddress", "", "Server:BindAddress must be an IP address")]
    [InlineData("Server:BindAddress", "localhost", "Server:BindAddress must be an IP address")]
    [InlineData("Server:BindAddress", "example.com", "Server:BindAddress must be an IP address")]
    [InlineData("Server:Port", "0", "Port")]
    [InlineData("Server:Port", "65536", "Port")]
    [InlineData("Server:PublicUrl", "", "Server:PublicUrl must be an absolute http or https URL")]
    [InlineData("Server:PublicUrl", "ftp://ecnanib.example.com", "Server:PublicUrl must be an absolute http or https URL")]
    [InlineData("Server:PublicUrl", "localhost:5207", "Server:PublicUrl must be an absolute http or https URL")]
    [InlineData("Server:PublicUrl", "ecnanib.example.com", "Server:PublicUrl must be an absolute http or https URL")]
    [InlineData("Server:PublicUrl", "http://ecnanib.example.com/app", "Server:PublicUrl must be an absolute http or https URL")]
    [InlineData("Server:PublicUrl", "http://ecnanib.example.com/?q=1", "Server:PublicUrl must be an absolute http or https URL")]
    [InlineData("Server:PublicUrl", "http://ecnanib.example.com/#top", "Server:PublicUrl must be an absolute http or https URL")]
    [InlineData("Server:PublicUrl", "http://user:secret@ecnanib.example.com", "Server:PublicUrl must be an absolute http or https URL")]
    public void InvalidValuesFailWithPropertyPath(string key, string value, string expectedFragment)
    {
        var ex = Assert.Throws<OptionsValidationException>(() => Resolve(new() { [key] = value }));

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
