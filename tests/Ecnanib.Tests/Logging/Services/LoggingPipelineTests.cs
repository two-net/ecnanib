using System.Globalization;
using Ecnanib.Logging.Configuration;
using Ecnanib.Logging.Services;
using Ecnanib.Tests.Support;
using Microsoft.Extensions.Logging;
using Serilog.Events;
using Serilog.Extensions.Logging;
using Serilog.Formatting.Display;

namespace Ecnanib.Tests.Logging.Services;

/// <summary>FR-LOG-01 and FR-LOG-03 through the ILogger bridge: one root, two sinks, each with its own minimum and the shared category levels.</summary>
public class LoggingPipelineTests
{
    [Fact]
    public void EachSinkGetsItsOwnMinimum()
    {
        var options = new LoggingOptions { Console = { MinimumLevel = "Debug" }, Database = { MinimumLevel = "Warning" } };
        var (factory, console, database) = Build(options);
        using var bridge = factory;
        var logger = factory.CreateLogger("Ecnanib.Agents.Services.AgentService");

        logger.LogTrace("t");
        logger.LogDebug("d");
        logger.LogInformation("i");
        logger.LogWarning("w");

        Assert.Equal(["d", "i", "w"], console.Messages);
        Assert.Equal(["w"], database.Messages);
        Assert.False(logger.IsEnabled(LogLevel.Trace));
        Assert.True(logger.IsEnabled(LogLevel.Debug));
    }

    [Fact]
    public void CategoryLevelsApplyToBothSinksAndReplaceTheirMinimums()
    {
        var options = new LoggingOptions
        {
            Console = { MinimumLevel = "Information" },
            Database = { MinimumLevel = "Information" },
            LogLevel = { ["Microsoft.EntityFrameworkCore"] = "Error", ["Ecnanib.Council"] = "Trace" },
        };
        var (factory, console, database) = Build(options);
        using var bridge = factory;
        var ef = factory.CreateLogger("Microsoft.EntityFrameworkCore.Database.Command");
        var council = factory.CreateLogger("Ecnanib.Council.Services.Council");
        var agents = factory.CreateLogger("Ecnanib.Agents.Services.AgentScheduler");

        ef.LogWarning("ef warning");
        ef.LogError("ef error");
        council.LogTrace("council trace");
        agents.LogDebug("agents debug");
        agents.LogInformation("agents info");

        Assert.Equal(["ef error", "council trace", "agents info"], console.Messages);
        Assert.Equal(["ef error", "council trace", "agents info"], database.Messages);
        Assert.False(ef.IsEnabled(LogLevel.Warning));
        Assert.True(council.IsEnabled(LogLevel.Trace));
        Assert.False(agents.IsEnabled(LogLevel.Debug));
    }

    [Fact]
    public void TheFallbackPipelineWritesInformationToTheConsoleOnly()
    {
        var console = new CollectingLogEventSink();
        var serilog = LoggingPipeline.ConfigureFallback(new Serilog.LoggerConfiguration(), sinks => sinks.Sink(console)).CreateLogger();
        using var factory = new SerilogLoggerFactory(serilog, dispose: true);
        var logger = factory.CreateLogger("Ecnanib.Database.Migrations.DatabaseMigrator");

        logger.LogDebug("hidden");
        logger.LogCritical("Configuration is invalid");

        Assert.Equal(["Configuration is invalid"], console.Messages);
        Assert.False(logger.IsEnabled(LogLevel.Debug));
    }

    [Fact]
    public void TheConsoleLineHasTimestampLevelComponentAndMessage()
    {
        var formatter = new MessageTemplateTextFormatter(LoggingPipeline.ConsoleOutputTemplate, CultureInfo.InvariantCulture);
        using var writer = new StringWriter(CultureInfo.InvariantCulture);

        formatter.Format(SinkLevelFilterTests.Event(LogEventLevel.Information, "Ecnanib.Agents.Services.AgentService", "Agent started"), writer);

        Assert.Equal("2026-01-05 10:00:00.000 +00:00 [INF] Ecnanib.Agents.Services.AgentService: Agent started" + Environment.NewLine, writer.ToString());
    }

    private static (ILoggerFactory Factory, CollectingLogEventSink Console, CollectingLogEventSink Database) Build(LoggingOptions options)
    {
        var console = new CollectingLogEventSink();
        var database = new CollectingLogEventSink();
        var serilog = LoggingPipeline.Configure(new Serilog.LoggerConfiguration(), options, sinks => sinks.Sink(console), sinks => sinks.Sink(database)).CreateLogger();
        return (new SerilogLoggerFactory(serilog, dispose: true), console, database);
    }
}
