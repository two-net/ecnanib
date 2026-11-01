using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ecnanib.Platform.Contracts;
using Ecnanib.Tests.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog.Extensions.Logging;

namespace Ecnanib.Tests.Logging;

/// <summary>FR-LOG-01, FR-LOG-02 and FR-LOG-04 on the real sinks.</summary>
public sealed class LoggingSinkTests(PostgresFixture postgres)
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(LogLevel.Trace, "Verbose")]
    [InlineData(LogLevel.Debug, "Debug")]
    [InlineData(LogLevel.Information, "Information")]
    [InlineData(LogLevel.Warning, "Warning")]
    [InlineData(LogLevel.Error, "Error")]
    [InlineData(LogLevel.Critical, "Fatal")]
    public async Task Every_level_reaches_a_sink_under_its_srs_name(LogLevel level, string expected)
    {
        await using var app = new LoggedApp(postgres.NewConnectionString(), new() { ["Logging:MinimumLevel"] = "Verbose" });

        app.Logger("Ecnanib.Tests.Levels").Log(level, "Level check {Name}", "value");
        await app.StopAsync();

        var logged = app.FileEvents().Single(entry => entry.GetProperty("MessageTemplate").GetString() == "Level check {Name}");

        // Serilog's JSON leaves out Information, as the default.
        Assert.Equal(expected, logged.TryGetProperty("Level", out var name) ? name.GetString() : "Information");
        Assert.Equal("value", logged.GetProperty("Properties").GetProperty("Name").GetString());
    }

    [Fact]
    public async Task The_database_sink_has_its_own_minimum_level()
    {
        await using var app = new LoggedApp(
            postgres.NewConnectionString(),
            new() { ["Logging:Sinks:Database:MinimumLevel"] = "Warning" });

        var logger = app.Logger("Ecnanib.Tests.DatabaseLevel");
        logger.LogInformation("markerInformation");
        logger.LogWarning("markerWarning");
        await app.StopAsync();

        var table = await app.TableTextAsync(Cancellation);

        Assert.Contains("markerInformation", app.FileText(), StringComparison.Ordinal);
        Assert.DoesNotContain("markerInformation", table, StringComparison.Ordinal);
        Assert.Contains("markerWarning", table, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_database_row_keeps_its_fields()
    {
        var database = postgres.NewConnectionString();
        await using var app = new LoggedApp(database);

        using (app.Logger("Ecnanib.Tests.Row").BeginScope(new Dictionary<string, object?> { [LogProperties.CorrelationId] = "abc123" }))
        {
            app.Logger("Ecnanib.Tests.Row").LogError(new InvalidOperationException("It failed"), "Row {Number}", 7);
        }

        await app.StopAsync();

        var row = (await Sql.ColumnAsync(
            database,
            """
            SELECT concat_ws('|', level, category, message, correlation_id, properties ->> 'Number',
                extract(timezone FROM timestamp AT TIME ZONE 'UTC' AT TIME ZONE 'UTC'))
            FROM log_events WHERE correlation_id = 'abc123'
            """,
            Cancellation)).Single();

        Assert.StartsWith("Error|Ecnanib.Tests.Row|Row 7|abc123|7|", row, StringComparison.Ordinal);
        Assert.Contains(
            "It failed",
            await Sql.ScalarAsync<string>(database, "SELECT exception FROM log_events WHERE correlation_id = 'abc123'", Cancellation),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_nul_character_doesnt_cost_the_database_its_batch()
    {
        await using var app = new LoggedApp(postgres.NewConnectionString());

        var logger = app.Logger("Ecnanib.Tests.Nul");
        logger.LogError("markerNul {Value}", "before\0after");
        logger.LogError("markerNeighbour");
        await app.StopAsync();

        var table = await app.TableTextAsync(Cancellation);

        Assert.Contains("markerNul", table, StringComparison.Ordinal);
        Assert.Contains("markerNeighbour", table, StringComparison.Ordinal);
        Assert.Contains("beforeafter", table, StringComparison.Ordinal);
    }

    /// <summary>With EF logging at its most verbose, a write that logged itself would feed the next batch.</summary>
    [Fact]
    public async Task The_database_sink_doesnt_log_its_own_writes()
    {
        await using var app = new LoggedApp(
            postgres.NewConnectionString(),
            new()
            {
                ["Logging:MinimumLevel"] = "Verbose",
                ["Logging:Overrides:Microsoft.EntityFrameworkCore"] = "Verbose",
            });

        app.Logger("Ecnanib.Tests.Feedback").LogError("markerFeedback");
        await app.StopAsync();

        var table = await app.TableTextAsync(Cancellation);

        Assert.Contains("markerFeedback", table, StringComparison.Ordinal);
        Assert.DoesNotContain("INSERT INTO log_events", app.FileText(), StringComparison.Ordinal);
        Assert.DoesNotContain("INSERT INTO log_events", table, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Files_roll_by_day_under_a_dated_name()
    {
        await using var app = new LoggedApp(postgres.NewConnectionString());

        app.Logger("Ecnanib.Tests.Daily").LogError("markerDaily");
        await app.StopAsync();

        Assert.Matches(new Regex(@"ecnanib-\d{8}\.log$"), Assert.Single(app.Files()));
    }

    [Fact]
    public async Task Files_roll_by_size_and_keep_only_the_retained_count()
    {
        await using var app = new LoggedApp(
            postgres.NewConnectionString(),
            new()
            {
                ["Logging:Sinks:File:RollingInterval"] = "Size",
                ["Logging:Sinks:File:FileSizeLimitBytes"] = "2000",
                ["Logging:Sinks:File:RetainedFileCount"] = "2",
            });

        var logger = app.Logger("Ecnanib.Tests.Rolling");

        for (var number = 0; number < 200; number++)
        {
            logger.LogError("Filling the log with event {Number}", number);
        }

        await app.StopAsync();

        Assert.Equal(2, app.Files().Count);
        Assert.Contains("199", app.FileText(), StringComparison.Ordinal);
        Assert.DoesNotContain("Filling the log with event 0\"", app.FileText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Events_carry_every_field_in_FR_LOG_04()
    {
        await using var app = new LoggedApp(postgres.NewConnectionString());

        var logger = app.Logger("Ecnanib.Tests.Enrichment");
        var scope = new Dictionary<string, object?>
        {
            [LogProperties.CorrelationId] = "correlation",
            [LogProperties.UserId] = "user",
            [LogProperties.AgentId] = "agent",
            [LogProperties.CycleId] = "cycle",
            [LogProperties.Environment] = "Demo",
        };

        using (logger.BeginScope(scope))
        {
            logger.LogError("markerEnriched");
        }

        await app.StopAsync();

        var logged = app.FileEvents().Single(entry => entry.GetProperty("MessageTemplate").GetString() == "markerEnriched");
        var properties = logged.GetProperty("Properties");

        Assert.Equal("Error", logged.GetProperty("Level").GetString());
        Assert.Equal(TimeSpan.Zero, logged.GetProperty("Timestamp").GetDateTimeOffset().Offset);
        Assert.Equal("Ecnanib.Tests.Enrichment", Text(properties, "SourceContext"));
        Assert.Equal("correlation", Text(properties, LogProperties.CorrelationId));
        Assert.Equal("user", Text(properties, LogProperties.UserId));
        Assert.Equal("agent", Text(properties, LogProperties.AgentId));
        Assert.Equal("cycle", Text(properties, LogProperties.CycleId));
        Assert.Equal("Demo", Text(properties, LogProperties.Environment));
        Assert.False(string.IsNullOrEmpty(Text(properties, "ApplicationVersion")));
        Assert.Equal(Environment.MachineName, Text(properties, "MachineName"));
    }

    /// <summary>The default providers would see every event before masking.</summary>
    [Fact]
    public async Task Serilog_is_the_only_logging_provider()
    {
        await using var app = new AppFactory(postgres.NewConnectionString());
        app.Start();

        Assert.IsType<SerilogLoggerProvider>(Assert.Single(app.Services.GetServices<ILoggerProvider>()));
    }

    /// <summary>First_start_logs_no_errors relies on this, and would pass vacuously without it.</summary>
    [Fact]
    public async Task A_provider_injected_by_a_test_still_receives_events()
    {
        var recorder = new Recorder();
        await using var app = new AppFactory(
            postgres.NewConnectionString(),
            services => services.AddSingleton<ILoggerProvider>(recorder));
        app.Start();

        app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Ecnanib.Tests.Injected").LogError("markerInjected");

        Assert.Contains("markerInjected", recorder.Messages);
    }

    private static string? Text(JsonElement properties, string name) => properties.GetProperty(name).GetString();

    private sealed class Recorder : ILoggerProvider, ILogger
    {
        public ConcurrentQueue<string> Messages { get; } = new();

        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Enqueue(formatter(state, exception));

        public void Dispose()
        {
        }
    }
}
