using Ecnanib.Agents;
using Ecnanib.Platform.Settings;
using Ecnanib.RiskGuard;
using Ecnanib.Shared;

namespace Ecnanib.DecisionEngine;

/// <summary>
/// What a cycle does once it has started: it builds its data package, runs the four roles of the
/// agent's team in the order of the specification, validates what each answered, has the Risk
/// Guard check what goes on, hands what the Risk Guard accepted to what places the orders, and
/// records every call and every check (SRS 3.2, steps 2 to 7; FR-TEAM-6 to FR-TEAM-9, FR-RSK-3).
/// See docs/team.md, docs/risk-guard.md, and docs/orders.md.
/// </summary>
/// <remarks>
/// <para>
/// <b>The order.</b> The Market Analyst and the Sentiment Analyst run at the same time. The
/// Strategist runs when both are over, with both reports. The Risk Manager runs after it, for a
/// plan that opens a position: a plan that holds, closes a position, or adjusts protection is
/// not reviewed, so no review stands in the way of reducing risk (FR-TEAM-7).
/// </para>
/// <para>
/// <b>The lessons are recalled once, for the Strategist and the Risk Manager</b> (FR-EXP-7):
/// before the Strategist is called, and the same go to both. The analysts are handed none. See
/// <see cref="RecalledLessons"/>.
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
/// <b>What goes on of the plan is checked by the Risk Guard, after the Risk Manager and before
/// any order</b> (FR-RSK-3): <see cref="PlanChecks"/> gathers the facts and asks the fixed
/// rules, whatever the team decided and whoever the agent belongs to. Nothing here can leave the
/// check out: a plan that goes on is handed to it, and a plan that does not goes nowhere. The
/// outcome of a cycle stays the decision of its team, and what the Risk Guard made of it is in
/// the record beside it.
/// </para>
/// <para>
/// <b>It places no order itself,</b> and uses nothing of the exchange. The only path from what a
/// model answered to an order is schema validation, which is here, then the Risk Guard, then the
/// Exchange Gateway (NFR-SEC-6): what the Risk Guard accepted is handed to
/// <see cref="IPlanOrders"/>, and nothing else is. Before the team is run, the orders of the
/// agent are settled with the exchange, so that the team decides on what the agent holds now: a
/// stop-loss may have been executed since the last cycle.
/// </para>
/// <para>
/// <b>The loss limits of the agent are evaluated before its team is called</b> (FR-RSK-8), once
/// its orders are settled: <see cref="ILossLimits"/> closes the position of an agent that has
/// reached its daily loss limit or its drawdown limit, and halts it. Such a cycle calls no
/// model, and ends as <see cref="CycleOutcome.Halted"/>.
/// </para>
/// <para>
/// <b>The outcome of a cycle is the decision of its team,</b> whatever becomes of its orders:
/// what was placed is in the records of the orders, under the id of the cycle.
/// </para>
/// <para>
/// <b>A cycle is recorded once, when it ends, however it ends</b> (FR-TEAM-8, FR-RSK-9), as a
/// complete record: the summary of its data package, every call of a role, the checks of the
/// Risk Guard, and its outcome, with why it placed no entry where it placed none (FR-CYC-10).
/// Its orders are rows of their own, under its id. One object runs the cycles of every agent,
/// at the same time, so everything of a cycle is local to its run.
/// </para>
/// <para>
/// <b>A test cycle, which its user runs, places no order</b> (FR-CYC-12). It builds its data
/// package, runs its team, and has the Risk Guard check what goes on, as any cycle does, and is
/// recorded as one, with its kind. What the Risk Guard accepted is handed to nothing. It settles
/// no order and evaluates no loss limit either, so nothing changes at the exchange, and no
/// loss limit halts its agent for it. Its stale limit counts from the moment that it was run.
/// See "A test cycle" in docs/cycles.md.
/// </para>
/// </remarks>
internal sealed class TeamCycle(
    IDataPackages packages,
    IRoleResults results,
    RoleRuns roles,
    RecalledLessons recalled,
    PlanChecks checks,
    IPlanOrders orders,
    ILossLimits limits,
    ICycleRecords records,
    RunningCycles progress,
    TradingSettings settings,
    TimeProvider clock,
    ILogger<TeamCycle> logger) : IDecisionCycle
{
    /// <summary>
    /// How long the record of a cycle may take to store. A cycle that is stopped ends soon, and
    /// the run that started it waits for it.
    /// </summary>
    public static readonly TimeSpan RecordsWithin = TimeSpan.FromSeconds(5);

    public async Task RunAsync(CycleStart cycle, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cycle);

        var startedAt = clock.GetUtcNow();
        var kept = new CycleCalls();
        var outcome = CycleOutcome.Failed;
        var decided = false;

        // The cycle runs from here until it is recorded, and says each step that it comes to
        // and what each role of its team does, for the open pages of its user (FR-NTF-2,
        // FR-ANI-1). What it says is kept in memory alone, and for a moment after it has ended.
        using var running = progress.Begin(cycle, startedAt);

        try
        {
            (outcome, var accepted) = await DecideAsync(cycle, kept, running, cancellationToken);
            decided = true;

            // Step 7: what the Risk Guard accepted is what an order is made from, and nothing
            // else goes on to the orders (NFR-SEC-6). Of a test cycle nothing goes on to them
            // at all: it places no order (FR-CYC-12).
            if (accepted is not null && !cycle.IsTest)
            {
                running.At(CycleStep.Orders);

                // Why a plan to open a position placed no entry is kept for the record (FR-CYC-10).
                kept.Keep(await orders.PlaceAsync(cycle, accepted, cancellationToken));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A cycle that is stopped once its team has decided keeps the decision: it is only
            // the orders that were not all placed.
            if (!decided)
            {
                outcome = CycleOutcome.Stopped;
            }

            throw;
        }
        finally
        {
            // Nothing of a cycle goes on after it: a check of a key that it started is over.
            await kept.SettledAsync();

            running.At(CycleStep.Recording);

            await RecordAsync(cycle, kept, startedAt, outcome);
        }
    }

    /// <summary>Runs the team, and says what it decided, with what the Risk Guard accepted of it.</summary>
    private async Task<(CycleOutcome Outcome, GuardResult? Accepted)> DecideAsync(
        CycleStart cycle, CycleCalls kept, RunningCycles.Running running, CancellationToken cancellationToken)
    {
        // An agent runs with its whole team (FR-TEAM-1). Asked before anything is called, so
        // that no model is paid for a cycle that cannot be completed.
        var team = Enum.GetValues<Role>().ToDictionary(
            role => role,
            role => cycle.Roles.SingleOrDefault(given => given.Role == role)
                ?? throw new InvalidOperationException(
                    $"The {role} of the agent has no model, so its team cannot be run. An agent runs with its whole team (FR-TEAM-1)."));

        // A test cycle changes nothing at the exchange, and halts no agent (FR-CYC-12).
        // Settling takes an entry that rests off the exchange, and places the protection that
        // a position lacks, and a loss limit that is reached halts the agent and closes its
        // position: both are left to the cycles that a candle close starts, and to the jobs.
        // Its team decides on what the records of the agent say it holds.
        if (!cycle.IsTest)
        {
            running.At(CycleStep.Settling);

            // What the exchange says of the agent's orders first, so that the data package says
            // what the agent holds now.
            await orders.SettleAsync(cycle.Agent.Id, cancellationToken);

            // The loss limits of the agent are evaluated at every cycle (FR-RSK-8), on what the
            // exchange has just said of its orders, and before any model is called: an agent that
            // has reached one is halted, its position is closed, and its team decides nothing.
            if (await limits.EvaluateAsync(cycle.Agent, cancellationToken) == LossLimitVerdict.Halted)
            {
                logger.LogWarning(
                    "The cycle of agent {AgentId} for the close at {ClosedAt} ended before its team was called: a loss limit halted the agent",
                    cycle.Agent.Id,
                    cycle.ClosedAt);

                return (CycleOutcome.Halted, null);
            }
        }

        running.At(CycleStep.DataPackage);

        var package = await packages.BuildAsync(cycle, cancellationToken);

        // The record of the cycle keeps a summary of what its team decided on, however the
        // cycle ends from here (FR-CYC-10). It keeps no candles (NFR-DAT-6).
        kept.Keep(DataPackageSummary.Of(package, clock.GetUtcNow()));

        // Step 3: the two analysts, at the same time. Each is given its own inputs, and neither
        // reads what the other answers.
        running.At(CycleStep.Analysts);

        // Each says what it gave as it ends, and not when both are over: one analyst is done
        // while the other still works (FR-ANI-1).
        var analysing = SaidAsync(
            running,
            Role.MarketAnalyst,
            roles.RunAsync(kept, cycle, team[Role.MarketAnalyst], package.ForMarketAnalyst(), results.ReadMarketReport, cancellationToken));
        var searching = SaidAsync(
            running,
            Role.SentimentAnalyst,
            roles.RunAsync(kept, cycle, team[Role.SentimentAnalyst], package.ForSentimentAnalyst(), results.ReadSentiment, cancellationToken));

        await Task.WhenAll(analysing, searching);

        if ((await analysing).Result is not { } market)
        {
            return (NoDecision(cycle, Role.MarketAnalyst), null);
        }

        var sentiment = SentimentOf(cycle, await searching);

        // The lessons that the Strategist and the Risk Manager are handed (FR-EXP-7): those of
        // the agent first, then those of its user's other agents on what it trades. Recalled
        // once, so that both read the same. A test cycle recalls them as any cycle does.
        var lessons = await recalled.OfAsync(cycle.Agent, cancellationToken);

        // Step 4: the Strategist, with both reports. A plan does what a plan may do for the
        // agent as its prompt stated it: the actions of its market, one position, protection
        // that only tightens, and at most the risk that the agent allows (FR-CYC-4 to FR-CYC-7).
        running.At(CycleStep.Strategist);

        var planning = await SaidAsync(
            running,
            Role.Strategist,
            roles.RunAsync(
                kept,
                cycle,
                team[Role.Strategist],
                package.ForStrategist(lessons, market, sentiment),
                answered => results.ReadPlan(answered, package.Agent),
                cancellationToken));

        if (planning.Result is not { Value: { } plan } planned)
        {
            return (NoDecision(cycle, Role.Strategist), null);
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
            running.At(CycleStep.RiskManager);

            var reviewing = await SaidAsync(
                running,
                Role.RiskManager,
                roles.RunAsync(
                    kept,
                    cycle,
                    team[Role.RiskManager],
                    package.ForRiskManager(lessons, planned),
                    answered => results.ReadReview(answered, plan),
                    cancellationToken));

            if (reviewing.Result is not { Value: { } review })
            {
                return (NoDecision(cycle, Role.RiskManager), null);
            }

            decision = PlanReview.Of(plan, review);

            // The decision completes with the review. A plan that would go on, and is stale by
            // now, opens no position. A veto is a veto, in time or not: nothing goes on.
            if (decision.GoesOn is not null && IsStale(cycle))
            {
                decision = Stale(cycle, plan);
            }
        }

        logger.LogInformation(
            "The team of agent {AgentId} decided for the close at {ClosedAt}: {CycleOutcome}, for a plan to {PlanAction}",
            cycle.Agent.Id,
            cycle.ClosedAt,
            decision.Outcome,
            plan.Action);

        // Step 6: the Risk Guard checks what goes on, after the Risk Manager and before any
        // order (FR-RSK-3), and computes the size of the order (FR-RSK-5). Every plan that goes
        // on is handed to it, and what it accepted is what an order is made from.
        if (decision.GoesOn is not { } goesOn)
        {
            return (decision.Outcome, null);
        }

        running.At(CycleStep.RiskGuard);

        return (decision.Outcome, await checks.CheckAsync(cycle, goesOn, kept, cancellationToken));
    }

    /// <summary>
    /// Says what a role gave as its run ends, to the open pages of the user of the cycle: a
    /// valid result, or none, also when it was called once more (FR-ANI-1, FR-TEAM-9). A run
    /// that ends with an error says nothing here: the cycle ends with it, and says then that
    /// the role has failed.
    /// </summary>
    private static async Task<RoleRun<T>> SaidAsync<T>(RunningCycles.Running running, Role role, Task<RoleRun<T>> run)
        where T : class
    {
        var gave = await run;

        running.Over(role, valid: gave.Result is not null);

        return gave;
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
    /// timeframe after the close that the cycle is for (FR-CYC-9). For a test cycle the limit
    /// counts from the moment that its user ran it: see <see cref="CycleStart.StaleFrom"/>.
    /// </summary>
    private bool IsStale(CycleStart cycle) =>
        StaleLimit.IsStale(cycle.StaleFrom, clock.GetUtcNow(), settings.StaleSeconds.Of(cycle.Agent.Timeframe));

    /// <summary>What becomes of a plan that opens a position when its decision is stale, which is said as a warning.</summary>
    private TeamDecision Stale(CycleStart cycle, TradePlan plan)
    {
        var decision = StaleLimit.Of(plan);

        // For a test cycle, the seconds are those since its user ran it, which is what its
        // stale limit counts from.
        logger.LogWarning(
            "The decision of agent {AgentId} for the close at {ClosedAt} is stale: it completed {CompletedAfterSeconds} s after the close, " +
            "and the stale limit of {Timeframe} is {StaleSeconds} s. Its plan to {PlanAction} opens no position, and what goes on is {GoesOn}",
            cycle.Agent.Id,
            cycle.ClosedAt,
            Math.Round((clock.GetUtcNow() - cycle.StaleFrom).TotalSeconds, 1),
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
    /// Records the cycle with the summary of its data package, the calls of its roles, the
    /// checks of the Risk Guard, and why it placed no entry (FR-CYC-10). The cycle may have been
    /// stopped, so the write has a time limit of its own, and not the token of the cycle.
    /// </summary>
    private async Task RecordAsync(CycleStart cycle, CycleCalls kept, DateTimeOffset startedAt, CycleOutcome outcome)
    {
        var agent = cycle.Agent;
        var record = new DecisionCycle(
            cycle.CycleId,
            agent.Id,
            agent.UserId,
            agent.Mode,
            cycle.ClosedAt,
            startedAt,
            clock.GetUtcNow(),
            outcome,
            kept.Package?.ToJson(),
            kept.NoEntry,

            // What started it: the record of a test cycle is told apart by it (FR-CYC-12).
            cycle.IsTest ? CycleKind.Test : CycleKind.Scheduled);

        using var limit = new CancellationTokenSource(RecordsWithin, clock);

        try
        {
            await records.StoreAsync(record, kept.Outputs, kept.Guarded, limit.Token);
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
