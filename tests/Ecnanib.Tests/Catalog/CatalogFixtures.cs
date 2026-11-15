using System.Collections.Concurrent;
using Ecnanib.AiProviderGateway;
using Ecnanib.Catalog;
using Ecnanib.Platform.BackgroundWork;
using Ecnanib.Shared;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.Tests.Logging;
using Ecnanib.Tests.Persistence;
using Ecnanib.Tests.Settings;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Ecnanib.Tests.Catalog;

/// <summary>One time that <see cref="StandInModelLists"/> was asked for the models of a provider.</summary>
/// <param name="Provider">The code of the provider.</param>
/// <param name="Key">The key as it was handed over.</param>
internal sealed record AskedList(string Provider, string Key);

/// <summary>
/// Stands for the AI providers, where the application asks them for the list of their models
/// (FR-CAT-1): it keeps what it was asked, and answers as the test says. Unless a test says
/// otherwise, every provider lists two models to every key, and says nothing more about them.
/// </summary>
internal sealed class StandInModelLists : IAiModelLists
{
    private readonly ConcurrentQueue<AskedList> _asked = new();

    /// <summary>Every time it was asked, in order.</summary>
    public IReadOnlyList<AskedList> Asked => [.. _asked];

    public IReadOnlyCollection<AiProviderCode> Providers { get; set; } =
        [new AiProviderCode("claude"), new AiProviderCode("gemini"), new AiProviderCode("gpt"), new AiProviderCode("grok")];

    /// <summary>What a provider, named by its code, lists to a key.</summary>
    public Func<string, string, AiModelList> Answer { get; set; } =
        (provider, _) => Listed(Model($"{provider}-test-model"), Model($"{provider}-test-model-mini"));

    /// <summary>
    /// When set, it answers in place of <see cref="Answer"/>, in its own time: it may wait, and is
    /// told when the caller gives up.
    /// </summary>
    public Func<string, string, CancellationToken, Task<AiModelList>>? AnswerInTime { get; set; }

    public async Task<AiModelList> ListAsync(AiProviderCode provider, string apiKey, CancellationToken cancellationToken)
    {
        _asked.Enqueue(new AskedList(provider.Value, apiKey));

        return AnswerInTime is { } inTime
            ? await inTime(provider.Value, apiKey, cancellationToken)
            : Answer(provider.Value, apiKey);
    }

    /// <summary>A list of these models.</summary>
    public static AiModelList Listed(params ListedAiModel[] models) => new(AiModelListOutcome.Listed, models);

    /// <summary>No list, and how asking ended instead.</summary>
    public static AiModelList Ended(AiModelListOutcome outcome) => new(outcome, []);

    /// <summary>A model as a provider lists it, with what the provider says about it.</summary>
    public static ListedAiModel Model(
        string id,
        string? displayName = null,
        int? contextTokens = null,
        int? outputTokens = null,
        DateTimeOffset? releasedAt = null,
        DateTimeOffset? deprecatedAt = null,
        DateTimeOffset? retiresAt = null) =>
        new(id, displayName, contextTokens, outputTokens, releasedAt, deprecatedAt, retiresAt);
}

/// <summary>
/// The application on a database of the test's own, with <see cref="StandInModelLists"/> in place
/// of the providers' lists of their models, so that a test can say what the providers list and
/// what the catalog then holds.
/// </summary>
internal sealed class CatalogApplication : IDisposable
{
    /// <summary>The name of the job that syncs the catalog.</summary>
    public const string Job = "catalog-sync";

    /// <summary>The area the catalog logs under.</summary>
    public const string Area = "Ecnanib.Catalog.CatalogSync";

    private readonly LoggedApplication _application;

    /// <param name="clock">The time the application goes by, when the test sets it.</param>
    /// <param name="database">A database that an earlier application of the test has used.</param>
    /// <param name="providers">What stands for the providers, when an earlier application of the test has used it.</param>
    /// <param name="settings">Changes to the configuration file, such as a catalog key.</param>
    public CatalogApplication(
        TimeProvider? clock = null,
        TestDatabase? database = null,
        StandInModelLists? providers = null,
        Func<SettingsJson, SettingsJson>? settings = null)
    {
        Database = database ?? PostgresServer.CreateDatabase();
        Providers = providers ?? new StandInModelLists();

        var file = LoggedApplication.Settings();

        _application = new LoggedApplication(
            settings?.Invoke(file) ?? file,
            host => host.ConfigureServices(services =>
            {
                services.AddSingleton<IAiModelLists>(Providers);

                if (clock is not null)
                {
                    services.AddSingleton(clock);
                }
            }),
            Database);
    }

    public TestDatabase Database { get; }

    /// <summary>What stands for the providers when they are asked for their models.</summary>
    public StandInModelLists Providers { get; }

    /// <summary>What syncs the catalog outside the schedule.</summary>
    public ICatalogSync Catalog => _application.Services.GetRequiredService<ICatalogSync>();

    /// <summary>
    /// Starts the application, and waits until the job that syncs the catalog has had its first
    /// run on this database, which is due as the background work starts there.
    /// </summary>
    public async Task<CatalogApplication> StartedAsync()
    {
        _ = _application.Services;
        await Wait.UntilAsync(() => StoredJob.Of(Database, Job)?.Outcome is not null, "the first run of the job that syncs the catalog");

        return this;
    }

    /// <summary>
    /// Runs the job that syncs the catalog, now, as the scheduler does when it is due. A test
    /// runs it itself to see what a run does at a time of its choosing.
    /// </summary>
    public Task RunJobAsync(DateTimeOffset scheduledFor) =>
        _application.Services.GetServices<IBackgroundJob>()
            .Single(job => job.Name == Job)
            .RunAsync(new BackgroundJobRun(scheduledFor), TestContext.Current.CancellationToken);

    public IReadOnlyList<LoggedEvent> Events() => _application.Events();

    /// <summary>What the catalog itself wrote to the log.</summary>
    public IReadOnlyList<LoggedEvent> CatalogEvents() => [.. Events().Where(written => written.Area == Area)];

    public string LogText() => _application.LogText();

    /// <summary>What the scheduler wrote to the log about its jobs.</summary>
    public IReadOnlyList<LoggedEvent> SchedulerEvents() => SchedulerLog.Of(_application);

    public void Dispose() => _application.Dispose();
}

/// <summary>A row of the <c>ai_models</c> table, read by a test directly.</summary>
internal sealed record StoredAiModel(
    Guid Id,
    string Provider,
    string Identifier,
    string? DisplayName,
    int? ContextTokens,
    int? OutputTokens,
    DateTimeOffset? ReleasedAt,
    DateTimeOffset? DeprecatedAt,
    DateTimeOffset? RetiresAt,
    string Status,
    int MissedSyncs,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt)
{
    /// <summary>Every model of the catalog, by provider and identifier.</summary>
    public static IReadOnlyList<StoredAiModel> All(TestDatabase database)
    {
        using var connection = database.Open();
        using var command = new NpgsqlCommand(
            """
            SELECT id, provider, identifier, display_name, context_tokens, output_tokens, released_at, deprecated_at,
                   retires_at, status, missed_syncs, first_seen_at, last_seen_at
            FROM ai_models ORDER BY provider, identifier
            """,
            connection);
        using var reader = command.ExecuteReader();

        var models = new List<StoredAiModel>();
        while (reader.Read())
        {
            models.Add(new StoredAiModel(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetInt32(4),
                reader.IsDBNull(5) ? null : reader.GetInt32(5),
                reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
                reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7),
                reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8),
                reader.GetString(9),
                reader.GetInt32(10),
                reader.GetFieldValue<DateTimeOffset>(11),
                reader.GetFieldValue<DateTimeOffset>(12)));
        }

        return models;
    }

    /// <summary>The models of a provider, by identifier.</summary>
    public static IReadOnlyList<StoredAiModel> Of(TestDatabase database, string provider) =>
        [.. All(database).Where(model => model.Provider == provider)];

    /// <summary>A model of a provider, which must be in the catalog.</summary>
    public static StoredAiModel Of(TestDatabase database, string provider, string identifier) =>
        All(database).Single(model => model.Provider == provider && model.Identifier == identifier);
}

/// <summary>A row of the <c>catalog_sync_runs</c> table, read by a test directly.</summary>
internal sealed record StoredCatalogSync(
    string Provider,
    string Cause,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    string Result,
    string? Failure,
    int Added,
    int Changed,
    int Unavailable)
{
    /// <summary>Every sync, in the order they started.</summary>
    public static IReadOnlyList<StoredCatalogSync> All(TestDatabase database)
    {
        using var connection = database.Open();
        using var command = new NpgsqlCommand(
            """
            SELECT provider, cause, started_at, finished_at, result, failure, added, changed, unavailable
            FROM catalog_sync_runs ORDER BY started_at, id
            """,
            connection);
        using var reader = command.ExecuteReader();

        var syncs = new List<StoredCatalogSync>();
        while (reader.Read())
        {
            syncs.Add(new StoredCatalogSync(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetFieldValue<DateTimeOffset>(2),
                reader.GetFieldValue<DateTimeOffset>(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetInt32(6),
                reader.GetInt32(7),
                reader.GetInt32(8)));
        }

        return syncs;
    }

    /// <summary>The syncs of a provider, in the order they started.</summary>
    public static IReadOnlyList<StoredCatalogSync> Of(TestDatabase database, string provider) =>
        [.. All(database).Where(sync => sync.Provider == provider)];
}
