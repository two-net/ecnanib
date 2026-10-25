using Ecnanib.Exchange.Abstractions;
using Ecnanib.MarketData.Abstractions;
using Ecnanib.Tests.Agents.Live;
using Ecnanib.Tests.Database.Live;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.MarketData.Live;

/// <summary>The chart's history from the audit table (FR-UI-03 c, FR-DATA-05) against PostgreSQL.</summary>
[Trait("Category", "Live")]
public class CandleReaderLiveTests : IClassFixture<DatabaseLiveFixture>
{
    private static readonly MarketKey BtcFutures = new(ExchangeEnvironment.Production, MarketType.UsdFutures, "BTCUSDT");
    private readonly DatabaseLiveFixture _fixture;

    public CandleReaderLiveTests(DatabaseLiveFixture fixture)
    {
        _fixture = fixture;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task StoredCandlesServeAnUntrackedSeriesWhenEnoughAreStoredOtherwiseTheExchangeDoes()
    {
        _fixture.Require();
        await _fixture.MigrateAsync();
        await using (var db = _fixture.CreateContext())
        {
            db.AddRange(Enumerable.Range(1, 4).Select(i => LiveSeed.NewCandle(LiveSeed.Now.AddHours(-i))));
            await db.SaveChangesAsync(Ct);
        }

        var (provider, _, _, exchange, _) = AgentsLiveSeed.Build(_fixture, marketData: true);
        await using var _ = provider;
        var reader = provider.GetRequiredService<ICandleReader>();

        var stored = await reader.ReadAsync(BtcFutures, CandleInterval.OneHour, 3, Ct);

        Assert.Equal([LiveSeed.Now.AddHours(-3), LiveSeed.Now.AddHours(-2), LiveSeed.Now.AddHours(-1)], stored.Select(c => c.OpenTime));
        Assert.All(stored, c => Assert.True(c.IsClosed));
        Assert.Equal((50_000m, 50_500m, 49_800m, 50_200m, 12.5m), (stored[^1].Open, stored[^1].High, stored[^1].Low, stored[^1].Close, stored[^1].Volume));
        Assert.Empty(exchange.RecentCandleReads);

        await reader.ReadAsync(BtcFutures, CandleInterval.OneHour, 5, Ct);

        Assert.Contains(exchange.RecentCandleReads, r => r.Symbol == "BTCUSDT" && r.Interval == CandleInterval.OneHour && r.Count == 5);

        await using (var cleanup = _fixture.CreateContext())
            await cleanup.Candles.ExecuteDeleteAsync(Ct);
    }
}
