using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Ecnanib.Tests.Logging;
using Ecnanib.Tests.Persistence;
using Npgsql;

namespace Ecnanib.Tests.Health;

/// <summary>NFR-OBS-01.</summary>
public sealed class HealthEndpointTests(PostgresFixture postgres)
{
    private const string Live = "/health/live";
    private const string Ready = "/health/ready";
    private const string NewestMigration = """SELECT max(migration_id) FROM "__EFMigrationsHistory" """;

    /// <summary>The health check's own 4 s, plus room for a loaded test run. Far short of a hang's 18 s or more.</summary>
    private static readonly TimeSpan Answered = TimeSpan.FromSeconds(9);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_healthy_application_reports_the_database_and_its_migrations()
    {
        await using var app = new LoggedApp(postgres.NewConnectionString());
        using var client = app.App.CreateClient();

        using var live = await client.GetAsync(Live, Cancellation);
        using var ready = await client.GetAsync(Ready, Cancellation);
        await app.StopAsync();

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal("""{"status":"Healthy"}""", await live.Content.ReadAsStringAsync(Cancellation));
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal(
            """{"status":"Healthy","database":"connected","migrations":"current"}""",
            await ready.Content.ReadAsStringAsync(Cancellation));

        var routes = app.FileEvents()
            .Where(entry => entry.GetProperty("Properties").TryGetProperty("Route", out _))
            .Select(entry => entry.GetProperty("Properties").GetProperty("Route").GetString());

        Assert.Equal([Live, Ready], routes);
    }

    [Fact]
    public async Task During_an_outage_it_stays_live_and_stops_being_ready_then_recovers_without_a_restart()
    {
        var database = postgres.NewConnectionString();
        await using var app = new AppFactory(database);
        using var client = app.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Ready, Cancellation)).StatusCode);

        await postgres.RefuseConnectionsAsync(database, Cancellation);

        await AssertReportAsync(client, HttpStatusCode.ServiceUnavailable, "unreachable", "unknown");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Live, Cancellation)).StatusCode);

        await postgres.AllowConnectionsAsync(database, Cancellation);

        await AssertReportAsync(client, HttpStatusCode.OK, "connected", "current");
    }

    /// <summary>Reading the migration history survives a missing database, so this would otherwise read as pending.</summary>
    [Fact]
    public async Task A_dropped_database_is_unreachable_rather_than_pending()
    {
        var database = postgres.NewConnectionString();
        await using var app = new AppFactory(database);
        using var client = app.CreateClient();

        await postgres.DropDatabaseAsync(database, Cancellation);

        await AssertReportAsync(client, HttpStatusCode.ServiceUnavailable, "unreachable", "unknown");
    }

    [Theory]
    [InlineData("""DELETE FROM "__EFMigrationsHistory" WHERE migration_id = (SELECT max(migration_id) FROM "__EFMigrationsHistory")""", "pending")]
    [InlineData("""INSERT INTO "__EFMigrationsHistory" (migration_id, product_version) VALUES ('99990101000000_Newer', '10.0.0')""", "ahead")]
    [InlineData("""DROP TABLE "__EFMigrationsHistory" """, "pending")]
    public async Task A_schema_that_does_not_match_this_build_is_not_ready(string change, string migrations)
    {
        var database = postgres.NewConnectionString();
        await using var app = new AppFactory(database);
        using var client = app.CreateClient();

        await Sql.ExecuteAsync(database, change, Cancellation);

        await AssertReportAsync(client, HttpStatusCode.ServiceUnavailable, "connected", migrations);
    }

    /// <summary>A real blocked query, which cancellation can stop, so the check reports how far it got.</summary>
    [Fact]
    public async Task A_query_that_blocks_is_reported_within_the_deadline()
    {
        var database = postgres.NewConnectionString();
        await using var app = new AppFactory(database);
        using var client = app.CreateClient();

        await using var lockHolder = NpgsqlDataSource.Create(database);
        await using var connection = await lockHolder.OpenConnectionAsync(Cancellation);
        await using var transaction = await connection.BeginTransactionAsync(Cancellation);
        await using var command = new NpgsqlCommand(
            """LOCK TABLE "__EFMigrationsHistory" IN ACCESS EXCLUSIVE MODE""",
            connection,
            transaction);
        await command.ExecuteNonQueryAsync(Cancellation);

        var started = Stopwatch.GetTimestamp();

        await AssertReportAsync(client, HttpStatusCode.ServiceUnavailable, "connected", "unknown");

        Assert.InRange(Stopwatch.GetElapsedTime(started), TimeSpan.Zero, Answered);
    }

    [Fact]
    public async Task A_server_that_stops_answering_is_reported_within_the_deadline_and_recovers()
    {
        var silent = new SilentDatabase();
        await using var app = new AppFactory(postgres.NewConnectionString(), silent.AddTo);
        using var client = app.CreateClient();
        silent.Mute();

        var started = Stopwatch.GetTimestamp();

        await AssertReportAsync(client, HttpStatusCode.ServiceUnavailable, "unreachable", "unknown");

        Assert.InRange(Stopwatch.GetElapsedTime(started), TimeSpan.Zero, Answered);

        silent.Release();

        await LoggedApp.UntilAsync(async () =>
        {
            using var response = await client.GetAsync(Ready, Cancellation);
            return response.StatusCode == HttpStatusCode.OK;
        });
    }

    /// <summary>The endpoint is public, so a burst of callers must not become a burst of connections.</summary>
    [Fact]
    public async Task Concurrent_callers_share_one_probe()
    {
        var silent = new SilentDatabase();
        await using var app = new AppFactory(postgres.NewConnectionString(), silent.AddTo);
        using var client = app.CreateClient();
        silent.Mute();

        var responses = await Task.WhenAll(
            Enumerable.Range(0, 20).Select(_ => client.GetAsync(Ready, Cancellation)));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode));
        Assert.Equal(1, silent.Commands);

        silent.Release();
    }

    [Fact]
    public async Task A_caller_leaving_does_not_fail_the_others_waiting_on_the_same_probe()
    {
        var silent = new SilentDatabase();
        await using var app = new AppFactory(postgres.NewConnectionString(), silent.AddTo);
        using var client = app.CreateClient();
        silent.Mute();

        using var leaving = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        var leavingCaller = client.GetAsync(Ready, leaving.Token);

        await LoggedApp.UntilAsync(() => ValueTask.FromResult(silent.Commands == 1));

        var waitingCaller = client.GetAsync(Ready, Cancellation);

        // Time for it to join the probe. A late one runs its own, which proves less but can't fail.
        await Task.Delay(TimeSpan.FromMilliseconds(200), Cancellation);

        await leaving.CancelAsync();
        silent.Release();

        using var response = await waitingCaller;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Only now: the check ignores its caller, so the leaving one can't finish before the probe.
        await Record.ExceptionAsync(() => leavingCaller);
    }

    /// <summary>Masking only guards log sinks. What explains a failure must reach the log, and only the log.</summary>
    [Fact]
    public async Task A_failure_explains_itself_in_the_log_and_not_in_the_response()
    {
        var database = postgres.NewConnectionString();
        await using var app = new LoggedApp(
            database,
            new() { ["Logging:Sinks:Database:Enabled"] = "false" });
        using var client = app.App.CreateClient();

        var migration = await Sql.ScalarAsync<string>(database, NewestMigration, Cancellation);
        await Sql.ExecuteAsync(
            database,
            $"""DELETE FROM "__EFMigrationsHistory" WHERE migration_id = '{migration}'""",
            Cancellation);

        var pending = await ReadyBodyAsync(client);

        await postgres.RefuseConnectionsAsync(database, Cancellation);

        var unreachable = await ReadyBodyAsync(client);

        await app.StopAsync();

        Assert.Equal("""{"status":"Unhealthy","database":"connected","migrations":"pending"}""", pending);
        Assert.Equal("""{"status":"Unhealthy","database":"unreachable","migrations":"unknown"}""", unreachable);
        Assert.Contains(migration, app.FileText(), StringComparison.Ordinal);
        Assert.Contains(
            app.FileEvents(),
            entry => entry.GetProperty("RenderedMessage").GetString()!.Contains("The database probe failed", StringComparison.Ordinal)
                && entry.TryGetProperty("Exception", out var exception)
                && exception.GetString()!.Contains("Npgsql", StringComparison.Ordinal));
        Assert.DoesNotContain(postgres.Password, app.FileText(), StringComparison.Ordinal);
    }

    /// <summary>A body keeps a 503 away from the status page, which would answer with HTML.</summary>
    [Fact]
    public async Task A_failed_check_answers_with_json_and_logs_against_its_own_route()
    {
        var database = postgres.NewConnectionString();
        await using var app = new LoggedApp(database);
        using var client = app.App.CreateClient();

        await Sql.ExecuteAsync(
            database,
            """INSERT INTO "__EFMigrationsHistory" (migration_id, product_version) VALUES ('99990101000000_Newer', '10.0.0')""",
            Cancellation);

        using var response = await client.GetAsync(Ready, Cancellation);
        await app.StopAsync();

        var logged = Assert.Single(
            app.FileEvents(),
            entry => entry.GetProperty("Properties").TryGetProperty("Route", out _));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(Ready, logged.GetProperty("Properties").GetProperty("Route").GetString());
        Assert.Equal(503, logged.GetProperty("Properties").GetProperty("StatusCode").GetInt32());
    }

    [Theory]
    [InlineData(Live)]
    [InlineData(Ready)]
    public async Task A_health_response_is_never_cached(string route)
    {
        await using var app = new AppFactory(postgres.NewConnectionString());
        using var client = app.CreateClient();

        using var response = await client.GetAsync(route, Cancellation);

        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    private static async Task<string> ReadyBodyAsync(HttpClient client)
    {
        using var response = await client.GetAsync(Ready, Cancellation);

        return await response.Content.ReadAsStringAsync(Cancellation);
    }

    private static async Task AssertReportAsync(
        HttpClient client,
        HttpStatusCode status,
        string database,
        string migrations)
    {
        using var response = await client.GetAsync(Ready, Cancellation);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Cancellation));

        Assert.Equal(status, response.StatusCode);
        Assert.Equal(status == HttpStatusCode.OK ? "Healthy" : "Unhealthy", body.RootElement.GetProperty("status").GetString());
        Assert.Equal(database, body.RootElement.GetProperty("database").GetString());
        Assert.Equal(migrations, body.RootElement.GetProperty("migrations").GetString());
    }
}
