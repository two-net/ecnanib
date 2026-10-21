using Ecnanib.Database;
using Ecnanib.Experience.Services;
using Ecnanib.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ecnanib.Tests.Experience.Services;

/// <summary>The refresher's cadence and failure handling (FR-EXP-04) with a database that is down.</summary>
public class ExperienceRefresherTests
{
    private const string Summary = "Experience refresh wrote 0 statistic row(s) and expired 0 stale shadow outcome(s); 2 step(s) failed";

    [Fact]
    public async Task RunsAfterTheInitialDelayThenEveryIntervalAndSurvivesAFailingDatabase()
    {
        var time = new FakeTimeProvider();
        var logs = new TestLoggerProvider();
        var factory = new ThrowingDbContextFactory();
        await using var provider = ExperienceTestHost.BuildServices(logs: logs, time: time, config: new() { ["Experience:RefreshIntervalMinutes"] = "30" },
            configure: s => s.AddSingleton<IDbContextFactory<EcnanibDbContext>>(factory));
        var hosted = Assert.Single(provider.GetServices<IHostedService>(), h => h is ExperienceRefresher);

        await hosted.StartAsync(ExperienceTestHost.Ct);
        await Wait.SettleAsync();
        Assert.Equal(0, factory.Calls);

        time.Advance(ExperienceRefresher.InitialDelay);
        await Wait.UntilAsync(() => factory.Calls == 2, "first run");
        await Wait.UntilAsync(() => logs.Entries.Any(e => e.Level == LogLevel.Information && e.Message == Summary), "summary logged");
        Assert.Equal(2, logs.Entries.Count(e => e.Level == LogLevel.Warning && e.Message.EndsWith("retried next run", StringComparison.Ordinal)));
        Assert.DoesNotContain(logs.Entries, e => e.Level >= LogLevel.Error);

        time.Advance(TimeSpan.FromMinutes(29));
        await Wait.SettleAsync();
        Assert.Equal(2, factory.Calls);

        time.Advance(TimeSpan.FromMinutes(1));
        await Wait.UntilAsync(() => factory.Calls == 4, "second run");

        await hosted.StopAsync(ExperienceTestHost.Ct);
    }

    [Fact]
    public async Task OnePassCountsAFailingDatabaseAsFailuresAndThrowsNothing()
    {
        var factory = new ThrowingDbContextFactory();
        await using var provider = ExperienceTestHost.BuildServices(configure: s => s.AddSingleton<IDbContextFactory<EcnanibDbContext>>(factory));
        var refresher = provider.GetRequiredService<ExperienceRefresher>();

        var result = await refresher.RefreshOnceAsync(ExperienceTestHost.Ct);

        Assert.Equal(new ExperienceRefreshResult(0, 0, 2), result);
        Assert.Equal(2, factory.Calls);
    }
}
