using Ecnanib.Database.Entities;
using Ecnanib.Ui;
using Ecnanib.Ui.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Ecnanib.Tests.Ui.Configuration;

public class UiOptionsValidationTests
{
    [Fact]
    public void DefaultsAreValidAndFollowTheSystem()
    {
        var options = Resolve([]);

        Assert.Equal("System", options.DefaultTheme);
        Assert.Equal(UiTheme.System, options.Theme);
        Assert.False(options.ReducedMotion);
        Assert.Equal(240, options.TransitionMilliseconds);
        Assert.Equal(new(UiTheme.System, false), options.Defaults);
    }

    [Fact]
    public void OverridesAreHonouredAndTheThemeNameIsCaseInsensitive()
    {
        var options = Resolve(new() { ["Ui:DefaultTheme"] = "dark", ["Ui:ReducedMotion"] = "true", ["Ui:TransitionMilliseconds"] = "400" });

        Assert.Equal(UiTheme.Dark, options.Theme);
        Assert.True(options.ReducedMotion);
        Assert.Equal(400, options.TransitionMilliseconds);
        Assert.Equal(new(UiTheme.Dark, true), options.Defaults);
    }

    [Theory]
    [InlineData("System", UiTheme.System)]
    [InlineData(" light ", UiTheme.Light)]
    [InlineData("DARK", UiTheme.Dark)]
    public void ThemeNamesParse(string text, UiTheme expected) => Assert.Equal(expected, UiOptions.TryParseTheme(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("2")]
    [InlineData("blue")]
    [InlineData("auto")]
    public void OtherTextsAreNotThemes(string? text) => Assert.Null(UiOptions.TryParseTheme(text));

    [Theory]
    [InlineData("Ui:DefaultTheme", "", "Ui:DefaultTheme must be System, Dark or Light.")]
    [InlineData("Ui:DefaultTheme", "blue", "Ui:DefaultTheme must be System, Dark or Light.")]
    [InlineData("Ui:DefaultTheme", "2", "Ui:DefaultTheme must be System, Dark or Light.")]
    [InlineData("Ui:TransitionMilliseconds", "0", "Ui:TransitionMilliseconds must be between 1 and 400")]
    [InlineData("Ui:TransitionMilliseconds", "401", "Ui:TransitionMilliseconds must be between 1 and 400")]
    public void InvalidValuesFailWithPropertyPath(string key, string value, string expectedFragment)
    {
        var ex = Assert.Throws<OptionsValidationException>(() => Resolve(new() { [key] = value }));

        Assert.Contains(expectedFragment, ex.Message);
    }

    private static UiOptions Resolve(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddUi(configuration);
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<UiOptions>>().Value;
    }
}
