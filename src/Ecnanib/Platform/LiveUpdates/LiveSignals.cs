using Ecnanib.Shared;

namespace Ecnanib.Platform.LiveUpdates;

/// <summary>Registers the signals that the open pages follow. See docs/live-updates.md.</summary>
public static class LiveUpdates
{
    /// <summary>
    /// The channel that the triggers of the database notify on, and that the listener of each
    /// running instance listens to. The migration that adds the triggers names it as well.
    /// </summary>
    public const string Channel = "ecnanib_live";

    /// <summary>
    /// Registers the signals, and the listener that hears what the database says. Nothing
    /// connects here: the listener starts with the first page that follows.
    /// </summary>
    internal static IServiceCollection AddLiveUpdates(this IServiceCollection services)
    {
        services.AddSingleton<LiveListener>();
        services.AddSingleton<LiveSignals>();
        services.AddSingleton<ILiveSignals>(provider => provider.GetRequiredService<LiveSignals>());

        return services;
    }
}

/// <summary>What the listener tells what it hears, and how it stands.</summary>
internal interface ILiveSink
{
    /// <summary>A change of a user that the database said.</summary>
    void Hear(Guid userId, LiveSignal signal);

    /// <summary>The listener listens from now on. What the database said before was not heard.</summary>
    void Listening();

    /// <summary>The listener lost its connection. What the database says until it listens again is not heard.</summary>
    void Lost();
}

/// <summary>
/// Hands each signal to what follows its user, on this instance, and keeps the latest of them
/// for what begins to follow a moment after it read. See <see cref="ILiveSignals"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Signals are counted,</b> and what follows says the count that it read at. A clock would
/// not do: the database says a change at its own time, and two changes may come within one
/// tick of the application's.
/// </para>
/// <para>
/// <b>What was not heard is said as missed.</b> The database is listened to from the first page
/// that follows, and not while the connection of the listener is away. Each time the listener
/// begins to listen, every follower is told <see cref="LiveSubject.Missed"/>, and so is what
/// begins to follow with a count from before then.
/// </para>
/// <para>
/// <b>A follower is called by whoever raised the signal:</b> the listener, or a cycle that
/// runs. What a follower throws is logged, and reaches neither the others nor whoever raised.
/// </para>
/// <para>
/// Everything is in memory, for this process: a signal reaches the pages that this instance
/// serves. Each running instance has a listener of its own, so what the database says reaches
/// the pages of every instance.
/// </para>
/// </remarks>
internal sealed class LiveSignals(LiveListener listener, TimeProvider clock, ILogger<LiveSignals> logger) : ILiveSignals, ILiveSink
{
    /// <summary>
    /// For how long a signal is kept for what begins to follow after it. A page follows within
    /// a second or two of being written, once its connection is made.
    /// </summary>
    public static readonly TimeSpan Kept = TimeSpan.FromSeconds(30);

    /// <summary>The most signals that are kept, whatever their age: what comes after a larger burst is told that it missed something.</summary>
    public const int MostKept = 10_000;

    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, List<Follower>> _followers = [];
    private readonly Queue<Heard> _recent = new();

    // How many signals were heard so far, with one more for each time the listener began to listen.
    private long _heard;

    // The count from which every change of the database was heard, or null while the listener
    // does not listen.
    private long? _whole;

    // Every signal with a number above this one is still kept.
    private long _keptFrom;

    public long Mark()
    {
        listener.Start(this);

        lock (_gate)
        {
            return _heard;
        }
    }

    public IDisposable Follow(Guid userId, long since, Action<LiveSignal> heard)
    {
        ArgumentNullException.ThrowIfNull(heard);

        var follower = new Follower(this, userId, heard);
        IReadOnlyList<LiveSignal> missed;

        lock (_gate)
        {
            if (!_followers.TryGetValue(userId, out var ofUser))
            {
                _followers[userId] = ofUser = [];
            }

            ofUser.Add(follower);
            Forget(clock.GetUtcNow());

            if (_whole is not { } whole)
            {
                // The listener tells every follower that it missed something when it listens.
                missed = [];
            }
            else if (since < whole || since < _keptFrom || since > _heard)
            {
                // Before the listener listened, longer ago than signals are kept, or a count
                // that is not of this process: of another instance, or from before a restart.
                missed = [new LiveSignal(LiveSubject.Missed, AgentId: null)];
            }
            else
            {
                missed = [.. _recent.Where(kept => kept.UserId == userId && kept.Number > since).Select(kept => kept.Signal).Distinct()];
            }
        }

        listener.Start(this);

        foreach (var signal in missed)
        {
            Tell(follower, signal);
        }

        return follower;
    }

    public void Raise(Guid userId, Guid? agentId, LiveSubject subject)
    {
        try
        {
            Hear(userId, new LiveSignal(subject, agentId));
        }
        catch (Exception error)
        {
            // Whoever raises a signal is doing something else, such as running a cycle.
            logger.LogError(error, "A signal about {LiveSubject} could not be handed to what follows its user", subject);
        }
    }

    public void Hear(Guid userId, LiveSignal signal)
    {
        Follower[] told;

        lock (_gate)
        {
            var now = clock.GetUtcNow();

            _recent.Enqueue(new Heard(++_heard, now, userId, signal));
            Forget(now);

            told = _followers.TryGetValue(userId, out var ofUser) ? [.. ofUser] : [];
        }

        foreach (var follower in told)
        {
            Tell(follower, signal);
        }
    }

    public void Listening()
    {
        Follower[] told;

        lock (_gate)
        {
            // A count of its own, so that what read just before is told that it missed something.
            _whole = ++_heard;

            told = [.. _followers.Values.SelectMany(ofUser => ofUser)];
        }

        foreach (var follower in told)
        {
            Tell(follower, new LiveSignal(LiveSubject.Missed, AgentId: null));
        }
    }

    public void Lost()
    {
        lock (_gate)
        {
            _whole = null;
        }
    }

    /// <summary>Lets go of the signals that are kept too long, and of the oldest where there are too many.</summary>
    private void Forget(DateTimeOffset now)
    {
        while (_recent.Count > MostKept || (_recent.Count > 0 && now - _recent.Peek().At > Kept))
        {
            _keptFrom = _recent.Dequeue().Number;
        }
    }

    private void Tell(Follower follower, LiveSignal signal)
    {
        try
        {
            follower.Heard(signal);
        }
        catch (Exception error)
        {
            logger.LogError(
                error,
                "What follows the signals of user {UserId} failed on a signal about {LiveSubject}. The signal reached nothing there",
                follower.UserId,
                signal.Subject);
        }
    }

    private void Unfollow(Follower follower)
    {
        lock (_gate)
        {
            if (_followers.TryGetValue(follower.UserId, out var ofUser) && ofUser.Remove(follower) && ofUser.Count == 0)
            {
                _followers.Remove(follower.UserId);
            }
        }
    }

    /// <summary>A signal as it is kept: its number, when it was heard, and whose it is.</summary>
    private sealed record Heard(long Number, DateTimeOffset At, Guid UserId, LiveSignal Signal);

    /// <summary>What follows a user, until it is disposed of.</summary>
    private sealed class Follower(LiveSignals signals, Guid userId, Action<LiveSignal> heard) : IDisposable
    {
        public Guid UserId { get; } = userId;

        public Action<LiveSignal> Heard { get; } = heard;

        public void Dispose() => signals.Unfollow(this);
    }
}
