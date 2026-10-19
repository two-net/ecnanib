using Ecnanib.Agents.Abstractions;
using Ecnanib.Ai.Abstractions;
using Ecnanib.Database;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Agents.Services;

/// <summary>Input rules run before anything is looked up, and a failing database surfaces as the exception it is (no secrets involved).</summary>
public class AgentServiceTests
{
    [Fact]
    public async Task CreateRejectsBadInputBeforeTouchingTheDatabaseOrTheExchange()
    {
        var factory = new ThrowingDbContextFactory();
        var exchange = new FakeExchangeClientFactory();
        await using var provider = AgentsTestHost.BuildServices(exchange: exchange, configure: s => s.AddSingleton<IDbContextFactory<EcnanibDbContext>>(factory));
        var agents = provider.GetRequiredService<IAgentService>();

        var result = await agents.CreateAsync(Guid.NewGuid(), new AgentCreateInput("", MarketType.Spot, "btc usdt", CandleInterval.OneWeek, AgentMode.Live, null, null, null), AgentsTestHost.Ct);

        Assert.Equal(AgentSaveStatus.InvalidInput, result.Status);
        Assert.Equal(["name", "symbol", "timeframe", "exchangeKeyId"], result.Errors!.Select(e => e.Field));
        Assert.Equal(0, factory.Calls);
        Assert.Empty(exchange.ExchangeInfoReads);
    }

    [Fact]
    public async Task CouncilAndRiskLimitRulesRunBeforeTheDatabase()
    {
        var factory = new ThrowingDbContextFactory();
        await using var provider = AgentsTestHost.BuildServices(configure: s => s.AddSingleton<IDbContextFactory<EcnanibDbContext>>(factory));
        var agents = provider.GetRequiredService<IAgentService>();

        var council = await agents.UpdateCouncilAsync(Guid.NewGuid(), Guid.NewGuid(),
            new CouncilInput([new CouncilRoleInput(CouncilRole.RiskManager, AiProvider.Claude, "m", false)]), AgentsTestHost.Ct);
        var risk = await agents.UpdateRiskLimitsAsync(Guid.NewGuid(), Guid.NewGuid(), new RiskLimitsInput(50m, 1, 3m, null), AgentsTestHost.Ct);

        Assert.Equal(AgentSaveStatus.InvalidInput, council.Status);
        Assert.Equal(AgentSaveStatus.InvalidInput, risk.Status);
        Assert.Equal(0, factory.Calls);
    }

    [Fact]
    public async Task ADatabaseFailurePropagates()
    {
        var factory = new ThrowingDbContextFactory();
        var logs = new TestLoggerProvider();
        await using var provider = AgentsTestHost.BuildServices(logs: logs, configure: s => s.AddSingleton<IDbContextFactory<EcnanibDbContext>>(factory));
        var agents = provider.GetRequiredService<IAgentService>();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            agents.CreateAsync(Guid.NewGuid(), new AgentCreateInput("BTC", MarketType.Spot, "BTCUSDT", CandleInterval.OneHour, AgentMode.Paper, null, null, null), AgentsTestHost.Ct));

        Assert.Equal("database down", ex.Message);
        Assert.Equal(1, factory.Calls);
        await Assert.ThrowsAsync<InvalidOperationException>(() => agents.StartAsync(Guid.NewGuid(), Guid.NewGuid(), AgentsTestHost.Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => agents.ListAsync(Guid.NewGuid(), AgentsTestHost.Ct));
    }
}
