using Ecnanib.Agents.Abstractions;
using Ecnanib.Agents.Services;
using Ecnanib.Database.Entities;
using Ecnanib.Tests.Support;
using Microsoft.Extensions.Logging;

namespace Ecnanib.Tests.Agents.Services;

/// <summary>In-process events reach the owner's subscribers only, after Publish returns, and a failing handler is logged (FR-AGT-10, NFR-SEC-03).</summary>
public class AgentEventsTests
{
    [Fact]
    public async Task DeliversToTheOwnersSubscribersOnlyAndSurvivesAFailingHandler()
    {
        var logs = new TestLoggerProvider();
        var events = new AgentEvents(TestLogging.CreateFactory(logs).CreateLogger<AgentEvents>());
        var owner = Guid.NewGuid();
        var other = Guid.NewGuid();
        var received = new List<AgentEvent>();
        var otherReceived = new List<AgentEvent>();
        using var failing = events.Subscribe(owner, _ => throw new InvalidOperationException("boom"));
        using var mine = events.Subscribe(owner, e =>
        {
            lock (received)
                received.Add(e);
            return ValueTask.CompletedTask;
        });
        using var theirs = events.Subscribe(other, e =>
        {
            lock (otherReceived)
                otherReceived.Add(e);
            return ValueTask.CompletedTask;
        });
        Assert.Equal(3, events.SubscriberCount);

        events.Publish(new AgentStateChanged(Guid.NewGuid(), owner, DateTimeOffset.UnixEpoch, AgentState.Running, null));

        await Wait.UntilAsync(() => received.Count == 1, "owner notified");
        await Wait.UntilAsync(() => logs.Entries.Any(e => e.Level == LogLevel.Warning && e.Message.StartsWith("An agent event handler failed", StringComparison.Ordinal)), "failure logged");
        await Wait.SettleAsync();
        Assert.Empty(otherReceived);

        mine.Dispose();
        events.Publish(new AgentStateChanged(Guid.NewGuid(), owner, DateTimeOffset.UnixEpoch, AgentState.Paused, null));
        await Wait.SettleAsync();
        Assert.Single(received);
        Assert.Equal(2, events.SubscriberCount);
    }
}
