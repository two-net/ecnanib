using System.Reflection;

namespace Ecnanib.Tests.Architecture;

/// <summary>
/// Proves that the checker reports forbidden references, by running it over the planted
/// violations in <c>Fixtures</c>. Without these tests the architecture rules could pass only
/// because the checker sees nothing.
/// </summary>
public class DependencyCheckerTests
{
    private const string Fixtures = "Ecnanib.Tests.Architecture.Fixtures";

    private static readonly Assembly TestAssembly = typeof(DependencyCheckerTests).Assembly;

    private static readonly DependencyChecker Checker = new(new RuleSet(
        RootNamespace: Fixtures,
        AllowedDependencies: new Dictionary<string, IReadOnlySet<string>>
        {
            ["Low"] = new HashSet<string>(),
            ["Gateway"] = new HashSet<string>(),
            ["Mid"] = new HashSet<string> { "Low", "Gateway" },
            ["High"] = new HashSet<string> { "Low", "Gateway" },
        },
        RestrictedNamespaces: new Dictionary<string, IReadOnlySet<string>>
        {
            ["Gateway.Trading"] = new HashSet<string> { "Gateway", "High" },
        },
        OutboundNetworkNamespaces: ["System.Net.Http"],
        ReservedNamespaces: new Dictionary<string, IReadOnlySet<string>>
        {
            ["Microsoft.Extensions.Configuration"] = new HashSet<string> { "Low" },
        },
        ForbiddenCalls: ["System.Environment::GetEnvironmentVariable", "System.Environment::GetCommandLineArgs"]));

    [Fact]
    public void Reports_a_reference_to_a_forbidden_area_wherever_it_appears_in_a_type()
    {
        var origins = Checker.FindAreaViolations(TestAssembly).Select(violation => violation.Origin).Distinct();

        Assert.Equal(
            [
                $"{Fixtures}.Low.UsesHighAsGenericArgument",
                $"{Fixtures}.Low.UsesHighInAsyncBody",
                $"{Fixtures}.Low.UsesHighInBody",
                $"{Fixtures}.Low.UsesHighInLambda",
                $"{Fixtures}.Low.UsesHighInSignature",
            ],
            origins);
    }

    [Fact]
    public void Reports_use_of_a_restricted_namespace_and_of_another_components_adapter()
    {
        var violations = Checker.FindRestrictedNamespaceViolations(TestAssembly);

        Assert.Equal(
            [
                ("Ecnanib.Tests.Architecture.FixtureHost.Host", $"{Fixtures}.Gateway.Adapters.Fake.FakeAdapter"),
                ("Ecnanib.Tests.Architecture.FixtureHost.Host", $"{Fixtures}.Gateway.Trading.IOrders"),
                ($"{Fixtures}.Mid.UsesAdapterDirectly", $"{Fixtures}.Gateway.Adapters.Fake.FakeAdapter"),
                ($"{Fixtures}.Mid.UsesTrading", $"{Fixtures}.Gateway.Trading.IOrders"),
            ],
            violations.Select(violation => (violation.Origin, violation.Target)));
    }

    [Fact]
    public void Reports_network_types_used_outside_an_adapter()
    {
        var origins = Checker.FindNetworkViolations(TestAssembly)
            .Select(violation => violation.Origin)
            .Where(origin => origin.StartsWith(Fixtures, StringComparison.Ordinal))
            .Distinct();

        Assert.Equal([$"{Fixtures}.Mid.UsesNetwork"], origins);
    }

    [Fact]
    public void Reports_a_reserved_namespace_used_by_an_area_that_may_not()
    {
        var origins = Checker.FindReservedNamespaceViolations(TestAssembly)
            .Select(violation => violation.Origin)
            .Where(origin => origin.StartsWith(Fixtures, StringComparison.Ordinal))
            .Distinct();

        Assert.Equal([$"{Fixtures}.Mid.UsesConfiguration"], origins);
    }

    [Fact]
    public void Reports_calls_to_a_forbidden_method_wherever_they_appear_in_a_type()
    {
        var origins = Checker.FindForbiddenCalls(TestAssembly)
            .Select(violation => violation.Origin)
            .Where(origin => origin.StartsWith(Fixtures, StringComparison.Ordinal))
            .Distinct();

        Assert.Equal(
            [
                $"{Fixtures}.Mid.ReadsAllEnvironmentVariables",
                $"{Fixtures}.Mid.ReadsAnEnvironmentVariable",
                $"{Fixtures}.Mid.ReadsTheCommandLine",
            ],
            origins);
    }

    [Fact]
    public void Reports_a_type_that_sits_in_no_area()
    {
        var checker = new DependencyChecker(new RuleSet(
            RootNamespace: Fixtures,
            AllowedDependencies: new Dictionary<string, IReadOnlySet<string>> { ["Low"] = new HashSet<string>() },
            RestrictedNamespaces: new Dictionary<string, IReadOnlySet<string>>(),
            OutboundNetworkNamespaces: [],
            ReservedNamespaces: new Dictionary<string, IReadOnlySet<string>>(),
            ForbiddenCalls: []));

        var outside = checker.FindTypesOutsideAreas(TestAssembly);

        Assert.Contains($"{Fixtures}.High.Top", outside);
        Assert.DoesNotContain($"{Fixtures}.Low.Clean", outside);
    }
}
