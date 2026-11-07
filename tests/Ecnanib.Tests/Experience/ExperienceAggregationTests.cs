using Ecnanib.Kernel;
using Ecnanib.Tests.Persistence;

namespace Ecnanib.Tests.Experience;

/// <summary>
/// FR-EXP-02's figures, from cycles and trades seeded to give them exactly, and FR-EXP-08 and AC-12: Demo and Live are
/// never combined.
/// </summary>
public sealed class ExperienceAggregationTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Trade_results_are_per_quote_asset_with_drawdown_profit_factor_and_expectancy()
    {
        await using var app = new ExperienceApp(postgres);
        var (user, agent) = await app.UserAsync();
        var now = app.Clock.GetUtcNow();
        var cycle = await app.SeedCycleAsync(user.Id, agent, now.AddMinutes(-50), [SeedCall.Strategist("BUY"), SeedCall.Critic("approve")]);
        await app.RefreshAsync();

        // In the order they close: up 10, down 5 and 8, which is 13 from the top, then up 4.
        await app.SeedTradeAsync(user, agent, cycle, 10m, now.AddMinutes(-40), rMultiple: 2m);
        await app.SeedTradeAsync(user, agent, cycle, -5m, now.AddMinutes(-30), rMultiple: -1m);
        await app.SeedTradeAsync(user, agent, cycle, -8m, now.AddMinutes(-20), rMultiple: -1.6m);
        await app.SeedTradeAsync(user, agent, cycle, 4m, now.AddMinutes(-10), rMultiple: 0.8m);
        await app.SeedTradeAsync(user, agent, cycle, 0.001m, now.AddMinutes(-5), quoteAsset: "BTC", symbol: "ETHBTC");

        // Nothing priced, so it counts as a trade but neither a win nor a loss.
        await app.SeedTradeAsync(user, agent, cycle, 0m, now.AddMinutes(-1), exitQuantity: 0m);

        var trades = ExperienceApp.Row(await app.ReadAsync(user.Id), AiRole.Strategist).Trades;

        Assert.Equal(6, trades.Trades);
        Assert.Equal(5, trades.Priced);
        Assert.Equal(3, trades.Wins);
        Assert.Equal(2, trades.Losses);
        Assert.Equal(0.6m, trades.WinRate);
        Assert.Equal(0.05m, trades.AverageR);
        Assert.Equal(["BTC", "USDT"], trades.Results.Select(result => result.QuoteAsset));

        var usdt = trades.Results[1];
        Assert.Equal(4, usdt.Trades);
        Assert.Equal(1m, usdt.TotalPnl);
        Assert.Equal(14m, usdt.GrossProfit);
        Assert.Equal(13m, usdt.GrossLoss);
        Assert.Equal(14m / 13m, usdt.ProfitFactor);
        Assert.Equal(0.25m, usdt.Expectancy);
        Assert.Equal(13m, usdt.MaxDrawdown);

        var btc = trades.Results[0];
        Assert.Equal(0.001m, btc.TotalPnl);
        Assert.Null(btc.ProfitFactor);
        Assert.Equal(0m, btc.MaxDrawdown);

        // One Strategist call over six trades, and no net figure, because BTC isn't US dollars.
        Assert.Equal(ExperienceApp.CallCost / 6, trades.AiCostPerTrade);
        Assert.Null(trades.NetOfAiCostUsd);
    }

    [Fact]
    public async Task Net_of_AI_cost_is_given_when_every_trade_is_in_a_quote_asset_that_counts_as_dollars()
    {
        await using var app = new ExperienceApp(postgres);
        var (user, agent) = await app.UserAsync();
        var now = app.Clock.GetUtcNow();
        var cycle = await app.SeedCycleAsync(user.Id, agent, now.AddMinutes(-50), [SeedCall.Strategist("BUY"), SeedCall.Critic("approve")]);
        await app.RefreshAsync();

        await app.SeedTradeAsync(user, agent, cycle, 10m, now.AddMinutes(-20), quoteAsset: "USDC", symbol: "BTCUSDC");
        await app.SeedTradeAsync(user, agent, cycle, -3m, now.AddMinutes(-10));

        var report = await app.ReadAsync(user.Id);

        Assert.Equal(7m - ExperienceApp.CallCost, ExperienceApp.Row(report, AiRole.Strategist).Trades.NetOfAiCostUsd);
        Assert.Equal(7m - ExperienceApp.CallCost, ExperienceApp.Row(report, AiRole.RiskCritic).Trades.NetOfAiCostUsd);
    }

    [Fact]
    public async Task A_call_to_a_model_with_no_price_leaves_out_the_cost_per_trade_and_the_net()
    {
        await using var app = new ExperienceApp(postgres);
        var (user, agent) = await app.UserAsync();
        var now = app.Clock.GetUtcNow();
        var cycle = await app.SeedCycleAsync(user.Id, agent, now.AddMinutes(-50), [SeedCall.Strategist("BUY") with { Cost = null }]);
        await app.RefreshAsync();
        await app.SeedTradeAsync(user, agent, cycle, 10m, now.AddMinutes(-10));

        var strategist = ExperienceApp.Row(await app.ReadAsync(user.Id), AiRole.Strategist);

        Assert.Equal(1_200, strategist.Calls.UnpricedTokens);
        Assert.Equal(0m, strategist.Calls.Cost);
        Assert.Null(strategist.Trades.AiCostPerTrade);
        Assert.Null(strategist.Trades.NetOfAiCostUsd);
    }

    [Fact]
    public async Task Demo_and_Live_are_never_combined()
    {
        await using var app = new ExperienceApp(postgres);
        var (user, agent) = await app.UserAsync();
        var now = app.Clock.GetUtcNow();

        // One agent switched to Live keeps its Demo history, and each cycle and trade has its own environment.
        var demo = await app.SeedCycleAsync(user.Id, agent, now.AddMinutes(-50), [SeedCall.Strategist("BUY")]);
        var live = await app.SeedCycleAsync(
            user.Id, agent, now.AddMinutes(-20), [SeedCall.Strategist("BUY"), SeedCall.Strategist("HOLD")], TradingEnvironment.Live);
        await app.RefreshAsync();
        await app.SeedTradeAsync(user, agent, demo, 5m, now.AddMinutes(-30));
        await app.SeedTradeAsync(user, agent, live, -7m, now.AddMinutes(-5), TradingEnvironment.Live);

        var inDemo = ExperienceApp.Row(await app.ReadAsync(user.Id), AiRole.Strategist);
        var inLive = ExperienceApp.Row(await app.ReadAsync(user.Id, TradingEnvironment.Live), AiRole.Strategist);

        Assert.Equal(1, inDemo.Calls.Sent);
        Assert.Equal(new(Holds: 0, Entries: 1, Exits: 0, Adjustments: 0), inDemo.Strategist);
        Assert.Equal(5m, Assert.Single(inDemo.Trades.Results).TotalPnl);
        Assert.Equal(5m - ExperienceApp.CallCost, inDemo.Trades.NetOfAiCostUsd);

        Assert.Equal(2, inLive.Calls.Sent);
        Assert.Equal(new(Holds: 1, Entries: 1, Exits: 0, Adjustments: 0), inLive.Strategist);
        Assert.Equal(-7m, Assert.Single(inLive.Trades.Results).TotalPnl);
        Assert.Equal(-7m - (2 * ExperienceApp.CallCost), inLive.Trades.NetOfAiCostUsd);
    }

    [Fact]
    public async Task Latency_percentiles_take_every_day_in_the_range()
    {
        await using var app = new ExperienceApp(postgres);
        var (user, agent) = await app.UserAsync();
        var now = app.Clock.GetUtcNow();
        var fast = SeedCall.Strategist("HOLD");

        await app.SeedCycleAsync(user.Id, agent, now.AddDays(-1), [fast, fast, fast, fast, fast]);
        await app.SeedCycleAsync(user.Id, agent, now.AddMinutes(-30), [fast, fast, fast, fast, fast with { LatencySeconds = 30 }]);
        await app.RefreshAsync();

        var calls = ExperienceApp.Row(await app.ReadAsync(user.Id), AiRole.Strategist).Calls;

        // Each is its band's upper edge: the first of 100 ms × 1.25ⁿ at or above it.
        Assert.Equal(10, calls.Sent);
        Assert.Equal(2, calls.Cycles);
        Assert.Equal(Band(TimeSpan.FromSeconds(1)), calls.MedianLatency);
        Assert.Equal(Band(TimeSpan.FromSeconds(30)), calls.P95Latency);

        // Yesterday alone.
        var yesterday = ExperienceApp.Row(await app.ReadAsync(user.Id, from: app.Today.AddDays(-1), to: app.Today.AddDays(-1)), AiRole.Strategist).Calls;
        Assert.Equal(5, yesterday.Sent);
        Assert.Equal(Band(TimeSpan.FromSeconds(1)), yesterday.P95Latency);
    }

    [Fact]
    public async Task Each_kind_of_call_counts_apart_with_its_rate()
    {
        await using var app = new ExperienceApp(postgres);
        var (user, agent) = await app.UserAsync();
        var now = app.Clock.GetUtcNow();

        await app.SeedCycleAsync(
            user.Id,
            agent,
            now.AddMinutes(-30),
            [
                new(AiRole.Strategist, "BrokeContract", Truncated: true),
                SeedCall.Strategist("HOLD") with { Attempt = 2 },
                new(AiRole.Strategist, "Failed", "Timeout", Cost: 0m),
                new(AiRole.Strategist, "Failed", "Overloaded", Cost: 0m),
                new(AiRole.Strategist, "NotSent", "NoKey", Cost: null),
            ]);
        await app.RefreshAsync();

        var calls = ExperienceApp.Row(await app.ReadAsync(user.Id), AiRole.Strategist).Calls;

        Assert.Equal(4, calls.Sent);
        Assert.Equal(1, calls.NotSent);
        Assert.Equal(1, calls.Answered);
        Assert.Equal(1, calls.ContractViolations);
        Assert.Equal(1, calls.Truncated);
        Assert.Equal(1, calls.Timeouts);
        Assert.Equal(1, calls.Errors);
        Assert.Equal(0, calls.Refusals);
        Assert.Equal(0.25m, calls.TimeoutRate);
        Assert.Equal(0.25m, calls.ErrorRate);
        Assert.Equal(0.25m, calls.ContractViolationRate);
        Assert.Equal(0m, calls.RefusalRate);
        Assert.Equal(1, calls.Cycles);
        Assert.Equal(1, calls.AnsweredCycles);
    }

    [Fact]
    public async Task Filters_pick_the_market_timeframe_pair_and_days()
    {
        await using var app = new ExperienceApp(postgres);
        var (user, agent) = await app.UserAsync();
        var now = app.Clock.GetUtcNow();

        await app.SeedCycleAsync(user.Id, agent, now.AddMinutes(-30), [SeedCall.Strategist("HOLD")]);
        await app.SeedCycleAsync(
            user.Id, agent, now.AddMinutes(-20), [SeedCall.Strategist("HOLD"), SeedCall.Strategist("HOLD")], market: Market.Futures,
            timeframe: Timeframe.FourHours, symbol: "ETHUSDT");
        await app.SeedCycleAsync(user.Id, agent, now.AddDays(-3), [SeedCall.Strategist("HOLD"), SeedCall.Strategist("HOLD"), SeedCall.Strategist("HOLD"), SeedCall.Strategist("HOLD")]);
        await app.RefreshAsync();

        async Task<long> SentAsync(Market? market = null, Timeframe? timeframe = null, string? symbol = null, DateOnly? from = null) =>
            ExperienceApp.Row(await app.ReadAsync(user.Id, from: from, market: market, timeframe: timeframe, symbol: symbol), AiRole.Strategist).Calls.Sent;

        Assert.Equal(7, await SentAsync());
        Assert.Equal(2, await SentAsync(market: Market.Futures));
        Assert.Equal(5, await SentAsync(timeframe: Timeframe.OneHour));
        Assert.Equal(2, await SentAsync(symbol: "ETHUSDT"));
        Assert.Equal(3, await SentAsync(from: app.Today.AddDays(-1)));
        Assert.Empty((await app.ReadAsync(user.Id, symbol: "SOLUSDT")).Models);
    }

    [Fact]
    public async Task A_range_that_ends_before_it_starts_is_refused()
    {
        await using var app = new ExperienceApp(postgres);
        var (user, _) = await app.UserAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => app.ReadAsync(user.Id, from: app.Today, to: app.Today.AddDays(-1)));
    }

    /// <summary>The upper edge of the latency band <paramref name="latency"/> falls in.</summary>
    private static TimeSpan Band(TimeSpan latency)
    {
        for (var band = 0; ; band++)
        {
            var edge = TimeSpan.FromMilliseconds(Math.Round(100 * Math.Pow(1.25, band)));

            if (edge >= latency)
            {
                return edge;
            }
        }
    }
}
