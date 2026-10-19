using System.Security.Claims;
using Ecnanib.Accounts.Abstractions;
using Ecnanib.Accounts.Authentication;
using Ecnanib.Database.Entities;

namespace Ecnanib.Tests.Accounts.Authentication;

public class AccountSessionTests
{
    [Fact]
    public void PrincipalCarriesIdEmailAndStamp()
    {
        var user = new User { Id = Guid.NewGuid(), Email = "Trader@Example.com", NormalizedEmail = "TRADER@EXAMPLE.COM", PasswordHash = "h", SecurityStamp = "stamp-1" };

        var principal = AccountSession.CreatePrincipal(user);

        Assert.Equal(user.Id, principal.GetUserId());
        Assert.Equal(user.Id, principal.RequireUserId());
        Assert.Equal("Trader@Example.com", principal.Identity?.Name);
        Assert.Equal("stamp-1", principal.GetSecurityStamp());
        Assert.True(principal.Identity?.IsAuthenticated);
        Assert.Equal("Cookies", principal.Identity?.AuthenticationType);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void AnonymousOrMalformedPrincipalsHaveNoUserId(string? nameIdentifier)
    {
        var identity = nameIdentifier is null ? new ClaimsIdentity() : new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, nameIdentifier)], "Cookies");
        var principal = new ClaimsPrincipal(identity);

        Assert.Null(principal.GetUserId());
        Assert.Null(((ClaimsPrincipal?)null).GetUserId());
        Assert.Throws<InvalidOperationException>(() => principal.RequireUserId());
    }

    [Theory]
    [InlineData("/settings", "/settings")]
    [InlineData("/", "/")]
    [InlineData("/agents?x=1#y", "/agents?x=1#y")]
    [InlineData("//evil.example", "/")]
    [InlineData("/\\evil.example", "/")]
    [InlineData("https://evil.example", "/")]
    [InlineData("javascript:alert(1)", "/")]
    [InlineData("/a\nb", "/")]
    [InlineData(null, "/")]
    [InlineData("", "/")]
    [InlineData("settings", "/")]
    public void OnlyLocalPathsAreUsedAsReturnUrls(string? url, string expected)
    {
        Assert.Equal(expected, LocalReturnUrl.Sanitize(url));
    }
}
