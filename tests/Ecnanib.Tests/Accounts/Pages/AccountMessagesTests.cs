using Ecnanib.Components.Account;
using Ecnanib.Database.Entities;

namespace Ecnanib.Tests.Accounts.Pages;

public class AccountMessagesTests
{
    /// <summary>FR-ACC-08, NFR-USE-03: the acceptance status sentences write the instant in UTC whatever offset it carries.</summary>
    [Fact]
    public void DisclaimerStatusSentencesAreStampedInUtc()
    {
        var at = new DateTimeOffset(2026, 10, 19, 14, 30, 0, TimeSpan.FromHours(2));

        Assert.Equal("You accepted version 2026-10-19 on 2026-10-19 12:30 UTC.", AccountMessages.DisclaimerAcceptedOn("2026-10-19", at));
        Assert.Equal("The disclaimer has changed since you accepted it on 2026-10-19 12:30 UTC; please read and accept it again.", AccountMessages.DisclaimerChanged(at));
    }

    [Theory]
    [InlineData(UiTheme.System, "Follow the system")]
    [InlineData(UiTheme.Light, "Light")]
    [InlineData(UiTheme.Dark, "Dark")]
    public void ThemeLabelsNameTheChoice(UiTheme theme, string expected) => Assert.Equal(expected, AccountMessages.ThemeLabel(theme));

    [Fact]
    public void PreferenceTextsAreExact()
    {
        Assert.Equal("Reduce animation", AccountMessages.ReducedMotionLabel);
        Assert.Equal("Saved. The choice applies on every device you sign in on.", AccountMessages.PreferencesSaved);
        Assert.Equal("The choice could not be saved. Try again.", AccountMessages.PreferencesNotSaved);
        Assert.Contains("reduced-motion setting is always honoured", AccountMessages.ReducedMotionHint);
    }

    [Theory]
    [InlineData(null, "Too many attempts. Try again in 1 minute.")]
    [InlineData(30, "Too many attempts. Try again in 1 minute.")]
    [InlineData(90, "Too many attempts. Try again in 2 minutes.")]
    public void ThrottleMessagesRoundUpToWholeMinutes(int? seconds, string expected) =>
        Assert.Equal(expected, AccountMessages.TooManyAttempts(seconds is { } s ? TimeSpan.FromSeconds(s) : null));

    [Fact]
    public void PasswordRuleNamesBothBounds() => Assert.Equal("The password must be between 8 and 128 characters.", AccountMessages.PasswordRule(8));
}
