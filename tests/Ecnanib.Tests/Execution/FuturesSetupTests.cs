using System.Globalization;
using System.Net;
using Ecnanib.Kernel;
using Ecnanib.Modules.Execution.Contracts;
using Ecnanib.Modules.RiskEngine.Contracts;
using Ecnanib.Tests.Agents;
using Ecnanib.Tests.Exchange;
using Ecnanib.Tests.Persistence;
using Ecnanib.Tests.Risk;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Execution;

/// <summary>
/// FR-RISK-09: right before a Futures entry, the account must be in One-way Mode, and the symbol is set to isolated
/// margin at the entry's leverage. Only what differs is changed, so a symbol that's ready costs two reads.
/// </summary>
public sealed class FuturesSetupTests(PostgresFixture postgres)
{
    private const string AccountConfig = "/fapi/v1/accountConfig";
    private const string SymbolConfig = "/fapi/v1/symbolConfig";
    private const string MarginType = "/fapi/v1/marginType";
    private const string Leverage = "/fapi/v1/leverage";

    private static CancellationToken Cancellation => AgentApp.Cancellation;

    /// <summary>A market long the engine sized at its default leverage of 2.</summary>
    private static SizedOrder Entry() => RiskHarness.Evaluate(RiskHarness.FuturesLong(), RiskHarness.Futures()).Passes();

    [Fact]
    public async Task A_symbol_already_isolated_at_the_entrys_leverage_is_left_alone()
    {
        await using var app = new AgentApp(postgres);
        var user = await app.UserAsync();
        Answer(app, symbolConfig: BinanceSamples.SymbolConfig("ISOLATED", leverage: 2));

        Assert.Equal(FuturesSetupOutcome.Ready, await PrepareAsync(app, user));

        Assert.Equal([AccountConfig, SymbolConfig], FuturesCalls(app));
        Assert.Equal(FakeBinance.FuturesDemo, app.Binance.To(SymbolConfig).Single().Host);
        Assert.Equal("BTCUSDT", app.Binance.To(SymbolConfig).Single().Parameter("symbol"));
    }

    [Fact]
    public async Task A_crossed_symbol_is_isolated_then_given_the_entrys_leverage()
    {
        await using var app = new AgentApp(postgres);
        var user = await app.UserAsync();
        Answer(app, symbolConfig: BinanceSamples.SymbolConfig("CROSSED", leverage: 20));

        Assert.Equal(FuturesSetupOutcome.Ready, await PrepareAsync(app, user));

        Assert.Equal([AccountConfig, SymbolConfig, MarginType, Leverage], FuturesCalls(app));
        Assert.Equal("ISOLATED", app.Binance.To(MarginType).Single().Parameter("marginType"));
        Assert.Equal("2", app.Binance.To(Leverage).Single().Parameter("leverage"));
    }

    [Fact]
    public async Task A_symbol_isolated_meanwhile_is_ready_and_one_Binance_lists_nothing_for_is_set_up()
    {
        await using var app = new AgentApp(postgres);
        var user = await app.UserAsync();
        Answer(app, symbolConfig: BinanceSamples.SymbolConfig("CROSSED", leverage: 2));
        app.Binance.On(HttpMethod.Post, MarginType, _ => FakeBinance.Error(HttpStatusCode.BadRequest, -4046, "No need to change margin type."));

        Assert.Equal(FuturesSetupOutcome.Ready, await PrepareAsync(app, user));
        Assert.Empty(app.Binance.To(Leverage));

        Answer(app, symbolConfig: "[]");

        Assert.Equal(FuturesSetupOutcome.Ready, await PrepareAsync(app, user));
        Assert.Single(app.Binance.To(Leverage));
    }

    [Theory]
    [InlineData(false, false, false, FuturesSetupOutcome.TradingDisabled)]
    [InlineData(true, true, false, FuturesSetupOutcome.HedgeMode)]
    [InlineData(true, false, true, FuturesSetupOutcome.MultiAssetsMode)]
    public async Task An_account_that_cant_trade_isolated_in_One_way_Mode_changes_nothing(
        bool canTrade, bool hedgeMode, bool multiAssets, FuturesSetupOutcome expected)
    {
        await using var app = new AgentApp(postgres);
        var user = await app.UserAsync();
        Answer(app, accountConfig: BinanceSamples.FuturesAccountConfig(canTrade, hedgeMode, multiAssets));

        Assert.Equal(expected, await PrepareAsync(app, user));

        Assert.Equal([AccountConfig], FuturesCalls(app));
    }

    [Theory]
    [InlineData(-4047, FuturesSetupOutcome.MarginTypeLocked)]
    [InlineData(-4048, FuturesSetupOutcome.MarginTypeLocked)]
    [InlineData(-4168, FuturesSetupOutcome.MultiAssetsMode)]
    [InlineData(-1100, FuturesSetupOutcome.ExchangeUnavailable)]
    public async Task A_margin_type_Binance_wont_change_stops_before_the_leverage(int code, FuturesSetupOutcome expected)
    {
        await using var app = new AgentApp(postgres);
        var user = await app.UserAsync();
        Answer(app);
        app.Binance.On(HttpMethod.Post, MarginType, _ => FakeBinance.Error(HttpStatusCode.BadRequest, code, "Refused."));

        Assert.Equal(expected, await PrepareAsync(app, user));

        Assert.Empty(app.Binance.To(Leverage));
    }

    [Theory]
    [InlineData(-4161)]
    [InlineData(-4028)]
    [InlineData(-4209)]
    public async Task A_leverage_Binance_refuses_isnt_ready(int code)
    {
        await using var app = new AgentApp(postgres);
        var user = await app.UserAsync();
        Answer(app, symbolConfig: BinanceSamples.SymbolConfig("ISOLATED", leverage: 5));
        app.Binance.On(HttpMethod.Post, Leverage, _ => FakeBinance.Error(HttpStatusCode.BadRequest, code, "Refused."));

        Assert.Equal(FuturesSetupOutcome.LeverageRefused, await PrepareAsync(app, user));
    }

    [Fact]
    public async Task Binance_out_of_reach_or_a_deleted_key_isnt_ready()
    {
        await using var app = new AgentApp(postgres);
        var user = await app.UserAsync();
        Answer(app);
        app.Binance.On(HttpMethod.Get, SymbolConfig, _ => FakeBinance.Error(HttpStatusCode.ServiceUnavailable, -1001, "Internal error."));

        Assert.Equal(FuturesSetupOutcome.ExchangeUnavailable, await PrepareAsync(app, user));
        Assert.Empty(app.Binance.To(MarginType));

        await app.Credentials.DeleteAsync(user.Id, user.ExchangeKey, Cancellation);

        Assert.Equal(FuturesSetupOutcome.KeyUnusable, await PrepareAsync(app, user));
    }

    [Fact]
    public async Task Only_a_Futures_entry_on_a_valid_symbol_readies_one()
    {
        await using var app = new AgentApp(postgres);
        var user = await app.UserAsync();
        Answer(app);
        var spot = RiskHarness.Evaluate(RiskHarness.SpotBuy(), RiskHarness.Spot()).Passes();

        await Assert.ThrowsAsync<ArgumentException>(() => Setup(app).PrepareEntryAsync(user.Id, user.ExchangeKey, "BTCUSDT", spot, Cancellation));
        await Assert.ThrowsAsync<ArgumentException>(() => Setup(app).PrepareEntryAsync(user.Id, user.ExchangeKey, "btcusdt", Entry(), Cancellation));

        Assert.Empty(FuturesCalls(app));
    }

    private static IFuturesSetup Setup(AgentApp app) => app.App.Services.GetRequiredService<IFuturesSetup>();

    private static Task<FuturesSetupOutcome> PrepareAsync(AgentApp app, AgentApp.User user) =>
        Setup(app).PrepareEntryAsync(user.Id, user.ExchangeKey, "BTCUSDT", Entry(), Cancellation);

    /// <summary>A ready account, and a symbol crossed at 20 unless a test says otherwise. Each change succeeds.</summary>
    private static void Answer(AgentApp app, string? accountConfig = null, string? symbolConfig = null)
    {
        app.Binance.On(HttpMethod.Get, AccountConfig, _ => FakeBinance.Json(accountConfig ?? BinanceSamples.FuturesAccountConfig()));
        app.Binance.On(HttpMethod.Get, SymbolConfig, _ => FakeBinance.Json(symbolConfig ?? BinanceSamples.SymbolConfig()));
        app.Binance.On(HttpMethod.Post, MarginType, _ => FakeBinance.Json(BinanceSamples.MarginTypeChanged));
        app.Binance.On(HttpMethod.Post, Leverage, request =>
            FakeBinance.Json(BinanceSamples.Leverage(int.Parse(request.Parameter("leverage")!, CultureInfo.InvariantCulture))));
    }

    /// <summary>The setup's calls, in order, leaving out the clock and the key's validation.</summary>
    private static List<string> FuturesCalls(AgentApp app) =>
    [
        .. app.Binance.Requests
            .Select(request => request.Path)
            .Where(path => path is AccountConfig or SymbolConfig or MarginType or Leverage),
    ];
}
