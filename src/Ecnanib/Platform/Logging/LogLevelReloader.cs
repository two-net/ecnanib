using Ecnanib.Platform.Settings;
using Microsoft.Extensions.Primitives;

namespace Ecnanib.Platform.Logging;

/// <summary>
/// Puts the log levels of the configuration file in force when the file changes, without a
/// restart. Everything else in the file still takes effect at the next start (NFR-CFG-7).
/// </summary>
internal sealed class LogLevelReloader(
    IHostEnvironment environment,
    LogLevels levels,
    ILogger<LogLevelReloader> logger) : IHostedService, IDisposable
{
    // An editor writes a file in several steps. Reading waits until they have stopped.
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(300);

    private readonly Lock _gate = new();

    private Timer? _settled;
    private IDisposable? _watch;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _settled = new Timer(_ => Reload(), state: null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        // The content root's own file provider, so the platform's switch for file watching applies.
        _watch = ChangeToken.OnChange(
            () => environment.ContentRootFileProvider.Watch(SettingsFile.Name),
            RestartTheWait);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _watch?.Dispose();
        _watch = null;

        lock (_gate)
        {
            _settled?.Dispose();
            _settled = null;
        }
    }

    private void RestartTheWait()
    {
        lock (_gate)
        {
            _settled?.Change(Settle, Timeout.InfiniteTimeSpan);
        }
    }

    private void Reload()
    {
        try
        {
            // The whole file is validated, as at startup, and only the levels are taken from it.
            var settings = SettingsFile.Load(Path.Combine(environment.ContentRootPath, SettingsFile.Name));

            if (levels.Apply(settings.Logging))
            {
                logger.LogInformation(
                    "The configuration file changed. Log levels are now: minimum {MinimumLevel}, overrides {Overrides}. " +
                    "Any other change takes effect at the next start",
                    settings.Logging.MinimumLevel,
                    settings.Logging.Overrides);
            }
            else
            {
                logger.LogInformation(
                    "The configuration file changed. Log levels are the same; any other change takes effect at the next start");
            }
        }
        catch (InvalidSettingsException invalid)
        {
            // A problem names a key and a rule, never a value.
            logger.LogWarning(
                "The configuration file changed and is invalid, so log levels stay as they are: {Problems}",
                invalid.Problems.Select(problem => problem.ToString()).ToList());
        }
        catch (Exception error)
        {
            // A timer callback has no caller to report to, and an error in it would end the process.
            logger.LogWarning(error, "The configuration file changed but could not be read, so log levels stay as they are");
        }
    }
}
