using System.Text;
using System.Text.Json;
using Ecnanib.Tests.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ecnanib.Tests.Logging;

/// <summary>
/// The real application, with its sinks pointed somewhere a test can read them. Sinks write in the
/// background, so call StopAsync before asserting on what reached them.
/// </summary>
internal sealed class LoggedApp : IAsyncDisposable
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("ecnanib-logs-");
    private readonly string _connectionString;
    private readonly Dictionary<string, string?> _settings;

    public LoggedApp(
        string connectionString,
        Dictionary<string, string?>? settings = null,
        Action<IServiceCollection>? configureServices = null,
        Action<IConfigurationBuilder>? configure = null)
    {
        _connectionString = connectionString;

        var allSettings = new Dictionary<string, string?>
        {
            ["Logging:Sinks:Console:Enabled"] = "false",
            ["Logging:Sinks:File:Enabled"] = "true",
            ["Logging:Sinks:File:Path"] = Path.Combine(_directory.FullName, "ecnanib-.log"),
            ["Logging:Sinks:File:RetainedFileCount"] = "31",
            ["Logging:Sinks:Database:Enabled"] = "true",
            ["Logging:Sinks:Database:MinimumLevel"] = "Verbose",
        };

        foreach (var (key, value) in settings ?? [])
        {
            allSettings[key] = value;
        }

        _settings = allSettings;
        App = new AppFactory(connectionString, configureServices, allSettings, configure);
        App.Start();
    }

    public AppFactory App { get; }

    public ILogger Logger(string category) =>
        App.Services.GetRequiredService<ILoggerFactory>().CreateLogger(category);

    /// <summary>Changes a setting the way a saved file does. The change applies before this returns.</summary>
    public void Change(string key, string? value)
    {
        var configuration = (IConfigurationRoot)App.Services.GetRequiredService<IConfiguration>();

        _settings[key] = value;
        configuration[key] = value;
        configuration.Reload();
    }

    /// <summary>
    /// Stops the application and waits for its sinks. Disposing the factory isn't enough: under a
    /// test host app.Run() is on another thread, which disposes the host too, and the second caller
    /// returns at once while the first is still flushing. A real process has no such race. Events
    /// keep their order, so once a last event has landed, so has everything before it.
    /// </summary>
    public async Task StopAsync()
    {
        var last = $"last{Guid.NewGuid():N}";

        Logger("Ecnanib.Tests.LastEvent").LogCritical("{Last}", last);
        await App.DisposeAsync();

        await UntilAsync(async () =>
            (!IsEnabled("File") || FileText().Contains(last, StringComparison.Ordinal))
            && (!IsEnabled("Database") || (await TableTextAsync(TestContext.Current.CancellationToken)).Contains(last, StringComparison.Ordinal)));
    }

    /// <summary>Polls, for things that happen on another thread.</summary>
    public static async Task UntilAsync(Func<ValueTask<bool>> condition)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(Deadline);

        while (!await condition())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50), deadline.Token);
        }
    }

    public IReadOnlyList<string> Files() =>
        [.. _directory.GetFiles("ecnanib-*.log").OrderBy(file => file.Name).Select(file => file.FullName)];

    /// <summary>Everything in the log files, which may still be open.</summary>
    public string FileText()
    {
        var text = new StringBuilder();

        foreach (var file in Files())
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            text.AppendLine(reader.ReadToEnd());
        }

        return text.ToString();
    }

    public List<JsonElement> FileEvents() =>
    [
        .. FileText()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => JsonDocument.Parse(line).RootElement),
    ];

    /// <summary>Everything in the log table, in one string to scan.</summary>
    public async Task<string> TableTextAsync(CancellationToken cancellationToken)
    {
        var rows = await Sql.ColumnAsync(
            _connectionString,
            "SELECT concat_ws(' ', level, category, message, exception, correlation_id, properties::text) FROM log_events ORDER BY id",
            cancellationToken);

        return string.Join('\n', rows);
    }

    public async ValueTask DisposeAsync()
    {
        await App.DisposeAsync();
        _directory.Delete(recursive: true);
    }

    private bool IsEnabled(string sink) =>
        bool.Parse(_settings[$"Logging:Sinks:{sink}:Enabled"] ?? "false");
}
