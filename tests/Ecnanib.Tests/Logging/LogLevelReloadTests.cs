using Ecnanib.Platform.Settings;
using Ecnanib.Tests.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ecnanib.Tests.Logging;

/// <summary>
/// Log levels change without a restart when the configuration file changes. Other changes take
/// effect at restart (NFR-CFG-7).
/// </summary>
public class LogLevelReloadTests
{
    private const string Marker = "SECRET-MARKER";

    [Fact]
    public async Task A_minimum_level_changed_in_the_file_takes_effect_without_a_restart()
    {
        using var application = new LoggedApplication(SettingsJson.Valid().With("Logging:MinimumLevel", "Warning"));

        // A logger that exists before the change, as every component's does.
        var logger = application.Logger("Ecnanib.Tests.Reload");
        logger.LogInformation("Written {When}", "before the change");
        Assert.False(logger.IsEnabled(LogLevel.Information));

        await ChangeFileAsync(
            application,
            SettingsJson.Valid().With("Logging:MinimumLevel", "Information"),
            until: () => logger.IsEnabled(LogLevel.Information));

        logger.LogInformation("Written {When}", "after the change");

        Assert.Equal(
            ["after the change"],
            application.Events().Where(written => written.Has("When")).Select(written => written.Text("When")));
    }

    [Fact]
    public async Task An_override_added_to_the_file_takes_effect_and_so_does_removing_it()
    {
        using var application = new LoggedApplication(SettingsJson.Valid().With("Logging:MinimumLevel", "Warning"));
        var investigated = application.Logger("Ecnanib.Tests.Reload.Investigated");
        var other = application.Logger("Ecnanib.Tests.Reload.Other");

        await ChangeFileAsync(
            application,
            SettingsJson.Valid().WithJson(
                "Logging:Overrides", """{ "Microsoft.AspNetCore": "Error", "Ecnanib.Tests.Reload.Investigated": "Debug" }"""),
            until: () => investigated.IsEnabled(LogLevel.Debug));

        Assert.False(other.IsEnabled(LogLevel.Information));

        await ChangeFileAsync(
            application,
            SettingsJson.Valid(),
            until: () => !investigated.IsEnabled(LogLevel.Debug));

        Assert.False(investigated.IsEnabled(LogLevel.Information));
        Assert.True(investigated.IsEnabled(LogLevel.Warning));
    }

    [Fact]
    public async Task A_change_that_makes_the_file_invalid_is_reported_without_values_and_changes_nothing()
    {
        using var application = new LoggedApplication(SettingsJson.Valid().With("Logging:MinimumLevel", "Warning"));
        var logger = application.Logger("Ecnanib.Tests.Reload");

        await ChangeFileAsync(
            application,
            SettingsJson.Valid()
                .With("Logging:MinimumLevel", "Information")
                .With("Email:Port", 0)
                .With("ConnectionStrings:EcnanibDbContext", $"<{Marker}>"),
            until: () => application.Events().Any(written => written.Has("Problems")));

        Assert.False(logger.IsEnabled(LogLevel.Information));

        var reported = application.Events().First(written => written.Has("Problems"));
        Assert.Equal("Warning", reported.Level);
        Assert.Equal(
            ["ConnectionStrings:EcnanibDbContext: still holds a placeholder", "Email:Port: must be a whole number from 1 to 65535"],
            reported["Problems"].EnumerateArray().Select(problem => problem.GetString()));
        Assert.DoesNotContain(Marker, application.LogText());

        // Once the file is valid again, its levels apply.
        await ChangeFileAsync(
            application,
            SettingsJson.Valid().With("Logging:MinimumLevel", "Information"),
            until: () => logger.IsEnabled(LogLevel.Information));
    }

    [Fact]
    public async Task Any_other_change_in_the_file_waits_for_a_restart()
    {
        using var application = new LoggedApplication(SettingsJson.Valid().With("Logging:MinimumLevel", "Warning"));
        var logger = application.Logger("Ecnanib.Tests.Reload");

        await ChangeFileAsync(
            application,
            SettingsJson.Valid()
                .With("Logging:MinimumLevel", "Information")
                .With("Limits:MaxAgentsPerUser", 9)
                .Without("Logging:Outputs:File"),
            until: () => logger.IsEnabled(LogLevel.Information));

        // The level changed. The limit and the outputs are still those the application started with.
        Assert.Equal(7, application.Services.GetRequiredService<LimitsSettings>().MaxAgentsPerUser);
        Assert.Equal(7, application.Services.GetRequiredService<EcnanibSettings>().Limits.MaxAgentsPerUser);

        logger.LogInformation("Written {When}", "after the change");
        Assert.Contains(application.Events(), written => written.Text("When") == "after the change");
    }

    /// <summary>
    /// Replaces the configuration file of a running application and waits for the effect. The file
    /// is written again every second while waiting, in case a change notification is lost.
    /// </summary>
    private static Task ChangeFileAsync(LoggedApplication application, SettingsJson settings, Func<bool> until)
    {
        var text = settings.ToString();
        application.ContentRoot.Write(SettingsFile.Name, text);

        return Wait.UntilAsync(
            until,
            "the application noticing the changed configuration file",
            again: () => application.ContentRoot.Write(SettingsFile.Name, text));
    }
}
