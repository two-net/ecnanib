using System.Collections.Concurrent;
using Ecnanib.Agents;
using Ecnanib.ExchangeGateway;
using Ecnanib.Platform.Logging;
using Ecnanib.Platform.Persistence;
using Ecnanib.Shared;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.DecisionEngine;

/// <summary>
/// Runs a test cycle of an agent for its user: one cycle, on demand, that produces a full
/// decision record and places no orders (FR-CYC-12). See "A test cycle" in docs/cycles.md.
/// </summary>
/// <remarks>
/// <para>
/// A test cycle is for the latest closed candle of the agent's timeframe. It builds its data
/// package, runs the team of the agent, and has the Risk Guard check what goes on, as a cycle
/// that a candle close starts does, and is recorded as one, with the kind that tells it apart.
/// It places no order, settles nothing with the exchange, and evaluates no loss limit: nothing
/// changes at the exchange, and nothing halts its agent. A key that a provider refuses in it is
/// checked again, as in every cycle.
/// </para>
/// <para>
/// It takes the user by id, and not from a request: the Decision Engine may not use Accounts.
/// Whatever serves the request hands over the user who is signed in, and an agent of anyone else
/// is not found (NFR-SEC-3).
/// </para>
/// </remarks>
public interface ITestCycles
{
    /// <summary>
    /// Runs a test cycle of an agent of a user, in whatever state the agent is, and returns when
    /// the cycle has ended and is recorded.
    /// </summary>
    /// <param name="userId">The user who is signed in.</param>
    /// <param name="agentId">The agent.</param>
    /// <param name="cancellationToken">
    /// Ends the asking before the cycle starts. A cycle that has started is not given up when
    /// the browser goes away: its models are being called, on the keys of its user. It is
    /// stopped with the application, and when it runs longer than its time limit.
    /// </param>
    /// <returns>What became of it, with the id of the cycle when it was recorded.</returns>
    Task<TestCycleResult> RunAsync(Guid userId, Guid agentId, CancellationToken cancellationToken);
}

/// <summary>What became of a test cycle that a user asked for.</summary>
public enum TestCycleOutcome
{
    /// <summary>The cycle ran and is recorded, however it ended: its record says how.</summary>
    Recorded,

    /// <summary>The user has no agent of that id.</summary>
    NotFound,

    /// <summary>
    /// The team of the agent is not whole, or a role of it cannot work: no cycle was run, and no
    /// model was called (FR-TEAM-1).
    /// </summary>
    RolesBlock,

    /// <summary>A test cycle of the user runs already. One runs at a time, and nothing was started.</summary>
    AlreadyRunning,

    /// <summary>The exchange did not give the candles that the cycle starts with. No cycle was run.</summary>
    NoCandles,

    /// <summary>
    /// An error or a stop ended the cycle, and no record of it was found: it could not be
    /// stored, or whether it was could not be read.
    /// </summary>
    NotRecorded,
}

/// <summary>What became of a test cycle that a user asked for.</summary>
/// <param name="Outcome">What became of it.</param>
/// <param name="CycleId">The id of the cycle, when it was recorded: its timeline shows it.</param>
/// <param name="Roles">The roles that keep the team from working, when that is what stood in the way.</param>
public sealed record TestCycleResult(TestCycleOutcome Outcome, Guid? CycleId = null, IReadOnlyList<RoleBlock>? Roles = null);

/// <summary>Runs the test cycles that users ask for, each inside the request that asked.</summary>
/// <remarks>
/// <para>
/// <b>It starts outside the job that starts the cycles of a close,</b> so it takes no place in
/// the queue of a close, and waits for none (FR-CYC-11). What it adds to the calls of a provider
/// is bounded here instead: a user runs one test cycle at a time, which is two calls with one
/// key at most. The count is kept in memory, by each running instance for itself.
/// </para>
/// <para>
/// <b>A cycle is stopped, and never left behind.</b> It runs inside the request that asked for
/// it, which returns when the cycle has ended. It is stopped with the application, and when it
/// runs longer than <see cref="EndsWithin"/>. A cycle that is stopped is recorded as one.
/// </para>
/// </remarks>
internal sealed class TestCycles(
    IAgentTeams teams,
    ClosedCandles candles,
    IDecisionCycle work,
    IDbContextFactory<EcnanibDbContext> contexts,
    IHostApplicationLifetime lifetime,
    TimeProvider clock,
    ILogger<TestCycles> logger) : ITestCycles
{
    /// <summary>
    /// How long a test cycle may run. The longest cycle is three calls of the team one after
    /// the other, each made twice at most (FR-TEAM-9): with the time limit of a minute of the
    /// tracked file that is six minutes. It is fixed, and is no setting.
    /// </summary>
    internal static readonly TimeSpan EndsWithin = TimeSpan.FromMinutes(10);

    // The users who have a test cycle running, each with the agent that it is of.
    private readonly ConcurrentDictionary<Guid, Guid> _running = new();

    public async Task<TestCycleResult> RunAsync(Guid userId, Guid agentId, CancellationToken cancellationToken)
    {
        // The agent of the user who is signed in, as it is stored now, in whatever state it is.
        var team = await teams.FindAsync(userId, agentId, cancellationToken);

        if (team is null)
        {
            return new TestCycleResult(TestCycleOutcome.NotFound);
        }

        if (team.Blocks.Count > 0)
        {
            // A cycle is run with the whole team (FR-TEAM-1). Asked before anything is called,
            // so that no model is paid for a cycle that cannot be completed.
            logger.LogInformation(
                "User {UserId} was refused a test cycle of agent {AgentId}: {RoleCount} of the roles of its team cannot work ({Roles})",
                userId,
                agentId,
                team.Blocks.Count,
                string.Join(", ", team.Blocks.Select(role => $"{role.Role}: {role.Problem}")));

            return new TestCycleResult(TestCycleOutcome.RolesBlock, Roles: team.Blocks);
        }

        if (!_running.TryAdd(userId, agentId))
        {
            logger.LogInformation(
                "User {UserId} was refused a test cycle of agent {AgentId}: a test cycle of theirs runs already", userId, agentId);

            return new TestCycleResult(TestCycleOutcome.AlreadyRunning);
        }

        try
        {
            return await RunAsync(team, cancellationToken);
        }
        finally
        {
            _running.TryRemove(userId, out _);
        }
    }

    private async Task<TestCycleResult> RunAsync(AgentTeam team, CancellationToken cancellationToken)
    {
        var agent = team.Agent;
        var cycleId = Guid.CreateVersion7(clock.GetUtcNow());

        // Work that a job does not start opens the scope of a cycle as a job does, so that every
        // event of the cycle carries the user, the agent, and the cycle.
        using var scope = logger.BeginCorrelation(new Correlation(
            UserId: agent.UserId.ToString(), AgentId: agent.Id.ToString(), CycleId: cycleId.ToString()));

        // Canceled when the application stops, and when the cycle runs too long. The request
        // that asked does not cancel it once its models are being called.
        using var limit = new CancellationTokenSource(EndsWithin, clock);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping, limit.Token);

        if (await CandlesAsync(agent, stop.Token, cancellationToken) is not var (closedAt, through))
        {
            return new TestCycleResult(TestCycleOutcome.NoCandles);
        }

        var began = clock.GetTimestamp();

        logger.LogInformation(
            "User {UserId} runs a test cycle of agent {AgentId}, which is {AgentState}, for the close at {ClosedAt} ({Timeframe})",
            agent.UserId,
            agent.Id,
            agent.State,
            closedAt,
            agent.Timeframe.Code());

        try
        {
            // The stale limit of its decision counts from now, and not from the close of its
            // candle, which may be hours ago (FR-CYC-9).
            await work.RunAsync(new CycleStart(cycleId, agent, team.Roles, closedAt, through, TestedAt: clock.GetUtcNow()), stop.Token);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            if (lifetime.ApplicationStopping.IsCancellationRequested)
            {
                logger.LogInformation(
                    "The test cycle of agent {AgentId} for the close at {ClosedAt} was stopped after {DurationMs} ms, with the application",
                    agent.Id,
                    closedAt,
                    Milliseconds(began));
            }
            else
            {
                logger.LogWarning(
                    "The test cycle of agent {AgentId} for the close at {ClosedAt} was stopped after {DurationMs} ms: it ran longer than {EndsWithinMinutes} minutes",
                    agent.Id,
                    closedAt,
                    Milliseconds(began),
                    EndsWithin.TotalMinutes);
            }

            // A cycle that is stopped is recorded as one, when its record could still be stored.
            return await RecordedAsync(agent, cycleId, closedAt);
        }
        catch (Exception error)
        {
            // Logged here, once, inside the scope of the cycle, as the job logs a cycle that
            // failed. Its record says that it failed, when it could be stored.
            logger.LogError(
                error,
                "The test cycle of agent {AgentId} for the close at {ClosedAt} failed after {DurationMs} ms, with no decision",
                agent.Id,
                closedAt,
                Milliseconds(began));

            return await RecordedAsync(agent, cycleId, closedAt);
        }

        logger.LogInformation(
            "The test cycle of agent {AgentId} for the close at {ClosedAt} ended in {DurationMs} ms", agent.Id, closedAt, Milliseconds(began));

        return new TestCycleResult(TestCycleOutcome.Recorded, cycleId);
    }

    /// <summary>
    /// The latest close of the agent's timeframe, with the closed candles through it, or null
    /// when the exchange did not give them. It does not throw for an exchange that fails.
    /// </summary>
    private async Task<(DateTimeOffset ClosedAt, ExchangeCandles Through)?> CandlesAsync(
        Agent agent, CancellationToken stop, CancellationToken cancellationToken)
    {
        // Until the cycle starts, the request that asked ends the asking as well.
        using var asking = CancellationTokenSource.CreateLinkedTokenSource(stop, cancellationToken);

        var group = new CandleGroup(agent.Market, agent.Mode, agent.Symbol, agent.Timeframe);

        while (true)
        {
            var closedAt = agent.Timeframe.LatestClose(clock.GetUtcNow());

            ExchangeCandles? through;
            ExchangeCallOutcome last;

            try
            {
                // Asked as the job asks for the candles of a close: just after a close the
                // exchange may not have begun the next candle, and the candles come one short.
                (through, last) = await candles.ThroughAsync(group, closedAt, asking.Token);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                // The application stops before the cycle has started: nothing was run.
                return null;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // A symbol that the configuration file no longer lists, for one.
                logger.LogError(
                    error,
                    "The candles of {Symbol} on {Market} in {TradingMode} could not be asked for ({Timeframe}), for a test cycle of agent {AgentId}",
                    agent.Symbol,
                    agent.Market,
                    agent.Mode,
                    agent.Timeframe.Code(),
                    agent.Id);

                return null;
            }

            if (through is not null)
            {
                return (closedAt, through);
            }

            if (agent.Timeframe.LatestClose(clock.GetUtcNow()) != closedAt)
            {
                // A candle closed while the exchange was asked. The test cycle is for that one.
                continue;
            }

            logger.LogWarning(
                "The exchange did not give the candle of {Symbol} on {Market} in {TradingMode} that closed at {ClosedAt} ({Timeframe}), " +
                "for a test cycle of agent {AgentId}: {ExchangeCallOutcome}",
                agent.Symbol,
                agent.Market,
                agent.Mode,
                closedAt,
                agent.Timeframe.Code(),
                agent.Id,
                last);

            return null;
        }
    }

    /// <summary>
    /// What to answer for a cycle that an error or a stop ended: whether its record is there.
    /// The cycle may have been stopped, so the read has a time limit of its own. It does not
    /// throw: what ended the cycle is said already, and a read that fails beside it, while the
    /// database is away or the application stops, answers that no record was found.
    /// </summary>
    private async Task<TestCycleResult> RecordedAsync(Agent agent, Guid cycleId, DateTimeOffset closedAt)
    {
        try
        {
            using var limit = new CancellationTokenSource(TeamCycle.RecordsWithin, clock);

            await using var context = await contexts.CreateDbContextAsync(limit.Token);

            var recorded = await context.Set<DecisionCycle>()
                .AsNoTracking()
                .OwnedBy(agent.UserId)
                .AnyAsync(cycle => cycle.Id == cycleId && cycle.AgentId == agent.Id, limit.Token);

            return recorded ? new TestCycleResult(TestCycleOutcome.Recorded, cycleId) : new TestCycleResult(TestCycleOutcome.NotRecorded);
        }
        catch (Exception error)
        {
            logger.LogWarning(
                error,
                "Whether the test cycle of agent {AgentId} for the close at {ClosedAt} is recorded could not be read",
                agent.Id,
                closedAt);

            return new TestCycleResult(TestCycleOutcome.NotRecorded);
        }
    }

    private double Milliseconds(long began) => Math.Round(clock.GetElapsedTime(began).TotalMilliseconds, 1);
}
