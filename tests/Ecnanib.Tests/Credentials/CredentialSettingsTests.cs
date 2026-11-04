using Ecnanib.Tests.Persistence;

namespace Ecnanib.Tests.Credentials;

/// <summary>CFG-03 for SRS 7.2's egress addresses, which users put on a Binance key's IP restriction (FR-KEY-05).</summary>
public sealed class CredentialSettingsTests(PostgresFixture postgres)
{
    [Theory]
    [InlineData("not an address")]
    [InlineData("203.0.113.0/24")]
    [InlineData("203.0.113")]
    [InlineData("fe80::1%eth0")]
    [InlineData("ecnanib.example.com")]
    [InlineData(" ")]
    public async Task An_entry_that_is_not_one_address_stops_startup_and_is_named(string value)
    {
        await using var app = new AppFactory(
            postgres.NewConnectionString(),
            settings: new Dictionary<string, string?> { ["App:EgressIpAddresses:0"] = "203.0.113.10", ["App:EgressIpAddresses:1"] = value });

        var exception = Assert.ThrowsAny<Exception>(app.Start);

        Assert.Contains("App:EgressIpAddresses:1", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Addresses_are_listed_once_each_in_their_usual_form()
    {
        await using var app = new CredentialApp(
            postgres,
            new() { ["App:EgressIpAddresses:2"] = "2001:0DB8:0:0::10", ["App:EgressIpAddresses:3"] = " 203.0.113.10 " });

        Assert.Equal([CredentialApp.EgressAddress, CredentialApp.EgressAddressV6], app.Credentials.EgressIpAddresses);
    }

    [Fact]
    public async Task Without_addresses_the_list_is_empty()
    {
        await using var app = new AppFactory(postgres.NewConnectionString());
        app.Start();

        Assert.Empty(Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
            .GetRequiredService<Modules.Credentials.Contracts.ICredentials>(app.Services).EgressIpAddresses);
    }
}
