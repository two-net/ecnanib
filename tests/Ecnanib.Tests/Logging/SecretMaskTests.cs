using Ecnanib.Platform.Logging;
using Ecnanib.Platform.Settings;

namespace Ecnanib.Tests.Logging;

/// <summary>
/// The rules that decide what is masked in a log: by the name a value is logged under, by the
/// value of a known secret, and by the shape of a credential in text (NFR-LOG-4).
/// </summary>
public class SecretMaskTests
{
    private static readonly SecretMask Mask = new([new Secret("a-known-secret-value"), new Secret("short")]);

    [Theory]
    [InlineData("Password")]
    [InlineData("password")]
    [InlineData("NewPassword")]
    [InlineData("db_password")]
    [InlineData("Pwd")]
    [InlineData("Passphrase")]
    [InlineData("Secret")]
    [InlineData("ClientSecret")]
    [InlineData("api_secret")]
    [InlineData("ApiKey")]
    [InlineData("apiKey")]
    [InlineData("APIKey")]
    [InlineData("api_key")]
    [InlineData("apikey")]
    [InlineData("X-MBX-APIKEY")]
    [InlineData("x-api-key")]
    [InlineData("x-goog-api-key")]
    [InlineData("CatalogKey")]
    [InlineData("MasterKey")]
    [InlineData("MasterKeys")]
    [InlineData("PrivateKey")]
    [InlineData("listenKey")]
    [InlineData("Key")]
    [InlineData("Token")]
    [InlineData("AccessToken")]
    [InlineData("access_token")]
    [InlineData("refreshtoken")]
    [InlineData("Authorization")]
    [InlineData("Proxy-Authorization")]
    [InlineData("Cookie")]
    [InlineData("Set-Cookie")]
    [InlineData("Signature")]
    [InlineData("Credentials")]
    [InlineData("ConnectionString")]
    [InlineData("Otp")]
    public void A_name_that_says_secret_is_sensitive(string name)
    {
        Assert.True(Mask.IsSensitiveName(name), $"'{name}' should be masked.");
    }

    [Theory]
    [InlineData("InputTokens")]
    [InlineData("OutputTokens")]
    [InlineData("MaxTokens")]
    [InlineData("total_tokens")]
    [InlineData("ActiveKeyId")]
    [InlineData("KeyId")]
    [InlineData("Keyboard")]
    [InlineData("Symbol")]
    [InlineData("RequestId")]
    [InlineData("UserId")]
    [InlineData("Model")]
    [InlineData("Provider")]
    [InlineData("Usage")]
    [InlineData("X-MBX-USED-WEIGHT-1M")]
    [InlineData("SourceContext")]
    [InlineData("")]
    public void A_name_that_does_not_is_not(string name)
    {
        Assert.False(Mask.IsSensitiveName(name), $"'{name}' should not be masked.");
    }

    [Theory]

    // The value of a known secret, wherever it is.
    [InlineData("it was a-known-secret-value, in a sentence", "it was ***, in a sentence")]

    // Authorization schemes.
    [InlineData("Authorization: Bearer abc123.def456-ghi", "Authorization: Bearer ***")]
    [InlineData("basic dXNlcjpwYXNzd29yZA==", "basic ***")]

    // Connection strings.
    [InlineData("Host=db;Username=app;Password=hunter2;Database=x", "Host=db;Username=app;Password=***;Database=x")]
    [InlineData("Server=db;User Id=app;Pwd=hunter2", "Server=db;User Id=app;Pwd=***")]

    // Query strings and form bodies.
    [InlineData("/order?symbol=BTCUSDT&signature=9f8e7d&timestamp=1", "/order?symbol=BTCUSDT&signature=***&timestamp=1")]
    [InlineData("https://ai.example/v1/models?key=abcdef", "https://ai.example/v1/models?key=***")]
    [InlineData("listenKey=pqia91ma19a5s61cv6a81va65sdf19v8a65a1", "listenKey=***")]
    [InlineData("api_key=abc&apiSecret=def&access_token=ghi", "api_key=***&apiSecret=***&access_token=***")]

    // JSON bodies.
    [InlineData("""{"apiKey":"abc","symbol":"BTCUSDT"}""", """{"apiKey":"***","symbol":"BTCUSDT"}""")]
    [InlineData("""{ "password" : "a \"quoted\" one", "user": "ada" }""", """{ "password" : "***", "user": "ada" }""")]

    // The formats AI providers issue, and signed tokens.
    [InlineData("the key sk-ant-api03-abcdefghijklmnop was used", "the key *** was used")]
    [InlineData("xai-abcdefghijklmnopqrstuvwxyz", "***")]
    [InlineData("AIzaSyA1234567890abcdefghijklmnopqrstuvw", "***")]
    [InlineData("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dBjftJeZ4CVPmB92K27uhbUJU1p1r_wW1gFWFOEjXk", "***")]
    public void A_secret_in_text_is_replaced(string text, string masked)
    {
        Assert.Equal(masked, Mask.Text(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Order 42 for BTCUSDT was filled at 64250.5")]
    [InlineData("max_tokens=4096 and InputTokens=120")]
    [InlineData("""{"total_tokens": 300, "max_tokens": 4096}""")]
    [InlineData("ActiveKeyId=k2")]
    [InlineData("a short word")]
    [InlineData("/api/health?verbose=true")]
    public void Text_without_a_secret_is_returned_as_it_is(string text)
    {
        // The same instance, so an event with nothing to mask is passed on untouched.
        Assert.Same(text, Mask.Text(text));
    }

    [Fact]
    public void Every_secret_of_the_configuration_file_is_known_to_the_mask()
    {
        using var directory = new Settings.SettingsDirectory(Settings.SettingsJson.Valid());
        var mask = SecretMask.For(SettingsFile.Load(directory.SettingsPath));

        Assert.Equal("m: ***", mask.Text($"m: {Settings.SettingsJson.MasterKey}"));
        Assert.Equal("e: ***", mask.Text("e: email-password"));
        Assert.Equal("c: ***", mask.Text("c: claude-catalog-key"));
        Assert.Equal("d: ***", mask.Text("d: Host=db.example;Database=ecnanib;Username=app;Password=database-password"));
    }
}
