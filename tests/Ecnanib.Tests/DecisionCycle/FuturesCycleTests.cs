using System.Net;
using System.Text.Json;
using Ecnanib.Kernel;
using Ecnanib.Tests.Exchange;
using Ecnanib.Tests.Persistence;

namespace Ecnanib.Tests.DecisionCycle;

/// <summary>
/// A Futures cycle gives the Risk Engine the symbol's leverage brackets on the agent's account, and its funding
/// interval, so an entry can pass the liquidation buffer, and the funding filter counts per 8 hours from the right
/// interval. Without brackets every Futures entry fails, but the cycle still ends with its trace.
/// </summary>
public sealed class FuturesCycleTests(PostgresFixture postgres)
{
    /// <summary>
    /// A Futures market long below the sample book's ask of 60120.20, with its stop about 1.7 ATR away and the nearer
    /// target near 3R.
    /// </summary>
    private const string Long = """
        {"action":"OPEN_LONG","side":"long","entry_type":"market","entry_price":null,"stop_price":60115,
         "target_prices":[60150,60135],"risk_percent":1,"limit_ttl_candles":null,"close_fraction":null,
         "invalidation":"A close below 60115.","rationale":"Momentum above the 20 EMA.","confidence":0.6}
        """;

    private static CancellationToken Cancellation => CycleApp.Cancellation;

    [Fact]
    public async Task An_entry_is_checked_against_the_accounts_brackets_and_the_symbols_funding_interval()
    {
        await using var app = new CycleApp(postgres);
        app.Replies[AiRole.Strategist] = _ => CycleApp.Reply(Long);
        app.Agents.Binance.On(HttpMethod.Get, "/fapi/v1/fundingInfo", _ => FakeBinance.Json("""
            [{"symbol":"BTCUSDT","adjustedFundingRateCap":"0.02","adjustedFundingRateFloor":"-0.02","fundingIntervalHours":4,"disclaimer":false}]
            """));
        var (user, agent) = await app.ActiveAgentAsync((_, settings) => settings with { Market = Market.Futures });

        await app.RunAsync(user, agent);

        Assert.Equal(["AwaitingApproval"], await app.CyclesAsync(agent.Id));
        var evaluation = (await app.EvaluationAsync(agent.Id))!.Value;
        Assert.Equal("Pass", evaluation.GetProperty("verdict").GetString());

        // The bracket the position falls in is the first, which allows 125x.
        var liquidation = Rule(evaluation, "LiquidationBuffer");
        Assert.Equal("Pass", liquidation.GetProperty("outcome").GetString());
        Assert.Equal(("0.004", "125"), (Input(liquidation, "MaintenanceMarginRate"), Input(liquidation, "BracketLeverage")));
        Assert.Equal(2, evaluation.GetProperty("sizedOrder").GetProperty("leverage").GetInt32());

        // 0.01% every 4 hours is 0.02% per 8.
        var funding = Rule(evaluation, "Funding");
        Assert.Equal(("4", "0.02"), (Input(funding, "IntervalHours"), Input(funding, "AgainstPercentPer8Hours")));

        var brackets = Assert.Single(app.Agents.Binance.To("/fapi/v1/leverageBracket"));
        Assert.Equal(("BTCUSDT", FakeBinance.FuturesDemo), (brackets.Parameter("symbol"), brackets.Host));
    }

    [Fact]
    public async Task Without_brackets_a_Futures_entry_fails_and_the_cycle_still_ends()
    {
        await using var app = new CycleApp(postgres);
        app.Replies[AiRole.Strategist] = _ => CycleApp.Reply(Long);
        app.Agents.Binance.On(HttpMethod.Get, "/fapi/v1/leverageBracket", _ => FakeBinance.Error(HttpStatusCode.ServiceUnavailable, -1001, "Internal error."));
        var (user, agent) = await app.ActiveAgentAsync((_, settings) => settings with { Market = Market.Futures });

        await app.RunAsync(user, agent);

        Assert.Equal(["BlockedByRiskEngine"], await app.CyclesAsync(agent.Id));
        var liquidation = Rule((await app.EvaluationAsync(agent.Id))!.Value, "LiquidationBuffer");
        Assert.Equal(("Fail", "LiquidationUnknown"), (liquidation.GetProperty("outcome").GetString(), liquidation.GetProperty("reason").GetString()));
    }

    private static JsonElement Rule(JsonElement evaluation, string rule) =>
        evaluation.GetProperty("rules").EnumerateArray().Single(result => result.GetProperty("rule").GetString() == rule);

    private static string? Input(JsonElement rule, string name) =>
        rule.GetProperty("inputs").EnumerateArray().Single(input => input.GetProperty("name").GetString() == name).GetProperty("value").GetString();
}
