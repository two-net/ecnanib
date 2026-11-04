using System.Text;
using System.Text.Json;
using Ecnanib.Kernel;
using Ecnanib.Tests.Logging;

namespace Ecnanib.Tests.Secrets;

/// <summary>
/// FR-KEY-09: a decrypted secret never shows its value by accident. The logging side of this is in
/// CanaryCases, which sends one through every route into a log.
/// </summary>
public sealed class SecretValueTests
{
    [Fact]
    public void It_reveals_its_value_only_when_asked()
    {
        var canary = CanaryCases.NewCanary();
        using var secret = SecretValue.FromString(canary);

        Assert.Equal(canary, secret.Reveal());
        Assert.Equal(canary, Encoding.UTF8.GetString(secret.RevealUtf8()));
        Assert.Equal("***", secret.ToString());
        Assert.Equal("key ***", $"key {secret}");
        Assert.Equal("***", string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0}", secret));
    }

    [Fact]
    public void It_has_no_json_form()
    {
        using var secret = SecretValue.FromString(CanaryCases.NewCanary());

        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(new { Secret = secret }));
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize<SecretValue>("\"value\""));
    }

    [Fact]
    public void Disposing_it_clears_it()
    {
        var secret = SecretValue.FromUtf8("value"u8);

        secret.Dispose();

        Assert.Throws<ObjectDisposedException>(secret.Reveal);
        Assert.Throws<ObjectDisposedException>(() => secret.RevealUtf8().Length);
    }

    /// <summary>A string that isn't valid text would otherwise be stored changed, and then fail to sign anything.</summary>
    [Fact]
    public void Invalid_text_is_refused_rather_than_changed() =>
        Assert.Throws<EncoderFallbackException>(() => SecretValue.FromString("\ud800"));
}
