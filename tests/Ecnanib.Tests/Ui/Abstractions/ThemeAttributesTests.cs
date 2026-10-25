using Ecnanib.Database.Entities;
using Ecnanib.Ui.Abstractions;

namespace Ecnanib.Tests.Ui.Abstractions;

public class ThemeAttributesTests
{
    [Fact]
    public void SystemRendersNoThemeAttributeSoTheStylesheetFollowsTheOperatingSystem()
    {
        var attributes = ThemeAttributes.For(new UserPreferences(UiTheme.System, false), 240);

        Assert.Null(attributes.Theme);
        Assert.Null(attributes.Motion);
        Assert.Equal("--ui-duration-base:240ms", attributes.Style);
    }

    [Theory]
    [InlineData(UiTheme.Dark, "dark")]
    [InlineData(UiTheme.Light, "light")]
    public void AnExplicitThemeIsNamed(UiTheme theme, string expected) => Assert.Equal(expected, ThemeAttributes.For(new UserPreferences(theme, false), 240).Theme);

    [Fact]
    public void ReducedMotionIsNamedAndTheDurationFollowsTheOptions()
    {
        var attributes = ThemeAttributes.For(new UserPreferences(UiTheme.Dark, true), 100);

        Assert.Equal("reduced", attributes.Motion);
        Assert.Equal("--ui-duration-base:100ms", attributes.Style);
    }
}
