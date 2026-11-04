using Ecnanib.Tests.Persistence;

namespace Ecnanib.Tests.Exchange;

/// <summary>CFG-03, EXT-BIN-01, NFR-SEC-10.</summary>
public sealed class BinanceSettingsTests(PostgresFixture postgres)
{
    private const string SpotLive = "Binance:Spot:LiveBaseUrl";
    private const string SpotDemo = "Binance:Spot:DemoBaseUrl";
    private const string FuturesLive = "Binance:Futures:LiveBaseUrl";
    private const string FuturesDemo = "Binance:Futures:DemoBaseUrl";
    private const string MarketData = "Binance:MarketDataBaseUrl";
    private const string RecvWindow = "Binance:RecvWindowMs";
    private const string Headroom = "Binance:WeightHeadroomPercent";
    private const string Refresh = "Binance:ExchangeInfoRefreshHours";

    public static TheoryData<string, string?> InvalidSettings => new()
    {
        { SpotLive, null },
        { SpotDemo, "" },
        { FuturesLive, "fapi.binance.com" },
        { FuturesDemo, "ftp://demo-fapi.binance.com" },
        { MarketData, "https://data-api.binance.vision/api" },
        { SpotLive, "https://api.binance.com/?x=1" },
        { SpotDemo, "https://someone:secret@demo-api.binance.com" },
        { RecvWindow, "0" },
        { RecvWindow, "10001" },
        { RecvWindow, "five seconds" },
        { Headroom, "-1" },
        { Headroom, "91" },
        { Refresh, "0" },
        { Refresh, "169" },
    };

    [Theory]
    [MemberData(nameof(InvalidSettings))]
    public async Task An_invalid_setting_stops_startup_and_is_named(string key, string? value)
    {
        await using var app = new AppFactory(postgres.NewConnectionString(), settings: new Dictionary<string, string?> { [key] = value });

        var exception = Assert.ThrowsAny<Exception>(app.Start);

        Assert.Contains(key, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("someone:secret", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_invalid_setting_stops_startup_before_any_migration()
    {
        var cancellation = TestContext.Current.CancellationToken;
        var database = await postgres.CreateDatabaseAsync(cancellation);
        await using var app = new AppFactory(database, settings: new Dictionary<string, string?> { [FuturesLive] = null });

        Assert.ThrowsAny<Exception>(app.Start);

        Assert.Equal(0, await Sql.ScalarAsync<long>(
            database,
            "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public'",
            cancellation));
    }

    [Fact]
    public async Task Outside_Development_a_server_must_use_https()
    {
        await using var app = new AppFactory(
            postgres.NewConnectionString(),
            settings: new Dictionary<string, string?> { [FuturesDemo] = "http://demo-fapi.binance.com" },
            environment: "Production");

        var exception = Assert.ThrowsAny<Exception>(app.Start);

        Assert.Contains($"{FuturesDemo} must use https outside Development", exception.ToString(), StringComparison.Ordinal);
    }
}
