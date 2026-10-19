using Ecnanib.Accounts.Abstractions;
using Ecnanib.Database.Entities;
using Ecnanib.Tests.Database.Live;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Accounts.Live;

/// <summary>FR-ACC-07: the configured limits count a user's own agents and are the same for everyone.</summary>
[Trait("Category", "Live")]
public class FairUseLimitsLiveTests : IClassFixture<DatabaseLiveFixture>
{
    private readonly DatabaseLiveFixture _fixture;

    public FairUseLimitsLiveTests(DatabaseLiveFixture fixture)
    {
        _fixture = fixture;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CountsAgentsAndRunningAgentsPerUserAgainstTheSameLimits()
    {
        _fixture.Require();
        await _fixture.MigrateAsync();
        var first = LiveSeed.NewUser("limits-first@example.com");
        var second = LiveSeed.NewUser("limits-second@example.com");
        await using (var db = _fixture.CreateContext())
        {
            db.AddRange(first, second);
            foreach (var (user, name, state) in new[] { (first, "running", AgentState.Running), (first, "paused", AgentState.Paused), (first, "error", AgentState.Error), (second, "running", AgentState.Running) })
            {
                var agent = LiveSeed.NewAgent(user, name);
                agent.State = state;
                db.Add(agent);
            }

            await db.SaveChangesAsync(Ct);
        }

        await using var provider = AccountsTestHost.BuildServices(_fixture.ConnectionString, config: new() { ["Agents:MaxAgentsPerUser"] = "3", ["Agents:MaxRunningAgentsPerUser"] = "2" });
        var limits = provider.GetRequiredService<IFairUseLimits>();

        Assert.Equal(new FairUseCheck("agents per user", false, 3, 3), await limits.CheckCreateAgentAsync(first.Id, Ct));
        Assert.Equal(new FairUseCheck("running agents per user", false, 2, 2), await limits.CheckStartAgentAsync(first.Id, Ct));
        Assert.Equal(new FairUseCheck("agents per user", true, 1, 3), await limits.CheckCreateAgentAsync(second.Id, Ct));
        Assert.Equal(new FairUseCheck("running agents per user", true, 1, 2), await limits.CheckStartAgentAsync(second.Id, Ct));

        await using (var db = _fixture.CreateContext())
            await db.Users.Where(u => u.Id == first.Id || u.Id == second.Id).ExecuteDeleteAsync(Ct);
    }
}
