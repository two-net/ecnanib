using Ecnanib.Kernel;
using Ecnanib.Modules.Agents.Contracts;
using Ecnanib.Modules.Catalog.Contracts;
using Ecnanib.Modules.Credentials.Contracts;

namespace Ecnanib.Modules.Agents;

/// <summary>
/// Which models suit each role (FR-ROLE-02), and the default assignment (FR-ROLE-03 to FR-ROLE-05), so the
/// settings page and activation follow one rule. See Role assignments in docs/architecture.md.
/// </summary>
internal sealed class RoleModels(ICredentials credentials, ICatalog catalog)
{
    /// <summary>The order roles take their defaults in: the Strategist first, so the Risk Critic can avoid its provider.</summary>
    private static readonly AiRole[] DefaultOrder = [AiRole.Strategist, .. Enum.GetValues<AiRole>().Where(role => role != AiRole.Strategist)];

    /// <summary>The catalog as the user's keys reach it, with <paramref name="keys"/> as the caller read them.</summary>
    public async Task<UserCatalog> ReadAsync(Guid userId, IReadOnlyList<CredentialRecord> keys, CancellationToken cancellationToken)
    {
        var models = (await catalog.ListAsync(cancellationToken)).ToDictionary(model => new ModelRef(model.Provider, model.ModelId));

        // Validity comes from Credentials, not the catalog's rows, which follow a key's change a moment later.
        var validKeys = keys.Where(key => key is { Kind: CredentialKind.Ai, Status: CredentialStatus.Valid }).Select(key => key.Id).ToHashSet();
        var reachable = (await catalog.ListAccessAsync(userId, cancellationToken))
            .Where(access => access.Available && validKeys.Contains(access.CredentialId))
            .Select(access => new ModelRef(access.Provider, access.ModelId))
            .ToHashSet();

        return new UserCatalog(models, reachable);
    }

    /// <summary>The models the user can give each role, and the assignment a new agent starts with.</summary>
    public async Task<RoleChoices> ChoicesAsync(Guid userId, CancellationToken cancellationToken)
    {
        var keys = await credentials.ListAsync(userId, cancellationToken);
        var user = await ReadAsync(userId, keys, cancellationToken);
        var usable = user.Models.Values.Where(user.IsUsable).ToList();

        var options = Enum.GetValues<AiRole>().ToDictionary(
            role => role,
            role => (IReadOnlyList<ModelChoice>)[.. Listed(role, usable).Select(Choice)]);

        return new RoleChoices(options, Defaults(usable, await catalog.ListRoleDefaultsAsync(cancellationToken)));
    }

    /// <summary>
    /// The assignment a new agent starts with, from the models the user can use (FR-ROLE-03). Each role takes
    /// its default model, else its default provider's best, else another provider's best, so a single key fills
    /// every role (FR-ROLE-05). The Risk Critic takes a provider other than the Strategist's when there is one
    /// (FR-ROLE-04). Each fallback comes from a provider other than its role's, else its role's.
    /// </summary>
    public static IReadOnlyList<RoleAssignment> Defaults(IReadOnlyList<CatalogModel> usable, IReadOnlyList<RoleDefault> defaults)
    {
        var assignments = new Dictionary<AiRole, RoleAssignment>();

        foreach (var role in DefaultOrder)
        {
            var candidates = Candidates(role, usable, defaults.FirstOrDefault(value => value.Role == role));
            var strategist = assignments.GetValueOrDefault(AiRole.Strategist)?.Model.Provider;

            if (role == AiRole.RiskCritic && strategist is not null)
            {
                candidates = [.. candidates.OrderBy(model => model.Provider == strategist)];
            }

            if (candidates.FirstOrDefault() is not { } primary)
            {
                continue;
            }

            var fallback = candidates
                .Where(model => model != primary)
                .OrderBy(model => model.Provider == primary.Provider)
                .ThenBy(model => role == AiRole.RiskCritic && model.Provider == strategist)
                .FirstOrDefault();

            assignments[role] = new RoleAssignment(role, RefOf(primary), fallback is null ? null : RefOf(fallback));
        }

        return [.. assignments.Values.OrderBy(assignment => assignment.Role)];
    }

    /// <summary>
    /// Every usable model, best first for the role: its default model, its default provider's by rank, then
    /// the other providers' by name and rank.
    /// </summary>
    private static List<CatalogModel> Candidates(AiRole role, IReadOnlyList<CatalogModel> usable, RoleDefault? value)
    {
        var candidates = new List<CatalogModel>();

        if (value is not null)
        {
            candidates.AddRange(usable.Where(model => model.Provider == value.Provider && model.ModelId == value.ModelId));
            candidates.AddRange(Ranked(role, usable.Where(model => model.Provider == value.Provider)));
        }

        foreach (var provider in usable.Select(model => model.Provider).Distinct().Order(StringComparer.Ordinal))
        {
            if (provider != value?.Provider)
            {
                candidates.AddRange(Ranked(role, usable.Where(model => model.Provider == provider)));
            }
        }

        return [.. candidates.Distinct()];
    }

    /// <summary>
    /// One provider's models, best first for the role. Nothing says which model is strongest, so newer goes
    /// first: by release date, which Gemini doesn't give, then by when a sync first listed it.
    /// </summary>
    private static IEnumerable<CatalogModel> Ranked(AiRole role, IEnumerable<CatalogModel> models) =>
        models
            .OrderBy(model => Preference(role, model))
            .ThenByDescending(model => model.ReleasedAt.HasValue)
            .ThenByDescending(model => model.ReleasedAt)
            .ThenByDescending(model => model.FirstSeenAt)
            .ThenBy(model => model.ModelId, StringComparer.Ordinal);

    /// <summary>The settings page's order: what the role prefers, then by provider and name.</summary>
    private static IEnumerable<CatalogModel> Listed(AiRole role, IEnumerable<CatalogModel> models) =>
        models
            .OrderBy(model => Preference(role, model))
            .ThenBy(model => model.Provider, StringComparer.Ordinal)
            .ThenBy(model => model.DisplayName, StringComparer.CurrentCulture)
            .ThenBy(model => model.ModelId, StringComparer.Ordinal);

    /// <summary>
    /// Lower is better. The Sentiment Analyst prefers live search (FR-ROLE-02), where a null means nobody
    /// said. Image input is optional for the Technical Analyst, so it ranks nothing.
    /// </summary>
    private static int Preference(AiRole role, CatalogModel model) => role switch
    {
        AiRole.SentimentAnalyst => model.Capabilities.LiveSearch switch
        {
            true => 0,
            null => 1,
            false => 2,
        },
        _ => 0,
    };

    private static ModelChoice Choice(CatalogModel model) => new(RefOf(model), model.DisplayName, model.Capabilities.LiveSearch);

    private static ModelRef RefOf(CatalogModel model) => new(model.Provider, model.ModelId);
}

/// <summary>Why a model can't fill a role, in the order activation checks.</summary>
internal enum ModelProblem
{
    /// <summary>It isn't in the catalog, or none of the user's valid keys can use it.</summary>
    NotAccessible,

    NotActive,

    /// <summary>The provider or the configuration says it can't be held to a JSON schema (FR-ROLE-02).</summary>
    LacksStructuredOutput,
}

/// <summary>The catalog by model, and the models the user's valid AI keys can use.</summary>
internal sealed record UserCatalog(IReadOnlyDictionary<ModelRef, CatalogModel> Models, IReadOnlySet<ModelRef> Reachable)
{
    /// <summary>
    /// Why the model can't fill a role, or null when it can. Every role needs structured output, but only an
    /// explicit false counts against a model: only Anthropic reports it, so a null means nobody said, and the
    /// council validates every answer anyway (FR-PRM-08).
    /// </summary>
    public ModelProblem? ProblemOf(ModelRef model) =>
        !Models.TryGetValue(model, out var listed) || !Reachable.Contains(model) ? ModelProblem.NotAccessible
        : listed.Status != ModelStatus.Active ? ModelProblem.NotActive
        : listed.Capabilities.StructuredOutput == false ? ModelProblem.LacksStructuredOutput
        : null;

    public bool IsUsable(CatalogModel model) => ProblemOf(new ModelRef(model.Provider, model.ModelId)) is null;
}
