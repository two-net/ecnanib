using Ecnanib.Kernel;
using Ecnanib.Modules.Agents.Contracts;
using Ecnanib.Tests.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Agents;

/// <summary>FR-AGT-03: what an agent has on the exchange locks where it trades, and keeps it from being archived.</summary>
public sealed class AgentExposureTests(PostgresFixture postgres)
{
    private static CancellationToken Cancellation => AgentApp.Cancellation;

    [Fact]
    public async Task With_open_positions_or_orders_the_market_pair_timeframe_and_key_cant_change()
    {
        var exposure = new FakeExposure();
        await using var app = new AgentApp(postgres, exposure: exposure);
        var user = await app.UserAsync();
        var otherKey = await app.AddBinanceAsync(user.Id, "Other account");
        var agent = await app.ActiveAsync(user);
        exposure.Exposed.Add(agent.Id);

        AgentSettings[] locked =
        [
            agent.Settings with { Market = Market.Futures },
            agent.Settings with { Timeframe = Timeframe.FourHours },
            agent.Settings with { ExchangeCredentialId = otherKey },
        ];

        foreach (var settings in locked)
        {
            var result = await app.Agents.UpdateAsync(user.Id, agent.Id, agent.Version, settings, Cancellation);
            Assert.Equal(AgentSaveOutcome.LockedByOpenPositions, result.Outcome);
        }

        var renamed = await app.Agents.UpdateAsync(
            user.Id, agent.Id, agent.Version, agent.Settings with { Name = "Renamed", CapitalAllocation = 150m }, Cancellation);

        Assert.Equal(AgentSaveOutcome.Saved, renamed.Outcome);
    }

    [Fact]
    public async Task An_agent_with_open_positions_or_orders_cant_be_archived()
    {
        var exposure = new FakeExposure();
        await using var app = new AgentApp(postgres, exposure: exposure);
        var user = await app.UserAsync();
        var agent = await app.ActiveAsync(user);
        await app.Agents.StopAsync(user.Id, agent.Id, Cancellation);
        exposure.Exposed.Add(agent.Id);

        var result = await app.Agents.ArchiveAsync(user.Id, agent.Id, Cancellation);

        Assert.Equal(TransitionOutcome.OpenPositions, result.Outcome);
        Assert.Equal(AgentStatus.Stopped, await app.StatusAsync(user.Id, agent.Id));

        exposure.Exposed.Clear();

        Assert.Equal(TransitionOutcome.Done, (await app.Agents.ArchiveAsync(user.Id, agent.Id, Cancellation)).Outcome);
    }

    /// <summary>Nothing places orders before #34, so Execution finds nothing.</summary>
    [Fact]
    public async Task Execution_finds_no_positions_or_orders_yet()
    {
        await using var app = new AgentApp(postgres);
        var user = await app.UserAsync();
        var agent = await app.ActiveAsync(user);
        var exposure = app.App.Services.GetRequiredService<IAgentExposure>();

        Assert.False(await exposure.HasOpenPositionsOrOrdersAsync(user.Id, agent.Id, Cancellation));
        Assert.Equal(250.12m, await exposure.GetAvailableBalanceAsync(user.Id, user.ExchangeKey, Market.Spot, "USDT", Cancellation));
        Assert.Equal(0m, await exposure.GetAvailableBalanceAsync(user.Id, user.ExchangeKey, Market.Spot, "ETH", Cancellation));

        // Another user's key, or one that can't be used, gives no balance rather than a guess.
        var stranger = Guid.NewGuid();
        Assert.Null(await exposure.GetAvailableBalanceAsync(stranger, user.ExchangeKey, Market.Spot, "USDT", Cancellation));
    }
}
