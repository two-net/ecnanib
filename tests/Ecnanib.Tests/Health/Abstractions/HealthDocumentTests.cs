using System.Text.Json;
using Ecnanib.Agents.Abstractions;
using Ecnanib.Ai.Abstractions;
using Ecnanib.Database;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Health;
using Ecnanib.Health.Abstractions;
using Ecnanib.ModelSync.Abstractions;

namespace Ecnanib.Tests.Health.Abstractions;

/// <summary>The <c>/health</c> document (NFR-OBS-01): states and times, never a name, a message or a count that describes users (NFR-SEC-03), in the wire's spelling.</summary>
public class HealthDocumentTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 5, 12, 0, 0, TimeSpan.Zero);

    private static readonly Guid KeyId = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");

    /// <summary>Every part failing or busy, with a name, a message or a count in every place the report has one.</summary>
    private static SystemHealthReport Report()
    {
        IReadOnlyList<StreamStatus> market =
        [
            new("candles:production:spot:btcusdt:1h", StreamHealth.Connected, Now.AddSeconds(-2), Now.AddHours(-1), null),
            new("price:production:usdfutures:ethusdt", StreamHealth.Down, null, Now.AddHours(-1), "poll failed at fstream.binance.com"),
        ];
        IReadOnlyList<StreamStatus> userData = [new($"userdata:production:spot:{KeyId}", StreamHealth.Polling, Now, Now.AddHours(-1), $"socket reset for key {KeyId}")];
        var scheduler = new SchedulerStatus(SchedulerPhase.Running, Now.AddHours(-1), Now.AddSeconds(-30), 3, 1, 2, 10, "agent 7 failed", Now.AddMinutes(-5), 4, Now.AddMinutes(-6), 1);
        IReadOnlyList<ProviderSyncView> sync =
        [
            new(AiProvider.Claude, Now.AddMinutes(-30), SyncStatus.Succeeded, null, 12, SyncTrigger.Scheduled, 11, 1),
            new(AiProvider.Gpt, Now.AddMinutes(-31), SyncStatus.Failed, "401 from api.openai.com", 5, SyncTrigger.Manual, 5, 0),
            new(AiProvider.Gemini, null, SyncStatus.Never, null, 0, null, 0, 0),
        ];
        var limits = new RateLimitSnapshot([new RateLimitUsage("REQUEST_WEIGHT", 1200, 6000, TimeSpan.FromMinutes(1))], Now.AddMinutes(-1), Now.AddSeconds(20));
        var reconciliation = new ReconciliationStatus(true, Now.AddHours(-1), Now.AddMinutes(-1), 3, 1, 2, 1, 1, Now.AddMinutes(-30), "key could not be read", Now.AddMinutes(-1));
        var instanceLock = new InstanceLockStatus(InstanceLockState.Reacquiring, Now.AddHours(-1), 4242, Now.AddSeconds(-10), "connection reset by db.internal", Now.AddSeconds(-10));
        var job = new ModelSyncJobStatus(Now.AddHours(-1), null, Now.AddMinutes(-30), Now.AddHours(5), 2, 1, 1, 0, "TimeoutException", Now.AddHours(-2));
        var states = new SystemHealthStates(HealthState.Unhealthy, HealthState.Degraded, HealthState.Healthy, HealthState.Degraded, HealthState.Healthy, HealthState.Degraded, HealthState.Degraded, HealthState.Degraded);
        return new SystemHealthReport(Now, states.Overall, new DatabaseHealth(HealthState.Unhealthy, TimeSpan.FromMilliseconds(1234.6), "NpgsqlException"), market, userData, scheduler, sync,
            "the sync states query failed", limits, reconciliation, instanceLock, job, states);
    }

    private static string Serialise(SystemHealthReport report) => JsonSerializer.Serialize(HealthDocument.From(report), HealthEndpointRouteBuilderExtensions.Json);

    private static string[] Keys(JsonElement element) => element.EnumerateObject().Select(p => p.Name).ToArray();

    [Fact]
    public void TheDocumentCarriesStatesAndTimesOnly()
    {
        var json = Serialise(Report());
        using var parsed = JsonDocument.Parse(json);
        var root = parsed.RootElement;
        var modelSync = root.GetProperty("modelSync");

        Assert.Equal(["status", "at", "database", "instanceLock", "scheduler", "marketStreams", "accountStreams", "modelSync", "rateLimits", "reconciliation"], Keys(root));
        Assert.Equal(["status", "latencyMs"], Keys(root.GetProperty("database")));
        Assert.Equal(["status", "state"], Keys(root.GetProperty("instanceLock")));
        Assert.Equal(["status", "phase", "startedAt", "lastTickAt"], Keys(root.GetProperty("scheduler")));
        Assert.Equal(["status", "connected", "polling", "down", "stopped"], Keys(root.GetProperty("marketStreams")));
        Assert.Equal(["status", "connected", "polling", "down", "stopped"], Keys(root.GetProperty("accountStreams")));
        Assert.Equal(["status", "providers", "job"], Keys(modelSync));
        Assert.Equal(["provider", "status", "lastSyncAt"], Keys(modelSync.GetProperty("providers")[0]));
        Assert.Equal(["startedAt", "runningSince", "lastRunAt", "nextRunAt"], Keys(modelSync.GetProperty("job")));
        Assert.Equal(["status", "retryAfterUntil"], Keys(root.GetProperty("rateLimits")));
        Assert.Equal(["status", "available", "lastRoundAt"], Keys(root.GetProperty("reconciliation")));

        Assert.Equal(JsonValueKind.Null, root.GetProperty("database").GetProperty("latencyMs").ValueKind);
        Assert.Equal((1, 0, 1, 0), Counts(root.GetProperty("marketStreams")));
        Assert.Equal((0, 1, 0, 0), Counts(root.GetProperty("accountStreams")));
        Assert.Equal(3, modelSync.GetProperty("providers").GetArrayLength());
        Assert.Equal(Now.AddSeconds(20), root.GetProperty("rateLimits").GetProperty("retryAfterUntil").GetDateTimeOffset());
        Assert.Equal(Now.AddHours(5), modelSync.GetProperty("job").GetProperty("nextRunAt").GetDateTimeOffset());

        // Not a symbol, a key id, a message, a kind, a limit's name or a count of agents: the page every signed-in user opens has those, the anonymous document does not.
        foreach (var kept in new[] { "btcusdt", "ethusdt", KeyId.ToString(), "NpgsqlException", "db.internal", "api.openai.com", "agent 7", "key could not be read", "connection reset", "query failed", "poll failed", "socket reset", "TimeoutException", "REQUEST_WEIGHT", "tracked", "deadline", "adoptions", "backend" })
            Assert.DoesNotContain(kept, json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheDocumentSpellsCamelCaseNamesAndLowerCamelValues()
    {
        var json = Serialise(Report());

        Assert.Contains("\"status\":\"unhealthy\"", json, StringComparison.Ordinal);
        Assert.Contains("\"at\":\"2026-01-05T12:00:00+00:00\"", json, StringComparison.Ordinal);
        Assert.Contains("\"state\":\"reacquiring\"", json, StringComparison.Ordinal);
        Assert.Contains("\"phase\":\"running\"", json, StringComparison.Ordinal);
        Assert.Contains("\"provider\":\"claude\",\"status\":\"succeeded\"", json, StringComparison.Ordinal);
        Assert.Contains("\"provider\":\"gemini\",\"status\":\"never\",\"lastSyncAt\":null", json, StringComparison.Ordinal);
        Assert.Contains("\"runningSince\":null", json, StringComparison.Ordinal);
        Assert.Contains("\"available\":true", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Unhealthy", json, StringComparison.Ordinal);
    }

    [Fact]
    public void AHostWithoutJobsProjectsNullsNotErrors()
    {
        var report = new SystemHealthReport(Now, HealthState.Unhealthy, new DatabaseHealth(HealthState.Unhealthy, null, "No answer within 5 s."), [], [],
            new SchedulerStatus(SchedulerPhase.NotStarted, null, null, 0, 0, 0, 10, null, null, 0, null, 0), [], "TimeoutException", RateLimitSnapshot.Empty, ReconciliationStatus.Unavailable,
            InstanceLockStatus.NotHeld, ModelSyncJobStatus.NotStarted, SystemHealthStates.AllHealthy with { Database = HealthState.Unhealthy, Scheduler = HealthState.Unhealthy, ModelSync = HealthState.Degraded });

        var document = HealthDocument.From(report);

        Assert.Equal(HealthState.Unhealthy, document.Status);
        Assert.Null(document.Database.LatencyMs);
        Assert.Equal(InstanceLockState.NotHeld, document.InstanceLock.State);
        Assert.Equal(SchedulerPhase.NotStarted, document.Scheduler.Phase);
        Assert.Equal(new StreamsDocument(HealthState.Healthy, 0, 0, 0, 0), document.MarketStreams);
        Assert.Empty(document.ModelSync.Providers);
        Assert.Equal(new ModelSyncJobDocument(null, null, null, null), document.ModelSync.Job);
        Assert.False(document.Reconciliation.Available);
        Assert.Contains("\"startedAt\":null", JsonSerializer.Serialize(document, HealthEndpointRouteBuilderExtensions.Json), StringComparison.Ordinal);
    }

    /// <summary>A monitor graphing the latency must not see the probe's bound as a measurement.</summary>
    [Fact]
    public void LatencyIsTheAnswerTimeAndOnlyWhenTheDatabaseAnswered()
    {
        var report = Report();
        var answered = report with { Database = new DatabaseHealth(HealthState.Healthy, TimeSpan.FromMilliseconds(1234.6), null), States = report.States with { Database = HealthState.Healthy } };

        Assert.Equal(1235, HealthDocument.From(answered).Database.LatencyMs);
        Assert.Null(HealthDocument.From(report).Database.LatencyMs);
        Assert.Null(HealthDocument.From(report with { Database = new DatabaseHealth(HealthState.Unhealthy, TimeSpan.FromSeconds(5), "No answer within 5 s.") }).Database.LatencyMs);
    }

    [Fact]
    public void UnhealthyIs503AndTheRestIs200()
    {
        Assert.Equal(200, HealthEndpointRouteBuilderExtensions.StatusCodeFor(HealthState.Healthy));
        Assert.Equal(200, HealthEndpointRouteBuilderExtensions.StatusCodeFor(HealthState.Degraded));
        Assert.Equal(503, HealthEndpointRouteBuilderExtensions.StatusCodeFor(HealthState.Unhealthy));
    }

    private static (int Connected, int Polling, int Down, int Stopped) Counts(JsonElement streams) =>
        (streams.GetProperty("connected").GetInt32(), streams.GetProperty("polling").GetInt32(), streams.GetProperty("down").GetInt32(), streams.GetProperty("stopped").GetInt32());
}
