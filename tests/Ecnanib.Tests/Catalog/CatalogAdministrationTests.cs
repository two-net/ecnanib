using System.Net;
using System.Net.Http;
using Ecnanib.Kernel;
using Ecnanib.Modules.Catalog.Contracts;
using Ecnanib.Tests.Ai;
using Ecnanib.Tests.Persistence;

namespace Ecnanib.Tests.Catalog;

/// <summary>
/// FR-CAT-05, FR-CAT-08 and FR-ADM-03 behind ICatalogAdministration: administrators' overrides are a third
/// layer above the configuration and the provider, persist across syncs, and are audited. Administration checks
/// who's asking before it calls, which CatalogPageTests covers.
/// </summary>
public sealed class CatalogAdministrationTests(PostgresFixture postgres)
{
    private static readonly Guid Administrator = CatalogApp.AdministratorId;

    private static CancellationToken Cancellation => CatalogApp.Cancellation;

    /// <summary>Field by field: the administrator's, else the configuration's, else the provider's.</summary>
    [Fact]
    public async Task An_administrators_overrides_beat_the_configuration_and_the_provider_and_outlast_a_sync()
    {
        await using var app = await SyncedAsync(new()
        {
            ["Catalog:Overrides:0:Provider"] = CatalogApp.Provider,
            ["Catalog:Overrides:0:Model"] = "model-a",
            ["Catalog:Overrides:0:DisplayName"] = "Configured A",
            ["Catalog:Overrides:0:InputPricePerMillion"] = "1",
            ["Catalog:Overrides:0:OutputPricePerMillion"] = "2",
            ["Catalog:Overrides:0:ImageInput"] = "true",
            ["Catalog:Overrides:0:ToolUse"] = "true",
        });
        var edit = new ModelEdit(
            "Chosen A",
            Hidden: false,
            new ModelCapabilities(ImageInput: false, null, null, LiveSearch: true, null),
            new ModelPrice(3m, 4.5m),
            [AiRole.RiskCritic, AiRole.Strategist]);

        Assert.Equal(ModelEditOutcome.Saved, (await SaveAsync(app, "model-a", edit)).Outcome);
        await app.RunScheduleAsync();

        var model = await ModelAsync(app, "model-a");
        Assert.Equal(("Chosen A", new ModelPrice(3m, 4.5m)), (model.DisplayName, model.Price));
        Assert.Equal(new ModelCapabilities(ImageInput: false, null, ToolUse: true, LiveSearch: true, null), model.Capabilities);
        Assert.Equal([AiRole.Strategist, AiRole.RiskCritic], model.RecommendedRoles);
        Assert.Equal(("Model model-b", null), ((await ModelAsync(app, "model-b")).DisplayName, (await ModelAsync(app, "model-b")).Price));

        var detail = await app.Administration.GetModelAsync(CatalogApp.Provider, "model-a", Cancellation);
        Assert.NotNull(detail);
        Assert.Equal(model, detail.Model with { RecommendedRoles = model.RecommendedRoles });
        Assert.Equal("Model model-a", detail.Reported.DisplayName);
        Assert.Equal(("Configured A", new ModelPrice(1m, 2m)), (detail.Configured?.DisplayName, detail.Configured?.Price));
        Assert.Equal(("Chosen A", new ModelPrice(3m, 4.5m)), (detail.Overridden.DisplayName, detail.Overridden.Price));
        Assert.Equal([AiRole.Strategist, AiRole.RiskCritic], detail.Overridden.RecommendedRoles);
    }

    /// <summary>Hiding is an override, so a model's status stays its lifecycle, and the pages show it as Hidden.</summary>
    [Fact]
    public async Task Hiding_and_showing_a_model_leaves_its_status_alone()
    {
        await using var app = await SyncedAsync();

        await SaveAsync(app, "model-a", ModelEdit.None with { Hidden = true });
        var hidden = await ModelAsync(app, "model-a");

        Assert.Equal((true, ModelStatus.Active, ModelStatus.Hidden), (hidden.Hidden, hidden.Status, hidden.ShownStatus));

        await SaveAsync(app, "model-a", ModelEdit.None);
        var shown = await ModelAsync(app, "model-a");

        Assert.Equal((false, ModelStatus.Active), (shown.Hidden, shown.ShownStatus));
    }

    /// <summary>An edit that sets nothing leaves nothing behind, so the model inherits every field again.</summary>
    [Fact]
    public async Task Setting_every_field_back_removes_the_overrides()
    {
        await using var app = await SyncedAsync();

        await SaveAsync(app, "model-a", new ModelEdit("Chosen A", true, ModelCapabilities.Unknown with { ToolUse = true }, new ModelPrice(1m, 2m), [AiRole.Reviewer]));
        Assert.Equal(1L, await OverrideCountAsync(app));

        await SaveAsync(app, "model-a", ModelEdit.None with { DisplayName = "  " });

        Assert.Equal(0L, await OverrideCountAsync(app));
        Assert.Equal("Model model-a", (await ModelAsync(app, "model-a")).DisplayName);
    }

    /// <summary>The configuration's rules apply to administrators too, and an invalid edit changes nothing.</summary>
    [Fact]
    public async Task An_invalid_edit_changes_nothing()
    {
        await using var app = await SyncedAsync();

        var result = await SaveAsync(app, "model-a", ModelEdit.None with { DisplayName = new string('x', 101), Price = new ModelPrice(-1m, 1_000_001m) });
        var control = await SaveAsync(app, "model-a", ModelEdit.None with { DisplayName = "Model\u0000A" });

        Assert.Equal(ModelEditOutcome.Invalid, result.Outcome);
        Assert.Equal([ModelEditField.DisplayName, ModelEditField.InputPrice, ModelEditField.OutputPrice], result.Problems);
        Assert.Equal([ModelEditField.DisplayName], control.Problems);
        Assert.Equal(ModelEditOutcome.NotFound, (await SaveAsync(app, "model-unlisted", ModelEdit.None with { Hidden = true })).Outcome);
        Assert.Null(await app.Administration.GetModelAsync(CatalogApp.Provider, "model-unlisted", Cancellation));
        Assert.Equal(0L, await OverrideCountAsync(app));
        Assert.Equal(0L, await AuditCountAsync(app, "CatalogModelEdited"));
    }

    /// <summary>
    /// FR-AUD-01: each edit is audited as the administrator, platform-wide, with what changed, and an edit that
    /// changes nothing isn't.
    /// </summary>
    [Fact]
    public async Task Each_edit_is_audited_with_what_changed()
    {
        await using var app = await SyncedAsync();
        var edit = ModelEdit.None with { Hidden = true, Price = new ModelPrice(1.25m, 10m) };

        await SaveAsync(app, "model-a", edit);
        await SaveAsync(app, "model-a", edit);

        Assert.Equal(
            [$"CatalogModelEdited Administrator {Administrator} none Model {CatalogApp.Provider}/model-a"],
            await Sql.ColumnAsync(
                app.ConnectionString,
                "SELECT concat_ws(' ', action, actor_kind, actor_id, coalesce(user_id::text, 'none'), target_type, target_id) FROM audit_events WHERE action = 'CatalogModelEdited'",
                Cancellation));

        var summary = await Sql.ScalarAsync<string>(app.ConnectionString, "SELECT summary::text FROM audit_events WHERE action = 'CatalogModelEdited'", Cancellation);
        Assert.Contains(CatalogAuditFields.Hidden, summary, StringComparison.Ordinal);
        Assert.Contains(CatalogAuditFields.InputPrice, summary, StringComparison.Ordinal);
        Assert.Contains("1.25", summary, StringComparison.Ordinal);
        Assert.DoesNotContain(CatalogAuditFields.DisplayName, summary, StringComparison.Ordinal);
    }

    /// <summary>FR-CAT-03: an administrator's sync of everyone's keys is audited when it starts, and only then.</summary>
    [Fact]
    public async Task A_sync_of_everyone_is_audited_only_when_it_starts()
    {
        await using var app = new CatalogApp(postgres, observers: false);
        await app.AddAsync(Guid.NewGuid());
        var release = new TaskCompletionSource();
        app.Ai.On(HttpMethod.Get, CatalogApp.ModelsPath, async (_, token) =>
        {
            await release.Task.WaitAsync(token);
            return FakeAiProvider.Models(null, "model-a");
        });

        Assert.Equal(SyncAllOutcome.Started, await app.Administration.SyncAllAsync(Administrator, Cancellation));
        await Logging.LoggedApp.UntilAsync(async () => (await app.RunsAsync()).Count == 1);
        Assert.Equal(SyncAllOutcome.AlreadyRunning, await app.Administration.SyncAllAsync(Administrator, Cancellation));
        release.SetResult();
        await app.UntilFinishedAsync("catalog-sync-all", 1);

        Assert.Equal(1L, await AuditCountAsync(app, "CatalogSyncRequested"));
    }

    /// <summary>FR-CAT-08: the runs, newest first, with a count of their errors and nothing of what they say.</summary>
    [Fact]
    public async Task The_runs_are_listed_newest_first_with_their_errors_counted()
    {
        await using var app = await SyncedAsync();
        app.Answers(CatalogApp.AiKey, () => FakeAiProvider.Error(HttpStatusCode.ServiceUnavailable, "busy", "down"));
        await app.RunScheduleAsync();

        // Every provider has a row per run, with or without keys.
        var all = await app.Administration.ListRunsAsync(100, Cancellation);
        var runs = all.Where(run => run.Provider == CatalogApp.Provider).ToList();

        Assert.Equal([(1, 1), (1, 0)], runs.Select(run => (run.Keys, run.Errors)));
        Assert.Equal([1, 0], runs.Select(run => run.KeysFailed));
        Assert.Equal(all.OrderByDescending(run => run.Id), all);
        Assert.All(all, run => Assert.Equal(CatalogSyncTrigger.Schedule, run.Trigger));
        Assert.Single(await app.Administration.ListRunsAsync(1, Cancellation));
    }

    /// <summary>One key that lists model-a and model-b, synced by the schedule.</summary>
    private async Task<CatalogApp> SyncedAsync(Dictionary<string, string?>? settings = null)
    {
        var app = new CatalogApp(postgres, settings, observers: false);
        app.Lists(CatalogApp.AiKey, "model-a", "model-b");
        await app.AddAsync(Guid.NewGuid());
        await app.RunScheduleAsync();

        return app;
    }

    private static Task<ModelEditResult> SaveAsync(CatalogApp app, string model, ModelEdit edit) =>
        app.Administration.SaveModelAsync(Administrator, CatalogApp.Provider, model, edit, Cancellation);

    private static async Task<CatalogModel> ModelAsync(CatalogApp app, string model) =>
        (await app.Catalog.ListAsync(Cancellation)).Single(candidate => candidate.Provider == CatalogApp.Provider && candidate.ModelId == model);

    private static Task<long> OverrideCountAsync(CatalogApp app) =>
        Sql.ScalarAsync<long>(app.ConnectionString, "SELECT count(*) FROM model_overrides", Cancellation);

    private static Task<long> AuditCountAsync(CatalogApp app, string action) =>
        Sql.ScalarAsync<long>(app.ConnectionString, $"SELECT count(*) FROM audit_events WHERE action = '{action}'", Cancellation);
}
