using Ecnanib.Tests.Configuration;
using Ecnanib.Tests.Support;
using Ecnanib.Ui.Abstractions;
using Ecnanib.Ui.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Ecnanib.Tests.Ui;

/// <summary>
/// FR-UI-02, FR-UI-03, NFR-USE-02 for a visitor, rendered by the pipeline: the document element carries the Ui section's
/// defaults before the first paint, so a reduced-motion default stops every decorative loop and zeroes every transition
/// without a script, and the stock defaults carry neither attribute, leaving the operating system in charge.
/// </summary>
public class ThemeAttributesPipelineTests
{
    [Fact]
    public async Task TheStockDefaultsRenderNeitherAttributeAndTheTransitionLength()
    {
        await using var host = await PipelineTestHost.StartAsync();
        var (_, html) = await PipelineSession.GetAsync(host, "/");
        var root = AccessibilityChecks.Parse(html).DocumentElement;
        var options = host.Services.GetRequiredService<IOptions<UiOptions>>().Value;

        Assert.False(root.HasAttribute(ThemeAttributes.ThemeAttribute));
        Assert.False(root.HasAttribute(ThemeAttributes.MotionAttribute));
        Assert.Equal($"--ui-duration-base:{options.TransitionMilliseconds}ms", root.GetAttribute("style"));
    }

    [Fact]
    public async Task AReducedMotionDarkDefaultReachesTheDocumentElementForAVisitor()
    {
        await using var host = await PipelineTestHost.StartAsync(new() { ["Ui:ReducedMotion"] = "true", ["Ui:DefaultTheme"] = "Dark", ["Ui:TransitionMilliseconds"] = "120" });
        var (_, html) = await PipelineSession.GetAsync(host, "/sign-in");
        var root = AccessibilityChecks.Parse(html).DocumentElement;

        Assert.Equal(ThemeAttributes.Reduced, root.GetAttribute(ThemeAttributes.MotionAttribute));
        Assert.Equal(ThemeAttributes.Dark, root.GetAttribute(ThemeAttributes.ThemeAttribute));
        Assert.Equal("--ui-duration-base:120ms", root.GetAttribute("style"));
    }
}
