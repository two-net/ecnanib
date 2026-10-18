using Ecnanib.Database;
using Ecnanib.Database.Retention;
using Ecnanib.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ecnanib.Tests.Database.Retention;

/// <summary>Scheduling and failure handling of the sweeper, without a database.</summary>
public class RetentionSweeperTests
{
    [Fact]
    public async Task AFailingDatabaseIsLoggedAtWarningAndNeverThrown()
    {
        var logs = new TestLoggerProvider();
        var factory = new ThrowingFactory();
        await using var provider = Build(factory, logs);
        var sweeper = provider.GetRequiredService<RetentionSweeper>();

        var result = await sweeper.SweepOnceAsync(DatabaseTestHost.Ct);

        Assert.Equal(3, result.Failures);
        Assert.Equal(0, result.TotalDeleted);
        Assert.Equal(3, factory.Calls);
        Assert.Equal(3, logs.Entries.Count(e => e.Level == LogLevel.Warning && e.Message.Contains("will be retried")));
        Assert.DoesNotContain(logs.Entries, e => e.Level >= LogLevel.Error);
    }

    [Fact]
    public async Task SweepsAfterTheInitialDelayAndThenEveryInterval()
    {
        var time = new FakeTimeProvider();
        var factory = new ThrowingFactory();
        await using var provider = Build(factory, time: time, config: new() { ["Retention:SweepIntervalMinutes"] = "30" });
        var hosted = Assert.Single(provider.GetServices<IHostedService>().OfType<RetentionSweeper>());

        await hosted.StartAsync(DatabaseTestHost.Ct);
        await Wait.SettleAsync();
        Assert.Equal(0, factory.Calls);

        time.Advance(RetentionSweeper.InitialDelay);
        await Wait.UntilAsync(() => factory.Calls == 3, "first sweep");

        time.Advance(TimeSpan.FromMinutes(29));
        await Wait.SettleAsync();
        Assert.Equal(3, factory.Calls);

        time.Advance(TimeSpan.FromMinutes(1));
        await Wait.UntilAsync(() => factory.Calls == 6, "second sweep");

        await hosted.StopAsync(DatabaseTestHost.Ct);
    }

    private static ServiceProvider Build(ThrowingFactory factory, TestLoggerProvider? logs = null, TimeProvider? time = null, Dictionary<string, string?>? config = null) =>
        // Registered after the module so the throwing factory wins.
        DatabaseTestHost.BuildServices(logs: logs, config: config, time: time, configure: s => s.AddSingleton<IDbContextFactory<EcnanibDbContext>>(factory));

    private sealed class ThrowingFactory : IDbContextFactory<EcnanibDbContext>
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public EcnanibDbContext CreateDbContext()
        {
            Interlocked.Increment(ref _calls);
            throw new InvalidOperationException("database down");
        }
    }
}
