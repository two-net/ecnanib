using Ecnanib.Kernel;
using Ecnanib.Modules.Agents.Contracts;
using Ecnanib.Modules.Catalog.Contracts;
using Ecnanib.Modules.Credentials.Contracts;
using Ecnanib.Modules.Notifications.Contracts;
using Ecnanib.Platform.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Modules.Agents;

/// <summary>
/// Acts on the catalog's deprecations and retirements for every agent that isn't archived (FR-CAT-04, UC-07).
/// A model that's Deprecated warns the agents that use it. One that's Retired moves each role it fills to the
/// role's fallback, when the fallback can fill the role. An Active agent with a required role that's still on a
/// retired model pauses. The owner is told once per agent, model and status. See Acting on retired models in
/// docs/architecture.md.
/// </summary>
internal sealed class ModelLifecycle(
    IDbContextFactory<EcnanibDbContext> contextFactory,
    ICatalog catalog,
    ICredentials credentials,
    RoleModels roleModels,
    INotifications notifications,
    IAuditTrail audit,
    TimeProvider time,
    AgentObservers observers,
    ILogger<ModelLifecycle> logger)
{
    /// <summary>The one-off run that follows a change to models' statuses.</summary>
    public const string ChangeJobName = "agent-model-check";

    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Every agent that isn't archived and uses a Deprecated or Retired model it hasn't been told about, user by
    /// user. The event and the hourly recovery both run this.
    /// </summary>
    public async Task ApplyForEveryoneAsync(JobContext job, CancellationToken cancellationToken)
    {
        var listed = await catalog.ListAsync(cancellationToken);
        var lapsed = listed
            .Where(model => model.Status is ModelStatus.Deprecated or ModelStatus.Retired)
            .ToDictionary(model => new ModelRef(model.Provider, model.ModelId), model => model.Status);

        await ForgetReinstatedAsync(
            [.. listed.Where(model => model.Status == ModelStatus.Active).Select(model => new ModelRef(model.Provider, model.ModelId))],
            cancellationToken);

        var pending = await PendingAsync(lapsed, cancellationToken);
        var (switched, paused, told, failed) = (0, 0, 0, 0);

        foreach (var user in pending.GroupBy(agent => agent.UserId))
        {
            job.Stopping.ThrowIfCancellationRequested();

            UserCatalog models;

            // One user's failure never stops the others (NFR-REL-05).
            try
            {
                models = await roleModels.ReadAsync(user.Key, await credentials.ListAsync(user.Key, cancellationToken), cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failed += user.Count();
                logger.LogError(exception, "Reading the models of user {UserId} failed", user.Key);
                continue;
            }

            foreach (var agentId in user.Select(agent => agent.Id))
            {
                try
                {
                    var outcome = await ApplyAsync(agentId, user.Key, models, cancellationToken);
                    switched += outcome.Switched;
                    paused += outcome.Paused ? 1 : 0;
                    told += outcome.Told ? 1 : 0;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    failed++;
                    logger.LogError(exception, "Acting on the retired models of agent {AgentId} of user {UserId} failed", agentId, user.Key);
                }
            }
        }

        job.Details = $"Agents {pending.Count}, roles switched {switched}, paused {paused}, told {told}, failed {failed}";
    }

    /// <summary>
    /// One agent, under the user's lock and in one transaction, worked out again from the agent as it is now:
    /// switch, pause, then tell. <paramref name="models"/> is the user's catalog, read before the lock.
    /// </summary>
    private async Task<Outcome> ApplyAsync(Guid agentId, Guid userId, UserCatalog models, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await AgentLock.TakeAsync(context, userId, cancellationToken);

        var agent = await context.Set<Agent>()
            .AsNoTracking()
            .Include(row => row.Roles)
            .SingleOrDefaultAsync(row => row.Id == agentId && row.UserId == userId && row.Status != AgentStatus.Archived, cancellationToken);

        if (agent is null)
        {
            return Outcome.None;
        }

        var told = (await context.Set<AgentModelNotice>()
                .Where(notice => notice.AgentId == agentId)
                .Select(notice => new { notice.Provider, notice.ModelId, notice.Status })
                .ToListAsync(cancellationToken))
            .Select(notice => (new ModelRef(notice.Provider, notice.ModelId), notice.Status))
            .ToHashSet();
        var news = Uses(agent)
            .Select(model => (Model: model, Status: StatusOf(models, model)))
            .Where(use => use.Status is ModelStatus.Deprecated or ModelStatus.Retired && !told.Contains((use.Model, use.Status.Value)))
            .Select(use => (use.Model, Status: use.Status!.Value))
            .Distinct()
            .ToList();

        if (news.Count == 0)
        {
            return Outcome.None;
        }

        var now = time.GetUtcNow();
        var version = agent.Version;
        var switched = await SwitchAsync(context, agent, models, now, cancellationToken);

        if (switched is null)
        {
            // The agent changed since it was read, which the lock should prevent. The next run sees it as it is.
            return Outcome.None;
        }

        version += switched.Count > 0 ? 1 : 0;

        var stillRetired = agent.Roles
            .Where(role => AgentRoles.IsRequired(role.Role) && !switched.Contains(role.Role))
            .Any(role => StatusOf(models, new ModelRef(role.Provider, role.ModelId)) == ModelStatus.Retired);
        var paused = agent.Status == AgentStatus.Active
            && stillRetired
            && await AgentPause.TryAsync(context, audit, userId, agentId, version, AgentStatusReason.ModelRetired, now, cancellationToken);

        foreach (var (model, status) in news)
        {
            context.Set<AgentModelNotice>().Add(new AgentModelNotice
            {
                AgentId = agentId,
                Provider = model.Provider,
                ModelId = model.ModelId,
                Status = status,
                CreatedAt = now,
            });
        }

        // One of each per agent, however many of its models changed at once.
        var demo = agent.Environment == TradingEnvironment.Demo;

        foreach (var status in news.Select(use => use.Status).Distinct().Order())
        {
            var type = status == ModelStatus.Retired ? NotificationType.ModelRetired : NotificationType.ModelDeprecated;
            notifications.Add(context, new Notice(type, userId, $"/agents/{agentId}/settings", agent.Name, demo));
        }

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        observers.Changed(userId, agentId);

        logger.LogInformation(
            "Told the owner of agent {AgentId} of user {UserId} about {Models} models, switched {Switched} roles, paused {Paused}",
            agentId, userId, news.Count, switched.Count, paused);

        return new Outcome(switched.Count, paused, Told: true);
    }

    /// <summary>
    /// Each role whose model retired takes its fallback, when the fallback can fill it as activation requires,
    /// and is left without one. Null when the agent changed since it was read.
    /// </summary>
    private async Task<IReadOnlySet<AiRole>?> SwitchAsync(
        EcnanibDbContext context, Agent agent, UserCatalog models, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var switched = new HashSet<AiRole>();
        var changes = new List<AuditChange>();

        foreach (var role in agent.Roles.OrderBy(role => role.Role))
        {
            var model = new ModelRef(role.Provider, role.ModelId);

            if (StatusOf(models, model) != ModelStatus.Retired
                || role.ToAssignment().Fallback is not { } fallback
                || models.ProblemOf(fallback) is not null)
            {
                continue;
            }

            await context.Set<AgentRoleAssignment>()
                .Where(row => row.AgentId == agent.Id && row.Role == role.Role)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(row => row.Provider, fallback.Provider)
                        .SetProperty(row => row.ModelId, fallback.ModelId)
                        .SetProperty(row => row.FallbackProvider, (string?)null)
                        .SetProperty(row => row.FallbackModelId, (string?)null),
                    cancellationToken);

            switched.Add(role.Role);
            changes.Add(new AuditChange(AgentAuditFields.RoleField(role.Role), model.ToString(), fallback.ToString()));
            changes.Add(new AuditChange(AgentAuditFields.FallbackField(role.Role), fallback.ToString(), null));
        }

        if (switched.Count == 0)
        {
            return switched;
        }

        var updated = await context.Set<Agent>()
            .Where(row => row.Id == agent.Id && row.UserId == agent.UserId && row.Version == agent.Version)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(row => row.UpdatedAt, now)
                    .SetProperty(row => row.Version, row => row.Version + 1),
                cancellationToken);

        if (updated == 0)
        {
            return null;
        }

        audit.Add(context, AgentAudit.BySystem(AuditAction.AgentUpdated, agent.UserId, agent.Id, changes));

        return switched;
    }

    /// <summary>A model that's Active again forgets what its agents were told, so a later deprecation tells them again.</summary>
    private async Task ForgetReinstatedAsync(HashSet<ModelRef> active, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var noticed = (await context.Set<AgentModelNotice>()
                .Select(notice => new { notice.Provider, notice.ModelId })
                .Distinct()
                .ToListAsync(cancellationToken))
            .Select(notice => new ModelRef(notice.Provider, notice.ModelId));

        foreach (var model in noticed.Where(active.Contains))
        {
            await context.Set<AgentModelNotice>()
                .Where(notice => notice.Provider == model.Provider && notice.ModelId == model.ModelId)
                .ExecuteDeleteAsync(cancellationToken);
        }
    }

    /// <summary>The agents that aren't archived and use a lapsed model they haven't been told about yet.</summary>
    private async Task<List<Agent>> PendingAsync(IReadOnlyDictionary<ModelRef, ModelStatus> lapsed, CancellationToken cancellationToken)
    {
        if (lapsed.Count == 0)
        {
            return [];
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // By ID first, which the database can do, then exactly, with the provider.
        var ids = lapsed.Keys.Select(model => model.ModelId).Distinct().ToList();
        var agents = await context.Set<Agent>()
            .AsNoTracking()
            .Include(agent => agent.Roles)
            .Where(agent => agent.Status != AgentStatus.Archived
                && agent.Roles.Any(role => ids.Contains(role.ModelId) || (role.FallbackModelId != null && ids.Contains(role.FallbackModelId))))
            .ToListAsync(cancellationToken);
        var agentIds = agents.Select(agent => agent.Id).ToList();
        var told = (await context.Set<AgentModelNotice>()
                .Where(notice => agentIds.Contains(notice.AgentId))
                .Select(notice => new { notice.AgentId, notice.Provider, notice.ModelId, notice.Status })
                .ToListAsync(cancellationToken))
            .Select(notice => (notice.AgentId, new ModelRef(notice.Provider, notice.ModelId), notice.Status))
            .ToHashSet();

        return
        [
            .. agents
                .Where(agent => Uses(agent).Any(model => lapsed.TryGetValue(model, out var status) && !told.Contains((agent.Id, model, status))))
                .OrderBy(agent => agent.UserId)
                .ThenBy(agent => agent.CreatedAt),
        ];
    }

    /// <summary>Every model the agent names, as a role's model or its fallback.</summary>
    private static IEnumerable<ModelRef> Uses(Agent agent)
    {
        foreach (var assignment in agent.Roles.Select(role => role.ToAssignment()))
        {
            yield return assignment.Model;

            if (assignment.Fallback is { } fallback)
            {
                yield return fallback;
            }
        }
    }

    private static ModelStatus? StatusOf(UserCatalog models, ModelRef model) =>
        models.Models.TryGetValue(model, out var listed) ? listed.Status : null;

    private sealed record Outcome(int Switched, bool Paused, bool Told)
    {
        public static Outcome None { get; } = new(0, false, false);
    }
}

/// <summary>
/// Acts on the agents that use a model as soon as a sync changes its status. It runs as a job, so the sync
/// doesn't wait. A job is lost with the process, and <see cref="ModelLifecycleJob"/> recovers it.
/// </summary>
internal sealed class AgentModelObserver(ModelLifecycle lifecycle, IJobRunner jobs) : IModelStatusObserver
{
    public void OnChanged(IReadOnlyList<ModelStatusChange> changes) =>

        // RunAsync never throws for the work's failure, so the task can be left to run.
        _ = jobs.RunAsync(ModelLifecycle.ChangeJobName, ModelLifecycle.Timeout, lifecycle.ApplyForEveryoneAsync);
}

/// <summary>
/// Every hour, does what <see cref="AgentModelObserver"/> does, in case the event was lost. It reads only the
/// catalog and the agents.
/// </summary>
internal sealed class ModelLifecycleJob(ModelLifecycle lifecycle) : IRecurringJob
{
    public const string JobName = "agent-models";

    public string Name => JobName;

    public JobSchedule Schedule { get; } = JobSchedule.Every(TimeSpan.FromHours(1));

    public TimeSpan Timeout => ModelLifecycle.Timeout;

    public Task RunAsync(JobContext context, CancellationToken cancellationToken) => lifecycle.ApplyForEveryoneAsync(context, cancellationToken);
}
