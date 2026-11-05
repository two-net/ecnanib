using System.Globalization;
using System.Text.Json;
using Ecnanib.Kernel;
using Ecnanib.Modules.Catalog.Contracts;
using Ecnanib.Platform.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Modules.Catalog;

/// <summary>
/// Administrators' controls over the catalog (FR-CAT-03, FR-CAT-05, FR-ADM-03). It doesn't check who's asking:
/// Administration does before it calls, and BannedApis keeps everyone else out. Each change is audited in the
/// transaction that makes it. See Administration in docs/architecture.md.
/// </summary>
internal sealed class CatalogAdministration(
    IDbContextFactory<EcnanibDbContext> contextFactory,
    Catalog catalog,
    CatalogSync sync,
    IAuditTrail audit,
    ILogger<CatalogAdministration> logger) : ICatalogAdministration
{
    /// <summary>The audit record's target types.</summary>
    public const string ModelTarget = "Model";

    public const string RoleTarget = "Role";

    /// <summary>The audit trail's limit, which Catalog can't see.</summary>
    private const int TargetIdMaxLength = 128;

    public async Task<SyncAllOutcome> SyncAllAsync(Guid administratorId, CancellationToken cancellationToken)
    {
        var outcome = sync.StartEveryone();

        if (outcome == SyncAllOutcome.Started)
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

            audit.Add(context, ByAdministrator(AuditAction.CatalogSyncRequested, administratorId));
            await context.SaveChangesAsync(cancellationToken);

            logger.LogInformation("Administrator {AdministratorId} started a sync of every user's AI keys", administratorId);
        }

        return outcome;
    }

    public async Task<IReadOnlyList<SyncRunRecord>> ListRunsAsync(int count, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // By ID, which the primary key orders, rather than the start time, which only the provider's index has.
        var runs = await context.Set<CatalogSyncRun>()
            .AsNoTracking()
            .OrderByDescending(run => run.Id)
            .Take(count)
            .ToListAsync(cancellationToken);

        return [.. runs.Select(run => new SyncRunRecord(
            run.Id,
            run.Provider,
            run.Trigger,
            run.StartedAt,
            run.FinishedAt,
            run.Keys,
            run.KeysFailed,
            run.ModelsAdded,
            run.ModelsUpdated,
            run.ModelsDeprecated,
            run.ModelsRetired,
            ErrorCount(run.Errors)))];
    }

    public async Task<ModelDetail?> GetModelAsync(string provider, string modelId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var model = await context.Set<Model>()
            .AsNoTracking()
            .SingleOrDefaultAsync(row => row.Provider == provider && row.ModelId == modelId, cancellationToken);

        if (model is null)
        {
            return null;
        }

        var row = await context.Set<ModelOverrideRow>()
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Provider == provider && candidate.ModelId == modelId, cancellationToken);

        return new ModelDetail(
            catalog.Effective(model, row),
            Catalog.Reported(model),
            catalog.Configured(provider, modelId),
            row?.ToEdit() ?? ModelEdit.None);
    }

    public async Task<ModelEditResult> SaveModelAsync(
        Guid administratorId, string provider, string modelId, ModelEdit edit, CancellationToken cancellationToken)
    {
        edit = Normalize(edit);

        if (Problems(edit) is { Count: > 0 } problems)
        {
            return new ModelEditResult(ModelEditOutcome.Invalid, problems);
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        // Locks the model's row, so two administrators' edits of one model take turns, and each audit record's
        // "before" is what the other left. A sync's write waits a moment too.
        var found = await context.Set<Model>()
            .FromSql($"SELECT * FROM models WHERE provider = {provider} AND model_id = {modelId} FOR UPDATE")
            .AsNoTracking()
            .AnyAsync(cancellationToken);

        if (!found)
        {
            return ModelEditResult.NotFound;
        }

        var row = await context.Set<ModelOverrideRow>()
            .SingleOrDefaultAsync(candidate => candidate.Provider == provider && candidate.ModelId == modelId, cancellationToken);
        var changes = Changes(row?.ToEdit() ?? ModelEdit.None, edit);

        if (changes.Count == 0)
        {
            return ModelEditResult.Saved;
        }

        if (edit.IsNone)
        {
            context.Remove(row!);
        }
        else if (row is null)
        {
            row = new ModelOverrideRow { Provider = provider, ModelId = modelId };
            row.Set(edit);
            context.Add(row);
        }
        else
        {
            row.Set(edit);
        }

        var target = $"{provider}/{modelId}";
        audit.Add(
            context,
            ByAdministrator(AuditAction.CatalogModelEdited, administratorId) with
            {
                TargetType = ModelTarget,

                // The ID is unbounded, so a long one is named only in the summary.
                TargetId = target.Length <= TargetIdMaxLength ? target : null,
                Changes =
                [
                    new AuditChange(CatalogAuditFields.Provider, provider, provider),
                    new AuditChange(CatalogAuditFields.Model, modelId, modelId),
                    .. changes,
                ],
            });

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Administrator {AdministratorId} changed {Fields} of {Provider} {ModelId}",
            administratorId,
            string.Join(", ", changes.Select(change => change.Field)),
            provider,
            modelId);

        return ModelEditResult.Saved;
    }

    public async Task<RoleDefaultsResult> SaveRoleDefaultsAsync(
        Guid administratorId, IReadOnlyList<RoleDefaultEdit> edits, CancellationToken cancellationToken)
    {
        if (edits.DistinctBy(edit => edit.Role).Count() != edits.Count)
        {
            throw new ArgumentException("Give each role at most once.", nameof(edits));
        }

        edits = [.. edits.Select(edit => edit with { Provider = Blank(edit.Provider), ModelId = Blank(edit.ModelId) })];

        var problems = new Dictionary<AiRole, RoleDefaultProblem>();

        foreach (var edit in edits)
        {
            if (ProblemOf(edit) is { } problem)
            {
                problems[edit.Role] = problem;
            }
        }

        if (problems.Count > 0)
        {
            return new RoleDefaultsResult(problems);
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        // Readers carry on, and two administrators' saves take turns, so neither adds a row the other just did.
        await context.Database.ExecuteSqlAsync($"LOCK TABLE role_defaults IN SHARE ROW EXCLUSIVE MODE", cancellationToken);

        var rows = await context.Set<RoleDefaultRow>().ToDictionaryAsync(row => row.Role, cancellationToken);
        var changed = new List<AiRole>();

        foreach (var edit in edits)
        {
            var row = rows.GetValueOrDefault(edit.Role);
            var (provider, model) = (row?.Provider, row?.ModelId);

            if (provider == edit.Provider && model == edit.ModelId)
            {
                continue;
            }

            // A role with no row and no default stays without a row, so the RoleDefaults section can still give
            // it one. A role that had one keeps its row with no provider, so the seeder never fills it again.
            if (row is null)
            {
                context.Add(new RoleDefaultRow { Role = edit.Role, Provider = edit.Provider, ModelId = edit.ModelId });
            }
            else
            {
                row.Provider = edit.Provider;
                row.ModelId = edit.ModelId;
            }

            audit.Add(
                context,
                ByAdministrator(AuditAction.RoleDefaultChanged, administratorId) with
                {
                    TargetType = RoleTarget,
                    TargetId = edit.Role.ToString(),
                    Changes =
                    [
                        new AuditChange(CatalogAuditFields.DefaultProvider, provider, edit.Provider),
                        new AuditChange(CatalogAuditFields.DefaultModel, model, edit.ModelId),
                    ],
                });

            changed.Add(edit.Role);
        }

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        if (changed.Count > 0)
        {
            logger.LogInformation(
                "Administrator {AdministratorId} changed the default of {Roles}", administratorId, string.Join(", ", changed));
        }

        return new RoleDefaultsResult(new Dictionary<AiRole, RoleDefaultProblem>());
    }

    private RoleDefaultProblem? ProblemOf(RoleDefaultEdit edit) =>
        edit.Provider is null && edit.ModelId is not null ? RoleDefaultProblem.ModelWithoutProvider
        : edit.Provider is not null && !catalog.Providers.Contains(edit.Provider, StringComparer.Ordinal) ? RoleDefaultProblem.UnknownProvider
        : edit.ModelId is not null && !CatalogRules.IsModelId(edit.ModelId) ? RoleDefaultProblem.InvalidModel
        : null;

    /// <summary>A blank name is no name, and the roles are distinct and known, in role order.</summary>
    private static ModelEdit Normalize(ModelEdit edit) => edit with
    {
        DisplayName = Blank(edit.DisplayName?.Trim()),
        RecommendedRoles = [.. edit.RecommendedRoles.Where(Enum.IsDefined).Distinct().Order()],
    };

    private static List<ModelEditField> Problems(ModelEdit edit)
    {
        var problems = new List<ModelEditField>();

        if (edit.DisplayName is { } name && !CatalogRules.IsText(name))
        {
            problems.Add(ModelEditField.DisplayName);
        }

        if (edit.Price is { } price)
        {
            if (!CatalogRules.IsPrice(price.InputPerMillion))
            {
                problems.Add(ModelEditField.InputPrice);
            }

            if (!CatalogRules.IsPrice(price.OutputPerMillion))
            {
                problems.Add(ModelEditField.OutputPrice);
            }
        }

        return problems;
    }

    /// <summary>The fields that differ, before and after. A null means inherited.</summary>
    private static List<AuditChange> Changes(ModelEdit before, ModelEdit after)
    {
        var changes = new List<AuditChange>();

        void Compare(string field, string? from, string? to)
        {
            if (from != to)
            {
                changes.Add(new AuditChange(field, from, to));
            }
        }

        Compare(CatalogAuditFields.DisplayName, before.DisplayName, after.DisplayName);
        Compare(CatalogAuditFields.Hidden, before.Hidden.ToString(), after.Hidden.ToString());

        foreach (var capability in Enum.GetValues<ModelCapability>())
        {
            Compare(
                CatalogAuditFields.CapabilityPrefix + capability,
                before.Capabilities.Of(capability)?.ToString(),
                after.Capabilities.Of(capability)?.ToString());
        }

        Compare(CatalogAuditFields.InputPrice, Price(before.Price?.InputPerMillion), Price(after.Price?.InputPerMillion));
        Compare(CatalogAuditFields.OutputPrice, Price(before.Price?.OutputPerMillion), Price(after.Price?.OutputPerMillion));
        Compare(CatalogAuditFields.RecommendedRoles, Roles(before.RecommendedRoles), Roles(after.RecommendedRoles));

        return changes;
    }

    private static AuditEntry ByAdministrator(AuditAction action, Guid administratorId) =>
        new() { Action = action, ActorKind = AuditActorKind.Administrator, ActorId = administratorId };

    private static string? Price(decimal? value) => value?.ToString(CultureInfo.InvariantCulture);

    private static string? Roles(IReadOnlyCollection<AiRole> roles) =>
        roles.Count == 0 ? null : string.Join(",", roles.Order().Select(role => role.ToString()));

    private static string? Blank(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static int ErrorCount(string? errors)
    {
        if (errors is null)
        {
            return 0;
        }

        using var document = JsonDocument.Parse(errors);

        return document.RootElement.ValueKind == JsonValueKind.Array ? document.RootElement.GetArrayLength() : 0;
    }
}
