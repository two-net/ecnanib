namespace Ecnanib.Tests.Security;

/// <summary>
/// Tests the scan against deliberately broken fixtures. It's the only automated guard on these
/// risks, so a rule that silently stopped matching would be invisible without this.
/// </summary>
public sealed class BannedApiGuardTests
{
    private const string Root = "Ecnanib.Tests.Security.Fixtures";

    private static readonly List<BannedUse> Uses = new BannedApis(typeof(BannedApiGuardTests).Assembly, Root).FindUses();

    [Theory]
    [InlineData("MarkupStringCast", "renders text as HTML")]
    [InlineData("MarkupStringConstructed", "renders text as HTML")]
    [InlineData("MarkupStringInLambda", "renders text as HTML")]
    [InlineData("FromSqlRawCall", "builds SQL from a string")]
    [InlineData("ExecuteSqlRawCall", "builds SQL from a string")]
    [InlineData("ExecuteSqlRawAsyncCall", "builds SQL from a string")]
    [InlineData("SqlQueryRawCall", "builds SQL from a string")]
    [InlineData("AntiforgeryTurnedOff", "turns off cross-site request forgery")]
    [InlineData("HttpClientOutsideAdapters", "reaches the network")]
    [InlineData("HttpClientInAsyncMethod", "reaches the network")]
    [InlineData("HttpClientFactoryOutsideAdapters", "reaches the network")]
    [InlineData("HttpClientPassedToAnExtension", "reaches the network")]
    [InlineData("SecretRevealedOutsideItsOwners", "reveals a user's secret")]
    [InlineData("SecretStoredOutsideItsOwners", "reveals a user's secret")]
    [InlineData("AddressesOutsideNotifications", "hands out other users' addresses")]
    [InlineData("MailKitClientOutsideEmail", "reaches an SMTP server")]
    [InlineData("FrameworkSmtpClient", "no longer recommends")]
    [InlineData("FrameworkSmtpClientInEmail", "no longer recommends")]
    public void A_banned_use_is_found(string fixture, string risk) =>
        Assert.Contains(Uses, use => use.Source.Contains(fixture, StringComparison.Ordinal) && use.Risk.Contains(risk, StringComparison.Ordinal));

    [Theory]
    [InlineData("InterpolatedSql")]
    [InlineData("AnAdapter")]
    [InlineData("AnOwner")]
    [InlineData("TheEmailJob")]
    [InlineData("TheEmailSender")]
    public void An_allowed_use_passes(string fixture) =>
        Assert.DoesNotContain(Uses, use => use.Source.Contains(fixture, StringComparison.Ordinal));
}
