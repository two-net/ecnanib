using Ecnanib.Database;
using Ecnanib.Keys.Services;
using Ecnanib.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ecnanib.Tests.Keys.Services;

/// <summary>Scheduling and failure handling of the re-validation job, without a database.</summary>
public class KeyRevalidatorTests
{
    [Fact]
    public async Task RunsAfterTheInitialDelayThenEveryIntervalAndSurvivesAFailingDatabase()
    {
        var time = new FakeTimeProvider();
        var logs = new TestLoggerProvider();
        var factory = new ThrowingDbContextFactory();
        await using var provider = KeysTestHost.BuildServices(logs: logs, time: time, config: new() { ["Keys:RevalidationIntervalMinutes"] = "30" },
            configure: s => s.AddSingleton<IDbContextFactory<EcnanibDbContext>>(factory));
        var hosted = Assert.Single(provider.GetServices<IHostedService>().OfType<KeyRevalidator>());

        await hosted.StartAsync(KeysTestHost.Ct);
        await Wait.SettleAsync();
        Assert.Equal(0, factory.Calls);

        time.Advance(KeyRevalidator.InitialDelay);
        await Wait.UntilAsync(() => factory.Calls == 1, "first run");
        await Wait.UntilAsync(() => logs.Entries.Any(e => e.Level == LogLevel.Warning && e.Message.StartsWith("Key re-validation failed", StringComparison.Ordinal)), "warning logged");
        Assert.DoesNotContain(logs.Entries, e => e.Level >= LogLevel.Error);

        time.Advance(TimeSpan.FromMinutes(29));
        await Wait.SettleAsync();
        Assert.Equal(1, factory.Calls);

        time.Advance(TimeSpan.FromMinutes(1));
        await Wait.UntilAsync(() => factory.Calls == 2, "second run");

        await hosted.StopAsync(KeysTestHost.Ct);
    }

    [Fact]
    public async Task OnePassThrowsWhenTheKeyListCannotBeRead()
    {
        var factory = new ThrowingDbContextFactory();
        await using var provider = KeysTestHost.BuildServices(configure: s => s.AddSingleton<IDbContextFactory<EcnanibDbContext>>(factory));
        var revalidator = provider.GetRequiredService<KeyRevalidator>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => revalidator.RevalidateOnceAsync(KeysTestHost.Ct));

        Assert.Equal(1, factory.Calls);
    }
}
