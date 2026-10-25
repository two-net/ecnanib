using Ecnanib.Agents.Abstractions;

namespace Ecnanib.Tests.Support;

/// <summary>Records every published event and invokes subscribed handlers synchronously, so a test can assert on order.</summary>
internal sealed class FakeAgentEvents : IAgentEvents
{
    private readonly Lock _lock = new();
    private readonly List<AgentEvent> _published = [];
    private readonly List<(Guid UserId, Func<AgentEvent, ValueTask> Handler)> _handlers = [];

    public IReadOnlyList<AgentEvent> Published
    {
        get
        {
            lock (_lock)
                return _published.ToArray();
        }
    }

    public IReadOnlyList<T> Of<T>() where T : AgentEvent => Published.OfType<T>().ToList();

    public IDisposable Subscribe(Guid userId, Func<AgentEvent, ValueTask> handler)
    {
        var entry = (userId, handler);
        lock (_lock)
            _handlers.Add(entry);
        return new Unsubscriber(() =>
        {
            lock (_lock)
                _handlers.Remove(entry);
        });
    }

    public void Publish(AgentEvent agentEvent)
    {
        (Guid UserId, Func<AgentEvent, ValueTask> Handler)[] targets;
        lock (_lock)
        {
            _published.Add(agentEvent);
            targets = _handlers.Where(h => h.UserId == agentEvent.UserId).ToArray();
        }

        foreach (var (_, handler) in targets)
            handler(agentEvent).AsTask().GetAwaiter().GetResult();
    }

    private sealed class Unsubscriber(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
