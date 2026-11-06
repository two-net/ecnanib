using System.Globalization;
using System.Net;
using Ecnanib.Kernel;
using Ecnanib.Modules.Agents.Contracts;
using Ecnanib.Modules.RiskEngine.Contracts;
using Ecnanib.Platform.Contracts;
using Ecnanib.Tests.Credentials;
using Ecnanib.Tests.Exchange;
using Ecnanib.Tests.Persistence;

namespace Ecnanib.Tests.Agents;

/// <summary>
/// EXT-BIN-07: a Futures agent is activated only when its account has Futures enabled, is in One-way Mode, and has
/// margin enough for the smallest position Binance accepts for the pair at the agent's leverage. Multi-Assets Mode is
/// refused too, because it allows no isolated margin (FR-RISK-09). BTCUSDT's minimum notional is 50 USDT in the
/// samples, so at the default leverage of 2 the smallest position needs 25 USDT.
/// </summary>
public sealed class FuturesPrerequisiteTests(PostgresFixture postgres)
{
    private const string ResumeJob = "LossLimitResumeJob";

    private static CancellationToken Cancellation => AgentApp.Cancellation;

    [Fact]
    public async Task A_Futures_agent_on_a_ready_account_activates_and_a_Spot_agent_never_asks()
    {
        await using var app = new AgentApp(postgres);
        var user = await app.UserAsync();

        await app.ActiveAsync(user, "Spot");
        Assert.Empty(app.Binance.To(AgentApp.FuturesAccountConfigPath));

        await app.ActiveAsync(user, "Futures", Market.Futures);
        var request = Assert.Single(app.Binance.To(AgentApp.FuturesAccountConfigPath));
        Assert.Equal(FakeBinance.FuturesDemo, request.Host);
    }

    [Fact]
    public async Task Hedge_Mode_and_Multi_Assets_Mode_each_keep_the_agent_from_activating()
    {
        await using var app = new AgentApp(postgres);
        var user = await app.UserAsync();
        var agent = await app.CreateAsync(user.Id, AgentApp.Ready(user, market: Market.Futures));
        AccountConfig(app, hedgeMode: true, multiAssets: true);

        var result = await app.Agents.ActivateAsync(user.Id, agent.Id, Cancellation);

        Assert.Equal(ActivationOutcome.Refused, result.Outcome);
        Assert.Equal(
            [ActivationProblemKind.FuturesHedgeMode, ActivationProblemKind.FuturesMultiAssetsMode],
            result.Problems!.Select(problem => problem.Kind));
        Assert.Equal(AgentStatus.Draft, await app.StatusAsync(user.Id, agent.Id));
    }

    [Theory]
    [InlineData(2, "25", "24.99999999", "25")]
    [InlineData(3, "16.66666667", "16.66666666", "16.66666667")]
    public async Task Margin_for_the_smallest_position_at_the_agents_leverage_is_enough(
        int leverage, string enough, string tooLittle, string required)
    {
        await using var app = new AgentApp(postgres);
        var user = await app.UserAsync();
        var settings = AgentApp.Ready(user, market: Market.Futures);
        var agent = await app.CreateAsync(user.Id, settings with { Risk = settings.Risk with { MaxLeverage = leverage } });

        app.Binance.On(HttpMethod.Get, CredentialApp.FuturesAccountPath, _ => FakeBinance.Json(FuturesAccount(tooLittle)));
        var refused = await app.Agents.ActivateAsync(user.Id, agent.Id, Cancellation);

        var problem = Assert.Single(refused.Problems!);
        Assert.Equal(
            (ActivationProblemKind.FuturesMarginInsufficient, "USDT", Amount(required), Amount(tooLittle)),
            (problem.Kind, problem.Asset, problem.Required, problem.Available));

        // Binance's answer is kept for a minute, because a page's clicks never reach the rate limiter.
        app.Binance.On(HttpMethod.Get, CredentialApp.FuturesAccountPath, _ => FakeBinance.Json(FuturesAccount(enough)));
        Assert.Equal(ActivationOutcome.Refused, (await app.Agents.ActivateAsync(user.Id, agent.Id, Cancellation)).Outcome);

        app.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(ActivationOutcome.Activated, (await app.Agents.ActivateAsync(user.Id, agent.Id, Cancellation)).Outcome);
    }

    [Fact]
    public async Task An_account_Binance_cant_be_asked_about_is_unverifiable_and_one_that_refuses_the_key_isnt_enabled()
    {
        await using var app = new AgentApp(postgres);
        var user = await app.UserAsync();
        var agent = await app.CreateAsync(user.Id, AgentApp.Ready(user, market: Market.Futures));

        app.Binance.On(HttpMethod.Get, AgentApp.FuturesAccountConfigPath, _ => FakeBinance.Error(HttpStatusCode.ServiceUnavailable, -1001, "Internal error."));
        Assert.Equal([ActivationProblemKind.FuturesAccountUnverifiable], await ProblemsAsync(app, user, agent));

        app.Clock.Advance(TimeSpan.FromMinutes(1));
        app.Binance.On(HttpMethod.Get, AgentApp.FuturesAccountConfigPath, _ => CredentialApp.Refusal());
        Assert.Equal([ActivationProblemKind.MarketNotEnabled], await ProblemsAsync(app, user, agent));

        app.Clock.Advance(TimeSpan.FromMinutes(1));
        AccountConfig(app, canTrade: false);
        Assert.Equal([ActivationProblemKind.MarketNotEnabled], await ProblemsAsync(app, user, agent));
    }

    [Fact]
    public async Task An_active_agents_edit_is_refused_once_its_account_is_in_Hedge_Mode()
    {
        await using var app = new AgentApp(postgres);
        var user = await app.UserAsync();
        var agent = await app.ActiveAsync(user, market: Market.Futures);

        AccountConfig(app, hedgeMode: true);
        app.Clock.Advance(TimeSpan.FromMinutes(1));
        var result = await app.Agents.UpdateAsync(user.Id, agent.Id, agent.Version, agent.Settings with { CapitalAllocation = 150m }, Cancellation);

        Assert.Equal(AgentSaveOutcome.WouldFailActivation, result.Outcome);
        Assert.Equal([ActivationProblemKind.FuturesHedgeMode], result.Problems!.Select(problem => problem.Kind));
    }

    [Fact]
    public async Task A_loss_limits_pause_ends_once_Binance_can_check_the_account_again()
    {
        await using var app = new AgentApp(postgres);
        var user = await app.UserAsync();
        var agent = await app.ActiveAsync(user, market: Market.Futures);
        var breach = new LossLimitBreach(LossLimitKind.Daily, RiskPeriods.NextDay(app.Clock.GetUtcNow()));
        await app.Agents.PauseForLossLimitAsync(user.Id, agent.Id, breach, Cancellation);

        app.Binance.On(HttpMethod.Get, AgentApp.FuturesAccountConfigPath, _ => FakeBinance.Error(HttpStatusCode.ServiceUnavailable, -1001, "Internal error."));
        app.Clock.Advance(breach.Until!.Value - app.Clock.GetUtcNow());
        Assert.Equal(JobStatus.Succeeded, await app.RunJobAsync(ResumeJob));

        // Only reaching Binance failed, so the next run tries again.
        var held = (await app.Agents.FindAsync(user.Id, agent.Id, Cancellation))!;
        Assert.Equal((AgentStatus.Paused, breach.Until), (held.Status, held.LossLimitUntil));

        AccountConfig(app);
        app.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(JobStatus.Succeeded, await app.RunJobAsync(ResumeJob));

        Assert.Equal(AgentStatus.Active, await app.StatusAsync(user.Id, agent.Id));
    }

    private static decimal? Amount(string value) => decimal.Parse(value, CultureInfo.InvariantCulture);

    private static void AccountConfig(AgentApp app, bool canTrade = true, bool hedgeMode = false, bool multiAssets = false) =>
        app.Binance.On(
            HttpMethod.Get,
            AgentApp.FuturesAccountConfigPath,
            _ => FakeBinance.Json(BinanceSamples.FuturesAccountConfig(canTrade, hedgeMode, multiAssets)));

    private static async Task<IEnumerable<ActivationProblemKind>> ProblemsAsync(AgentApp app, AgentApp.User user, AgentRecord agent)
    {
        var result = await app.Agents.ActivateAsync(user.Id, agent.Id, Cancellation);

        Assert.Equal(ActivationOutcome.Refused, result.Outcome);

        return result.Problems!.Select(problem => problem.Kind);
    }

    /// <summary>A Futures account with <paramref name="available"/> USDT available, and the top-level figures well above it.</summary>
    private static string FuturesAccount(string available) => $$"""
        {"totalWalletBalance":"500.00000000","totalMarginBalance":"500.00000000","availableBalance":"500.00000000",
         "assets":[{"asset":"USDT","walletBalance":"500.00000000","marginBalance":"500.00000000","availableBalance":"{{available}}"}],
         "positions":[]}
        """;
}
