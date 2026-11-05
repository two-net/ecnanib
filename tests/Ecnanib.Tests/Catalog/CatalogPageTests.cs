using System.Net;
using Ecnanib.Kernel;
using Ecnanib.Modules.Administration.Contracts;
using Ecnanib.Modules.Catalog.Contracts;
using Ecnanib.Tests.Identity;
using Ecnanib.Tests.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Catalog;

/// <summary>
/// FR-CAT-07 and FR-ADM-03 through the pages: users see the models that are offered, filtered, with the keys of
/// theirs that can use each one, and only administrators reach the controls, on the page and behind the contract.
/// </summary>
public sealed class CatalogPageTests(PostgresFixture postgres)
{
    private const string Secret = "a bootstrap secret of plenty of characters";
    private const string AdminPage = "/admin/catalog";
    private const string ModelPage = "/admin/catalog/model?provider=Test&model=model-a";

    private static CancellationToken Cancellation => CatalogApp.Cancellation;

    /// <summary>
    /// A hidden model isn't listed. A model only someone else's key can use is listed, with none of the user's
    /// keys, and the labels the user chose show as text.
    /// </summary>
    [Fact]
    public async Task The_page_lists_offered_models_with_the_keys_that_can_use_them()
    {
        await using var setup = await SetUpAsync();
        var app = setup.App;
        app.Lists(CatalogApp.OtherAiKey, "model-c");
        await app.AddAsync(Guid.NewGuid(), CatalogApp.OtherAiKey);
        await app.RunScheduleAsync();
        await app.Administration.SaveModelAsync(CatalogApp.AdministratorId, CatalogApp.Provider, "model-b", ModelEdit.None with { Hidden = true }, Cancellation);

        var page = await setup.User.PageAsync("/catalog");

        Assert.Contains("Personal", RowOf(page, "model-a"), StringComparison.Ordinal);
        Assert.Contains("None", RowOf(page, "model-c"), StringComparison.Ordinal);
        Assert.DoesNotContain("model-b", page, StringComparison.Ordinal);
        Assert.Contains("Showing 2 of 2 models", page, StringComparison.Ordinal);
        Assert.Contains("href=\"/catalog\"", await setup.User.PageAsync("/agents"), StringComparison.Ordinal);
    }

    /// <summary>A capability matches only a model known to have it, and the filters can be bookmarked.</summary>
    [Fact]
    public async Task The_page_filters_by_provider_capability_and_status()
    {
        await using var setup = await SetUpAsync(new()
        {
            ["Catalog:Overrides:0:Provider"] = CatalogApp.Provider,
            ["Catalog:Overrides:0:Model"] = "model-b",
            ["Catalog:Overrides:0:ToolUse"] = "true",
        });
        var app = setup.App;
        app.Lists(CatalogApp.ThirdAiKey, "model-z");
        await app.AddAsync(setup.UserId, CatalogApp.ThirdAiKey, CatalogApp.Second);
        await app.RunScheduleAsync();

        var tools = await setup.User.PageAsync("/catalog?capability=ToolUse");
        var second = await setup.User.PageAsync("/catalog?provider=Second");
        var retired = await setup.User.PageAsync("/catalog?status=Retired");
        var nonsense = await setup.User.PageAsync("/catalog?provider=Nobody&status=Hidden&capability=Flying");

        Assert.Equal((false, true), (tools.Contains("model-a", StringComparison.Ordinal), tools.Contains("model-b", StringComparison.Ordinal)));
        Assert.Equal((false, true), (second.Contains("model-a", StringComparison.Ordinal), second.Contains("model-z", StringComparison.Ordinal)));
        Assert.Contains("No models match these filters.", retired, StringComparison.Ordinal);
        Assert.Contains("Showing 3 of 3 models", nonsense, StringComparison.Ordinal);
        Assert.Contains("<option value=\"ToolUse\" selected", tools, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_visitor_is_sent_to_sign_in()
    {
        await using var setup = await SetUpAsync();
        using var visitor = Visitor.To(setup.App.App);

        foreach (var path in new[] { "/catalog", AdminPage, ModelPage })
        {
            using var response = await visitor.GetAsync(path);

            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.StartsWith("/sign-in", Visitor.Target(response), StringComparison.Ordinal);
        }
    }

    /// <summary>FR-ADM-03: anyone else gets 403 from the pages, and nothing from the contract, which changes nothing.</summary>
    [Fact]
    public async Task Anyone_else_is_refused_by_the_pages_and_the_contract()
    {
        await using var setup = await SetUpAsync();
        var controls = Controls(setup.App);
        var user = setup.UserId;

        foreach (var path in new[] { AdminPage, ModelPage })
        {
            using var response = await setup.User.GetAsync(path);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        Assert.Null(await controls.SyncAllAsync(user, Cancellation));
        Assert.Null(await controls.ListRunsAsync(user, 10, Cancellation));
        Assert.Null(await controls.GetModelAsync(user, CatalogApp.Provider, "model-a", Cancellation));
        Assert.Null(await controls.SaveModelAsync(user, CatalogApp.Provider, "model-a", ModelEdit.None with { Hidden = true }, Cancellation));
        Assert.Null(await controls.SaveRoleDefaultsAsync(user, [new RoleDefaultEdit(AiRole.Strategist, "Test", null)], Cancellation));
        Assert.Equal(0L, await Sql.ScalarAsync<long>(setup.App.ConnectionString, "SELECT count(*) FROM model_overrides", Cancellation));
        Assert.Equal(0L, await Sql.ScalarAsync<long>(setup.App.ConnectionString, "SELECT count(*) FROM role_defaults", Cancellation));
        Assert.DoesNotContain($"href=\"{AdminPage}\"", await setup.User.PageAsync("/account"), StringComparison.Ordinal);
    }

    /// <summary>The contract checks an administrator itself, because a circuit has no middleware.</summary>
    [Fact]
    public async Task An_administrator_gets_through_the_contract()
    {
        await using var setup = await SetUpAsync();
        var controls = Controls(setup.App);

        Assert.Equal(SyncAllOutcome.Started, await controls.SyncAllAsync(setup.AdministratorId, Cancellation));
        Assert.NotNull(await controls.ListRunsAsync(setup.AdministratorId, 10, Cancellation));
        Assert.NotNull(await controls.GetModelAsync(setup.AdministratorId, CatalogApp.Provider, "model-a", Cancellation));
        Assert.Contains($"href=\"{AdminPage}\"", await setup.Administrator.PageAsync("/account"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_administrator_edits_a_model_on_its_page()
    {
        await using var setup = await SetUpAsync();

        using var response = await setup.Administrator.SubmitRepeatedAsync(
            ModelPage,
            "model-edit",
            [
                ("Input.DisplayName", "Renamed A"),
                ("Input.Hidden", "true"),
                ("Input.ToolUse", "true"),
                ("Input.InputPrice", "1.5"),
                ("Input.OutputPrice", "6"),
                ("Input.Strategist", "true"),
            ]);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/admin/catalog?saved=model", Visitor.Target(response));

        var model = (await setup.App.Catalog.ListAsync(Cancellation)).Single(candidate => candidate.ModelId == "model-a");
        Assert.Equal(("Renamed A", true, true, new ModelPrice(1.5m, 6m)), (model.DisplayName, model.Hidden, model.Capabilities.ToolUse, model.Price));
        Assert.Equal([AiRole.Strategist], model.RecommendedRoles);

        var page = await setup.Administrator.PageAsync(ModelPage);
        Assert.Contains("value=\"Renamed A\"", page, StringComparison.Ordinal);
        Assert.Contains("Inherited: Model model-a", page, StringComparison.Ordinal);
        Assert.Contains("Renamed A", RowOf(await setup.Administrator.PageAsync(AdminPage + "?status=Hidden"), "model-a"), StringComparison.Ordinal);
    }

    /// <summary>A cost needs both prices, and an invalid form changes nothing.</summary>
    [Fact]
    public async Task A_model_form_with_one_price_changes_nothing()
    {
        await using var setup = await SetUpAsync();

        using var response = await setup.Administrator.SubmitAsync(ModelPage, "model-edit", ("Input.InputPrice", "2"));
        var page = await response.Content.ReadAsStringAsync(Cancellation);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Some fields need correcting. Nothing changed.", page, StringComparison.Ordinal);
        Assert.Equal(0L, await Sql.ScalarAsync<long>(setup.App.ConnectionString, "SELECT count(*) FROM model_overrides", Cancellation));
    }

    [Fact]
    public async Task An_unknown_model_is_not_found()
    {
        await using var setup = await SetUpAsync();

        using var response = await setup.Administrator.GetAsync("/admin/catalog/model?provider=Test&model=model-gone");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task An_administrator_sets_the_roles_defaults()
    {
        await using var setup = await SetUpAsync();
        var fields = Enum.GetValues<AiRole>().SelectMany(role => new[]
        {
            ($"Roles.{role}Provider", role == AiRole.Strategist ? CatalogApp.Provider : ""),
            ($"Roles.{role}Model", role == AiRole.Strategist ? "model-b" : ""),
        });

        using var response = await setup.Administrator.SubmitRepeatedAsync(AdminPage, "role-defaults", fields);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/admin/catalog?saved=roles", Visitor.Target(response));
        Assert.Equal(
            [new RoleDefault(AiRole.Strategist, CatalogApp.Provider, "model-b")],
            await setup.App.Catalog.ListRoleDefaultsAsync(Cancellation));
        Assert.Contains("The defaults are saved.", await setup.Administrator.PageAsync(AdminPage + "?saved=roles"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_invalid_default_is_named_and_changes_nothing()
    {
        await using var setup = await SetUpAsync();

        using var response = await setup.Administrator.SubmitRepeatedAsync(
            AdminPage, "role-defaults", [("Roles.StrategistProvider", CatalogApp.Provider), ("Roles.StrategistModel", "model b")]);
        var page = await response.Content.ReadAsStringAsync(Cancellation);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Enter one model&#x27;s exact ID", page, StringComparison.Ordinal);
        Assert.Empty(await setup.App.Catalog.ListRoleDefaultsAsync(Cancellation));
    }

    /// <summary>FR-CAT-03: the button starts a sync of everyone's keys, which is audited and shows among the runs.</summary>
    [Fact]
    public async Task An_administrator_syncs_every_key()
    {
        await using var setup = await SetUpAsync();

        using var response = await setup.Administrator.SubmitAsync(AdminPage, "catalog-sync");
        await setup.App.UntilFinishedAsync("catalog-sync-all", 1);

        Assert.Equal("/admin/catalog?sync=started", Visitor.Target(response));
        Assert.Equal(1L, await Sql.ScalarAsync<long>(setup.App.ConnectionString, "SELECT count(*) FROM audit_events WHERE action = 'CatalogSyncRequested'", Cancellation));
        Assert.Contains("Administrator", await setup.Administrator.PageAsync(AdminPage), StringComparison.Ordinal);
    }

    /// <summary>The table row that names a model ID, from its opening tag to its closing one.</summary>
    private static string RowOf(string page, string modelId)
    {
        var decoded = WebUtility.HtmlDecode(page);
        var id = decoded.IndexOf($">{modelId}<", StringComparison.Ordinal);
        Assert.True(id >= 0, $"No row for {modelId}.");

        var start = decoded.LastIndexOf("<tr", id, StringComparison.Ordinal);
        var end = decoded.IndexOf("</tr>", id, StringComparison.Ordinal);

        return decoded[start..end];
    }

    private static ICatalogControls Controls(CatalogApp app) => app.App.Services.GetRequiredService<ICatalogControls>();

    /// <summary>
    /// An administrator confirmed with the bootstrap secret, and a user whose key lists model-a and model-b,
    /// both signed in, after the schedule has synced.
    /// </summary>
    private async Task<Setup> SetUpAsync(Dictionary<string, string?>? settings = null)
    {
        var administratorEmail = Visitor.NewEmail();
        var all = new Dictionary<string, string?> { ["Admin:AdminEmails:0"] = administratorEmail, ["Admin:BootstrapSecret"] = Secret };

        foreach (var (key, value) in settings ?? [])
        {
            all[key] = value;
        }

        var app = new CatalogApp(postgres, all, observers: false);
        var administrator = Visitor.To(app.App);
        await administrator.SignUpAsync(administratorEmail);
        (await administrator.SubmitAsync("/account/administrator", Visitor.ConfirmForm, ("Input.Secret", Secret))).Dispose();

        var user = Visitor.To(app.App);
        var userEmail = await user.SignUpAsync();
        var userId = await AccountRows.IdAsync(app.ConnectionString, userEmail, Cancellation);
        app.Lists(CatalogApp.AiKey, "model-a", "model-b");
        await app.AddAsync(userId);
        await app.RunScheduleAsync();

        return new Setup(app, administrator, await AccountRows.IdAsync(app.ConnectionString, administratorEmail, Cancellation), user, userId);
    }

    private sealed record Setup(CatalogApp App, Visitor Administrator, Guid AdministratorId, Visitor User, Guid UserId) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Administrator.Dispose();
            User.Dispose();
            await App.DisposeAsync();
        }
    }
}
