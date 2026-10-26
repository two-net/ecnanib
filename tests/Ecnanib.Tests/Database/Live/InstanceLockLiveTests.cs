using Ecnanib.Database;
using Ecnanib.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ecnanib.Tests.Database.Live;

/// <summary>NFR-REL-04 against PostgreSQL: one process per database, a restart that waits for the previous one, a lost connection taken again, a lock lost to another instance.</summary>
[Trait("Category", "Live")]
public class InstanceLockLiveTests : IClassFixture<DatabaseLiveFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 1, 5, 0, 0, 5, TimeSpan.Zero);

    /// <summary>PostgreSQL files a 64-bit advisory key as its high and low 32 bits; the lock lives in the database it was taken in.</summary>
    private static readonly string HeldLocks =
        $"SELECT count(*) FROM pg_locks WHERE locktype = 'advisory' AND granted AND objsubid = 1 AND classid = {(int)(InstanceLock.Key >> 32)} AND objid = {(int)(InstanceLock.Key & 0xFFFFFFFF)} AND database = (SELECT oid FROM pg_database WHERE datname = current_database())";

    private readonly DatabaseLiveFixture _fixture;

    public InstanceLockLiveTests(DatabaseLiveFixture fixture)
    {
        _fixture = fixture;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ASecondInstanceWaitsThenRefusesAndAcquiresOnceTheFirstStops()
    {
        _fixture.Require();
        var time = new FakeTimeProvider(Now);
        var logs = new TestLoggerProvider();
        var config = new Dictionary<string, string?> { ["Database:InstanceLockWaitSeconds"] = "2" };
        await using var first = _fixture.BuildServices(logs, time, config);
        await using var second = _fixture.BuildServices(logs, time, config);
        var a = first.GetRequiredService<InstanceLock>();
        var b = second.GetRequiredService<InstanceLock>();

        await a.StartAsync(Ct);

        Assert.Equal(InstanceLockState.Held, a.GetStatus().State);
        Assert.NotNull(a.GetStatus().BackendPid);
        Assert.Equal(Now, a.GetStatus().HeldSince);
        Assert.Equal(1, await _fixture.ScalarAsync<long>(HeldLocks));
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("Instance lock on database") && e.Message.Contains("acquired as backend"));

        // The second instance retries every second on the fake clock, which a side task advances, and gives up after the configured wait.
        var starting = b.StartAsync(Ct);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!starting.IsCompleted && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20, Ct);
            time.Advance(InstanceLock.RetryInterval);
        }

        var ex = await Assert.ThrowsAsync<InstanceLockException>(() => starting);
        Assert.Contains("this instance will not start", ex.Message);
        Assert.Contains($"backend {a.GetStatus().BackendPid}", ex.Message);
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("waiting up to"));
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Critical && e.Message.Contains("Another Ecnanib instance holds database"));
        Assert.Equal(InstanceLockState.NotHeld, b.GetStatus().State);
        Assert.Equal(1, await _fixture.ScalarAsync<long>(HeldLocks));

        await a.StopAsync(Ct);
        Assert.Equal(InstanceLockState.NotHeld, a.GetStatus().State);
        Assert.Equal(0, await _fixture.ScalarAsync<long>(HeldLocks));

        await b.StartAsync(Ct);
        Assert.Equal(InstanceLockState.Held, b.GetStatus().State);
        Assert.Equal(1, await _fixture.ScalarAsync<long>(HeldLocks));
        await b.StopAsync(Ct);
        Assert.Equal(0, await _fixture.ScalarAsync<long>(HeldLocks));
    }

    [Fact]
    public async Task TheKeepAliveReacquiresAfterALostConnectionAndStopsTheHostWhenAnotherInstanceHoldsIt()
    {
        _fixture.Require();
        var time = new FakeTimeProvider(Now);
        var logs = new TestLoggerProvider();
        var lifetime = new FakeHostApplicationLifetime();
        // The retrying execution strategy stays on: it reopens a broken connection under the context, which is exactly how a lock can be lost while a plain probe still answers.
        var config = new Dictionary<string, string?> { ["Database:InstanceLockWaitSeconds"] = "1" };
        await using var first = DatabaseTestHost.BuildServices(_fixture.ConnectionString, logs, config, time, s => s.AddSingleton<IHostApplicationLifetime>(lifetime));
        var a = first.GetRequiredService<InstanceLock>();
        await a.StartAsync(Ct);
        var pid = a.GetStatus().BackendPid!.Value;
        Assert.Equal(pid, await _fixture.ScalarAsync<int>(HeldLocks.Replace("count(*)", "min(pid)")));

        // The backend holding the lock dies (a database restart, a dropped connection); the next keep-alive notices and takes the lock again.
        Assert.True(await _fixture.ScalarAsync<bool>($"SELECT pg_terminate_backend({pid})"));
        await Wait.UntilAsync(() => time.ActiveTimerCount >= 1, "keep-alive scheduled");
        time.Advance(InstanceLock.KeepAliveInterval);
        await Wait.UntilAsync(() => a.GetStatus().State == InstanceLockState.Held && a.GetStatus().BackendPid != pid, "re-acquired", 30000);
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("was lost"));
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("re-acquired as backend"));
        Assert.False(lifetime.StopRequested);
        Assert.Equal(1, await _fixture.ScalarAsync<long>(HeldLocks));

        // It dies again and another instance takes the lock first: this one must stop.
        var second = a.GetStatus().BackendPid!.Value;
        Assert.True(await _fixture.ScalarAsync<bool>($"SELECT pg_terminate_backend({second})"));
        await using var other = _fixture.BuildServices(logs, time, config);
        var b = other.GetRequiredService<InstanceLock>();
        await b.StartAsync(Ct);
        Assert.Equal(InstanceLockState.Held, b.GetStatus().State);
        await Wait.UntilAsync(() => time.ActiveTimerCount >= 2, "both keep-alives scheduled");
        time.Advance(InstanceLock.KeepAliveInterval);
        await Wait.UntilAsync(() => a.GetStatus().State == InstanceLockState.Lost, "lost", 30000);

        Assert.True(lifetime.StopRequested);
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Critical && e.Message.Contains("took the lock on database") && e.Message.Contains("this instance stops"));
        Assert.Equal(InstanceLockState.Held, b.GetStatus().State);
        Assert.Equal(1, await _fixture.ScalarAsync<long>(HeldLocks));

        await b.StopAsync(Ct);
        await a.StopAsync(Ct);
        Assert.Equal(0, await _fixture.ScalarAsync<long>(HeldLocks));
    }
}
