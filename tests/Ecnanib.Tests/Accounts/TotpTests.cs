using System.Text;
using Ecnanib.Accounts;

namespace Ecnanib.Tests.Accounts;

/// <summary>
/// The codes are those of an authenticator app: time-based one-time passwords as RFC 6238 defines
/// them, with HMAC-SHA1, six digits, and steps of 30 seconds (FR-ACC-3).
/// </summary>
public class TotpTests
{
    // The key of the test values in RFC 6238, appendix B.
    private static readonly byte[] RfcKey = Encoding.ASCII.GetBytes("12345678901234567890");

    [Theory]
    [InlineData(59L, "287082")]
    [InlineData(1111111109L, "081804")]
    [InlineData(1111111111L, "050471")]
    [InlineData(1234567890L, "005924")]
    [InlineData(2000000000L, "279037")]
    [InlineData(20000000000L, "353130")]
    public void The_codes_are_those_of_RFC_6238_for_SHA1(long second, string code)
    {
        // The RFC lists eight digits. A code of six digits is the last six of them.
        Assert.Equal(code, Totp.CodeAt(RfcKey, DateTimeOffset.FromUnixTimeSeconds(second)));
    }

    [Fact]
    public void A_code_lasts_for_a_step_of_thirty_seconds()
    {
        var start = DateTimeOffset.FromUnixTimeSeconds(1111111110);

        // From the first second of a step to its last, and no longer.
        Assert.Equal(Totp.StepAt(start), Totp.StepAt(start.AddSeconds(29)));
        Assert.Equal(Totp.StepAt(start) + 1, Totp.StepAt(start.AddSeconds(30)));
        Assert.Equal(Totp.StepAt(start) - 1, Totp.StepAt(start.AddSeconds(-1)));

        Assert.Equal("050471", Totp.CodeAt(RfcKey, start));
        Assert.Equal("050471", Totp.CodeAt(RfcKey, start.AddSeconds(29)));
        Assert.NotEqual("050471", Totp.CodeAt(RfcKey, start.AddSeconds(30)));
        Assert.Equal("081804", Totp.CodeAt(RfcKey, start.AddSeconds(-1)));
    }

    [Fact]
    public void A_code_has_six_digits_with_its_leading_zeros()
    {
        var codes = Enumerable.Range(0, 2000).Select(step => Totp.CodeAt(RfcKey, (long)step)).ToList();

        Assert.All(codes, code => Assert.Matches("^[0-9]{6}$", code));
        Assert.Contains(codes, code => code.StartsWith('0'));
    }

    [Fact]
    public void A_key_is_twenty_random_bytes()
    {
        var keys = Enumerable.Range(0, 50).Select(_ => Totp.NewKey()).ToList();

        Assert.All(keys, key => Assert.Equal(20, key.Length));
        Assert.Equal(50, keys.Select(Convert.ToHexString).Distinct().Count());
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("f", "MY")]
    [InlineData("fo", "MZXQ")]
    [InlineData("foo", "MZXW6")]
    [InlineData("foob", "MZXW6YQ")]
    [InlineData("fooba", "MZXW6YTB")]
    [InlineData("foobar", "MZXW6YTBOI")]
    [InlineData("12345678901234567890", "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ")]
    public void A_key_is_shown_in_Base32_without_padding(string key, string text)
    {
        // The test values of RFC 4648, and the key of RFC 6238 as an app takes it.
        Assert.Equal(text, Totp.TextOf(Encoding.ASCII.GetBytes(key)));
    }

    [Fact]
    public void The_address_for_an_app_names_the_service_the_account_and_the_key()
    {
        var address = Totp.UriFor("Ecnanib Test", "ada+trading@example.com", RfcKey);

        // What is not a letter or a digit is escaped, the colon between the two names is not.
        Assert.Equal(
            "otpauth://totp/Ecnanib%20Test:ada%2Btrading%40example.com"
            + "?secret=GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ&issuer=Ecnanib%20Test",
            address);
    }
}
