using Ecnanib.Exchange.Abstractions;

namespace Ecnanib.Tests.Exchange.Abstractions;

public class ExchangeCredentialsTests
{
    [Fact]
    public void MasksKeyToLastFourCharacters()
    {
        var credentials = new ExchangeCredentials("abcdefgh12345", "supersecret");

        Assert.Equal("…2345", credentials.MaskedApiKey);
        Assert.Equal("abcdefgh12345", credentials.ApiKey);
    }

    [Fact]
    public void ToStringNeverContainsSecretOrFullKey()
    {
        var credentials = new ExchangeCredentials("abcdefgh12345", "supersecret");
        var handle = new ExchangeKeyHandle("key-1", ExchangeEnvironment.Testnet, credentials);

        foreach (var text in new[] { credentials.ToString(), handle.ToString() })
        {
            Assert.DoesNotContain("supersecret", text);
            Assert.DoesNotContain("abcdefgh12345", text);
            Assert.Contains("…2345", text);
        }
    }

    [Fact]
    public void ShortKeysAreFullyMasked()
    {
        Assert.Equal("…***", ExchangeCredentials.Mask("abc"));
        Assert.Equal("…", ExchangeCredentials.Mask(""));
    }

    [Theory]
    [InlineData("", "secret")]
    [InlineData("  ", "secret")]
    [InlineData("key", "")]
    public void RejectsBlankValues(string key, string secret)
    {
        Assert.Throws<ArgumentException>(() => new ExchangeCredentials(key, secret));
    }

    [Fact]
    public void HandleRequiresKeyId()
    {
        var credentials = new ExchangeCredentials("abcdefgh12345", "supersecret");
        Assert.Throws<ArgumentException>(() => new ExchangeKeyHandle(" ", ExchangeEnvironment.Production, credentials));
    }

    [Fact]
    public void MarketKeyNormalisesSymbol()
    {
        var key = new MarketKey(ExchangeEnvironment.Production, MarketType.Spot, " btcusdt ");
        Assert.Equal("BTCUSDT", key.Symbol);
        Assert.Equal(new MarketKey(ExchangeEnvironment.Production, MarketType.Spot, "BTCUSDT"), key);
    }
}
