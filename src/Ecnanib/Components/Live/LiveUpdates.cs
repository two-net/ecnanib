using Ecnanib.Modules.Agents.Contracts;
using Ecnanib.Modules.DecisionCycle.Contracts;
using Ecnanib.Modules.Execution.Contracts;
using Ecnanib.Modules.Notifications.Contracts;

namespace Ecnanib.Components.Live;

/// <summary>What changed for a user. Each says only what to read again, apart from a cycle, which carries its progress.</summary>
public enum LiveChange
{
    /// <summary>A decision cycle started, moved on a role or finished (FR-ROLE-08).</summary>
    Cycle,

    /// <summary>An agent's status or settings changed, or it was created or deleted.</summary>
    Agent,

    /// <summary>An agent's orders, position or trades changed.</summary>
    Execution,

    /// <summary>The user has a new notification.</summary>
    Notification,
}

/// <param name="AgentId">The agent it's about, or null for a notification.</param>
/// <param name="Cycle">A cycle's progress as it stands, for <see cref="LiveChange.Cycle"/>.</param>
public sealed record LiveEvent(LiveChange Change, Guid? AgentId, CycleProgress? Cycle = null);

/// <summary>
/// The Web UI's side of the modules' events (FR-UI-05). One instance hears every user's changes, after they commit, and
/// passes each to the circuits of the user it's for. A page subscribes when its circuit starts, and reads again what an
/// event names: the events carry no data a page could show stale or another user's. They're lost on a crash, so a page
/// reads everything when it opens. Nothing here calls .NET from a browser, so a page nobody touches still times out.
/// See Live updates in docs/architecture.md.
/// </summary>
public sealed class LiveUpdates(ILogger<LiveUpdates> logger) : ICycleObserver, IAgentObserver, IExecutionObserver, INotificationObserver
{
    private readonly Lock _lock = new();

    /// <summary>Copied on change, so raising reads an array no subscriber can change under it.</summary>
    private readonly Dictionary<Guid, Subscriber[]> _users = [];

    /// <summary>
    /// Sends <paramref name="handler"/> each change for <paramref name="userId"/> until the returned subscription is
    /// disposed, which a component must do when it's disposed. It's called on the thread that made the change, so it
    /// must return at once: a component hands it to <c>InvokeAsync</c>. One that throws is logged and changes nothing
    /// else.
    /// </summary>
    public IDisposable Subscribe(Guid userId, Action<LiveEvent> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        var subscriber = new Subscriber(handler);

        lock (_lock)
        {
            _users[userId] = _users.TryGetValue(userId, out var current) ? [.. current, subscriber] : [subscriber];
        }

        return new Subscription(() => Unsubscribe(userId, subscriber));
    }

    /// <summary>How many subscriptions the user has, for tests.</summary>
    public int CountFor(Guid userId)
    {
        lock (_lock)
        {
            return _users.TryGetValue(userId, out var current) ? current.Length : 0;
        }
    }

    void ICycleObserver.OnChanged(CycleProgress progress) => Raise(progress.UserId, new LiveEvent(LiveChange.Cycle, progress.AgentId, progress));

    void IAgentObserver.OnChanged(Guid userId, Guid agentId) => Raise(userId, new LiveEvent(LiveChange.Agent, agentId));

    void IExecutionObserver.OnChanged(Guid userId, Guid agentId) => Raise(userId, new LiveEvent(LiveChange.Execution, agentId));

    void INotificationObserver.OnAdded(Guid userId) => Raise(userId, new LiveEvent(LiveChange.Notification, null));

    private void Unsubscribe(Guid userId, Subscriber subscriber)
    {
        lock (_lock)
        {
            if (!_users.TryGetValue(userId, out var current))
            {
                return;
            }

            var remaining = current.Where(other => other != subscriber).ToArray();

            if (remaining.Length == 0)
            {
                _users.Remove(userId);
            }
            else
            {
                _users[userId] = remaining;
            }
        }
    }

    /// <summary>On the thread that made the change, which may be protective work, so nothing here waits or throws.</summary>
    private void Raise(Guid userId, LiveEvent change)
    {
        Subscriber[]? subscribers;

        lock (_lock)
        {
            _users.TryGetValue(userId, out subscribers);
        }

        foreach (var subscriber in subscribers ?? [])
        {
            try
            {
                subscriber.Handler(change);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "A page failed to take a live {Change}", change.Change);
            }
        }
    }

    private sealed class Subscriber(Action<LiveEvent> handler)
    {
        public Action<LiveEvent> Handler { get; } = handler;
    }

    private sealed class Subscription(Action unsubscribe) : IDisposable
    {
        private Action? _unsubscribe = unsubscribe;

        public void Dispose() => Interlocked.Exchange(ref _unsubscribe, null)?.Invoke();
    }
}
