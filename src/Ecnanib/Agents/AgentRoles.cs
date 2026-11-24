using Ecnanib.Accounts;
using Ecnanib.Catalog;
using Ecnanib.Credentials;
using Ecnanib.Experience;
using Ecnanib.Notifications;
using Ecnanib.Platform.Persistence;
using Ecnanib.Platform.Settings;
using Ecnanib.Shared;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Agents;

/// <summary>
/// What a user can give the roles of an agent (FR-AGT-5): every AI provider with the models
/// that the user can select of it, and the provider that fills each role by default.
/// </summary>
/// <param name="Providers">Every AI provider, in the order of the providers' codes.</param>
/// <param name="Defaults">
/// The provider that the configuration file gives each role on a new agent (FR-TEAM-2,
/// FR-TEAM-3). A user gives a role another provider when they choose to.
/// </param>
public sealed record RoleChoices(IReadOnlyList<ProviderChoice> Providers, IReadOnlyDictionary<Role, AiProviderCode> Defaults)
{
    /// <summary>What the user can select of one provider, or null when there is no provider of that code.</summary>
    public ProviderChoice? Of(AiProviderCode provider) => Providers.FirstOrDefault(choice => choice.Provider == provider);
}

/// <summary>What a user can select of one AI provider's models for a role of an agent.</summary>
/// <param name="Provider">The provider.</param>
/// <param name="Name">Its name as users read it, such as "Claude".</param>
/// <param name="Block">
/// Why the user can select none of the provider's models, as far as their key goes, or null when
/// the key can be used: <see cref="ModelBlock.NoKey"/>, <see cref="ModelBlock.KeyNotValid"/>, or
/// <see cref="ModelBlock.NotKnown"/>.
/// </param>
/// <param name="Models">
/// The models that can be selected at all and that the user's own key can use, by identifier
/// (FR-CAT-6, FR-CAT-9). It is empty with a <paramref name="Block"/>, and also when the key can
/// use none of the models that can be selected.
/// </param>
public sealed record ProviderChoice(AiProviderCode Provider, string Name, ModelBlock? Block, IReadOnlyList<SelectableModel> Models);

/// <summary>A role of an agent with the model that it was given, as the agent's user may read it.</summary>
/// <param name="Role">The role.</param>
/// <param name="ModelId">The id of the model in the catalog.</param>
/// <param name="Provider">The provider of the model, which is the provider of the role.</param>
/// <param name="Identifier">The provider's identifier of the model.</param>
/// <param name="DisplayName">The name the provider gives the model for people to read, when it gives one.</param>
public sealed record AgentRoleView(Role Role, Guid ModelId, AiProviderCode Provider, string Identifier, string? DisplayName);

/// <summary>What a form brought for one role of an agent: text, as it was chosen.</summary>
/// <param name="Role">The role.</param>
/// <param name="Provider">The code of the provider that was chosen for the role, or null when the form brought none.</param>
/// <param name="Model">
/// The id of the model that was chosen. It is empty when the form said that none is chosen, and
/// null when the form brought nothing for the role, which leaves the role as it is.
/// </param>
public sealed record RoleEntry(Role Role, string? Provider, string? Model);

/// <summary>What is wrong with the model of a role of an agent: with what was chosen for it, or with what it has.</summary>
public enum RoleProblem
{
    /// <summary>The role has no model: none was chosen for a role that had one, or the role never had one.</summary>
    Missing,

    /// <summary>What was chosen is not the id of a model.</summary>
    NotAModel,

    /// <summary>The model is of another provider than the one chosen for the role.</summary>
    NotOfProvider,

    /// <summary>
    /// The model cannot be selected by anyone: the catalog holds no such model, its provider no
    /// longer offers it, or the configuration file does not allow it (FR-CAT-6).
    /// </summary>
    NotSelectable,

    /// <summary>The user has stored no key for the provider of the model.</summary>
    NoKey,

    /// <summary>The user's key for the provider of the model is not valid.</summary>
    KeyNotValid,

    /// <summary>It is not known yet which models the user's key for the provider can use.</summary>
    NotKnown,

    /// <summary>The provider did not list the model to the user's key, so the key cannot use it (FR-CAT-9).</summary>
    NotListed,
}

/// <summary>What about one role keeps an agent from running.</summary>
/// <param name="Role">The role.</param>
/// <param name="Problem">What is wrong with its model.</param>
/// <param name="ProviderName">The name of the provider of its model as users read it, when it has a model.</param>
/// <param name="ModelName">The name of its model as users read it, when it has one.</param>
public sealed record RoleBlock(Role Role, RoleProblem Problem, string? ProviderName = null, string? ModelName = null);

/// <summary>
/// What a user is warned of when the providers of an agent's roles are not four different ones
/// (FR-TEAM-5).
/// </summary>
/// <param name="Repeated">The providers that hold more than one role, in the order of their codes.</param>
/// <param name="StrategistAndRiskManager">
/// The provider that both the Strategist and the Risk Manager use, or null when they use
/// different ones. The Risk Manager reviews the plan of the Strategist, so this is said of its own.
/// </param>
public sealed record RoleWarnings(IReadOnlyList<AiProviderCode> Repeated, AiProviderCode? StrategistAndRiskManager)
{
    /// <summary>Whether the team is reduced: a provider holds more than one role.</summary>
    public bool Reduced => Repeated.Count > 0;
}

/// <summary>What the roles of an agent's team must keep to, and what their user is warned of.</summary>
public static class AgentRoleRules
{
    /// <summary>The four roles, in the order they work in.</summary>
    public static IReadOnlyList<Role> All { get; } = Enum.GetValues<Role>();

    /// <summary>
    /// What to warn of for the providers that the roles of an agent have (FR-TEAM-5). One
    /// provider may hold more than one role, and nothing keeps a user from it: they are told.
    /// </summary>
    /// <param name="providers">The provider of each role that has one.</param>
    public static RoleWarnings WarningsOf(IReadOnlyDictionary<Role, AiProviderCode> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);

        var repeated = providers.Values
            .GroupBy(provider => provider)
            .Where(sameProvider => sameProvider.Count() > 1)
            .Select(sameProvider => sameProvider.Key)
            .OrderBy(provider => provider.Value, StringComparer.Ordinal)
            .ToList();

        AiProviderCode? both =
            providers.TryGetValue(Role.Strategist, out var strategist)
            && providers.TryGetValue(Role.RiskManager, out var riskManager)
            && strategist == riskManager
                ? strategist
                : null;

        return new RoleWarnings(repeated, both);
    }

    /// <summary>What keeps a user from selecting a model, as what is wrong with the role that was given it.</summary>
    internal static RoleProblem ProblemOf(ModelBlock block) => block switch
    {
        ModelBlock.NotSelectable => RoleProblem.NotSelectable,
        ModelBlock.NoKey => RoleProblem.NoKey,
        ModelBlock.KeyNotValid => RoleProblem.KeyNotValid,
        ModelBlock.NotKnown => RoleProblem.NotKnown,
        ModelBlock.NotListed => RoleProblem.NotListed,
        _ => throw new ArgumentOutOfRangeException(nameof(block), block, "The block is no problem of a role."),
    };
}

/// <summary>A model that a role of an agent is given, read from a form and found to be one that its user can select.</summary>
internal sealed record RoleChoice(Role Role, Guid ModelId);

/// <summary>
/// Gives the roles of an agent's team their models (FR-AGT-5, FR-TEAM-1): says what a user can
/// select, holds what a form brought to it, stores the choices, and says what about the team
/// keeps an agent from running. It also says which models are the strongest in each role by
/// measured experience, which a role is recommended from (FR-TEAM-4). See docs/agents.md.
/// </summary>
/// <remarks>
/// <para>
/// Which models a user can select is for Credentials to say, through <see cref="IKeyModels"/>:
/// the models that can be selected at all, and that the user's own key can use (FR-CAT-6,
/// FR-CAT-9). It is asked for what a selector offers, and again when a choice is saved: what is
/// posted need not have come from a selector.
/// </para>
/// <para>
/// A choice that a role has already is not asked about again when the settings of its agent are
/// saved: it was held to what its user could select when it was stored. So the name of an agent
/// can be changed while the key of a provider of its team is gone. The team is asked about, whole,
/// when the agent is started or resumed.
/// </para>
/// </remarks>
internal sealed class AgentRoles(IKeyModels keyModels, ICatalogModels catalog, IRoleStrengths strengths, TradingSettings trading)
{
    /// <summary>What a user can give the roles of an agent, and what each role has by default.</summary>
    public async Task<RoleChoices> ChoicesAsync(Guid userId, CancellationToken cancellationToken)
    {
        var selectable = await keyModels.SelectableAsync(userId, cancellationToken);

        // The names of the providers as users read them, which the catalog keeps.
        var names = (await catalog.ListAsync(cancellationToken))
            .ToDictionary(provider => provider.Provider, provider => provider.ProviderName);

        return new RoleChoices(
            [
                .. selectable.Select(provider => new ProviderChoice(
                    provider.Provider,
                    names.GetValueOrDefault(provider.Provider, provider.Provider.Value),
                    provider.Block,
                    provider.Models)),
            ],
            AgentRoleRules.All.ToDictionary(role => role, trading.DefaultRoles.Of));
    }

    /// <summary>
    /// The models that have a strength score in each role on a market and a timeframe, from
    /// what the agents of one user did, strongest first, with which of them the user can give a
    /// role now (FR-TEAM-4). Experience says the scores of each mode, and
    /// <see cref="RoleRecommendations.Of"/> which mode each model is gone by (FR-EXP-9).
    /// </summary>
    /// <remarks>
    /// Every sample counts, however old: a recommendation has no period. Nothing of what the
    /// agents of anyone else did is in it (NFR-SEC-3). It only reads, and asks no provider.
    /// </remarks>
    public async Task<RoleRecommendations> RecommendationsAsync(
        Guid userId, Market market, Timeframe timeframe, CancellationToken cancellationToken)
    {
        var slices = new TrackRecordFilter(Role: null, market, timeframe, Mode: null, TrackPeriod.AllTime);

        var live = await strengths.OfUserAsync(userId, slices, TradingMode.Live, cancellationToken);
        var demo = await strengths.OfUserAsync(userId, slices, TradingMode.Demo, cancellationToken);

        var selectable = (await keyModels.SelectableAsync(userId, cancellationToken))
            .SelectMany(provider => provider.Models)
            .ToList();

        return RoleRecommendations.Of(market, timeframe, live, demo, selectable);
    }

    /// <summary>The roles of every agent of a user with their models, by agent, each agent's in the order of the roles.</summary>
    public static async Task<ILookup<Guid, AgentRoleView>> OfUserAsync(
        EcnanibDbContext context, Guid userId, CancellationToken cancellationToken)
    {
        var rows = await (
            from role in context.Set<AgentRole>().AsNoTracking().OwnedBy(userId)
            join model in context.Set<AiModel>().AsNoTracking() on role.ModelId equals model.Id
            select new { role.AgentId, role.Role, role.ModelId, model.Provider, model.Identifier, model.DisplayName })
            .ToListAsync(cancellationToken);

        return rows
            .OrderBy(row => row.Role)
            .ToLookup(row => row.AgentId, row => new AgentRoleView(row.Role, row.ModelId, row.Provider, row.Identifier, row.DisplayName));
    }

    /// <summary>The roles of one agent of a user with their models, in the order of the roles.</summary>
    public static async Task<IReadOnlyList<AgentRoleView>> OfAgentAsync(
        EcnanibDbContext context, Guid userId, Guid agentId, CancellationToken cancellationToken)
    {
        var rows = await (
            from role in context.Set<AgentRole>().AsNoTracking().OwnedBy(userId)
            where role.AgentId == agentId
            join model in context.Set<AiModel>().AsNoTracking() on role.ModelId equals model.Id
            select new { role.Role, role.ModelId, model.Provider, model.Identifier, model.DisplayName })
            .ToListAsync(cancellationToken);

        return
        [
            .. rows
                .OrderBy(row => row.Role)
                .Select(row => new AgentRoleView(row.Role, row.ModelId, row.Provider, row.Identifier, row.DisplayName)),
        ];
    }

    /// <summary>
    /// Reads what a form brought for the roles of an agent, and holds it to what its user can
    /// select now. A role that no model was chosen for stays without one, unless it has one: a
    /// role that was given a model keeps one, which can be another. A role that the form brought
    /// nothing for is left as it is.
    /// </summary>
    /// <param name="userId">The user whose agent it is.</param>
    /// <param name="entries">What was chosen, role by role.</param>
    /// <param name="stored">The roles that the agent has a model for now. A new agent has none.</param>
    /// <param name="problems">Takes what is wrong, by role.</param>
    /// <param name="cancellationToken">Cancels the questions.</param>
    /// <returns>
    /// The roles that are given a model they do not have yet. It is what to store when nothing
    /// is wrong.
    /// </returns>
    public async Task<IReadOnlyList<RoleChoice>> ReadAsync(
        Guid userId,
        IReadOnlyList<RoleEntry> entries,
        IReadOnlyList<AgentRoleView> stored,
        Dictionary<Role, RoleProblem> problems,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(problems);

        problems.Clear();

        var choices = new List<RoleChoice>();

        // The provider of each model that the user can select, asked once, and only when a
        // role is given a model that it does not have.
        Dictionary<Guid, AiProviderCode>? providerOf = null;

        foreach (var role in AgentRoleRules.All)
        {
            var entry = entries.FirstOrDefault(brought => brought.Role == role);
            var has = stored.FirstOrDefault(given => given.Role == role);

            if (entry?.Model is null)
            {
                // The form brought nothing for the role: it says nothing about it, and the role
                // is as it was.
                continue;
            }

            var modelText = entry.Model.Trim();
            var providerText = (entry.Provider ?? string.Empty).Trim();
            AiProviderCode? chosenProvider = providerText.Length == 0 ? null : new AiProviderCode(providerText);

            if (modelText.Length == 0)
            {
                // The form says that the role has no model. A role that has one keeps one.
                if (has is not null)
                {
                    problems[role] = RoleProblem.Missing;
                }

                continue;
            }

            if (!Guid.TryParse(modelText, out var modelId))
            {
                problems[role] = RoleProblem.NotAModel;

                continue;
            }

            if (has is not null && has.ModelId == modelId)
            {
                // The model that the role has. A provider that was chosen beside it and is not
                // its own is a choice that was not finished: no model of that provider was chosen.
                if (chosenProvider is { } unfinished && unfinished != has.Provider)
                {
                    problems[role] = RoleProblem.NotOfProvider;
                }

                continue;
            }

            // Asked again now, whatever a selector offered: what is posted need not have come
            // from one (FR-CAT-6, FR-CAT-9).
            if (await keyModels.BlockForAsync(userId, modelId, cancellationToken) is { } block)
            {
                problems[role] = AgentRoleRules.ProblemOf(block);

                continue;
            }

            providerOf ??= (await keyModels.SelectableAsync(userId, cancellationToken))
                .SelectMany(provider => provider.Models)
                .ToDictionary(model => model.Id, model => model.Provider);

            if (!providerOf.TryGetValue(modelId, out var provider))
            {
                // It could be selected a moment ago, and cannot be now.
                problems[role] = RoleProblem.NotSelectable;

                continue;
            }

            if (chosenProvider is { } named && named != provider)
            {
                problems[role] = RoleProblem.NotOfProvider;

                continue;
            }

            choices.Add(new RoleChoice(role, modelId));
        }

        return choices;
    }

    /// <summary>
    /// Stores the models that roles of an agent are given, inside the transaction of whoever
    /// asks, which has locked the row of the account. A role that had a model has the new one in
    /// its place.
    /// </summary>
    public static async Task StoreAsync(
        EcnanibDbContext context, Guid userId, Guid agentId, IReadOnlyList<RoleChoice> choices, CancellationToken cancellationToken)
    {
        if (choices.Count == 0)
        {
            return;
        }

        var roles = choices.Select(choice => choice.Role).ToArray();

        await context.Set<AgentRole>()
            .OwnedBy(userId)
            .Where(role => role.AgentId == agentId && roles.Contains(role.Role))
            .ExecuteDeleteAsync(cancellationToken);

        foreach (var choice in choices)
        {
            context.Set<AgentRole>().Add(new AgentRole(agentId, userId, choice.Role, choice.ModelId));
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// What about the team of an agent keeps it from running, role by role, in the order of the
    /// roles: a role that has no model, and a role whose model its user cannot select now. It is
    /// empty when nothing does.
    /// </summary>
    /// <remarks>
    /// It is asked under the lock that deleting a key and marking one invalid take, so whether
    /// the user has a valid key for each provider holds until the agent runs. Which models a key
    /// can use is recorded apart from that lock, when its provider lists them, so that part of
    /// the answer is as it was last recorded: see "Not built yet" in docs/agents.md.
    /// </remarks>
    public async Task<IReadOnlyList<RoleBlock>> BlocksAsync(
        EcnanibDbContext context, Guid userId, Guid agentId, CancellationToken cancellationToken)
    {
        var given = await OfAgentAsync(context, userId, agentId, cancellationToken);

        var blocks = new List<RoleBlock>();

        foreach (var role in AgentRoleRules.All)
        {
            if (given.FirstOrDefault(has => has.Role == role) is not { } has)
            {
                blocks.Add(new RoleBlock(role, RoleProblem.Missing));

                continue;
            }

            if (await keyModels.BlockForAsync(userId, has.ModelId, cancellationToken) is not { } block)
            {
                continue;
            }

            // Named as users read them. The catalog finds a model for good, also one that is gone.
            var model = await catalog.FindAsync(has.ModelId, cancellationToken);

            blocks.Add(new RoleBlock(
                role,
                AgentRoleRules.ProblemOf(block),
                model?.ProviderName ?? has.Provider.Value,
                has.DisplayName ?? has.Identifier));
        }

        return blocks;
    }
}

/// <summary>
/// Moves the agents that run with a model to Attention required when a sync of the catalog marks
/// the model unavailable, and puts no other model in its place (FR-CAT-8). The Catalog tells it,
/// inside the transaction that marks the models. See docs/agents.md.
/// </summary>
/// <remarks>
/// <para>
/// The agents are those of every user, so the owner of each comes from its own rows. The
/// accounts that have such an agent are locked one after the other, in the order of their ids,
/// as what changes an agent or a key locks its account: two providers are synced at the same
/// time, and so lock in the same order. Under the lock the agents of the account are read, and
/// the ones that run are moved, as a key that goes moves them.
/// </para>
/// <para>
/// No lock is taken on a model: the transaction of the sync holds the rows of the models already.
/// What changes an agent holds the lock of its account and takes none on a model that this
/// transaction would have to wait for, so neither waits for the other in a circle.
/// </para>
/// </remarks>
internal sealed class AgentModelDependents(INotifications notifications, TimeProvider clock, ILogger<AgentModelDependents> logger) : IModelDependents
{
    public async Task ModelsUnavailableAsync(
        EcnanibDbContext context, AiProviderCode provider, IReadOnlyList<Guid> modelIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(modelIds);

        var owners = await context.Set<AgentRole>()
            .AsNoTracking()
            .Where(role => modelIds.Contains(role.ModelId))
            .Select(role => role.UserId)
            .Distinct()
            .OrderBy(userId => userId)
            .ToListAsync(cancellationToken);

        foreach (var userId in owners)
        {
            if (!await StoredAgents.LockAccountAsync(context, userId, cancellationToken))
            {
                // The account was deleted meanwhile, and its agents with it.
                continue;
            }

            // Read under the lock, as what a user does to an agent reads it: a change that waited
            // for the lock is later than the change that held it, and says so.
            var now = clock.GetUtcNow();

            // Read under the lock: these are the agents of the account until the transaction ends.
            var agents = await context.Set<Agent>()
                .AsNoTracking()
                .OwnedBy(userId)
                .Where(agent => context.Set<AgentRole>().Any(role => role.AgentId == agent.Id && modelIds.Contains(role.ModelId)))
                .OrderBy(agent => agent.Id)
                .Select(agent => new { agent.Id, agent.State, agent.Mode, agent.Symbol })
                .ToListAsync(cancellationToken);

            foreach (var agent in agents.Where(agent => AgentStates.NeedsItsKey(agent.State)))
            {
                await AgentStateChanges.MoveAsync(
                    context, userId, agent.Id, agent.Mode, AgentState.AttentionRequired, AgentStateCause.ModelUnavailable, now, cancellationToken);

                // The roles of the agent whose model is gone, in the order of the team.
                var roles = await context.Set<AgentRole>()
                    .AsNoTracking()
                    .Where(role => role.AgentId == agent.Id && modelIds.Contains(role.ModelId))
                    .Select(role => role.Role)
                    .ToListAsync(cancellationToken);

                // Its user is told that the agent waits for them, and for which roles, in the
                // same transaction: so they are told when the models are marked, and not when
                // they are not (FR-NTF-1).
                await notifications.RaiseInAsync(
                    context,
                    userId,
                    NotificationType.ModelUnavailable,
                    agent.Id,
                    agent.Mode,
                    Says(agent.Symbol, agent.Mode, roles),
                    cancellationToken);

                // Stored when the models are marked, and not when they are not: both are of the
                // transaction of the sync.
                logger.LogInformation(
                    "Agent {AgentId} of user {UserId} requires attention: the AI provider {Provider} no longer offers a model of its team",
                    agent.Id,
                    userId,
                    provider.Value);
            }
        }
    }

    /// <summary>
    /// What the user of an agent is told when a model of its team is gone: which roles had it,
    /// and what to do. A team has four roles, so it is short whatever the roles are.
    /// </summary>
    private static string Says(string symbol, TradingMode mode, IReadOnlyList<Role> roles)
    {
        var (whose, offered, give) = roles.Count > 1
            ? ($"the models of its {RoleNames.Listed(roles)}", "are no longer offered by their provider", "Give each of these roles another model")
            : ($"the model of its {RoleNames.Listed(roles)}", "is no longer offered by its provider", "Give the role another model");

        return $"The agent in {symbol} ({mode}) waits for you: {whose} {offered}. {give}, and resume the agent.";
    }
}

/// <summary>A role of an agent with its model, in the export of a user's data (NFR-SEC-9).</summary>
/// <param name="AgentId">The agent whose role it is, by the id that the section of the agents has.</param>
/// <param name="Role">The role.</param>
/// <param name="Provider">The code of the provider of the model.</param>
/// <param name="Model">The provider's identifier of the model.</param>
public sealed record ExportedAgentRole(Guid AgentId, Role Role, string Provider, string Model);

/// <summary>The models that the roles of a user's agents were given, for the export of their data (NFR-SEC-9, FR-AGT-5).</summary>
internal sealed class AgentRolesExport : IExportedPart
{
    public string Section => "agentRoles";

    public async Task<IReadOnlyList<object>> ReadAsync(EcnanibDbContext context, Guid userId, CancellationToken cancellationToken)
    {
        var rows = await (
            from role in context.Set<AgentRole>().AsNoTracking().OwnedBy(userId)
            join model in context.Set<AiModel>().AsNoTracking() on role.ModelId equals model.Id
            select new { role.AgentId, role.Role, model.Provider, model.Identifier })
            .ToListAsync(cancellationToken);

        return
        [
            .. rows
                .OrderBy(row => row.AgentId)
                .ThenBy(row => row.Role)
                .Select(row => new ExportedAgentRole(row.AgentId, row.Role, row.Provider.Value, row.Identifier)),
        ];
    }
}

/// <summary>How a notification names the roles of a team.</summary>
internal static class RoleNames
{
    /// <summary>
    /// Roles by their names, in the order of the team, as a sentence lists them: "Strategist",
    /// "Strategist and Risk Manager", "Market Analyst, Strategist and Risk Manager".
    /// </summary>
    public static string Listed(IReadOnlyList<Role> roles)
    {
        var names = roles.Order().Select(RoleReassignments.NameOf).ToList();

        return names.Count switch
        {
            0 => "team",
            1 => names[0],
            _ => $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}",
        };
    }
}
