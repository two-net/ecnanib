using Ecnanib.Platform.Settings;
using Ecnanib.Shared;
using Npgsql;

namespace Ecnanib.Platform.LiveUpdates;

/// <summary>
/// Hears what the database says changed: the notifications that its triggers send on
/// <see cref="LiveUpdates.Channel"/> when a change of a user's rows is committed. See
/// docs/live-updates.md.
/// </summary>
/// <remarks>
/// <para>
/// It listens on a database session of its own, which stays open: a session of the pool would
/// be handed to someone else. It starts with the first page that follows, and not with the
/// application, so an instance that serves no such page keeps no connection for it.
/// </para>
/// <para>
/// It is no job. A job runs on the one instance that holds the lock of the background work,
/// and a page is served by the instance that its connection is to: each running instance
/// listens for its own pages.
/// </para>
/// <para>
/// Nothing that goes wrong here stops the application. A database that is away is waited for,
/// and what it said meanwhile is told as missed when the listener listens again.
/// </para>
/// </remarks>
internal sealed class LiveListener(
    ConnectionStringsSettings connectionStrings,
    TimeProvider clock,
    ILogger<LiveListener> logger) : IAsyncDisposable
{
    /// <summary>How long the listener waits before it connects again, after it lost its connection or found none.</summary>
    public static readonly TimeSpan TriesAgainAfter = TimeSpan.FromSeconds(2);

    // How often the session is asked whether it is still there while nothing is said, in
    // seconds. A connection that broke without saying so is found within this.
    private const int AsksEverySeconds = 15;

    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private Task? _listening;

    /// <summary>Begins to listen, when it does not yet, and tells <paramref name="sink"/> what it hears. It returns at once.</summary>
    public void Start(ILiveSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);

        lock (_gate)
        {
            if (_listening is not null || _stop.IsCancellationRequested)
            {
                return;
            }

            // It begins with the first that follows, which is a request of a page. What it logs
            // from then on is of the instance, and of no request: nothing of the request that it
            // happened to begin with flows into it.
            using (ExecutionContext.SuppressFlow())
            {
                _listening = Task.Run(() => ListenAsync(sink, _stop.Token));
            }
        }
    }

    private async Task ListenAsync(ILiveSink sink, CancellationToken stop)
    {
        // Set by the first report of a loss, so that it is not reported again with every try.
        var troubled = false;

        while (!stop.IsCancellationRequested)
        {
            try
            {
                await using var session = new NpgsqlConnection(
                    new NpgsqlConnectionStringBuilder(connectionStrings.EcnanibDbContext.Reveal())
                    {
                        Pooling = false,
                        KeepAlive = AsksEverySeconds,
                    }.ConnectionString);

                session.Notification += (_, said) => Take(sink, said.Payload);

                await session.OpenAsync(stop);

                // The name of a channel is no value of a statement. It is fixed text.
                await using (var listen = new NpgsqlCommand($"LISTEN {LiveUpdates.Channel}", session))
                {
                    await listen.ExecuteNonQueryAsync(stop);
                }

                if (troubled)
                {
                    logger.LogInformation("The changes that the database says are heard again, so the open pages follow them again");
                    troubled = false;
                }
                else
                {
                    logger.LogInformation("This instance hears the changes that the database says, for the pages that follow them");
                }

                sink.Listening();

                while (true)
                {
                    await session.WaitAsync(stop);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                // The application is stopping.
                return;
            }
            catch (Exception error)
            {
                sink.Lost();

                if (!troubled)
                {
                    troubled = true;

                    // The database is away, most likely. The pages are served as before, and
                    // show what changed when they are asked for again.
                    logger.LogWarning(
                        error,
                        "The changes that the database says cannot be heard, so the open pages follow nothing until they can. " +
                        "This instance tries again every {TriesAgainSeconds} seconds",
                        TriesAgainAfter.TotalSeconds);
                }
            }

            try
            {
                await Task.Delay(TriesAgainAfter, clock, stop);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Reads what a trigger said: the subject, the owner of the row, and the agent that the row
    /// is about, where it is about one. See the migration that adds the triggers.
    /// </summary>
    private void Take(ILiveSink sink, string payload)
    {
        try
        {
            var parts = payload.Split(' ');

            if (parts.Length is 2 or 3
                && Enum.TryParse<LiveSubject>(parts[0], out var subject)
                && Enum.IsDefined(subject)
                && Guid.TryParse(parts[1], out var userId))
            {
                Guid? agentId = parts.Length == 3 && Guid.TryParse(parts[2], out var agent) ? agent : null;

                sink.Hear(userId, new LiveSignal(subject, agentId));

                return;
            }

            logger.LogWarning(
                "The database said a change on {Channel} that could not be read, so it reached no page. It is {PayloadLength} characters long",
                LiveUpdates.Channel,
                payload.Length);
        }
        catch (Exception error)
        {
            // An error that left this would end the session that listens.
            logger.LogError(error, "A change that the database said could not be handed on, so it reached no page");
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task? listening;

        lock (_gate)
        {
            if (_stop.IsCancellationRequested)
            {
                return;
            }

            _stop.Cancel();
            listening = _listening;
        }

        if (listening is not null)
        {
            try
            {
                await listening;
            }
            catch (Exception)
            {
                // It ends by itself, and says what went wrong where it went wrong.
            }
        }

        _stop.Dispose();
    }
}
