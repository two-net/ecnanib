using Ecnanib.Kernel;
using Ecnanib.Modules.RiskEngine;
using Ecnanib.Modules.RiskEngine.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Risk;

/// <summary>
/// The application's own engine, from the registration the application uses, without starting it, and two
/// baselines that pass with numbers easy to check by hand. Tests vary one thing from a baseline.
/// </summary>
/// <remarks>
/// <para>Spot: price 100 (bid 99.9), ATR 2, capital 1,000 at 1% risk, so a budget of 10. A market buy with its
/// stop at 98 risks 2 a unit, so it sizes to 5, a notional of 500.</para>
/// <para>Futures: price 1,000 (bid 999.9, mark 1,000), ATR 10, the same capital and budget, leverage 2. A market
/// long with its stop at 980 risks 20 a unit, so it sizes to 0.5.</para>
/// </remarks>
internal static class RiskHarness
{
    /// <summary>A Wednesday.</summary>
    public static readonly DateTimeOffset Now = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

    public static IRiskEngine Engine(IDictionary<string, string?>? settings = null) =>
        Provider(settings).GetRequiredService<IRiskEngine>();

    public static ServiceProvider Provider(IDictionary<string, string?>? settings = null) =>
        new ServiceCollection()
            .AddLogging()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings ?? new Dictionary<string, string?>()).Build())
            .AddRiskEngine()
            .BuildServiceProvider();

    public static RiskEvaluation Evaluate(OrderIntent intent, RiskInputs inputs) => Engine().Evaluate(intent, inputs);

    public static RiskInputs Spot() => new()
    {
        Market = Market.Spot,
        Now = Now,
        Settings = RiskSettings.Default,
        CapitalAllocation = 1000m,
        Filters = new ExchangeFilters(
            Trading: true,
            Price: new TickRule(0.01m, 1_000_000m, 0.01m),
            Lot: new LotRule(0.001m, 9000m, 0.001m),
            MarketLot: new LotRule(0m, 100m, 0m),
            Notional: new NotionalRule(5m, 9_000_000m),
            PercentPrice: new PercentPriceRule(5m, 0.2m, 5m, 0.2m)),
        Prices = new MarketPrices(Bid: 99.9m, Ask: 100m, Last: 100m),
        Atr = 2m,
        AvailableQuote = 10_000m,
        BaseHeld = 0m,
    };

    public static RiskInputs Futures() => new()
    {
        Market = Market.Futures,
        Now = Now,
        Settings = RiskSettings.Default,
        CapitalAllocation = 1000m,
        Filters = new ExchangeFilters(
            Trading: true,
            Price: new TickRule(0.1m, 1_000_000m, 0.1m),
            Lot: new LotRule(0.001m, 1000m, 0.001m),
            MarketLot: new LotRule(0.001m, 120m, 0.001m),
            Notional: new NotionalRule(100m, null),
            PercentPrice: new PercentPriceRule(1.05m, 0m, 0m, 0.95m)),
        Prices = new MarketPrices(Bid: 999.9m, Ask: 1000m, Last: 1000m, Mark: 1000m),
        Atr = 10m,
        Funding = new FundingInfo(0.0001m),
        AvailableQuote = 5000m,
        Brackets =
        [
            new LeverageBracket(0m, 50_000m, 0.004m, 0m, 125),
            new LeverageBracket(50_000m, 250_000m, 0.005m, 50m, 100),
            new LeverageBracket(250_000m, 1_000_000m, 0.01m, 1300m, 50),
        ],
    };

    public static OrderIntent SpotBuy() => new()
    {
        Action = IntentAction.Buy,
        EntryType = EntryType.Market,
        StopPrice = 98m,
        TargetPrice = 104m,
    };

    public static OrderIntent FuturesLong() => new()
    {
        Action = IntentAction.OpenLong,
        EntryType = EntryType.Market,
        StopPrice = 980m,
        TargetPrice = 1040m,
    };

    public static OrderIntent FuturesShort() => new()
    {
        Action = IntentAction.OpenShort,
        EntryType = EntryType.Market,
        StopPrice = 1020m,
        TargetPrice = 960m,
    };

    public static RuleResult Rule(this RiskEvaluation evaluation, RiskRule rule) => evaluation.Rules.Single(result => result.Rule == rule);

    public static RuleResult Rule(this LossLimitCheck check, RiskRule rule) => check.Rules.Single(result => result.Rule == rule);

    public static string Input(this RuleResult result, string name) => result.Inputs.Single(input => input.Name == name).Value;

    /// <summary>Asserts the evaluation failed on exactly this rule and reason, and carries no order.</summary>
    public static void FailsOn(this RiskEvaluation evaluation, RiskRule rule, RiskReason reason)
    {
        var failures = evaluation.Failures.Select(failure => (failure.Rule, failure.Reason)).ToList();

        Assert.False(evaluation.Passed);
        Assert.Null(evaluation.Order);
        Assert.Contains((rule, (RiskReason?)reason), failures);
    }

    /// <summary>Asserts the evaluation passed, and returns its order.</summary>
    public static SizedOrder Passes(this RiskEvaluation evaluation)
    {
        Assert.True(
            evaluation.Passed,
            "Failed: " + string.Join(", ", evaluation.Failures.Select(failure => $"{failure.Rule} {failure.Reason}")));

        return Assert.IsType<SizedOrder>(evaluation.Order);
    }
}
