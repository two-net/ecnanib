using Ecnanib.Agents;
using Ecnanib.Platform.Settings;
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
/// other role gave none ends with no decision and no new entry (FR-TEAM-9). A plan that does
/// what a plan may not do is no valid result (FR-CYC-4 to FR-CYC-7).
/// </para>
/// <para>
/// <b>A decision that is stale opens no position</b> (FR-CYC-9): one that completes later than
/// the stale limit of the agent's timeframe after the close. It is asked when the plan is read,
/// so that a plan which cannot open is not reviewed, and again when the review is. A plan that
/// closes a position or adjusts protection goes on all the same. See <see cref="StaleLimit"/>.
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
    TradingSettings settings,
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

        // Step 4: the Strategist, with both reports. A plan does what a plan may do for the
        // agent as its prompt stated it: the actions of its market, one position, protection
        // that only tightens, and at most the risk that the agent allows (FR-CYC-4 to FR-CYC-7).
        var planning = await roles.RunAsync(
            kept,
            cycle,
            team[Role.Strategist],
            package.ForStrategist(NoLessons, market, sentiment),
            answered => results.ReadPlan(answered, package.Agent),
            cancellationToken);

        if (planning.Result is not { Value: { } plan } planned)
        {
            return NoDecision(cycle, Role.Strategist);
        }

        TeamDecision decision;

        if (!PlanReview.Reviews(plan))
        {
            // It holds, closes a position, or adjusts protection: it is not reviewed, and it is
            // not held to the stale limit either, so nothing stands in the way of reducing risk.
            decision = PlanReview.Of(plan, review: null);
        }
        else if (IsStale(cycle))
        {
            // The plan opens a position, and the decision is stale before it is reviewed: it
            // opens none whatever a review says (FR-CYC-9), so no model is called to review it.
            decision = Stale(cycle, plan);
        }
        else
        {
            // Step 5: the Risk Manager, for a plan that opens a position. It is given the plan,
            // and neither report.
            var reviewing = await roles.RunAsync(
                kept,
                cycle,
                team[Role.RiskManager],
                package.ForRiskManager(NoLessons, planned),
                answered => results.ReadReview(answered, plan),
                cancellationToken);

            if (reviewing.Result is not { Value: { } review })
            {
                return NoDecision(cycle, Role.RiskManager);
            }

            decision = PlanReview.Of(plan, review);

            // The decision completes with the review. A plan that would go on, and is stale by
            // now, opens no position. A veto is a veto, in time or not: nothing goes on.
            if (decision.GoesOn is not null && IsStale(cycle))
            {
                decision = Stale(cycle, plan);
            }
        }

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

    /// <summary>
    /// Whether a decision that completes now is stale: later than the stale limit of the agent's
    /// timeframe after the close that the cycle is for (FR-CYC-9).
    /// </summary>
    private bool IsStale(CycleStart cycle) =>
        StaleLimit.IsStale(cycle.ClosedAt, clock.GetUtcNow(), settings.StaleSeconds.Of(cycle.Agent.Timeframe));

    /// <summary>What becomes of a plan that opens a position when its decision is stale, which is said as a warning.</summary>
    private TeamDecision Stale(CycleStart cycle, TradePlan plan)
    {
        var decision = StaleLimit.Of(plan);

        logger.LogWarning(
            "The decision of agent {AgentId} for the close at {ClosedAt} is stale: it completed {CompletedAfterSeconds} s after the close, " +
            "and the stale limit of {Timeframe} is {StaleSeconds} s. Its plan to {PlanAction} opens no position, and what goes on is {GoesOn}",
            cycle.Agent.Id,
            cycle.ClosedAt,
            Math.Round((clock.GetUtcNow() - cycle.ClosedAt).TotalSeconds, 1),
            cycle.Agent.Timeframe.Code(),
            settings.StaleSeconds.Of(cycle.Agent.Timeframe).TotalSeconds,
            plan.Action,
            decision.GoesOn?.Action.ToString() ?? "nothing");

        return decision;
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
