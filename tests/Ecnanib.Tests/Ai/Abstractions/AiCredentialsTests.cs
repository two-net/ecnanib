using System.Text.Json;
using Ecnanib.Ai.Abstractions;

namespace Ecnanib.Tests.Ai.Abstractions;

public class AiCredentialsTests
{
    private const string Key = AiTestHost.Key;

    [Fact]
    public void MasksKeyToLastFourCharactersAndTrims()
    {
        var credentials = new AiCredentials("  " + Key + " ");

        Assert.Equal("…abcd", credentials.MaskedApiKey);
        Assert.Equal(Key, credentials.ApiKey);
    }

    [Fact]
    public void ToStringNeverContainsTheKey()
    {
        var credentials = new AiCredentials(Key);
        var handle = new AiKeyHandle("key-1", AiProvider.Gpt, credentials);

        foreach (var text in new[] { credentials.ToString(), handle.ToString() })
        {
            Assert.DoesNotContain(Key, text);
            Assert.Contains("…abcd", text);
        }
    }

    [Fact]
    public void ShortKeysAreFullyMasked()
    {
        Assert.Equal("…***", AiCredentials.Mask("abc"));
        Assert.Equal("…", AiCredentials.Mask(""));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("sk-with space")]
    [InlineData("sk-with\nnewline")]
    [InlineData("sk-ké")]
    public void RejectsBlankOrNonPrintableKeysWithoutEchoingThem(string key)
    {
        var ex = Assert.Throws<ArgumentException>(() => new AiCredentials(key));

        if (key.Trim().Length > 0)
            Assert.DoesNotContain(key.Trim(), ex.Message);
    }

    [Fact]
    public void ScrubReplacesExactAndProviderMaskedForms()
    {
        var credentials = new AiCredentials(Key);
        var text = $"Incorrect API key provided: {Key}. Masked {AiTestHost.ProviderMaskedKey}, short sk-test-ABCD***abcd, twice {Key}{Key}.";

        var scrubbed = credentials.Scrub(text);

        Assert.Equal("Incorrect API key provided: …abcd. Masked …abcd, short …abcd, twice …abcd…abcd.", scrubbed);
    }

    [Fact]
    public void ScrubReplacesTheUrlEncodedForm()
    {
        var credentials = new AiCredentials("sk-a+b/c=dXYZ1234");

        Assert.Equal("key …1234 and …1234", credentials.Scrub("key sk-a+b/c=dXYZ1234 and sk-a%2Bb%2Fc%3DdXYZ1234"));
    }

    [Fact]
    public void ScrubHandlesPunctuationInsideKeysAndAfterThem()
    {
        var plain = new AiCredentials(Key);
        Assert.Equal("Key …abcd. Next", plain.Scrub($"Key {AiTestHost.ProviderMaskedKey}. Next"));
        Assert.Equal("(…abcd), …abcd;", plain.Scrub($"({AiTestHost.ProviderMaskedKey}), {AiTestHost.ProviderMaskedKey};"));

        var punctuated = new AiCredentials("sk-a+b/c=dXYZ1234");
        Assert.Equal("…1234.", punctuated.Scrub("sk-a+b/c****1234."));
        Assert.Equal("…1234", punctuated.Scrub("sk-a+b/c=dX***1234"));
    }

    [Fact]
    public void ScrubLeavesUnrelatedMaskedTokensAlone()
    {
        var credentials = new AiCredentials(Key);
        const string text = "card 4111********1111, prefix only sk-****, rated **** stars";

        Assert.Equal(text, credentials.Scrub(text));
        Assert.Equal(string.Empty, credentials.Scrub(null));
    }

    [Fact]
    public void HandleRequiresKeyIdAndCredentials()
    {
        Assert.Throws<ArgumentException>(() => new AiKeyHandle(" ", AiProvider.Claude, new AiCredentials(Key)));
        Assert.Throws<ArgumentNullException>(() => new AiKeyHandle("key-1", AiProvider.Claude, null!));
    }

    [Fact]
    public void RequestValidatesItsFieldsAndNeverPrintsPrompts()
    {
        Assert.Throws<ArgumentException>(() => new AiRequest(" ", "system", "user"));
        Assert.Throws<ArgumentNullException>(() => new AiRequest("m", null!, "user"));
        Assert.Throws<ArgumentException>(() => new AiRequest("m", "system", " "));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AiRequest("m", "system", "user", MaxOutputTokens: 0));
        Assert.Throws<ArgumentException>(() => new AiRequest("m", "system", "user", OutputSchema: JsonDocument.Parse("[1]").RootElement.Clone()));

        var request = new AiRequest(" model-1 ", "SYSTEM-TEXT", "USER-TEXT", AiTestHost.Schema(), 10, "risk-manager");

        Assert.Equal("model-1", request.Model);
        Assert.DoesNotContain("SYSTEM-TEXT", request.ToString());
        Assert.DoesNotContain("USER-TEXT", request.ToString());
        Assert.Contains("risk-manager", request.ToString());
        Assert.Equal(15, new AiTokenUsage(10, 5, null, null).TotalTokens);
    }


    /// <summary>FR-KEY-04, FR-LOG-05: nothing a serialiser or a formatter can reach carries the key.</summary>
    [Fact]
    public void SerialisingCredentialsOrAHandleRevealsNothing()
    {
        var credentials = new AiCredentials("sk-test-ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789abcd");
        var handle = new AiKeyHandle("key-1", AiProvider.Claude, credentials);

        foreach (var text in new[] { JsonSerializer.Serialize(credentials), JsonSerializer.Serialize(handle), credentials.ToString(), handle.ToString() })
        {
            Assert.DoesNotContain("sk-test-ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789abcd", text, StringComparison.Ordinal);
            // The serialiser escapes the ellipsis; the mask's tail is what remains visible either way.
            Assert.Contains("abcd", text, StringComparison.Ordinal);
        }
    }
}
