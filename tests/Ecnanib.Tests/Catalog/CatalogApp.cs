using System.Collections.Concurrent;
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
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ecnanib.Tests.Catalog;

/// <summary>
/// The application with its own database and clock, and a <see cref="FakeAiProvider"/> behind the Test and
/// Second providers and the four real adapters. The schedule's job runs only when a test runs it, and the
/// credential observer is on unless a test turns it off to stand in for a lost event.
/// </summary>
internal sealed class CatalogApp : IAsyncDisposable
{
    public const string Provider = CredentialApp.Provider;
    public const string Second = "Second";
    public const string ModelsPath = CredentialApp.ModelsPath;
    public const string AiKey = CredentialApp.AiKey;
    public const string OtherAiKey = CredentialApp.OtherAiKey;
    public const string ThirdAiKey = "sk-test-aaaabbbbccccddddEFGH";

    private readonly ConcurrentDictionary<string, Func<HttpResponseMessage>> _answers = new(StringComparer.Ordinal);

    public CatalogApp(
        PostgresFixture postgres,
        Dictionary<string, string?>? settings = null,
        bool observers = true,
        Action<IServiceCollection>? services = null)
    {
        App = AccountApp.Create(
            postgres,
            CredentialApp.Settings(settings),
            collection =>
            {
                collection.AddSingleton<TimeProvider>(Clock);
                AiHarness.Use(collection, Ai, new TestAdapter(Provider), new TestAdapter(Second));

                if (!observers)
                {
                    collection.RemoveAll<ICredentialObserver>();
                }

                services?.Invoke(collection);
            },
            manualJobs: ["CatalogSyncJob"]);

        // Each key lists what a test gave it, and model-a otherwise.
        Ai.On(HttpMethod.Get, ModelsPath, request =>
            _answers.TryGetValue(request.Key ?? "", out var answer) ? answer() : FakeAiProvider.Models(null, "model-a"));
        App.Start();
    }

    public TestClock Clock { get; } = new();

    public FakeAiProvider Ai { get; } = new();

    public AppFactory App { get; }

    public ICredentials Credentials => App.Services.GetRequiredService<ICredentials>();

    public ICredentialKeys Keys => App.Services.GetRequiredService<ICredentialKeys>();

    public ICatalog Catalog => App.Services.GetRequiredService<ICatalog>();

    public ICatalogSync Sync => App.Services.GetRequiredService<ICatalogSync>();

    public ICatalogAdministration Administration => App.Services.GetRequiredService<ICatalogAdministration>();

    public string ConnectionString => App.ConnectionString();

    public static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>From now on, <paramref name="key"/> lists these models.</summary>
    public void Lists(string key, params string[] ids) => _answers[key] = () => FakeAiProvider.Models(null, ids);

    /// <summary>From now on, a request with <paramref name="key"/> gets this answer.</summary>
    public void Answers(string key, Func<HttpResponseMessage> answer) => _answers[key] = answer;

    /// <summary>Adds a key, which must be saved.</summary>
    public async Task<CredentialRecord> AddAsync(Guid userId, string key = AiKey, string provider = Provider)
    {
        using var apiKey = SecretValue.FromString(key);
        var result = await Credentials.AddAiKeyAsync(userId, provider, "Personal", apiKey, Cancellation);

        Assert.Equal(SaveOutcome.Saved, result.Outcome);

        return result.Credential!;
    }

    public async Task<CredentialRecord> RotateAsync(Guid userId, Guid id, string key = OtherAiKey)
    {
        using var apiKey = SecretValue.FromString(key);
        var result = await Credentials.RotateAiKeyAsync(userId, id, apiKey, Cancellation);

        Assert.Equal(SaveOutcome.Saved, result.Outcome);

        return result.Credential!;
    }

    /// <summary>Checks a key on demand, a minute after the last check, so it isn't too soon.</summary>
    public async Task<ValidationResult> CheckAsync(Guid userId, Guid id)
    {
        Clock.Advance(TimeSpan.FromMinutes(1));

        return await Credentials.ValidateAsync(userId, id, Cancellation);
    }

    /// <summary>Runs the schedule's job now, under a name of its own, so the real schedule isn't moved.</summary>
    public Task<JobStatus> RunScheduleAsync(TimeSpan? timeout = null) =>
        App.Services.GetRequiredService<IJobRunner>().RunAsync(
            $"test-catalog-sync-{Guid.NewGuid():N}", timeout ?? TimeSpan.FromSeconds(30), App.ManualJob("CatalogSyncJob").RunAsync);

    /// <summary>Waits until this many runs started by credential changes have finished.</summary>
    public Task UntilCredentialSyncsAsync(int count) => UntilFinishedAsync("catalog-sync-credential", count);

    public Task UntilFinishedAsync(string job, int count) =>
        LoggedApp.UntilAsync(async () => await FinishedAsync(job) >= count);

    public Task<long> FinishedAsync(string job) =>
        Sql.ScalarAsync<long>(ConnectionString, $"SELECT count(*) FROM job_runs WHERE job_name = '{job}' AND finished_at IS NOT NULL", Cancellation);

    public Task<List<string>> JobNamesAsync() =>
        Sql.ColumnAsync(ConnectionString, "SELECT DISTINCT job_name FROM job_runs ORDER BY job_name", Cancellation);

    /// <summary>A user's access, as "model:available" for one key, in model order.</summary>
    public async Task<List<string>> AccessAsync(Guid userId, Guid credentialId) =>
        [.. (await Catalog.ListAccessAsync(userId, Cancellation))
            .Where(access => access.CredentialId == credentialId)
            .Select(access => $"{access.ModelId}:{(access.Available ? "yes" : "no")}")];

    public async Task<List<string>> ModelIdsAsync(string provider = Provider) =>
        [.. (await Catalog.ListAsync(Cancellation)).Where(model => model.Provider == provider).Select(model => model.ModelId)];

    /// <summary>One provider's runs, oldest first.</summary>
    public async Task<List<SyncRun>> RunsAsync(string provider = Provider) =>
        [.. (await Sql.ColumnAsync(
                ConnectionString,
                $"""
                SELECT concat_ws('|', trigger, keys, keys_failed, models_added, models_updated, models_deprecated, models_retired,
                                 (finished_at IS NOT NULL)::text, coalesce(errors::text, ''), (finished_at >= started_at)::text)
                FROM catalog_sync_runs WHERE provider = '{provider}' ORDER BY id
                """,
                Cancellation))
            .Select(SyncRun.Parse)];

    public ValueTask DisposeAsync() => App.DisposeAsync();
}

/// <summary>A row of <c>catalog_sync_runs</c>, as a test reads it.</summary>
internal sealed record SyncRun(
    string Trigger, int Keys, int KeysFailed, int Added, int Updated, int Deprecated, int Retired, bool Finished, string Errors)
{
    public static SyncRun Parse(string row)
    {
        var fields = row.Split('|');

        // A finished run can't end before it started.
        Assert.True(fields.Length < 10 || fields[9] is "true" or "");

        return new SyncRun(
            fields[0],
            int.Parse(fields[1], System.Globalization.CultureInfo.InvariantCulture),
            int.Parse(fields[2], System.Globalization.CultureInfo.InvariantCulture),
            int.Parse(fields[3], System.Globalization.CultureInfo.InvariantCulture),
            int.Parse(fields[4], System.Globalization.CultureInfo.InvariantCulture),
            int.Parse(fields[5], System.Globalization.CultureInfo.InvariantCulture),
            int.Parse(fields[6], System.Globalization.CultureInfo.InvariantCulture),
            fields[7] == "true",
            fields[8]);
    }
}
