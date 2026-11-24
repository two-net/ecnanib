using Ecnanib.Platform.LiveUpdates;
using Ecnanib.Shared;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.Tests.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.LiveUpdates;

/// <summary>
/// A user receives updates only for their own agents (FR-NTF-2): a signal is of one user, and is
/// handed to what follows that user, and to nothing else. What begins to follow is told what
/// was heard since it read, so nothing is missed between the two. See docs/live-updates.md.
/// </summary>
public class LiveSignalTests
{
    private static readonly DateTimeOffset Morning = new(2026, 11, 24, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task What_follows_a_user_hears_the_signals_of_that_user_and_of_no_other()
    {
        using var application = new LoggedApplication(LoggedApplication.Settings());
        var signals = application.Services.GetRequiredService<ILiveSignals>();
        await signals.ListensAsync();

        var (ada, grace) = (Guid.NewGuid(), Guid.NewGuid());
        var agent = Guid.NewGuid();
        using var ofAda = new Following(signals, ada);
        using var ofGrace = new Following(signals, grace);

        signals.Raise(ada, agent, LiveSubject.CycleStep);
        signals.Raise(grace, agentId: null, LiveSubject.Notification);
        signals.Raise(ada, agent, LiveSubject.CycleStep);

        // Each is told at once, by whoever raised the signal, and in the order of the signals.
        Assert.Equal([new LiveSignal(LiveSubject.CycleStep, agent), new LiveSignal(LiveSubject.CycleStep, agent)], ofAda.Heard);
        Assert.Equal([new LiveSignal(LiveSubject.Notification, null)], ofGrace.Heard);
    }

    [Fact]
    public async Task Each_page_of_a_user_that_follows_hears_a_signal_of_theirs()
    {
        using var application = new LoggedApplication(LoggedApplication.Settings());
        var signals = application.Services.GetRequiredService<ILiveSignals>();
        await signals.ListensAsync();

        var ada = Guid.NewGuid();
        using var one = new Following(signals, ada);
        using var another = new Following(signals, ada);

        signals.Raise(ada, agentId: null, LiveSubject.Agent);

        Assert.Equal([new LiveSignal(LiveSubject.Agent, null)], one.Heard);
        Assert.Equal(one.Heard, another.Heard);
    }

    [Fact]
    public async Task What_no_longer_follows_hears_nothing()
    {
        using var application = new LoggedApplication(LoggedApplication.Settings());
        var signals = application.Services.GetRequiredService<ILiveSignals>();
        await signals.ListensAsync();

        var ada = Guid.NewGuid();
        var gone = new Following(signals, ada);
        using var stays = new Following(signals, ada);

        gone.Dispose();
        signals.Raise(ada, agentId: null, LiveSubject.Agent);

        Assert.Empty(gone.Heard);
        Assert.Single(stays.Heard);
    }

    [Fact]
    public async Task A_follower_that_fails_keeps_the_signal_from_nobody_else_and_fails_nobody()
    {
        using var application = new LoggedApplication(LoggedApplication.Settings());
        var signals = application.Services.GetRequiredService<ILiveSignals>();
        await signals.ListensAsync();

        var ada = Guid.NewGuid();
        using var failing = signals.Follow(ada, signals.Mark(), _ => throw new InvalidOperationException("The page is gone."));
        using var stays = new Following(signals, ada);

        // Whoever raises a signal is doing something else, such as running a cycle.
        signals.Raise(ada, agentId: null, LiveSubject.CycleStep);

        Assert.Equal([new LiveSignal(LiveSubject.CycleStep, null)], stays.Heard);

        var said = Assert.Single(application.Events(), written => written.Level == "Error");
        Assert.Equal(ada.ToString(), said.Text("UserId"));
        Assert.Equal("CycleStep", said.Text("LiveSubject"));
    }

    [Fact]
    public async Task What_begins_to_follow_is_told_what_was_heard_since_it_read_once_each()
    {
        using var application = new LoggedApplication(LoggedApplication.Settings());
        var signals = application.Services.GetRequiredService<ILiveSignals>();
        await signals.ListensAsync();

        var (ada, grace) = (Guid.NewGuid(), Guid.NewGuid());
        var agent = Guid.NewGuid();

        signals.Raise(ada, agent, LiveSubject.Holdings);

        // A page is written, and its connection is made a moment later.
        var read = signals.Mark();

        signals.Raise(ada, agent, LiveSubject.Agent);
        signals.Raise(ada, agent, LiveSubject.Agent);
        signals.Raise(grace, agentId: null, LiveSubject.Notification);
        signals.Raise(ada, agentId: null, LiveSubject.Notification);

        using var late = new Following(signals, ada, since: read);

        // What was heard before the read is on the page already. What was heard after it is
        // told, a signal that says the same twice once, and that of another user not at all.
        Assert.Equal([new LiveSignal(LiveSubject.Agent, agent), new LiveSignal(LiveSubject.Notification, null)], late.Heard);
    }

    [Fact]
    public async Task What_read_before_the_application_listened_is_told_that_it_missed_something()
    {
        using var application = new LoggedApplication(LoggedApplication.Settings());
        var signals = application.Services.GetRequiredService<ILiveSignals>();

        // The first page of an application: nothing listened while it was written.
        var read = signals.Mark();
        using var first = new Following(signals, Guid.NewGuid(), since: read);

        await Wait.UntilAsync(() => first.Heard.Count > 0, "the first follower to be told that it missed something");

        Assert.Equal([LiveFixtures.Missed], first.Heard);
    }

    [Fact]
    public async Task A_count_that_is_not_of_this_instance_is_told_that_it_missed_something()
    {
        using var application = new LoggedApplication(LoggedApplication.Settings());
        var signals = application.Services.GetRequiredService<ILiveSignals>();
        await signals.ListensAsync();

        // A page that another instance wrote, or this one before it was started again.
        using var elsewhere = new Following(signals, Guid.NewGuid(), since: signals.Mark() + 1000);

        Assert.Equal([LiveFixtures.Missed], elsewhere.Heard);
    }

    [Fact]
    public async Task What_read_longer_ago_than_signals_are_kept_is_told_that_it_missed_something()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        var signals = application.LiveSignals;
        await signals.ListensAsync();

        var (ada, grace) = (Guid.NewGuid(), Guid.NewGuid());
        var read = signals.Mark();

        signals.Raise(ada, agentId: null, LiveSubject.Agent);

        // Within the time, it is told the signal itself.
        clock.Set(Morning.AddSeconds(30));
        signals.Raise(grace, agentId: null, LiveSubject.Agent);

        using (var inTime = new Following(signals, ada, since: read))
        {
            Assert.Equal([new LiveSignal(LiveSubject.Agent, null)], inTime.Heard);
        }

        // After it, the signal is no longer kept, and what read before it cannot be told what it was.
        clock.Set(Morning.AddSeconds(31));
        signals.Raise(grace, agentId: null, LiveSubject.Agent);

        using var tooLate = new Following(signals, ada, since: read);

        Assert.Equal([LiveFixtures.Missed], tooLate.Heard);
    }
}
