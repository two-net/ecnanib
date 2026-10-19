using Ecnanib.Accounts.Services;

namespace Ecnanib.Tests.Accounts.Services;

public class PasswordPolicyTests
{
    [Theory]
    [InlineData("12345678", 8, true)]
    [InlineData("1234567", 8, false)]
    [InlineData(null, 8, false)]
    [InlineData("", 8, false)]
    [InlineData("aaaaaaaaaaa", 12, false)]
    [InlineData("aaaaaaaaaaaa", 12, true)]
    public void AcceptsLengthWithinTheConfiguredBounds(string? password, int minLength, bool expected)
    {
        Assert.Equal(expected, PasswordPolicy.IsAcceptable(password, minLength));
    }

    [Fact]
    public void AcceptsUpTo128CharactersAndNoMore()
    {
        Assert.True(PasswordPolicy.IsAcceptable(new string('x', 128), 8));
        Assert.False(PasswordPolicy.IsAcceptable(new string('x', 129), 8));
    }

    [Fact]
    public void HasNoCompositionRuleAndTrimsNothing()
    {
        Assert.True(PasswordPolicy.IsAcceptable("aaaaaaaa", 8));
        Assert.True(PasswordPolicy.IsAcceptable("        ", 8));
    }
}
