using Ecnanib.Exchange.Abstractions;
using System.Text.Json;

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


    /// <summary>FR-KEY-04: a reason built from exchange text can be stored and shown once the key and the secret are scrubbed from it.</summary>
    [Fact]
    public void ScrubReplacesTheKeyAndTheSecretInExchangeText()
    {
        var credentials = new ExchangeCredentials("ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789abcd", "S3CR3T-VALUE-9876");

        Assert.Equal("API-key …abcd rejected; secret … invalid.", credentials.Scrub("API-key ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789abcd rejected; secret S3CR3T-VALUE-9876 invalid."));
        Assert.Equal("Timestamp for this request is outside of the recvWindow.", credentials.Scrub("Timestamp for this request is outside of the recvWindow."));
        Assert.Equal(string.Empty, credentials.Scrub(null));
    }

    /// <summary>FR-KEY-04, FR-LOG-05: nothing a serialiser or a formatter can reach carries the key or the secret.</summary>
    [Fact]
    public void SerialisingCredentialsOrAHandleRevealsNothing()
    {
        var credentials = new ExchangeCredentials("ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789abcd", "S3CR3T-VALUE-9876");
        var handle = new ExchangeKeyHandle("key-1", ExchangeEnvironment.Production, credentials);

        foreach (var text in new[] { JsonSerializer.Serialize(credentials), JsonSerializer.Serialize(handle), credentials.ToString(), handle.ToString() })
        {
            Assert.DoesNotContain("ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789abcd", text, StringComparison.Ordinal);
            Assert.DoesNotContain("S3CR3T", text, StringComparison.Ordinal);
            // The serialiser escapes the ellipsis; the mask's tail is what remains visible either way.
            Assert.Contains("abcd", text, StringComparison.Ordinal);
        }
    }
}
