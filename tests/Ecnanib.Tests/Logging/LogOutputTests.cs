using System.Globalization;
using Ecnanib.Tests.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ecnanib.Tests.Logging;

/// <summary>
/// The minimum level, the per-area overrides, and the outputs come from the configuration file
/// and from nowhere else. The file output rolls daily and keeps a limited number of files
/// (NFR-LOG-2). The console output is covered by <see cref="ConsoleOutputTests"/>.
/// </summary>
public class LogOutputTests
{
    [Fact]
    public void The_only_log_output_of_the_host_is_the_configured_one()
    {
        using var application = new LoggedApplication(LoggedApplication.Settings());

        // None of the outputs a host adds by default: console, debug, event source, event log.
        var provider = Assert.Single(application.Services.GetServices<ILoggerProvider>());
        Assert.Equal("SerilogLoggerProvider", provider.GetType().Name);
    }

    [Fact]
    public void The_file_is_under_the_content_root_and_carries_the_date()
    {
        using var application = new LoggedApplication(LoggedApplication.Settings());

        application.Logger().LogInformation("Written {Marker}", "today");

        // The configured path is logs/test.log. The file output puts the date before the extension.
        var today = DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        Assert.Equal([$"test{today}.log"], application.LogFiles());
    }

    [Fact]
    public void Without_the_file_output_no_file_is_written()
    {
        using var application = new LoggedApplication(LoggedApplication.Settings().Without("Logging:Outputs:File"));

        application.Logger().LogInformation("Written {Marker}", "nowhere on disk");

        Assert.Empty(application.LogFiles());
        Assert.False(Directory.Exists(application.LogFolder));
    }

    [Fact]
    public void Files_beyond_the_retention_limit_are_deleted()
    {
        using var application = new LoggedApplication(
            LoggedApplication.Settings().With("Logging:Outputs:File:RetainedFiles", 3));

        // Five days of earlier files, and one that is not a log file of the application.
        Directory.CreateDirectory(application.LogFolder);
        foreach (var day in new[] { "20200101", "20200102", "20200103", "20200104", "20200105" })
        {
            File.WriteAllText(Path.Combine(application.LogFolder, $"test{day}.log"), "{}");
        }

        File.WriteAllText(Path.Combine(application.LogFolder, "notes.txt"), "kept");

        application.Logger().LogInformation("Written {Marker}", "today");

        // Three files are kept, today's included.
        var today = DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        Assert.Equal(
            ["notes.txt", "test20200104.log", "test20200105.log", $"test{today}.log"],
            application.LogFiles());
    }

    [Fact]
    public void The_minimum_level_and_the_overrides_decide_what_is_written()
    {
        using var application = new LoggedApplication(SettingsJson.Valid()
            .With("Logging:MinimumLevel", "Warning")
            .WithJson("Logging:Overrides", """{ "Ecnanib.Tests.Chatty": "Debug", "Ecnanib.Tests.Chatty.Muted": "Error" }"""));

        var quiet = application.Logger("Ecnanib.Tests.Quiet");
        var chatty = application.Logger("Ecnanib.Tests.Chatty.Part");
        var muted = application.Logger("Ecnanib.Tests.Chatty.Muted.Part");

        // An override applies to an area and everything below it. The longest one that fits wins.
        Assert.False(quiet.IsEnabled(LogLevel.Information));
        Assert.True(quiet.IsEnabled(LogLevel.Warning));
        Assert.False(chatty.IsEnabled(LogLevel.Trace));
        Assert.True(chatty.IsEnabled(LogLevel.Debug));
        Assert.False(muted.IsEnabled(LogLevel.Warning));
        Assert.True(muted.IsEnabled(LogLevel.Error));

        foreach (var logger in new[] { quiet, chatty, muted })
        {
            logger.LogDebug("At {Level}", "debug");
            logger.LogInformation("At {Level}", "information");
            logger.LogWarning("At {Level}", "warning");
            logger.LogError("At {Level}", "error");
        }

        Assert.Equal(
            [
                ("Ecnanib.Tests.Quiet", "warning"),
                ("Ecnanib.Tests.Quiet", "error"),
                ("Ecnanib.Tests.Chatty.Part", "debug"),
                ("Ecnanib.Tests.Chatty.Part", "information"),
                ("Ecnanib.Tests.Chatty.Part", "warning"),
                ("Ecnanib.Tests.Chatty.Part", "error"),
                ("Ecnanib.Tests.Chatty.Muted.Part", "error"),
            ],
            application.Events()
                .Where(written => written.Template == "At {Level}")
                .Select(written => (written.Area!, written.Text("Level")!)));
    }
}
