using Ecnanib.Configuration;

namespace Ecnanib.Tests.Configuration;

/// <summary>NFR-SEC-04: the one host predicate both allowlists use.</summary>
public class HostNamesTests
{
    [Theory]
    [InlineData("binance.com", "binance.com", true)]
    [InlineData("API.Binance.COM", "binance.com", true)]
    [InlineData("api1.binance.com.", "binance.com", true)]
    [InlineData("testnet.binance.vision", "binance.vision", true)]
    [InlineData("evilbinance.com", "binance.com", false)]
    [InlineData("binance.com.example", "binance.com", false)]
    [InlineData("api.x.ai", "x.ai", true)]
    [InlineData("api.xx.ai", "x.ai", false)]
    [InlineData("", "x.ai", false)]
    public void AHostIsUnderADomainWhenItIsThatDomainOrASubdomain(string host, string domain, bool expected) =>
        Assert.Equal(expected, HostNames.IsUnder(host, domain));
}
