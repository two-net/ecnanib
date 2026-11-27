using System.Globalization;
using Ecnanib.Platform.Availability;
using Ecnanib.Platform.BackgroundWork;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.Tests.Logging;
using Ecnanib.Tests.Persistence;
using Ecnanib.Tests.Settings;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Ecnanib.Tests.Reliability;

/// <summary>
/// The application on a database of the test's own, with a clock that the test moves, and with
/// the job that says that the service is up for the test to run: the scheduler of no application
/// of the tests runs it. See "Availability" in docs/reliability.md.
/// </summary>
/// <remarks>
/// An instance counts as started when the test first asks for the job, which is when the
/// application makes what the job runs: see <see cref="Start"/>.
/// </remarks>
internal sealed class AvailabilityApplication : IDisposable
{
    /// <summary>The area that what says that the service is up logs under.</summary>
    public const string UptimeArea = "Ecnanib.Platform.Availability.Uptime";

    private readonly LoggedApplication _application;

    /// <param name="clock">The clock of the application, at the moment that it starts.</param>
    /// <param name="database">The database, when an earlier application of the test has used it.</param>
    /// <param name="settings">What the test changes in the configuration file.</param>
    public AvailabilityApplication(TestClock clock, TestDatabase? database = null, Func<SettingsJson, SettingsJson>? settings = null)
    {
        Clock = clock;
        Database = database ?? PostgresServer.CreateDatabase();

        var file = LoggedApplication.Settings();

        _application = new LoggedApplication(
            settings?.Invoke(file) ?? file,
            host => host.ConfigureServices(services => services.AddSingleton<TimeProvider>(Clock)),
            Database);

        // Started here, so that the database is migrated before the test asks it anything.
        _ = _application.Services;
    }

    public TestDatabase Database { get; }

    public TestClock Clock { get; }

    public IBackgroundJob Job => _application.Services.GetRequiredKeyedService<IBackgroundJob>(EcnanibApplicationFactory.UptimeKey);

    /// <summary>What reads how available the service was.</summary>
    public IAvailability Availability => _application.Services.GetRequiredService<IAvailability>();

    /// <summary>Has the instance count as started now: what says that the service is up is made, and notes the moment.</summary>
    public AvailabilityApplication Start()
    {
        _ = Job;
        return this;
    }

    /// <summary>Runs the job that says that the service is up, now, as the scheduler does every minute.</summary>
    public Task BeatAsync() => Job.RunAsync(new BackgroundJobRun(Clock.GetUtcNow()), TestContext.Current.CancellationToken);

    /// <summary>Moves the clock, and has the service say that it is up then.</summary>
    public Task BeatAsync(DateTimeOffset at)
    {
        Clock.Set(at);
        return BeatAsync();
    }

    /// <summary>The month that runs, and the one before it, as the application reads them now.</summary>
    public async Task<AvailabilityReading> ReadAsync() => await Availability.ReadAsync(TestContext.Current.CancellationToken);

    /// <summary>What was logged about the service being up, in order.</summary>
    public IReadOnlyList<LoggedEvent> Events() => [.. _application.Events().Where(logged => logged.Area == UptimeArea)];

    public void Dispose() => _application.Dispose();
}

/// <summary>A row of the <c>up_spans</c> table, read by a test directly.</summary>
internal sealed record StoredUpSpan(DateTimeOffset BeganAt, DateTimeOffset BeatAt)
{
    /// <summary>Every row, in the order they began in.</summary>
    public static IReadOnlyList<StoredUpSpan> All(TestDatabase database)
    {
        using var connection = database.Open();
        using var command = new NpgsqlCommand("SELECT began_at, beat_at FROM up_spans ORDER BY began_at, id", connection);
        using var reader = command.ExecuteReader();

        var spans = new List<StoredUpSpan>();
        while (reader.Read())
        {
            spans.Add(new StoredUpSpan(reader.GetFieldValue<DateTimeOffset>(0), reader.GetFieldValue<DateTimeOffset>(1)));
        }

        return spans;
    }

    /// <summary>Writes a row as a service did that said from when to when it was up.</summary>
    public static void Give(TestDatabase database, DateTimeOffset beganAt, DateTimeOffset beatAt) =>
        database.Execute(
            $"INSERT INTO up_spans (id, began_at, beat_at) VALUES ('{Guid.CreateVersion7()}', {Sql(beganAt)}, {Sql(beatAt)})");

    internal static string Sql(DateTimeOffset moment) =>
        $"'{moment.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture)}+00'";
}

/// <summary>A row of the <c>maintenance_windows</c> table, read by a test directly.</summary>
internal sealed record StoredMaintenance(Guid Id, DateTimeOffset StartsAt, DateTimeOffset EndsAt, DateTimeOffset AnnouncedAt, bool InAdvance)
{
    /// <summary>Every row, in the order they start in.</summary>
    public static IReadOnlyList<StoredMaintenance> All(TestDatabase database)
    {
        using var connection = database.Open();
        using var command = new NpgsqlCommand(
            "SELECT id, starts_at, ends_at, announced_at, in_advance FROM maintenance_windows ORDER BY starts_at, id", connection);
        using var reader = command.ExecuteReader();

        var windows = new List<StoredMaintenance>();
        while (reader.Read())
        {
            windows.Add(new StoredMaintenance(
                reader.GetGuid(0),
                reader.GetFieldValue<DateTimeOffset>(1),
                reader.GetFieldValue<DateTimeOffset>(2),
                reader.GetFieldValue<DateTimeOffset>(3),
                reader.GetBoolean(4)));
        }

        return windows;
    }

    /// <summary>Writes a row as an operator did who announced maintenance, in advance or late.</summary>
    public static Guid Give(TestDatabase database, DateTimeOffset startsAt, DateTimeOffset endsAt, DateTimeOffset announcedAt, bool inAdvance = true)
    {
        var id = Guid.CreateVersion7();

        database.Execute(
            "INSERT INTO maintenance_windows (id, starts_at, ends_at, announced_at, in_advance) VALUES " +
            $"('{id}', {StoredUpSpan.Sql(startsAt)}, {StoredUpSpan.Sql(endsAt)}, {StoredUpSpan.Sql(announcedAt)}, {(inAdvance ? "TRUE" : "FALSE")})");

        return id;
    }
}
