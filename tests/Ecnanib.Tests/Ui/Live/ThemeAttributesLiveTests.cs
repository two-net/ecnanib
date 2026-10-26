using System.Net;
using Ecnanib.Accounts.Abstractions;
using Ecnanib.Database.Entities;
using Ecnanib.Tests.Agents.Live;
using Ecnanib.Tests.Configuration;
using Ecnanib.Tests.Database.Live;
using Ecnanib.Tests.Support;
using Ecnanib.Ui.Abstractions;
using Ecnanib.Ui.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Ecnanib.Tests.Ui.Live;

/// <summary>
/// NFR-USE-02 and FR-UI-02 end to end: a saved reduced-motion and theme preference reaches the document element of every
/// page the user opens, before the first paint and without a script, so the stylesheet stops the decorative loops and
/// zeroes every transition; the appearance panel is prerendered with the choice; a visitor gets neither attribute.
/// </summary>
[Trait("Category", "Live")]
public class ThemeAttributesLiveTests : IClassFixture<DatabaseLiveFixture>
{
    private const string Email = "motion@example.com";
    private readonly DatabaseLiveFixture _fixture;

    public ThemeAttributesLiveTests(DatabaseLiveFixture fixture)
    {
        _fixture = fixture;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ASavedPreferenceReachesTheDocumentElementAndAVisitorGetsTheDefaults()
    {
        _fixture.Require();
        await _fixture.MigrateAsync();
        await using var host = await PipelineTestHost.StartAsync(connectionString: _fixture.ConnectionString);
        var user = await PipelineSession.RegisterAsync(host, Email);
        Assert.True(await host.Services.GetRequiredService<IAccountService>().UpdatePreferencesAsync(user.Id, new UserPreferences(UiTheme.Dark, true), Ct));
        var session = await PipelineSession.SignInAsync(host, Email);
        var duration = $"--ui-duration-base:{host.Services.GetRequiredService<IOptions<UiOptions>>().Value.TransitionMilliseconds}ms";

        foreach (var path in new[] { "/agents", "/settings" })
        {
            var (response, html) = await PipelineSession.GetAsync(host, path, session);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var root = AccessibilityChecks.Parse(html).DocumentElement;
            Assert.Equal(ThemeAttributes.Reduced, root.GetAttribute(ThemeAttributes.MotionAttribute));
            Assert.Equal(ThemeAttributes.Dark, root.GetAttribute(ThemeAttributes.ThemeAttribute));
            Assert.Equal("en", root.GetAttribute("lang"));
            Assert.Equal(duration, root.GetAttribute("style"));
        }

        var (_, settings) = await PipelineSession.GetAsync(host, "/settings", session);
        var panel = AccessibilityChecks.Parse(settings);
        var preferences = panel.QuerySelector(".preferences")?.OuterHtml ?? "(no .preferences element)";
        Assert.True(panel.QuerySelector(".preferences input[type=radio][value=Dark]")?.HasAttribute("checked"), "the Dark radio is not prerendered checked: " + preferences);
        Assert.True(panel.QuerySelector(".preferences input[type=checkbox]")?.HasAttribute("checked"), "the reduced-motion checkbox is not prerendered checked: " + preferences);

        var (_, landing) = await PipelineSession.GetAsync(host, "/");
        var visitor = AccessibilityChecks.Parse(landing).DocumentElement;
        Assert.False(visitor.HasAttribute(ThemeAttributes.MotionAttribute));
        Assert.False(visitor.HasAttribute(ThemeAttributes.ThemeAttribute));
        Assert.Equal(duration, visitor.GetAttribute("style"));

        await AgentsLiveSeed.DeleteUsersAsync(_fixture, user.Id);
    }
}
