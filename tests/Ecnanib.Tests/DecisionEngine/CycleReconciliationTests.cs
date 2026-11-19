using Ecnanib.DecisionEngine;
using Ecnanib.Tests.Agents;
using Ecnanib.Tests.Persistence;
using Ecnanib.Tests.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.DecisionEngine;

/// <summary>
/// After a restart the system reconciles first, and a cycle starts only then (FR-CYC-8).
/// Whatever reconciles answers through <see cref="IReconciliation"/>, and an agent that is not
/// reconciled yet gets no cycle. What answers for an agent that holds something from before the
/// start is in <see cref="ReconciliationJobTests"/>. See "After a restart" in docs/cycles.md.
/// </summary>
public class CycleReconciliationTests
{
    private static readonly DateTimeOffset Quarter = new(2026, 11, 16, 10, 15, 0, TimeSpan.Zero);

    [Fact]
    public async Task An_agent_that_is_not_reconciled_yet_gets_no_cycle_and_the_others_get_theirs()
    {
        var reconciliation = new StandInReconciliation();
        using var application = new CyclesApplication(Quarter, reconciliation: reconciliation);
        var agents = application.Agents!;
        var ada = Guid.NewGuid();

        var waiting = agents.Give(ada);
        var reconciled = agents.Give(Guid.NewGuid());

        // The account of one user is not reconciled with the exchange yet.
        reconciliation.NotYet(waiting);

        await application.RunAsync(Quarter).Within();

        // Its agent gets no cycle for this close. No other agent waits for it.
        Assert.Equal([reconciled], application.Cycles.StartedFor);
        Assert.Equal(new[] { waiting, reconciled }.Order(), reconciliation.Asked.Order());

        var skipped = Assert.Single(application.Written(CyclesLog.Area, CyclesLog.NotReconciled));
        Assert.Equal(waiting.ToString(), skipped.Text("AgentId"));
        Assert.Equal(ada.ToString(), skipped.Text("UserId"));

        var summary = application.SummaryOf(Quarter);
        Assert.Equal((1, 1), (summary["Ended"].GetInt32(), summary["NotReconciled"].GetInt32()));

        // Once it is reconciled, the agent gets its cycles.
        reconciliation.Done(waiting);
        await application.RunAsync(Quarter).Within();

        Assert.Contains(waiting, application.Cycles.StartedFor);
    }

    [Fact]
    public async Task With_nothing_that_reconciles_every_agent_gets_its_cycle()
    {
        // As the application of a test of the cycles is: nothing that reconciles is registered.
        using var application = new CyclesApplication(Quarter);
        var agent = application.Agents!.Give(Guid.NewGuid());

        await application.RunAsync(Quarter).Within();

        Assert.Equal([agent], application.Cycles.StartedFor);
    }

    [Fact]
    public void Once_positions_or_orders_are_stored_what_reconciles_them_says_whether_an_agent_is_reconciled()
    {
        using var application = new EcnanibApplicationFactory();
        using var model = ModelOnly.Context();

        // Positions and orders are stored, so something is stored that could differ from the
        // exchange: a cycle that starts before its agent is reconciled would decide on records
        // that the exchange no longer agrees with, after a restart of all times. What settles
        // the orders answers, and so does what reconciles the agents, outside the tests.
        var stored = AgentsRules.TablesOfPositionsOrOrders(model.Model);
        var answers = application.Services.GetServices<IReconciliation>().Any();

        Assert.True(
            answers || stored.Count == 0,
            $"Positions or orders are stored ({string.Join(", ", stored)}), and nothing says whether an agent has been reconciled " +
            "with the exchange since the background work started. After a restart the system reconciles first, and a cycle " +
            $"starts only then (FR-CYC-8, NFR-REL-3). Implement {nameof(IReconciliation)} in the component that reconciles, " +
            "and register it. See docs/cycles.md.");
    }
}
