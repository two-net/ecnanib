using Ecnanib.Platform.Contracts;

namespace Ecnanib.Modules.Execution;

/// <summary>
/// Reconciles one agent, or every agent with an open position or order, from Execution's records: the key, market and
/// pair come from its newest entry, as the watch's do, so a paused, stopped or errored agent is reconciled like an
/// active one (FR-AGT-06). Each holds the agent's lock, opens its key for this use only (FR-KEY-09), and starts its
/// watch when work is left. See Reconciliation in docs/architecture.md.
/// </summary>
internal sealed class AgentReconciliation(
    AgentLocks locks,
    AgentSessions sessions,
    OrderStore store,
    ProtectionKeeper keeper,
    Reconciler reconciler,
    OrderWatch watch,
    ILogger<AgentReconciliation> logger)
{
    /// <summary>Agents reconciled at once by a sweep, so one slow agent doesn't hold up the rest (NFR-REL-05).</summary>
    public const int Concurrency = 4;

    /// <summary>One agent. Null when it has nothing recorded, or when <paramref name="skipIfBusy"/> and its lock is held.</summary>
    /// <param name="resume">After a restart: protections waiting for a stop get a full window again (NFR-REL-02).</param>
    /// <param name="skipIfBusy">Whoever holds the lock is reconciling anyway, so a sweep moves on.</param>
    /// <exception cref="AgentSessionException">The key can't be used, or Binance no longer lists the pair.</exception>
    public async Task<Reconciliation?> ReconcileAsync(Guid userId, Guid agentId, bool resume, bool skipIfBusy, CancellationToken cancellationToken)
    {
        using var held = skipIfBusy ? locks.TryHold(agentId) : await locks.HoldAsync(agentId, cancellationToken);

        if (held is null)
        {
            return null;
        }

        var latest = await store.FindLatestEntryAsync(userId, agentId, cancellationToken);

        if (latest is null)
        {
            return null;
        }

        using var session = await sessions.OpenAsync(
            userId, agentId, latest.CredentialId, latest.Market, latest.Environment, latest.Symbol, cancellationToken);

        if (resume)
        {
            await keeper.ResumeAsync(session, cancellationToken);
        }

        var result = await reconciler.ReconcileAsync(session, readPosition: false, cancellationToken);

        // Under the lock, so a watch that has just found nothing to do can't miss it.
        if (result.HasWork)
        {
            watch.Ensure(userId, agentId);
        }

        return result;
    }

    /// <summary>
    /// Every agent with an open position or order, a few at a time, each on its own: one that fails is logged and the
    /// rest go on (NFR-REL-05). No agent is started once shutdown begins, and one under way finishes.
    /// </summary>
    public async Task ReconcileEveryoneAsync(bool resume, JobContext context, CancellationToken cancellationToken)
    {
        var agents = await store.ListAgentsToReconcileAsync(cancellationToken);
        using var gate = new SemaphoreSlim(Concurrency);
        var runs = new List<Task<bool>>();

        foreach (var agent in agents)
        {
            if (context.Stopping.IsCancellationRequested)
            {
                break;
            }

            try
            {
                await gate.WaitAsync(context.Stopping);
            }
            catch (OperationCanceledException) when (context.Stopping.IsCancellationRequested)
            {
                break;
            }

            runs.Add(OneAsync(agent, resume, gate, cancellationToken));
        }

        var outcomes = await Task.WhenAll(runs);
        var failed = outcomes.Count(succeeded => !succeeded);

        if (failed > 0)
        {
            logger.LogWarning("{Failed} of {Count} agents couldn't be reconciled, so they're tried again next time", failed, agents.Count);
        }

        context.Details = $"Agents: {agents.Count}, failed: {failed}";
        context.Stopping.ThrowIfCancellationRequested();
    }

    private async Task<bool> OneAsync(AgentKey agent, bool resume, SemaphoreSlim gate, CancellationToken cancellationToken)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object?> { [LogProperties.AgentId] = agent.AgentId });

        try
        {
            var result = await ReconcileAsync(agent.UserId, agent.AgentId, resume, skipIfBusy: !resume, cancellationToken);

            return result is null or { Verified: true };
        }
        catch (AgentSessionException exception) when (exception.Failure == AgentSessionFailure.KeyUnusable)
        {
            // The credential changed since the agent was activated, which its own checks deal with by pausing it.
            logger.LogInformation("The agent's orders can't be reconciled, because its exchange key can't be used");
            return true;
        }
        catch (AgentSessionException exception)
        {
            logger.LogWarning("The agent's orders can't be reconciled: {Failure}", exception.Failure);
            return false;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Reconciling the agent's orders failed");
            return false;
        }
        finally
        {
            gate.Release();
        }
    }
}

/// <summary>
/// Reconciles every agent with an open position or order every <c>Binance:ReconcileIntervalSeconds</c>, whatever its
/// status, so a paused agent's stop that executed is still recorded and notified (FR-EXE-06, FR-AGT-06). An agent
/// whose lock is held is skipped, because its holder reconciles.
/// </summary>
internal sealed class ReconcileSweep(AgentReconciliation reconciliation, ExecutionSettings settings) : IRecurringJob
{
    public const string JobName = "reconcile-sweep";

    public string Name => JobName;

    public JobSchedule Schedule { get; } = JobSchedule.Every(settings.ReconcileInterval);

    /// <summary>A pass of a few seconds per agent, a few at a time, with room to spare.</summary>
    public TimeSpan Timeout { get; } = settings.ReconcileInterval + TimeSpan.FromMinutes(1);

    public Task RunAsync(JobContext context, CancellationToken cancellationToken) =>
        reconciliation.ReconcileEveryoneAsync(resume: false, context, cancellationToken);
}

/// <summary>
/// Reconciles every agent with an open position or order before any scheduled cycle runs (FR-EXE-08): the runner starts
/// recurring jobs only once every hosted service has started, and this one waits for its run. Protections waiting for
/// a stop get a full window again, and each agent's watch starts if it has work. It's bounded, so an unreachable
/// Binance can't hold up startup: each cycle reconciles its own agent anyway (FR-EXE-06).
/// </summary>
internal sealed class ReconcileStartup(OrderStore store, AgentReconciliation reconciliation, IJobRunner runner, ILogger<ReconcileStartup> logger)
    : IHostedService
{
    public const string JobName = "reconcile-startup";

    public static readonly TimeSpan Limit = TimeSpan.FromMinutes(2);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<AgentKey> agents;

        try
        {
            agents = await store.ListAgentsToReconcileAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Startup goes on: each cycle reconciles its agent, and the sweep the rest.
            logger.LogError(exception, "Couldn't find the agents to reconcile at startup");
            return;
        }

        if (agents.Count == 0)
        {
            return;
        }

        var status = await runner.RunAsync(JobName, Limit, (context, token) => reconciliation.ReconcileEveryoneAsync(resume: true, context, token));

        if (status != JobStatus.Succeeded)
        {
            logger.LogWarning("Reconciling {Count} agents at startup ended as {Status}, so cycles start without it", agents.Count, status);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
