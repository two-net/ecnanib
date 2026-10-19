using Ecnanib.Accounts.Services;

namespace Ecnanib.Tests.Accounts.Services;

public class EmailAddressTests
{
    [Theory]
    [InlineData(" Trader@Example.com ", "Trader@Example.com", "TRADER@EXAMPLE.COM")]
    [InlineData("a@b", "a@b", "A@B")]
    [InlineData("first.last+tag@sub.example.co.uk", "first.last+tag@sub.example.co.uk", "FIRST.LAST+TAG@SUB.EXAMPLE.CO.UK")]
    public void NormalizesByTrimmingAndUpperCasing(string input, string expectedEmail, string expectedNormalized)
    {
        Assert.True(EmailAddress.TryNormalize(input, out var email, out var normalized));

        Assert.Equal(expectedEmail, email);
        Assert.Equal(expectedNormalized, normalized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no-at")]
    [InlineData("a@")]
    [InlineData("@b")]
    [InlineData("a@@b.com")]
    [InlineData("a b@c.com")]
    [InlineData("a@c\n.com")]
    [InlineData("Bob <bob@x.com>")]
    [InlineData("Bob<bob@x.com>")]
    [InlineData("<bob@x.com>")]
    [InlineData("bob@x.com (comment)")]
    public void RejectsMalformedOrDecoratedAddresses(string? input)
    {
        Assert.False(EmailAddress.TryNormalize(input, out var email, out var normalized));

        Assert.Equal("", email);
        Assert.Equal("", normalized);
    }

    [Fact]
    public void AcceptsUpTo254CharactersAndNoMore()
    {
        var longest = "a@" + string.Join(".", Enumerable.Repeat("bbbbbbbbbb", 23));
        Assert.Equal(254, longest.Length);

        Assert.True(EmailAddress.TryNormalize(longest, out _, out _));
        Assert.False(EmailAddress.TryNormalize(longest + "x", out _, out _));
    }
}
