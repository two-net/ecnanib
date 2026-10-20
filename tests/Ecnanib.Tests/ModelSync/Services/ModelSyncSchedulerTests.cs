using Ecnanib.Database;
using Ecnanib.ModelSync.Services;
using Ecnanib.Tests.Keys;
using Ecnanib.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ecnanib.Tests.ModelSync.Services;

/// <summary>The scheduled sync's cadence and failure handling (FR-SYNC-02) with a database that is down.</summary>
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

        await hosted.StartAsync(KeysTestHost.Ct);
        await Wait.SettleAsync();
        Assert.Equal(0, factory.Calls);

        time.Advance(ModelSyncScheduler.InitialDelay);
        await Wait.UntilAsync(() => factory.Calls == 4, "first run");
        await Wait.UntilAsync(() => logs.Entries.Any(e => e.Level == LogLevel.Information && e.Message == Summary), "summary logged");
        Assert.Equal(4, logs.Entries.Count(e => e.Level == LogLevel.Warning && e.Message.EndsWith("failed; it will be retried next run", StringComparison.Ordinal)));
        Assert.DoesNotContain(logs.Entries, e => e.Level >= LogLevel.Error);

        time.Advance(TimeSpan.FromMinutes(29));
        await Wait.SettleAsync();
        Assert.Equal(4, factory.Calls);

        time.Advance(TimeSpan.FromMinutes(1));
        await Wait.UntilAsync(() => factory.Calls == 8, "second run");

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
