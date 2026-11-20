using System.Globalization;
using Ecnanib.Notifications;
using Ecnanib.Platform.Persistence;
using Ecnanib.Platform.Settings;
using Ecnanib.Shared;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Agents;

/// <summary>
/// Gives the roles of the agents that opted in other models by strength, on its own
/// (FR-TEAM-10): at most once in <c>Trading:RoleReassignmentDays</c> days for an agent, and only
/// while the agent has no open position. Each change is logged, and its user is told. See
/// "Reassigning the roles automatically" in docs/agents.md.
/// </summary>
/// <remarks>
/// <para>
/// <b>It goes by the rule that the form of an agent shows,</b> <see cref="RoleRecommendations"/>:
/// a role is given its strongest candidate only when that scores above the model that the role
/// has. So what the form recommends is what the service does, and nothing else.
/// </para>
/// <para>
/// <b>Everything is decided under the lock of the account,</b> of the agent as it is then, as
/// what its user does to it is: whether it still opted in, runs, is due, and holds nothing, and
/// which models its roles have. The models are held to what its user can select by what holds a
/// form to it, <see cref="AgentRoles.ReadAsync"/>. A cycle is handed the team as it is stored
/// when its turn comes, so a change applies from the next cycle (FR-AGT-8).
/// </para>
/// <para>
/// <b>It is the service's doing, and no action of a user.</b> Nobody is asked once more for an
/// agent in Live: turning it on was asked about (FR-UI-3). The audit trail does not have it, as
/// it has no model that a user gives a role. The log and the notification do.
/// </para>
/// </remarks>
internal sealed class RoleReassignments(
    IDbContextFactory<EcnanibDbContext> contexts,
    IEnumerable<IAgentExposure> exposures,
    AgentRoles roles,
    INotifications notifications,
    TradingSettings trading,
    TimeProvider clock,
    ILogger<RoleReassignments> logger)
{
    // The most characters of the name of a model in a notification, which names two of them and
    // says no more than Notification.ContentLength characters. A catalog holds a name of any
    // length. Where what is said is still too long, the names are cut further, down to the
    // fewest that still tell two models of one provider apart.
    private const int NameLength = 40;
    private const int ShortestNameLength = 12;

    // The states in which the cycles of an agent run: an agent that does not run stays as its
    // user left it, and is asked about once it runs again.
    private static readonly AgentState[] Running = [.. Enum.GetValues<AgentState>().Where(AgentStates.RunsCycles)];

    /// <summary>Reassigns the roles of every agent that is due, of all users.</summary>
    /// <returns>How many agents were given another model for a role, and how many roles that were.</returns>
    public async Task<Reassigned> ReassignDueAsync(CancellationToken cancellationToken)
    {
        var dueBefore = clock.GetUtcNow() - Wait;

        List<Due> due;

        await using (var context = await contexts.CreateDbContextAsync(cancellationToken))
        {
            // Across users, in the order of their ids: a job has no user who is signed in, and
            // takes the owner from the row. Most runs find none.
            due = await context.Set<Agent>()
                .AsNoTracking()
                .Where(agent => agent.ReassignsRoles
                    && Running.Contains(agent.State)
                    && (agent.RolesReassignedAt == null || agent.RolesReassignedAt <= dueBefore))
                .OrderBy(agent => agent.Id)
                .Select(agent => new Due(agent.Id, agent.UserId, agent.Market, agent.Timeframe))
                .ToListAsync(cancellationToken);
        }

        // The agents of one user on one market and one timeframe are recommended from the same
        // scores, so those are worked out once in a run.
        var recommended = new Dictionary<(Guid UserId, Market Market, Timeframe Timeframe), RoleRecommendations>();

        var (agents, changed) = (0, 0);

        foreach (var agent in due)
        {
            try
            {
                var changes = await ReassignAsync(agent, recommended, cancellationToken);

                if (changes > 0)
                {
                    agents++;
                    changed += changes;
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // Said once, with the agent. The others go on, and the next run asks again.
                logger.LogError(
                    error, "The roles of agent {AgentId} of user {UserId} could not be reassigned by strength", agent.Id, agent.UserId);
            }
        }

        return new Reassigned(agents, changed);
    }

    private TimeSpan Wait => TimeSpan.FromDays(trading.RoleReassignmentDays);

    /// <summary>Reassigns the roles of one agent, when something is recommended for it and nothing stands in the way.</summary>
    /// <returns>How many of its roles were given another model.</returns>
    private async Task<int> ReassignAsync(
        Due due,
        Dictionary<(Guid UserId, Market Market, Timeframe Timeframe), RoleRecommendations> recommended,
        CancellationToken cancellationToken)
    {
        var (agentId, userId, market, timeframe) = due;

        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        // Asked first, with no lock: most agents that are due hold a position, or are
        // recommended nothing, and neither takes the lock of an account or works out a score.
        if (await exposures.HoldPositionAsync(context, agentId, cancellationToken))
        {
            return 0;
        }

        if (!recommended.TryGetValue((userId, market, timeframe), out var recommendations))
        {
            recommended[(userId, market, timeframe)] = recommendations =
                await roles.RecommendationsAsync(userId, market, timeframe, cancellationToken);
        }

        if (Changes(recommendations, await AgentRoles.OfAgentAsync(context, userId, agentId, cancellationToken)).Count == 0)
        {
            return 0;
        }

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        if (!await StoredAgents.LockAccountAsync(context, userId, cancellationToken))
        {
            // The account was deleted meanwhile, and its agents with it.
            return 0;
        }

        // Read again under the lock: this is the agent until the transaction ends.
        var agent = await context.Set<Agent>()
            .AsNoTracking()
            .OwnedBy(userId)
            .SingleOrDefaultAsync(agent => agent.Id == agentId, cancellationToken);

        // The time as it is under the lock: a change that waited for the lock is later than the one that held it.
        var now = clock.GetUtcNow();

        // Its user may have turned it off, paused or stopped the agent, or given it another
        // market or timeframe meanwhile, which the scores were not worked out for.
        if (agent is null
            || !agent.ReassignsRoles
            || !AgentStates.RunsCycles(agent.State)
            || agent.Market != market
            || agent.Timeframe != timeframe
            || (agent.RolesReassignedAt is { } last && now - last < Wait))
        {
            return 0;
        }

        // Only while the agent has no open position (FR-TEAM-10). An entry that is on its way
        // or rests on the exchange may open one, and counts as one. Asked under the lock, which
        // what writes an entry of the agent takes too.
        if (await exposures.HoldPositionAsync(context, agentId, cancellationToken))
        {
            return 0;
        }

        // The models that the roles have under the lock, which its user may have changed a
        // moment ago: what is recommended is recommended against these.
        var given = await AgentRoles.OfAgentAsync(context, userId, agentId, cancellationToken);
        var changes = Changes(recommendations, given);

        // Held to what its user can select now, as a form is: a key may have gone, or have
        // stopped listing a model, since the scores were worked out (FR-CAT-6, FR-CAT-9).
        var problems = new Dictionary<Role, RoleProblem>();
        var chosen = await roles.ReadAsync(
            userId,
            [.. changes.Select(change => new RoleEntry(change.Role, change.To.Slice.Model.Provider.Value, change.To.ModelId.ToString()))],
            given,
            problems,
            cancellationToken);

        foreach (var (role, problem) in problems)
        {
            logger.LogInformation(
                "The role {Role} of agent {AgentId} of user {UserId} was not given the model {ModelId} by strength: {RoleProblem}",
                role,
                agentId,
                userId,
                changes.Single(change => change.Role == role).To.ModelId,
                problem);
        }

        if (chosen.Count == 0)
        {
            // Nothing changes, so nothing is stored: the days are counted from a change.
            return 0;
        }

        await AgentRoles.StoreAsync(context, userId, agentId, chosen, cancellationToken);

        await context.Set<Agent>()
            .OwnedBy(userId)
            .Where(stored => stored.Id == agentId)
            .ExecuteUpdateAsync(row => row.SetProperty(stored => stored.RolesReassignedAt, now), cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        var applied = changes.Where(change => chosen.Any(choice => choice.Role == change.Role)).ToList();

        // What the change made of the team, which its user is warned of on the form of the
        // agent as well (FR-TEAM-5): strength may give two roles to one provider.
        var providers = given.ToDictionary(role => role.Role, role => role.Provider);
        var before = AgentRoleRules.WarningsOf(providers);

        foreach (var change in applied)
        {
            providers[change.Role] = change.To.Slice.Model.Provider;
        }

        var warning = Warning(before, AgentRoleRules.WarningsOf(providers));

        foreach (var change in applied)
        {
            // Each change is logged (FR-TEAM-10): by ids, and with what it went by.
            logger.LogInformation(
                "Agent {AgentId} of user {UserId} was given the model {ModelId} for the role {Role} in place of {PreviousModelId}, by its strength score {Score} from {TradingMode} data",
                agentId,
                userId,
                change.To.ModelId,
                change.Role,
                change.From?.ModelId,
                change.To.Score,
                change.To.Basis);

            await TellAsync(agent, change, recommendations, warning);
        }

        return applied.Count;
    }

    /// <summary>The roles that are recommended another model than they have, in the order of the roles.</summary>
    private static List<Change> Changes(RoleRecommendations recommendations, IReadOnlyList<AgentRoleView> given)
    {
        var changes = new List<Change>();

        foreach (var role in AgentRoleRules.All)
        {
            var has = given.FirstOrDefault(held => held.Role == role);

            if (recommendations.RecommendedFor(role, has?.ModelId) is { } strongest)
            {
                changes.Add(new Change(role, has, strongest));
            }
        }

        return changes;
    }

    /// <summary>
    /// Tells the user of an agent that a role of it was given another model (FR-TEAM-10). A
    /// notification that cannot be stored undoes nothing: the change is stored, the log has it,
    /// and the form of the agent shows the model that the role has.
    /// </summary>
    private async Task TellAsync(Agent agent, Change change, RoleRecommendations recommendations, string? warning)
    {
        try
        {
            await notifications.RaiseAsync(
                agent.UserId,
                NotificationType.RolesReassigned,
                agent.Id,
                Says(agent, change, recommendations.Of(change.Role, change.From?.ModelId), trading.RoleReassignmentDays, warning),
                CancellationToken.None);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogError(
                error, "The user {UserId} could not be told that a role of agent {AgentId} was reassigned", agent.UserId, agent.Id);
        }
    }

    /// <summary>What the user of an agent is told about one role that was given another model, in plain words.</summary>
    /// <param name="agent">The agent.</param>
    /// <param name="change">The role, the model that it had, and the model that it has now.</param>
    /// <param name="before">The model that it had with its score, or null when that has none.</param>
    /// <param name="days">In how many days no role of the agent is reassigned again.</param>
    /// <param name="warning">What the reassignment made of the team that its user is warned of, when it made something (FR-TEAM-5).</param>
    private static string Says(Agent agent, Change change, ScoredModel? before, int days, string? warning)
    {
        // A sentence is never cut: the names are cut further while what is said is longer than
        // a notification holds, and then the warning is left out, which the form of the agent
        // says as well.
        for (var nameLength = NameLength; nameLength >= ShortestNameLength; nameLength -= 4)
        {
            if (Composed(nameLength, warning) is { Length: <= Notification.ContentLength } fits)
            {
                return fits;
            }
        }

        var shortest = Composed(ShortestNameLength, said: null);

        // Should a symbol be longer than any is, what is said is still said in the characters
        // that a notification holds.
        return shortest.Length <= Notification.ContentLength ? shortest : shortest[..Notification.ContentLength];

        string Composed(int nameLength, string? said)
        {
            var had = change.From is { } from ? $" in place of {NameOf(from.Identifier, from.DisplayName, nameLength)}" : string.Empty;

            var against = before is null
                ? "no strength score yet for the model before"
                : $"{Written(change.Role, before.Score)} from {Count(before.Slice.Measure.Samples)} samples of {before.Basis} data for the model before";

            return
                $"The {NameOf(change.Role)} of the agent in {agent.Symbol} ({agent.Mode}) now uses " +
                $"{NameOf(change.To.Slice.Model.Identifier, change.To.Slice.Model.DisplayName, nameLength)}{had}: " +
                "automatic reassignment by strength is on for it. " +
                $"On {agent.Market} {agent.Timeframe.Code()}, its strength score is {Written(change.Role, change.To.Score)} from " +
                $"{Count(change.To.Slice.Measure.Samples)} samples of {change.To.Basis} data, against {against}. " +
                (said is null ? string.Empty : $"{said} ") +
                $"It applies from the next cycle, and no role is reassigned again for {Count(days)} {(days == 1 ? "day" : "days")}.";
        }
    }

    /// <summary>
    /// What a reassignment made of the team that it did not have before, or null when nothing:
    /// the Strategist and the Risk Manager on one provider, which is said of its own because the
    /// one reviews the plan of the other, or a provider that holds more than one role (FR-TEAM-5).
    /// </summary>
    private static string? Warning(RoleWarnings before, RoleWarnings after)
    {
        if (after.StrategistAndRiskManager is not null && before.StrategistAndRiskManager is null)
        {
            return "The Strategist and the Risk Manager now use the same provider.";
        }

        return after.Reduced && !before.Reduced ? "A provider now holds more than one role of the agent." : null;
    }

    /// <summary>A model as a notification names it: by the name its provider gives it, or by its identifier, cut to a length.</summary>
    private static string NameOf(string identifier, string? displayName, int length)
    {
        var name = string.IsNullOrWhiteSpace(displayName) ? identifier : displayName.Trim();

        return name.Length <= length ? name : $"{name[..(length - 1)].TrimEnd()}…";
    }

    private static string NameOf(Role role) => role switch
    {
        Role.MarketAnalyst => "Market Analyst",
        Role.SentimentAnalyst => "Sentiment Analyst",
        Role.Strategist => "Strategist",
        Role.RiskManager => "Risk Manager",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "The role has no name."),
    };

    /// <summary>
    /// A strength score in the unit of its role, with its sign, as the experience screens write
    /// it: percentage points for an analyst, and R for the Strategist and the Risk Manager.
    /// </summary>
    private static string Written(Role role, decimal score) =>
        role is Role.MarketAnalyst or Role.SentimentAnalyst
            ? $"{Math.Round(score * 100m, 1, MidpointRounding.AwayFromZero).ToString("+0.#;-0.#;0", CultureInfo.InvariantCulture)} percentage points"
            : $"{Math.Round(score, 2, MidpointRounding.AwayFromZero).ToString("+0.##;-0.##;0", CultureInfo.InvariantCulture)} R";

    private static string Count(int count) => count.ToString(CultureInfo.InvariantCulture);

    /// <summary>An agent whose roles may be reassigned now, as it was read with no lock.</summary>
    private sealed record Due(Guid Id, Guid UserId, Market Market, Timeframe Timeframe);

    /// <summary>A role that is given another model by strength.</summary>
    /// <param name="Role">The role.</param>
    /// <param name="From">The model that it has, or null when it has none.</param>
    /// <param name="To">The model that it is given, with the score that it is given it for.</param>
    private sealed record Change(Role Role, AgentRoleView? From, ScoredModel To);
}

/// <summary>What a run of the automatic reassignment did.</summary>
/// <param name="Agents">How many agents were given another model for a role.</param>
/// <param name="Roles">How many roles were given another model.</param>
internal sealed record Reassigned(int Agents, int Roles);
