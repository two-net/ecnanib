using System.Text;

namespace Ecnanib.Tests.Identity;

/// <summary>The test's own authenticator against the RFCs, so that agreeing with it means something.</summary>
public sealed class AuthenticatorTests
{
    /// <summary>RFC 6238 Appendix B, SHA-1. The RFC's codes have 8 digits.</summary>
    [Theory]
    [InlineData(59L, "94287082")]
    [InlineData(1111111109L, "07081804")]
    [InlineData(1111111111L, "14050471")]
    [InlineData(1234567890L, "89005924")]
    [InlineData(2000000000L, "69279037")]
    [InlineData(20000000000L, "65353130")]
    public void Codes_match_RFC_6238(long unixTime, string expected)
    {
        var key = Encoding.ASCII.GetBytes("12345678901234567890");

        Assert.Equal(expected, Authenticator.Code(key, unixTime / 30, digits: 8));
    }

    /// <summary>Six digits are the last six of the eight, because both are the same number mod a power of ten.</summary>
    [Fact]
    public void A_six_digit_code_is_the_end_of_the_eight_digit_one()
    {
        var key = Encoding.ASCII.GetBytes("12345678901234567890");

        Assert.Equal("287082", Authenticator.Code(Authenticator.Base32Encode(key), DateTimeOffset.FromUnixTimeSeconds(59)));
    }

    /// <summary>RFC 4648 section 10, without the padding that authenticator keys leave off.</summary>
    [Theory]
    [InlineData("", "")]
    [InlineData("f", "MY")]
    [InlineData("fo", "MZXQ")]
    [InlineData("foo", "MZXW6")]
    [InlineData("foob", "MZXW6YQ")]
    [InlineData("fooba", "MZXW6YTB")]
    [InlineData("foobar", "MZXW6YTBOI")]
    public void Base32_matches_RFC_4648(string text, string expected)
    {
        Assert.Equal(expected, Authenticator.Base32Encode(Encoding.ASCII.GetBytes(text)));
        Assert.Equal(text, Encoding.ASCII.GetString(Authenticator.Base32Decode(expected)));
    }
}
