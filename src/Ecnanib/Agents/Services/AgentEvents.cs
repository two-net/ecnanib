using Ecnanib.Agents.Abstractions;
using Microsoft.Extensions.Logging;

namespace Ecnanib.Agents.Services;

/// <summary>In-process event publication (FR-AGT-10, FR-UI-04). Handlers run on the thread pool after Publish returns; one failing handler never affects another.</summary>
internal sealed class AgentEvents : IAgentEvents
{
    private readonly object _lock = new();
    private readonly List<Subscription> _subscriptions = [];
    private readonly ILogger<AgentEvents> _logger;

    public AgentEvents(ILogger<AgentEvents> logger)
    {
        _logger = logger;
    }

    public int SubscriberCount
    {
        get
        {
            lock (_lock)
                return _subscriptions.Count;
        }
    }

    public IDisposable Subscribe(Guid userId, Func<AgentEvent, ValueTask> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var subscription = new Subscription(this, userId, handler);
        lock (_lock)
            _subscriptions.Add(subscription);
        return subscription;
    }

    public void Publish(AgentEvent agentEvent)
    {
        ArgumentNullException.ThrowIfNull(agentEvent);
        Subscription[] targets;
        lock (_lock)
            targets = _subscriptions.Where(s => s.UserId == agentEvent.UserId).ToArray();
        foreach (var target in targets)
            _ = InvokeAsync(target, agentEvent);
    }

    private async Task InvokeAsync(Subscription subscription, AgentEvent agentEvent)
    {
        try
        {
            await Task.Yield();
            await subscription.Handler(agentEvent).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "An agent event handler failed for agent {AgentId} ({Event})", agentEvent.AgentId, agentEvent.GetType().Name);
        }
    }

    private void Remove(Subscription subscription)
    {
        lock (_lock)
            _subscriptions.Remove(subscription);
    }

    private sealed class Subscription(AgentEvents owner, Guid userId, Func<AgentEvent, ValueTask> handler) : IDisposable
    {
        public Guid UserId { get; } = userId;

        public Func<AgentEvent, ValueTask> Handler { get; } = handler;

        public void Dispose() => owner.Remove(this);
    }
}
