using System.Net.Http;
using Ecnanib.Kernel;
using Ecnanib.Modules.Catalog.Contracts;
using Ecnanib.Modules.Credentials.Contracts;
using Ecnanib.Platform.Contracts;
using Ecnanib.Tests.Ai;
using Ecnanib.Tests.Credentials;
using Ecnanib.Tests.Identity;
using Ecnanib.Tests.Logging;
using Ecnanib.Tests.Persistence;
using Microsoft.Extensions.DependencyInjection;
using HttpStatusCode = System.Net.HttpStatusCode;

namespace Ecnanib.Tests.Catalog;

/// <summary>FR-KEY-09, FR-LOG-03: no key reaches the logs or the catalog's records, through every kind of sync.</summary>
public sealed class CatalogLoggingTests(PostgresFixture postgres)
{
    private static CancellationToken Cancellation => CatalogApp.Cancellation;

    [Fact]
    public async Task Keys_stay_out_of_the_logs_and_the_sync_records()
    {
        var ai = new FakeAiProvider();
        var key = CanaryCases.NewCanary();
        var failingKey = CanaryCases.NewCanary();
        var userId = Guid.NewGuid();

        await using var app = new LoggedApp(
            postgres.NewConnectionString(),
            CredentialApp.Settings(new()
            {
                ["Logging:MinimumLevel"] = "Verbose",
                ["Logging:Overrides:System.Net.Http"] = "Verbose",
                ["Logging:Overrides:Microsoft.EntityFrameworkCore.Database.Command"] = "Verbose",
            }),
            services => AiHarness.Use(services, ai, new TestAdapter(CredentialApp.Provider), new TestAdapter("Second")),
            manualJobs: ["CatalogSyncJob"]);
        var services = app.App.Services;
        var credentials = services.GetRequiredService<ICredentials>();
        var connectionString = app.App.ConnectionString();
        var failing = false;
        ai.On(HttpMethod.Get, CredentialApp.ModelsPath, request =>
            failing && request.Key == failingKey
                ? FakeAiProvider.Error(HttpStatusCode.InternalServerError, "server_error", $"echoing {failingKey}")
                : FakeAiProvider.Models(null, "model-a"));

        var added = await AddAsync(credentials, userId, key);
        await AddAsync(credentials, Guid.NewGuid(), failingKey);
        await LoggedApp.UntilAsync(async () =>
            await Sql.ScalarAsync<long>(connectionString, "SELECT count(*) FROM job_runs WHERE job_name = 'catalog-sync-credential' AND finished_at IS NOT NULL", Cancellation) == 2);

        failing = true;
        await services.GetRequiredService<IJobRunner>().RunAsync("test-catalog-sync", TimeSpan.FromSeconds(30), app.App.ManualJob("CatalogSyncJob").RunAsync);
        Assert.Equal(UserSyncOutcome.Synced, (await services.GetRequiredService<ICatalogSync>().SyncUserAsync(userId, Cancellation)).Outcome);
        await credentials.DeleteAsync(userId, added.Id, Cancellation);

        await app.StopAsync();

        var text = app.FileText()
            + await app.TableTextAsync(Cancellation)
            + string.Join(' ', await Sql.ColumnAsync(connectionString, "SELECT t::text FROM catalog_sync_runs t", Cancellation))
            + string.Join(' ', await Sql.ColumnAsync(connectionString, "SELECT t::text FROM job_runs t", Cancellation));

        // The logs keep templates, so these show the sync and the failure were logged.
        Assert.Contains("Synced {Provider} models", text, StringComparison.Ordinal);
        Assert.Contains("Listing {Provider} models with credential {CredentialId} failed", text, StringComparison.Ordinal);
        Assert.DoesNotContain(key, text, StringComparison.Ordinal);
        Assert.DoesNotContain(failingKey, text, StringComparison.Ordinal);
    }

    private static async Task<CredentialRecord> AddAsync(ICredentials credentials, Guid userId, string key)
    {
        using var apiKey = SecretValue.FromString(key);

        return (await credentials.AddAiKeyAsync(userId, CredentialApp.Provider, "Personal", apiKey, Cancellation)).Credential!;
    }
}
