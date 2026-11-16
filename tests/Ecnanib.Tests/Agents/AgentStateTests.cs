using Ecnanib.Agents;
using Ecnanib.WebUi.Pages.Agents;

namespace Ecnanib.Tests.Agents;

/// <summary>
/// Agents follow the states of the specification (SRS 3.3, FR-AGT-10): which state runs cycles
/// and opens positions, and which control of its user leads from which state to which. These
/// tests hold the table of the code to the table of the specification, without an application.
/// </summary>
public class AgentStateTests
{
    [Fact]
    public void Each_state_runs_cycles_and_takes_new_entries_as_the_specification_says()
    {
        // The table of section 3.3: the state, whether its cycles run, and whether it opens a
        // new position. Only a running agent does either. An agent is active in every state
        // but Draft and Stopped (FR-AGT-9).
        (AgentState State, bool CyclesRun, bool NewEntries, bool Active)[] table =
        [
            (AgentState.Draft, false, false, false),
            (AgentState.Running, true, true, true),
            (AgentState.Paused, false, false, true),
            (AgentState.Halted, false, false, true),
            (AgentState.AttentionRequired, false, false, true),
            (AgentState.Stopped, false, false, false),
        ];

        Assert.Equal(Enum.GetValues<AgentState>(), table.Select(row => row.State));

        Assert.All(table, row => Assert.Equal(
            (row.CyclesRun, row.NewEntries, row.Active),
            (AgentStates.RunsCycles(row.State), AgentStates.TakesNewEntries(row.State), AgentStates.IsActive(row.State))));

        // An agent needs its key now only while it runs: that is the agent which moves to
        // Attention required when the key goes (FR-KEY-10).
        Assert.Equal([AgentState.Running], Enum.GetValues<AgentState>().Where(AgentStates.NeedsItsKey));
    }

    [Fact]
    public void A_control_leads_from_the_states_it_is_offered_in_and_from_no_other()
    {
        // Every move that a user makes, as "from, by, to". What is not here is not offered.
        (AgentState From, AgentControl By, AgentState To)[] moves =
        [
            (AgentState.Draft, AgentControl.Start, AgentState.Running),
            (AgentState.Stopped, AgentControl.Start, AgentState.Running),
            (AgentState.Running, AgentControl.Pause, AgentState.Paused),
            (AgentState.Paused, AgentControl.Resume, AgentState.Running),
            (AgentState.Halted, AgentControl.Resume, AgentState.Running),
            (AgentState.AttentionRequired, AgentControl.Resume, AgentState.Running),
            (AgentState.Running, AgentControl.Stop, AgentState.Stopped),
            (AgentState.Paused, AgentControl.Stop, AgentState.Stopped),
            (AgentState.Halted, AgentControl.Stop, AgentState.Stopped),
            (AgentState.AttentionRequired, AgentControl.Stop, AgentState.Stopped),
        ];

        foreach (var state in Enum.GetValues<AgentState>())
        {
            foreach (var control in Enum.GetValues<AgentControl>())
            {
                var expected = moves.Where(move => move.From == state && move.By == control).Select(move => (AgentState?)move.To).SingleOrDefault();

                Assert.Equal(expected, AgentStates.After(state, control));

                // A control leads to one state, wherever it is offered.
                if (expected is { } to)
                {
                    Assert.Equal(to, AgentStates.LeadsTo(control));
                }
            }

            Assert.Equal(moves.Where(move => move.From == state).Select(move => move.By), AgentStates.Offered(state));
        }

        // Every state leads somewhere, and every agent that was started and is not stopped can be stopped.
        Assert.All(Enum.GetValues<AgentState>(), state => Assert.NotEmpty(AgentStates.Offered(state)));
        Assert.All(
            Enum.GetValues<AgentState>().Where(AgentStates.IsActive),
            state => Assert.Contains(AgentControl.Stop, AgentStates.Offered(state)));

        // A draft is not stopped: it was never started. It is deleted.
        Assert.DoesNotContain(AgentControl.Stop, AgentStates.Offered(AgentState.Draft));
    }

    [Fact]
    public void The_causes_of_a_state_are_stored_by_these_names()
    {
        // Pinned: a member is stored by its name, in the row of the agent and in the changes of
        // its state. Renaming one changes stored data and needs a migration.
        Assert.Equal(
            [
                "Created",
                "UserStarted",
                "UserPaused",
                "UserResumed",
                "UserStopped",
                "UserStoppedClosingPosition",
                "UserStoppedKeepingPosition",
                "ExchangeKeyDeleted",
                "ExchangeKeyInvalid",
            ],
            Enum.GetNames<AgentStateCause>());
    }

    [Fact]
    public void Every_state_cause_and_control_is_said_in_plain_language()
    {
        // Where a state appears it is explained (NFR-USA-2), and a change of state is shown
        // with its cause (FR-AGT-10): each has words of its own, in sentences.
        var states = Enum.GetValues<AgentState>().Select(AgentPages.Explain).ToList();
        var causes = Enum.GetValues<AgentStateCause>().Select(AgentPages.Says).ToList();
        var controls = Enum.GetValues<AgentControl>().Select(AgentPages.Explain).ToList();

        Assert.All(states.Concat(causes).Concat(controls), text =>
        {
            Assert.EndsWith(".", text);
            Assert.DoesNotContain("AttentionRequired", text);
        });

        Assert.Equal(states.Count, states.Distinct().Count());
        Assert.Equal(causes.Count, causes.Distinct().Count());

        // What the table of the specification says of an open position, in the words of the pages.
        Assert.Contains("runs no cycle and trades nothing", AgentPages.Explain(AgentState.Draft));
        Assert.Contains("manages the open one", AgentPages.Explain(AgentState.Running));
        Assert.Contains("stays open with its stop-loss and take-profit orders", AgentPages.Explain(AgentState.Paused));
        Assert.Contains("position was closed", AgentPages.Explain(AgentState.Halted));
        Assert.Contains("stays open with its stop-loss and take-profit orders", AgentPages.Explain(AgentState.AttentionRequired));
        Assert.Contains("closed or kept, as you chose", AgentPages.Explain(AgentState.Stopped));

        // Every state that runs no cycle says so, and that it opens no position.
        Assert.All(
            Enum.GetValues<AgentState>().Where(state => state is not (AgentState.Draft or AgentState.Running)),
            state => Assert.Contains("runs no cycle and opens no position", AgentPages.Explain(state)));

        // Each control has a form of its own, and a button that says what it does.
        Assert.Equal(4, Enum.GetValues<AgentControl>().Select(AgentPages.FormOf).Distinct().Count());
        Assert.Equal(
            ["Start the agent", "Pause the agent", "Resume the agent", "Stop the agent"],
            Enum.GetValues<AgentControl>().Select(AgentPages.Describe));
    }
}
