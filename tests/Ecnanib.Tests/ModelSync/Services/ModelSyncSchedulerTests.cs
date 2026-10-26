using Ecnanib.Database;
using Ecnanib.ModelSync.Abstractions;
using Ecnanib.ModelSync.Services;
using Ecnanib.Tests.Keys;
using Ecnanib.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ecnanib.Tests.ModelSync.Services;

/// <summary>The scheduled sync's cadence, failure handling and heartbeat (FR-SYNC-02, NFR-OBS-01) with a database that is down.</summary>
public class ModelSyncSchedulerTests
{
    private const string Summary = "Scheduled model sync checked 4 provider(s): 0 synced, 0 without a valid key, 0 failed, 4 failure(s)";

    [Fact]
    public async Task RunsAfterTheInitialDelayThenEveryIntervalAndSurvivesAFailingDatabase()
    {
        var time = new FakeTimeProvider();
        var logs = new TestLoggerProvider();
        var factory = new ThrowingDbContextFactory();
        await using var provider = KeysTestHost.BuildServices(logs: logs, time: time, config: new() { ["Providers:ModelSyncIntervalMinutes"] = "30" },
            configure: s => s.AddSingleton<IDbContextFactory<EcnanibDbContext>>(factory));
        var hosted = Assert.Single(provider.GetServices<IHostedService>().OfType<ModelSyncScheduler>());
        var status = provider.GetRequiredService<IModelSyncStatus>();
        Assert.Equal(ModelSyncJobStatus.NotStarted, status.GetStatus());

        await hosted.StartAsync(KeysTestHost.Ct);
        await Wait.SettleAsync();
        Assert.Equal(0, factory.Calls);
        var started = status.GetStatus();
        Assert.Equal(time.GetUtcNow(), started.StartedAt);
        Assert.Equal(time.GetUtcNow() + ModelSyncScheduler.InitialDelay, started.NextRunAt);
        Assert.Null(started.LastRunAt);
        Assert.Null(started.RunningSince);

        time.Advance(ModelSyncScheduler.InitialDelay);
        await Wait.UntilAsync(() => factory.Calls == 4, "first run");
        await Wait.UntilAsync(() => logs.Entries.Any(e => e.Level == LogLevel.Information && e.Message == Summary), "summary logged");
        Assert.Equal(4, logs.Entries.Count(e => e.Level == LogLevel.Warning && e.Message.EndsWith("failed; it will be retried next run", StringComparison.Ordinal)));
        Assert.DoesNotContain(logs.Entries, e => e.Level >= LogLevel.Error);
        // The heartbeat: the pass is recorded with its counts and the next one is due an interval later; the error is a kind, never the message (NFR-SEC-03).
        await Wait.UntilAsync(() => status.GetStatus().NextRunAt == time.GetUtcNow() + TimeSpan.FromMinutes(30), "next run scheduled");
        var ran = status.GetStatus();
        Assert.Equal(time.GetUtcNow(), ran.LastRunAt);
        Assert.Null(ran.RunningSince);
        Assert.Equal((0, 0, 0, 4), (ran.LastRunSynced, ran.LastRunWithoutKey, ran.LastRunFailed, ran.LastRunFailures));
        Assert.Matches("^[A-Za-z]+Exception$", ran.LastError);
        Assert.DoesNotContain("database down", ran.LastError, StringComparison.Ordinal);
        Assert.Equal(time.GetUtcNow(), ran.LastErrorAt);

        time.Advance(TimeSpan.FromMinutes(29));
        await Wait.SettleAsync();
        Assert.Equal(4, factory.Calls);
        Assert.Equal(ran, status.GetStatus());

        time.Advance(TimeSpan.FromMinutes(1));
        await Wait.UntilAsync(() => factory.Calls == 8, "second run");
        await Wait.UntilAsync(() => status.GetStatus().LastRunAt == time.GetUtcNow(), "second run recorded");

        await hosted.StopAsync(KeysTestHost.Ct);
    }

    [Fact]
    public async Task OnePassCountsAFailingDatabaseAsFailuresAndThrowsNothing()
    {
        var factory = new ThrowingDbContextFactory();
        await using var provider = KeysTestHost.BuildServices(configure: s => s.AddSingleton<IDbContextFactory<EcnanibDbContext>>(factory));
        var scheduler = provider.GetRequiredService<ModelSyncScheduler>();

        var result = await scheduler.SyncOnceAsync(KeysTestHost.Ct);

        Assert.Equal(new ModelSyncRunResult(0, 0, 0, 4), result);
        Assert.Equal(4, factory.Calls);
    }
}
