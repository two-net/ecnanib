using System.Text.Json;
using Ecnanib.Tests.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Ecnanib.Tests.Logging;

/// <summary>
/// FR-LOG-03 and CFG-04. Loggers are created before each change, because that's what running code
/// holds: a logger created afterwards would prove nothing.
/// </summary>
public sealed class LogLevelReloadTests(PostgresFixture postgres)
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_global_level_changes_without_a_restart()
    {
        await using var app = new LoggedApp(postgres.NewConnectionString());
        var logger = app.Logger("Ecnanib.Tests.Reload");

        logger.LogDebug("markerBefore");
        app.Change("Logging:MinimumLevel", "Debug");
        logger.LogDebug("markerAfter");
        await app.StopAsync();

        var text = app.FileText();

        Assert.DoesNotContain("markerBefore", text, StringComparison.Ordinal);
        Assert.Contains("markerAfter", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_framework_category_follows_the_same_change()
    {
        await using var app = new LoggedApp(
            postgres.NewConnectionString(),
            new() { ["Logging:Overrides:Microsoft.EntityFrameworkCore"] = "Warning" });
        var logger = app.Logger("Microsoft.EntityFrameworkCore.Database.Command");

        logger.LogDebug("markerBefore");
        app.Change("Logging:Overrides:Microsoft.EntityFrameworkCore", "Debug");
        logger.LogDebug("markerAfter");
        await app.StopAsync();

        var text = app.FileText();

        Assert.DoesNotContain("markerBefore", text, StringComparison.Ordinal);
        Assert.Contains("markerAfter", text, StringComparison.Ordinal);
    }

    /// <summary>Serilog fixes its override keys when a logger is built, so this needs a real reload.</summary>
    [Fact]
    public async Task An_override_added_while_running_applies_to_its_category_only()
    {
        await using var app = new LoggedApp(postgres.NewConnectionString());
        var chosen = app.Logger("Ecnanib.Tests.Chosen.Service");
        var other = app.Logger("Ecnanib.Tests.Other");

        app.Change("Logging:Overrides:Ecnanib.Tests.Chosen", "Debug");
        chosen.LogDebug("markerChosen");
        other.LogDebug("markerOther");
        await app.StopAsync();

        var text = app.FileText();

        Assert.Contains("markerChosen", text, StringComparison.Ordinal);
        Assert.DoesNotContain("markerOther", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_sink_can_be_switched_on_and_off_without_a_restart()
    {
        await using var app = new LoggedApp(
            postgres.NewConnectionString(),
            new() { ["Logging:Sinks:Database:Enabled"] = "false" });
        var logger = app.Logger("Ecnanib.Tests.Sinks");

        logger.LogError("markerBeforeDatabase");
        app.Change("Logging:Sinks:Database:Enabled", "true");
        logger.LogError("markerAfterDatabase");
        app.Change("Logging:Sinks:File:Enabled", "false");
        logger.LogError("markerAfterFile");
        await app.StopAsync();

        var table = await app.TableTextAsync(Cancellation);
        var text = app.FileText();

        Assert.DoesNotContain("markerBeforeDatabase", table, StringComparison.Ordinal);
        Assert.Contains("markerAfterDatabase", table, StringComparison.Ordinal);
        Assert.Contains("markerAfterDatabase", text, StringComparison.Ordinal);
        Assert.DoesNotContain("markerAfterFile", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Logging:MinimumLevel", "Loud", "Logging:MinimumLevel")]
    [InlineData("Logging:MinimumLevel", null, "Logging:MinimumLevel")]
    [InlineData("Logging:Overrides:Http", "Loud", "Logging:Overrides:Http")]
    [InlineData("Logging:Sinks:File:RetainedFileCount", "0", "Logging:Sinks:File:RetainedFileCount")]
    [InlineData("Logging:Typo", "Debug", "Typo")]
    public async Task An_invalid_edit_is_refused_and_logging_carries_on(string key, string? value, string named)
    {
        await using var app = new LoggedApp(postgres.NewConnectionString());
        var logger = app.Logger("Ecnanib.Tests.Invalid");

        app.Change(key, value);
        logger.LogError("markerStillLogging");
        logger.LogDebug("markerStillAtInformation");
        await app.StopAsync();

        var text = app.FileText();
        var refusal = Assert.Single(
            app.FileEvents(),
            entry => entry.GetProperty("MessageTemplate").GetString()!.StartsWith("Ignored a change", StringComparison.Ordinal));

        Assert.Contains(named, refusal.GetProperty("Exception").GetString(), StringComparison.Ordinal);
        Assert.Contains("markerStillLogging", text, StringComparison.Ordinal);
        Assert.DoesNotContain("markerStillAtInformation", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The real path an operator takes. Deleting the file clears its data and signals a change, so
    /// logging must refuse that rather than fall back to something the operator never configured.
    /// </summary>
    [Fact]
    public async Task Editing_and_then_deleting_the_real_file_applies_and_then_keeps_the_settings()
    {
        var directory = Directory.CreateTempSubdirectory("ecnanib-settings-");
        var file = Path.Combine(directory.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? directory.FullName, "settings.json");

        try
        {
            await File.WriteAllTextAsync(file, Settings("Information"), Cancellation);

            await using var app = new LoggedApp(
                postgres.NewConnectionString(),
                new() { ["Logging:MinimumLevel"] = null },
                configure: configuration => configuration.AddJsonFile(file, optional: true, reloadOnChange: true));
            var logger = app.Logger("Ecnanib.Tests.RealFile");

            await File.WriteAllTextAsync(file, Settings("Debug"), Cancellation);
            await LoggedApp.UntilAsync(() =>
            {
                logger.LogDebug("markerDebug");
                return ValueTask.FromResult(app.FileText().Contains("markerDebug", StringComparison.Ordinal));
            });

            File.Delete(file);
            await LoggedApp.UntilAsync(() =>
                ValueTask.FromResult(app.FileText().Contains("Ignored a change", StringComparison.Ordinal)));

            // Still Debug, which the deleted file set, so this isn't the fallback of a default.
            logger.LogDebug("markerAfterDelete");
            await app.StopAsync();

            Assert.Contains("markerAfterDelete", app.FileText(), StringComparison.Ordinal);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static string Settings(string level) =>
        JsonSerializer.Serialize(new { Logging = new { MinimumLevel = level } });
}
