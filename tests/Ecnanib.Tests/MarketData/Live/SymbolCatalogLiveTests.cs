using Ecnanib.Agents.Abstractions;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Tests.Agents.Live;
using Ecnanib.Tests.Database.Live;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.MarketData.Live;

/// <summary>FR-DATA-01 at agent creation: the symbol is checked against the cached listing, read once per environment and market.</summary>
[Trait("Category", "Live")]
public class SymbolCatalogLiveTests : IClassFixture<DatabaseLiveFixture>
{
    private readonly DatabaseLiveFixture _fixture;

    public SymbolCatalogLiveTests(DatabaseLiveFixture fixture)
    {
        _fixture = fixture;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CreationChecksTheSymbolAgainstTheListingReadOncePerMarket()
    {
        _fixture.Require();
        await _fixture.MigrateAsync();
        var user = await AgentsLiveSeed.NewUserAsync(_fixture, "catalog@example.com");
        var (provider, _, _, exchange, _) = AgentsLiveSeed.Build(_fixture, marketData: true);
        await using var _ = provider;
        var agents = provider.GetRequiredService<IAgentService>();

        var unknown = await agents.CreateAsync(user.Id, new AgentCreateInput("x", MarketType.Spot, "DOGEUSDT", CandleInterval.OneHour, AgentMode.Paper, null, null, null), Ct);
        var btc = await agents.CreateAsync(user.Id, new AgentCreateInput("btc", MarketType.Spot, "BTCUSDT", CandleInterval.OneHour, AgentMode.Paper, null, null, null), Ct);
        var eth = await agents.CreateAsync(user.Id, new AgentCreateInput("eth", MarketType.Spot, "ethusdt", CandleInterval.FourHours, AgentMode.Paper, null, null, null), Ct);

        Assert.Equal(AgentSaveStatus.InvalidInput, unknown.Status);
        Assert.Equal("symbol", Assert.Single(unknown.Errors!).Field);
        Assert.Equal(AgentSaveStatus.Succeeded, btc.Status);
        Assert.Equal(AgentSaveStatus.Succeeded, eth.Status);
        Assert.Equal([(ExchangeEnvironment.Production, MarketType.Spot)], exchange.ExchangeInfoReads);

        var futures = await agents.CreateAsync(user.Id, new AgentCreateInput("fut", MarketType.UsdFutures, "BTCUSDT", CandleInterval.OneHour, AgentMode.Paper, null, 3, MarginType.Isolated), Ct);

        Assert.Equal(AgentSaveStatus.Succeeded, futures.Status);
        Assert.Equal([(ExchangeEnvironment.Production, MarketType.Spot), (ExchangeEnvironment.Production, MarketType.UsdFutures)], exchange.ExchangeInfoReads);
        await AgentsLiveSeed.DeleteUsersAsync(_fixture, user.Id);
    }
}
