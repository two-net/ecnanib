using Ecnanib.Agents.Abstractions;
using Ecnanib.Ai.Abstractions;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Health;
using Ecnanib.Health.Abstractions;
using Ecnanib.Tests.Agents.Live;
using Ecnanib.Tests.Database.Live;
using Ecnanib.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Health.Live;

/// <summary>The health report (NFR-OBS-01) against a real PostgreSQL database.</summary>
[Trait("Category", "Live")]
public class SystemHealthLiveTests : IClassFixture<DatabaseLiveFixture>
{
    private readonly DatabaseLiveFixture _fixture;

    public SystemHealthLiveTests(DatabaseLiveFixture fixture)
    {
        _fixture = fixture;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AReachableDatabaseIsHealthyAndAnUnstartedSchedulerMakesTheWholeUnhealthy()
    {
        _fixture.Require();
        await _fixture.MigrateAsync();
        var (provider, _, _, _, _) = AgentsLiveSeed.Build(_fixture, configure: s =>
        {
            s.AddHealth();
            s.AddSingleton<IExchangeHealth>(new FakeExchangeHealth());
        });
        await using var _ = provider;

        var report = await provider.GetRequiredService<ISystemHealth>().GetAsync(Ct);

        Assert.Equal(HealthState.Healthy, report.Database.State);
        Assert.Null(report.Database.Error);
        Assert.NotNull(report.Database.Latency);
        Assert.Equal(SchedulerPhase.NotStarted, report.Scheduler.Phase);
        Assert.Equal(HealthState.Unhealthy, report.Overall);
        Assert.Equal(Enum.GetValues<AiProvider>().Order(), report.ModelSync.Select(s => s.Provider));
        Assert.All(report.ModelSync, s => Assert.Equal(SyncStatus.Never, s.LastStatus));
        Assert.Null(report.ModelSyncError);
    }
}
