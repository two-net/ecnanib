namespace Ecnanib.Tests.Security;

/// <summary>NFR-SEC-03.</summary>
public sealed class BannedApiTests
{
    [Fact]
    public void The_application_uses_no_banned_api() =>
        Assert.Empty(new BannedApis(typeof(Program).Assembly, "Ecnanib").FindUses());
}
