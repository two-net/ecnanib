using Ecnanib.Agents;
using Ecnanib.Shared;

namespace Ecnanib.DecisionEngine;

/// <summary>
/// What a cycle does once it has started: it builds its data package, runs the four roles of the
/// agent's team in the order of the specification, validates what each answered, and records
/// every call (SRS 3.2, steps 2 to 5; FR-TEAM-6 to FR-TEAM-9). See docs/team.md.
/// </summary>
/// <remarks>
/// <para>
/// <b>The order.</b> The Market Analyst and the Sentiment Analyst run at the same time. The
/// Strategist runs when both are over, with both reports. The Risk Manager runs after it, for a
/// plan that opens a position: a plan that holds, closes a position, or adjusts protection is
/// not reviewed, so no review stands in the way of reducing risk (FR-TEAM-7).
/// </para>
/// <para>
/// <b>When a role gives no valid result,</b> also when it was called once more: a cycle whose
/// Sentiment Analyst gave none continues with sentiment marked unavailable, and a cycle whose
/// other role gave none ends with no decision and no new entry (FR-TEAM-9).
/// </para>
/// <para>
/// <b>It ends with the decision of the team, and places nothing.</b> The Risk Guard, which
/// checks every plan, and the orders are not built (FR-RSK-3). The only path from what a model
/// answered to an order is schema validation, which is here, then the Risk Guard, then the
/// Exchange Gateway (NFR-SEC-6): this uses nothing of the exchange.
/// </para>
/// <para>
/// <b>A cycle is recorded once, when it ends, however it ends</b> (FR-TEAM-8). One object runs
/// the cycles of every agent, at the same time, so everything of a cycle is local to its run.
/// </para>
/// </remarks>
internal sealed class TeamCycle(
    IDataPackages packages,
    IRoleResults results,
    RoleRuns roles,
    ICycleRecords records,
    TimeProvider clock,
    ILogger<TeamCycle> logger) : IDecisionCycle
{
    /// <summary>
    /// How long the record of a cycle may take to store. A cycle that is stopped ends soon, and
    /// the run that started it waits for it.
    /// </summary>
    public static readonly TimeSpan RecordsWithin = TimeSpan.FromSeconds(5);

    // The lessons that are recalled for the Strategist and the Risk Manager (FR-EXP-7). Nothing
    // keeps lessons yet, so a call is given none.
    private static readonly IReadOnlyList<string> NoLessons = [];

    public async Task RunAsync(CycleStart cycle, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cycle);

        var startedAt = clock.GetUtcNow();
        var kept = new CycleCalls();
        var outcome = CycleOutcome.Failed;

        try
        {
            outcome = await DecideAsync(cycle, kept, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            outcome = CycleOutcome.Stopped;

            throw;
        }
        finally
        {
            // Nothing of a cycle goes on after it: a check of a key that it started is over.
            await kept.SettledAsync();

            await RecordAsync(cycle, kept, startedAt, outcome);
        }
    }

    /// <summary>Runs the team, and says what it decided.</summary>
    private async Task<CycleOutcome> DecideAsync(CycleStart cycle, CycleCalls kept, CancellationToken cancellationToken)
    {
        // An agent runs with its whole team (FR-TEAM-1). Asked before anything is called, so
        // that no model is paid for a cycle that cannot be completed.
        var team = Enum.GetValues<Role>().ToDictionary(
            role => role,
            role => cycle.Roles.SingleOrDefault(given => given.Role == role)
                ?? throw new InvalidOperationException(
                    $"The {role} of the agent has no model, so its team cannot be run. An agent runs with its whole team (FR-TEAM-1)."));

        var package = await packages.BuildAsync(cycle, cancellationToken);

        // Step 3: the two analysts, at the same time. Each is given its own inputs, and neither
        // reads what the other answers.
        var analysing = roles.RunAsync(
            kept, cycle, team[Role.MarketAnalyst], package.ForMarketAnalyst(), results.ReadMarketReport, cancellationToken);
        var searching = roles.RunAsync(
            kept, cycle, team[Role.SentimentAnalyst], package.ForSentimentAnalyst(), results.ReadSentiment, cancellationToken);

        await Task.WhenAll(analysing, searching);

        if ((await analysing).Result is not { } market)
        {
            return NoDecision(cycle, Role.MarketAnalyst);
        }

        var sentiment = SentimentOf(cycle, await searching);

        // Step 4: the Strategist, with both reports. A plan risks at most what the agent allows.
        var planning = await roles.RunAsync(
            kept,
            cycle,
            team[Role.Strategist],
            package.ForStrategist(NoLessons, market, sentiment),
            answered => results.ReadPlan(answered, package.Agent.Limits),
            cancellationToken);

        if (planning.Result is not { Value: { } plan } planned)
        {
            return NoDecision(cycle, Role.Strategist);
        }

        // Step 5: the Risk Manager, for a plan that opens a position. It is given the plan, and
        // neither report.
        RiskReview? review = null;

        if (PlanReview.Reviews(plan))
        {
            var reviewing = await roles.RunAsync(
                kept,
                cycle,
                team[Role.RiskManager],
                package.ForRiskManager(NoLessons, planned),
                answered => results.ReadReview(answered, plan),
                cancellationToken);

            if (reviewing.Result is not { Value: { } reviewed })
            {
                return NoDecision(cycle, Role.RiskManager);
            }

            review = reviewed;
        }

        var decision = PlanReview.Of(plan, review);

        // Steps 6 and 7 are not built: the Risk Guard, which checks what goes on, and the
        // orders. The decision of the team is recorded, and nothing is placed.
        logger.LogInformation(
            "The team of agent {AgentId} decided for the close at {ClosedAt}: {CycleOutcome}, for a plan to {PlanAction}",
            cycle.Agent.Id,
            cycle.ClosedAt,
            decision.Outcome,
            plan.Action);

        return decision.Outcome;
    }

    /// <summary>
    /// The report of the Sentiment Analyst as the Strategist reads it, marked by the service
    /// with whether it rests on live data (FR-CYC-3), or the one that says that there is none
    /// (FR-TEAM-9).
    /// </summary>
    private SentimentReport SentimentOf(CycleStart cycle, RoleRun<SentimentRead> searching)
    {
        if (searching is { Answer: { } answer, Result: not null })
        {
            return SentimentReport.Of(searching.Call, answer);
        }

        logger.LogWarning(
            "The cycle of agent {AgentId} for the close at {ClosedAt} continues with sentiment marked unavailable",
            cycle.Agent.Id,
            cycle.ClosedAt);

        return SentimentReport.Unavailable;
    }

    private CycleOutcome NoDecision(CycleStart cycle, Role role)
    {
        logger.LogWarning(
            "The cycle of agent {AgentId} for the close at {ClosedAt} ended with no decision and no new entry: the {Role} gave no valid result",
            cycle.Agent.Id,
            cycle.ClosedAt,
            role);

        return CycleOutcome.NoDecision;
    }

    /// <summary>
    /// Records the cycle with the calls of its roles. The cycle may have been stopped, so the
    /// write has a time limit of its own, and not the token of the cycle.
    /// </summary>
    private async Task RecordAsync(CycleStart cycle, CycleCalls kept, DateTimeOffset startedAt, CycleOutcome outcome)
    {
        var agent = cycle.Agent;
        var record = new DecisionCycle(cycle.CycleId, agent.Id, agent.UserId, agent.Mode, cycle.ClosedAt, startedAt, clock.GetUtcNow(), outcome);

        using var limit = new CancellationTokenSource(RecordsWithin, clock);

        try
        {
            await records.StoreAsync(record, kept.Outputs, limit.Token);
        }
        catch (Exception error) when (outcome is CycleOutcome.Failed or CycleOutcome.Stopped)
        {
            // The cycle is ending with an error of its own, or is being stopped, which is what
            // the run that started it is told. That its record could not be stored is said here.
            logger.LogError(
                error,
                "The record of the cycle of agent {AgentId} for the close at {ClosedAt} could not be stored ({CycleOutcome})",
                agent.Id,
                cycle.ClosedAt,
                outcome);
        }
    }
}
