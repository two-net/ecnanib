using System.Diagnostics;
using System.Text.Json;
using Ecnanib.Tests.Settings;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ecnanib.Tests.Logging;

/// <summary>
/// The application, run in memory with a configuration file of the test's own, together with what
/// it wrote to its log file. Tests read the file because it is a real output: what is in it got
/// through everything the application does to an event.
/// </summary>
internal sealed class LoggedApplication : IDisposable
{
    private readonly EcnanibApplicationFactory _application;

    public LoggedApplication(SettingsJson settings, Action<IWebHostBuilder>? configure = null)
    {
        ContentRoot = new SettingsDirectory(settings);
        _application = new EcnanibApplicationFactory(ContentRoot, configure);
    }

    /// <summary>A valid file that logs from the information level up, to the console and to a file.</summary>
    public static SettingsJson Settings() => SettingsJson.Valid().With("Logging:MinimumLevel", "Information");

    public SettingsDirectory ContentRoot { get; }

    public IServiceProvider Services => _application.Services;

    /// <summary>The folder the configured path <c>logs/test.log</c> puts the log files in.</summary>
    public string LogFolder => Path.Combine(ContentRoot.Path, "logs");

    public HttpClient CreateClient() => _application.CreateClient();

    public ILogger Logger(string area = "Ecnanib.Tests.Logging") =>
        Services.GetRequiredService<ILoggerFactory>().CreateLogger(area);

    /// <summary>The names of the log files, oldest first.</summary>
    public IReadOnlyList<string> LogFiles() =>
        Directory.Exists(LogFolder)
            ? [.. Directory.GetFiles(LogFolder).Select(Path.GetFileName).Order(StringComparer.Ordinal)!]
            : [];

    /// <summary>Everything in the log files, as written.</summary>
    public string LogText() => string.Join(Environment.NewLine, LogLines());

    /// <summary>The events in the log files, in the order they were written.</summary>
    public IReadOnlyList<LoggedEvent> Events() =>
        [.. LogLines().Select(line => new LoggedEvent(JsonDocument.Parse(line).RootElement.Clone()))];

    public void Dispose()
    {
        _application.Dispose();
        ContentRoot.Dispose();
    }

    private List<string> LogLines()
    {
        var lines = new List<string>();

        foreach (var name in LogFiles())
        {
            // The application still has the file open for writing.
            using var file = new FileStream(
                Path.Combine(LogFolder, name), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(file);

            while (reader.ReadLine() is { } line)
            {
                if (line.Length > 0)
                {
                    lines.Add(line);
                }
            }
        }

        return lines;
    }
}

/// <summary>One event as the file output wrote it: a JSON object with the template, the level, and the properties.</summary>
internal sealed record LoggedEvent(JsonElement Json)
{
    public string Template => Json.GetProperty("@mt").GetString()!;

    /// <summary>The level. The file leaves it out for the information level.</summary>
    public string Level => Json.TryGetProperty("@l", out var level) ? level.GetString()! : "Information";

    public string? Exception => Json.TryGetProperty("@x", out var exception) ? exception.GetString() : null;

    public string? Area => Text("SourceContext");

    public JsonElement this[string property] => Json.GetProperty(property);

    public bool Has(string property) => Json.TryGetProperty(property, out _);

    /// <summary>The value of a text property, or null when the event has no such property.</summary>
    public string? Text(string property) =>
        Json.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public override string ToString() => Json.GetRawText();
}

/// <summary>Waits for something the application does in its own time, such as noticing a changed file.</summary>
internal static class Wait
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    /// <param name="condition">What is waited for.</param>
    /// <param name="what">What is waited for, in words, for the failure message.</param>
    /// <param name="again">Repeated every second while waiting, for a cause that can be missed once.</param>
    public static async Task UntilAsync(Func<bool> condition, string what, Action? again = null)
    {
        var waited = Stopwatch.StartNew();
        var nextRepeat = TimeSpan.FromSeconds(1);

        while (!condition())
        {
            Assert.True(waited.Elapsed < Limit, $"Waited {Limit.TotalSeconds} seconds for this, and it did not happen: {what}");

            await Task.Delay(50, TestContext.Current.CancellationToken);

            if (again is not null && waited.Elapsed >= nextRepeat)
            {
                again();
                nextRepeat += TimeSpan.FromSeconds(1);
            }
        }
    }
}
