using System.Net;
using Ecnanib.Accounts;
using Ecnanib.AiProviderGateway;
using Ecnanib.Credentials;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.Tests.Logging;
using Ecnanib.Tests.Settings;
using Ecnanib.WebUi;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Catalog;

/// <summary>
/// An Operator can trigger a catalog sync (FR-OPS-4): the catalog is synced on demand, outside
/// its schedule, with the key that the configuration file holds for each provider (FR-CAT-2,
/// FR-CAT-3). That an operator asked for it is recorded in the audit trail, and the page shows
/// no key and nothing of any user (FR-OPS-5). What a sync does to the catalog is in
/// <c>CatalogSyncTests</c>.
/// </summary>
public class OperatorCatalogSyncTests
{
    private const string Ada = "ada@example.com";
    private const string Olive = "olive@example.com";

    private const string SyncPage = WebUiModule.OperatorCatalogSyncPath;
    private const string SyncForm = "sync-catalog";
    private const string SyncButton = "<button type=\"submit\">Sync the catalog now</button>";
    private const string Target = "catalog:models";

    private const string AskedTemplate = "User {UserId} asked for a sync of the model catalog, outside its schedule";

    private const string NothingTemplate =
        "User {UserId} asked for a sync of the model catalog, and the configuration file holds a catalog key for no provider: nothing is synced";

    private static readonly DateTimeOffset Morning = new(2026, 11, 26, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    [ShowsAuditOf(AuditAction.CatalogSyncRequested)]
    public async Task An_operator_has_the_catalog_synced_and_that_is_done_recorded_and_shown()
    {
        var clock = new TestClock(Morning);
        using var application = Application(clock, host: host => host.From("203.0.113.7"));
        using var client = application.CreateClient();
        var olive = await application.RegisterVerifiedAsync(Olive);
        await client.SignInAsync(Olive);
        await SyncedAtTheStartAsync(application);

        // The screen of an operator leads to the page, which says what a sync does, shows the
        // sync of the start, and has the one button.
        Assert.Matches($"<a href=\"{SyncPage}\"[^>]*>Sync the model catalog</a>", await client.PageAsync(WebUiModule.OperatorStatusPath));

        var before = await client.PageAsync(SyncPage);

        Assert.Contains("<h1>Sync the model catalog</h1>", before);
        Assert.Contains("a model that the list of its provider lacks in 3 syncs in a row is marked unavailable", before);
        Assert.Contains("26 November 2026, 09:00 UTC, on the schedule", before);
        Assert.Contains(SyncButton, before);
        Assert.DoesNotContain("The sync that you asked for is over.", before);

        // The provider lists a model more by now.
        application.ModelLists.Answer = (provider, _) => StandInModelLists.Listed(
            StandInModelLists.Model($"{provider}-test-model"),
            StandInModelLists.Model($"{provider}-test-model-mini"),
            StandInModelLists.Model($"{provider}-test-model-new"));
        clock.Set(Morning.AddMinutes(5));

        var (path, page) = await client.SubmitAsync(SyncPage, SyncForm);

        // She is back on the page, by a request that syncs nothing again, and it says that the
        // sync is over and how it ended.
        Assert.Equal(SyncPage, path);
        Assert.Contains("<p class=\"form-done\" role=\"status\">The sync that you asked for is over. The table says how it ended for each provider.</p>", page);
        Assert.Contains("26 November 2026, 09:05 UTC, on demand", page);
        Assert.Contains("1 added, 0 changed, 0 marked unavailable", page);
        Assert.Contains(SyncButton, page);

        // The sync is a row of the catalog's own, which says that it was asked for (FR-CAT-7).
        Assert.Equal(
            [
                new StoredCatalogSync("claude", "Scheduled", Morning, Morning, "Succeeded", null, Added: 2, Changed: 0, Unavailable: 0),
                new StoredCatalogSync("claude", "OnDemand", Morning.AddMinutes(5), Morning.AddMinutes(5), "Succeeded", null, Added: 1, Changed: 0, Unavailable: 0),
            ],
            StoredCatalogSync.All(application.Database));
        Assert.Equal(3, StoredAiModel.Of(application.Database, "claude").Count);

        // And who asked for it is an event of the audit trail: who, what, when, and from where
        // (FR-OPS-5). The row of the sync names no user, and the event names no provider.
        var recorded = Assert.Single(StoredAuditEvent.All(application.Database), audited => audited.Target == Target);

        Assert.Equal(
            (olive.Id, "CatalogSyncRequested", Morning.AddMinutes(5), "203.0.113.7"),
            (recorded.UserId, recorded.Action, recorded.OccurredAt, recorded.Origin));

        // The log says it by the id of the account, and no more.
        var said = Assert.Single(application.Events(), written => written.Template == AskedTemplate);

        Assert.Equal(olive.Id.ToString(), said.Text("UserId"));

        // A reload of the page that she is on asks for no second sync.
        Assert.Contains("The sync that you asked for is over.", await client.PageAsync(WebUiModule.OperatorCatalogSyncedPath));

        Assert.Equal(2, StoredCatalogSync.All(application.Database).Count);
        Assert.Single(StoredAuditEvent.All(application.Database), audited => audited.Target == Target);
    }

    [Fact]
    public async Task A_sync_that_fails_for_a_provider_is_recorded_as_asked_for_and_shown_as_failed()
    {
        var clock = new TestClock(Morning);
        using var application = Application(clock);
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Olive);
        await client.SignInAsync(Olive);
        await SyncedAtTheStartAsync(application);

        application.ModelLists.Answer = (_, _) => StandInModelLists.Ended(AiModelListOutcome.TimedOut);
        clock.Set(Morning.AddMinutes(5));

        var (path, page) = await client.SubmitAsync(SyncPage, SyncForm);

        // The sync was made, and failed: the page says so, with the one that still stands.
        Assert.Equal(SyncPage, path);
        Assert.Contains("The sync that you asked for is over.", page);
        Assert.Contains("26 November 2026, 09:05 UTC, on demand", page);
        Assert.Contains("Failed: the provider took too long", page);
        Assert.Contains("<td>26 November 2026, 09:00 UTC</td>", page);

        // It was asked for all the same, and that is what the trail records.
        Assert.Single(StoredAuditEvent.All(application.Database), audited => audited.Action == "CatalogSyncRequested");
        Assert.Equal("Failed", StoredCatalogSync.All(application.Database)[^1].Result);
    }

    [Fact]
    public async Task Where_the_file_holds_a_key_for_no_provider_there_is_nothing_to_sync_and_nothing_is_recorded()
    {
        using var application = new AccountsApplication(clock: new TestClock(Morning), settings: Operators);
        using var client = application.CreateClient();
        var olive = await application.RegisterVerifiedAsync(Olive);
        await client.SignInAsync(Olive);

        // The page says so, in place of the button.
        var page = await client.PageAsync(SyncPage);

        Assert.Contains("The configuration file holds a key for no provider, so there is nothing to sync on demand", page);
        Assert.DoesNotContain(SyncButton, page);

        // A form that is sent all the same asks no provider, and leads back to the page as it was.
        var (path, again) = await client.PostAsync(SyncPage, SyncForm, Browser.AntiforgeryTokenIn(page));

        Assert.Equal(SyncPage, path);
        Assert.DoesNotContain("The sync that you asked for is over.", again);
        Assert.Empty(application.ModelLists.Asked);
        Assert.Empty(StoredCatalogSync.All(application.Database));

        // Nothing was done, so nothing is recorded. The log says who asked.
        Assert.DoesNotContain(StoredAuditEvent.All(application.Database), audited => audited.Target == Target);
        Assert.Equal(olive.Id.ToString(), Assert.Single(application.Events(), written => written.Template == NothingTemplate).Text("UserId"));
    }

    [Fact]
    public async Task A_user_who_is_not_an_operator_reaches_neither_the_page_nor_the_sync_and_a_visitor_is_sent_to_sign_in()
    {
        using var application = Application(new TestClock(Morning));
        using var visitor = application.CreateClient();
        using var user = application.CreateClient();
        var ada = await application.RegisterVerifiedAsync(Ada);
        await user.SignInAsync(Ada);
        await SyncedAtTheStartAsync(application);

        using (var refused = await user.GetAsync(SyncPage, TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Contains("<h1>Page not found</h1>", await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }

        using (var posted = await user.SendFormAsync(SyncPage, SyncForm, Browser.AntiforgeryTokenIn(await user.PageAsync("/account"))))
        {
            Assert.Equal(HttpStatusCode.Forbidden, posted.StatusCode);
        }

        Assert.Contains(
            application.Events(),
            written => written.Level == "Warning"
                && written.Template == "User {UserId} was refused {Path}, which is not for them"
                && written.Text("UserId") == ada.Id.ToString());

        // A visitor is sent to sign in, and a form that they post is not looked at.
        Assert.Equal(SessionAuthentication.PageWith(SessionAuthentication.SignInPath, SyncPage), (await visitor.OpenAsync(SyncPage)).Address);

        var (path, _) = await visitor.PostAsync(
            SyncPage, SyncForm, Browser.AntiforgeryTokenIn(await visitor.PageAsync(SessionAuthentication.SignInPath)));

        Assert.Equal(SessionAuthentication.SignInPath, path);

        // Nobody asked a provider for anything but the sync of the start, and nothing is recorded.
        Assert.Single(StoredCatalogSync.All(application.Database));
        Assert.DoesNotContain(StoredAuditEvent.All(application.Database), audited => audited.Target == Target);
    }

    [Fact]
    public async Task A_form_without_the_token_of_its_page_syncs_nothing()
    {
        using var application = Application(new TestClock(Morning));
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Olive);
        await client.SignInAsync(Olive);
        await SyncedAtTheStartAsync(application);

        // From a page of another site, which has no token of this one (NFR-SEC-5).
        using var answer = await client.SendFormAsync(SyncPage, SyncForm, "not-the-token");

        Assert.Equal(HttpStatusCode.BadRequest, answer.StatusCode);
        Assert.Single(StoredCatalogSync.All(application.Database));
        Assert.DoesNotContain(StoredAuditEvent.All(application.Database), audited => audited.Target == Target);
    }

    [Fact]
    public async Task What_has_the_catalog_synced_asks_again_whether_the_user_of_its_request_is_an_operator()
    {
        // Something that reaches the sync past its page, as nothing of the application does: it
        // has the catalog synced for whoever sent the request.
        const string direct = "/syncs-the-catalog-directly";

        using var application = Application(new TestClock(Morning), host: host => host.HandleAtTheEnd(async request =>
        {
            if (!request.Request.Path.StartsWithSegments(direct))
            {
                return false;
            }

            var synced = await request.RequestServices.GetRequiredService<IOperatorCatalogSync>().SyncAsync(request);
            await request.Response.WriteAsync(synced is null ? "Refused" : $"Synced {synced.Count}");

            return true;
        }));
        using var visitor = application.CreateClient();
        using var user = application.CreateClient();
        using var anOperator = application.CreateClient();
        var ada = await application.RegisterVerifiedAsync(Ada);
        await application.RegisterVerifiedAsync(Olive);
        await user.SignInAsync(Ada);
        await anOperator.SignInAsync(Olive);
        await SyncedAtTheStartAsync(application);

        // Nobody who is not an operator has it synced, signed in or not (NFR-SEC-3).
        Assert.Equal("Refused", await visitor.PageAsync(direct));
        Assert.Equal("Refused", await user.PageAsync(direct));
        Assert.Single(StoredCatalogSync.All(application.Database));
        Assert.DoesNotContain(StoredAuditEvent.All(application.Database), audited => audited.Target == Target);

        // It says so in the log, by the account of whoever asked, where there is one.
        Assert.Contains(
            application.Events(),
            written => written.Level == "Warning"
                && written.Template == "User {UserId} was refused a sync of the model catalog, which is for an operator"
                && written.Text("UserId") == ada.Id.ToString());

        // An operator has it synced, for the one provider that the file holds a key for.
        Assert.Equal("Synced 1", await anOperator.PageAsync(direct));
        Assert.Equal(2, StoredCatalogSync.All(application.Database).Count);
        Assert.Single(StoredAuditEvent.All(application.Database), audited => audited.Target == Target);
    }

    [Fact]
    public async Task The_page_shows_no_key_and_nothing_of_any_user()
    {
        var clock = new TestClock(Morning);
        using var application = Application(clock);
        using var client = application.CreateClient();
        var ada = await application.RegisterVerifiedAsync(Ada);
        var olive = await application.RegisterVerifiedAsync(Olive);
        await client.SignInAsync(Olive);
        await SyncedAtTheStartAsync(application);

        var seen = new List<string>
        {
            await client.PageAsync(SyncPage),
            (await client.SubmitAsync(SyncPage, SyncForm)).Page,
        };

        // The key of the service that the sync was made with is said to be there, and is not
        // shown. Nor is anything of a user, or who asked for the sync (FR-OPS-5).
        Assert.All(seen, page =>
        {
            Assert.Contains("The key of the service, on the schedule and on demand", page);
            Assert.DoesNotContain(SettingsJson.CatalogKey, page);
            Assert.DoesNotContain(Ada, page);
            Assert.DoesNotContain(ada.Id.ToString(), page);
            Assert.DoesNotContain(olive.Id.ToString(), page);
        });

        // The key was handed to the provider, and to nothing that is stored or logged.
        Assert.All(application.ModelLists.Asked, asked => Assert.Equal(SettingsJson.CatalogKey, asked.Key));
        Assert.DoesNotContain(SettingsJson.CatalogKey, application.EverythingStored());
        Assert.DoesNotContain(SettingsJson.CatalogKey, application.LogText());
    }

    /// <summary>The application with one operator, and a key of the service for the models of Claude.</summary>
    private static AccountsApplication Application(TestClock clock, Action<IWebHostBuilder>? host = null) =>
        new(clock: clock, settings: file => Operators(file).WithCatalogKey(), host: host);

    private static SettingsJson Operators(SettingsJson file) => file.WithJson("Application:Operators", $"[ \"{Olive}\" ]");

    /// <summary>Waits for the sync that the job makes as the background work starts on an empty catalog.</summary>
    private static Task SyncedAtTheStartAsync(AccountsApplication application) =>
        Wait.UntilAsync(
            () => StoredJob.Of(application.Database, CatalogApplication.Job)?.Outcome == "Succeeded", "the sync of the catalog at the start");
}
