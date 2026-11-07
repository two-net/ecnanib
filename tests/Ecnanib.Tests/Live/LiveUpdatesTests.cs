using Ecnanib.Components.Live;
using Ecnanib.Modules.Agents.Contracts;
using Ecnanib.Modules.DecisionCycle.Contracts;
using Ecnanib.Modules.Execution.Contracts;
using Ecnanib.Modules.Notifications.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ecnanib.Tests.Live;

/// <summary>
/// FR-UI-05 and NFR-SEC-04: the Web UI's side of the modules' events passes each change to its own user's open pages,
/// and to nobody else's, until a page leaves. One page that fails to take a change can't stop the rest, or the work
/// that made it.
/// </summary>
public sealed class LiveUpdatesTests
{
    [Fact]
    public void Each_change_reaches_only_its_own_users_pages_until_they_leave()
    {
        var live = new LiveUpdates(NullLogger<LiveUpdates>.Instance);
        var (user, stranger, agent) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var heard = new List<LiveEvent>();
        var overheard = new List<LiveEvent>();

        var page = live.Subscribe(user, heard.Add);
        using var other = live.Subscribe(stranger, overheard.Add);
        var progress = new CycleProgress(Guid.NewGuid(), user, agent, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, null, null, []);

        ((ICycleObserver)live).OnChanged(progress);
        ((IAgentObserver)live).OnChanged(user, agent);
        ((IExecutionObserver)live).OnChanged(user, agent);
        ((INotificationObserver)live).OnAdded(user);

        Assert.Equal(
            [
                new LiveEvent(LiveChange.Cycle, agent, progress),
                new LiveEvent(LiveChange.Agent, agent),
                new LiveEvent(LiveChange.Execution, agent),
                new LiveEvent(LiveChange.Notification, null),
            ],
            heard);
        Assert.Empty(overheard);

        page.Dispose();
        page.Dispose();
        ((IAgentObserver)live).OnChanged(user, agent);

        Assert.Equal(4, heard.Count);
        Assert.Equal(0, live.CountFor(user));
        Assert.Equal(1, live.CountFor(stranger));
    }

    [Fact]
    public void A_page_that_throws_doesnt_stop_the_others_or_the_change()
    {
        var live = new LiveUpdates(NullLogger<LiveUpdates>.Instance);
        var (user, agent) = (Guid.NewGuid(), Guid.NewGuid());
        var heard = 0;

        using var broken = live.Subscribe(user, _ => throw new InvalidOperationException("A page that broke."));
        using var page = live.Subscribe(user, _ => heard++);

        ((IExecutionObserver)live).OnChanged(user, agent);

        Assert.Equal(1, heard);
    }
}
