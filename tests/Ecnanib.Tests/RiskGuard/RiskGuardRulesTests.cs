using System.Reflection;
using Ecnanib.DecisionEngine;
using Ecnanib.RiskGuard;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.Architecture;
using Ecnanib.Tests.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.RiskGuard;

/// <summary>
/// The Risk Guard is a set of fixed rules applied after the Risk Manager and before any order.
/// Neither AI output nor a user action can bypass it for a trade (FR-RSK-3). These hold the code
/// to it: who makes a result of the Risk Guard, who gathers the facts that it is asked with, who
/// computes the size of an order (FR-RSK-5), that it calls no model, and that it has no switch.
/// See "What cannot bypass it" in docs/risk-guard.md.
/// </summary>
public class RiskGuardRulesTests(EcnanibApplicationFactory application) : IClassFixture<EcnanibApplicationFactory>
{
    /// <summary>What gathers the facts of a check, and asks the Risk Guard with them.</summary>
    private const string GathersTheFacts = "Ecnanib.DecisionEngine.PlanChecks";

    private static readonly Assembly Application = typeof(Program).Assembly;

    [Fact]
    public void Only_the_Risk_Guard_makes_a_result_of_a_check()
    {
        // A result is made by the check, from the outcome of every rule, and by nothing else: so
        // whatever holds one that says "accepted" holds a plan that every rule was asked of.
        Assert.Empty(typeof(GuardResult).GetConstructors(BindingFlags.Public | BindingFlags.Instance));

        var makers = DependencyChecker.FindCallersOf(Application, $"{typeof(GuardResult).FullName}::.ctor");

        // The rule finds the check, so it would find another that makes a result.
        Assert.Contains(makers, maker => maker.StartsWith(typeof(GuardCheck).FullName!, StringComparison.Ordinal));

        var others = makers.Where(maker => !maker.StartsWith(typeof(GuardCheck).FullName!, StringComparison.Ordinal)).ToList();

        Assert.True(
            others.Count == 0,
            $"These make a {nameof(GuardResult)}, and are not the check of the Risk Guard: {string.Join(", ", others)}. A result " +
            $"is made by {nameof(GuardCheck)}.{nameof(GuardCheck.Of)} alone, from the outcome of every rule (FR-RSK-3). " +
            "See docs/risk-guard.md.");

        // And nothing of a result can be changed once it is made.
        Assert.All(
            typeof(GuardResult).GetProperties(BindingFlags.Public | BindingFlags.Instance),
            property => Assert.Null(property.SetMethod));
    }

    [Fact]
    public void Only_what_checks_the_plan_of_a_cycle_says_the_facts_of_a_check()
    {
        // The facts of a check are read by the service, from the exchange, from what the agent
        // holds, and from the settings as they are stored: no page, no endpoint, and nothing
        // that reads the answer of a model makes a case, so neither a user nor a model can hand
        // the Risk Guard facts of their own.
        var makers = DependencyChecker.FindCallersOf(Application, $"{typeof(GuardCase).FullName}::.ctor")
            .Where(maker => maker != typeof(GuardCase).FullName)
            .ToList();

        // The rule finds what checks a plan, so it would find another that makes a case.
        Assert.Contains(makers, maker => maker.StartsWith(GathersTheFacts, StringComparison.Ordinal));

        var others = makers.Where(maker => !maker.StartsWith(GathersTheFacts, StringComparison.Ordinal)).ToList();

        Assert.True(
            others.Count == 0,
            $"These make a {nameof(GuardCase)}, and are not what checks the plan of a cycle: {string.Join(", ", others)}. The " +
            "facts of a check are gathered in one place, by the service (FR-RSK-3). See docs/risk-guard.md.");
    }

    [Fact]
    public void Only_the_Risk_Guard_computes_the_size_of_an_order()
    {
        // Models do not choose quantity or leverage, and neither does anything else: the size
        // of an order is made where the Risk Guard computes it, as a plan is checked (FR-RSK-5).
        const string Sizing = "Ecnanib.RiskGuard.OrderSizing";

        var makers = DependencyChecker.FindCallersOf(Application, $"{typeof(OrderSize).FullName}::.ctor")
            .Where(maker => maker != typeof(OrderSize).FullName)
            .ToList();

        // The rule finds what computes a size, so it would find another that makes one.
        Assert.Contains(makers, maker => maker.StartsWith(Sizing, StringComparison.Ordinal));

        var others = makers.Where(maker => !maker.StartsWith(Sizing, StringComparison.Ordinal)).ToList();

        Assert.True(
            others.Count == 0,
            $"These make an {nameof(OrderSize)}, and are not what computes one: {string.Join(", ", others)}. The quantity and " +
            "the leverage of an order are computed by the Risk Guard, from the risk fraction of the plan, the agent's equity, " +
            "and the distance to the stop-loss (FR-RSK-5). See docs/risk-guard.md.");

        // And nothing hands a check a size: a case has no place for one, so a result says the
        // size that its check computed.
        Assert.DoesNotContain(
            typeof(GuardCase).GetProperties(BindingFlags.Public | BindingFlags.Instance),
            property => property.PropertyType == typeof(OrderSize));
        Assert.Equal(typeof(OrderSize), typeof(GuardResult).GetProperty(nameof(GuardResult.Size))!.PropertyType);
    }

    [Fact]
    public void A_check_has_no_place_for_a_switch()
    {
        // What a check is asked with: the plan, and facts. Pinned, so that a place that is
        // added shows in a review as what it is. None of them turns a rule off: which rules a
        // plan is held to follows from what it does and from its market.
        Assert.Equal(
            [
                "Action", "CurrentPrice", "EntryPrice", "Equity", "EquityAtDayStart", "GlobalHalt", "Limits", "Market",
                "MarketPermitted", "ModePermitted", "OpensPosition", "PeakEquity", "Position", "RiskFraction", "SinceClose",
                "StaleLimit", "StopLoss", "Symbol", "SymbolsKnown", "TakeProfit",
            ],
            typeof(GuardCase).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal));

        // The check is one function of a case, and takes nothing else.
        var check = Assert.Single(typeof(GuardCheck).GetMethods(BindingFlags.Public | BindingFlags.Static));

        Assert.Equal((nameof(GuardCheck.Of), typeof(GuardResult)), (check.Name, check.ReturnType));
        Assert.Equal([typeof(GuardCase)], check.GetParameters().Select(parameter => parameter.ParameterType));

        // The configuration file has no key for the Risk Guard: its rules are fixed, and its
        // limits are the settings of the agent, which a user sets only within the hard bounds.
        Assert.DoesNotContain(
            Assembly.GetAssembly(typeof(Program))!.GetTypes(),
            type => type.Namespace == "Ecnanib.Platform.Settings" && type.Name.Contains("Guard", StringComparison.Ordinal));
    }

    [Fact]
    public void The_Risk_Guard_makes_no_model_call()
    {
        // The rules, and what gathers their facts: neither uses anything of the AI Provider
        // Gateway, so no model is asked whether a plan may go on.
        string[] checksAPlan =
        [
            .. Application.GetTypes()
                .Where(type => type.Namespace == "Ecnanib.RiskGuard" && !type.IsNested)
                .Select(type => type.FullName!),
            GathersTheFacts,
        ];

        Assert.Contains(typeof(GuardCheck).FullName, checksAPlan);

        // The rule finds what calls a model, so it would find a check that did.
        Assert.Contains(
            DependencyChecker.FindDependenciesOf(Application, "Ecnanib.DecisionEngine.RoleRuns"),
            used => used.StartsWith("Ecnanib.AiProviderGateway.", StringComparison.Ordinal));

        Assert.All(checksAPlan, type =>
        {
            var ofTheProviders = DependencyChecker.FindDependenciesOf(Application, type)
                .Where(used => used.StartsWith("Ecnanib.AiProviderGateway.", StringComparison.Ordinal))
                .ToList();

            Assert.True(
                ofTheProviders.Count == 0,
                $"{type} uses {string.Join(", ", ofTheProviders)}. The Risk Guard is fixed rules, and makes no model call " +
                "(FR-RSK-3). See docs/risk-guard.md.");
        });

        // What gathers the facts uses the Risk Guard: the rule would see it use a provider.
        Assert.Contains(
            DependencyChecker.FindDependenciesOf(Application, GathersTheFacts),
            used => used.StartsWith("Ecnanib.RiskGuard.", StringComparison.Ordinal));
    }

    [Fact]
    public void The_Risk_Guard_and_the_team_agree_on_when_a_decision_is_stale()
    {
        // The Risk Guard may use nothing of the Decision Engine, so it has the comparison of
        // the stale limit of its own. Both say the same of every moment: at the limit itself a
        // decision is in time, and the least later it is stale (FR-CYC-9).
        var closedAt = new DateTimeOffset(2026, 11, 18, 10, 15, 0, TimeSpan.Zero);
        var limit = TimeSpan.FromSeconds(120);

        TimeSpan[] after =
        [
            TimeSpan.Zero, TimeSpan.FromSeconds(60), limit - TimeSpan.FromTicks(1), limit, limit + TimeSpan.FromTicks(1),
            limit + TimeSpan.FromSeconds(1), TimeSpan.FromHours(4),
        ];

        Assert.All(after, sinceClose => Assert.Equal(
            StaleLimit.IsStale(closedAt, closedAt + sinceClose, limit),
            (GuardCases.OpenLong with { SinceClose = sinceClose, StaleLimit = limit }).OutcomeOf(GuardRule.NotStale) == RuleOutcome.Failed));
    }

    [Fact]
    public void Once_an_operator_can_switch_a_global_halt_the_Risk_Guard_is_told_whether_it_is_on()
    {
        // The operator functions are not built, and nothing stores a halt: no halt is on. As
        // soon as an operator can switch one, a check that still said so would let every agent
        // open positions while the halt is on (FR-OPS-3).
        var built = AuditRules.WhyOperatorFunctionsAreBuilt(application.Services);
        var answers = application.Services.GetServices<IGlobalHalt>().Any();

        Assert.True(
            answers || built is null,
            $"The operator functions are built ({built}), and nothing says whether the global halt is on. While it is, no agent " +
            $"opens a new position (FR-OPS-3). Implement {nameof(IGlobalHalt)} in what keeps the halt, and register it: what " +
            "checks the plan of a cycle asks it. See docs/risk-guard.md.");
    }

    [Fact]
    public void Once_the_gateway_places_orders_what_places_one_is_handed_what_the_Risk_Guard_accepted()
    {
        // Orders are not built yet, so nothing is placed. As soon as the gateway has its
        // namespace for them, an order is made from a plan that the Risk Guard accepted, and
        // from nothing else: the only path from what a model answered to an order is schema
        // validation, then the Risk Guard, then the Exchange Gateway (NFR-SEC-6).
        const string Trading = "Ecnanib.ExchangeGateway.Trading";
        var result = typeof(GuardResult).FullName!;

        // The rule finds what uses a result of the Risk Guard, so it would find what places an
        // order with none.
        Assert.Contains(result, DependencyChecker.FindDependenciesOf(Application, GathersTheFacts));

        // Whatever outside the gateway uses its operations that place orders. There is none yet.
        var placing = Application.GetTypes()
            .Where(type => !type.IsNested && type.Namespace is not null && !IsOf(type, "Ecnanib.ExchangeGateway"))
            .Where(type => DependencyChecker.FindDependenciesOf(Application, type.FullName!)
                .Any(used => used.StartsWith($"{Trading}.", StringComparison.Ordinal)))
            .Select(type => type.FullName!)
            .ToList();

        var unguarded = placing
            .Where(type => !DependencyChecker.FindDependenciesOf(Application, type).Contains(result))
            .ToList();

        Assert.True(
            unguarded.Count == 0,
            $"These place orders, and take no {nameof(GuardResult)}: {string.Join(", ", unguarded)}. An order is made from what " +
            $"the Risk Guard accepted: take the {nameof(GuardResult)} that {nameof(GuardResult.IsAccepted)} where the order " +
            "is made, and make the order from its case and its size (FR-RSK-3, FR-RSK-5, NFR-SEC-6). See docs/risk-guard.md.");

        static bool IsOf(Type type, string area) =>
            type.Namespace is { } name && (name == area || name.StartsWith($"{area}.", StringComparison.Ordinal));
    }
}
