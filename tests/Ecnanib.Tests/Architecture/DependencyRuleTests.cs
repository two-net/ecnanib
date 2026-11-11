using System.Reflection;
using Ecnanib.Accounts;

namespace Ecnanib.Tests.Architecture;

/// <summary>
/// Enforces <see cref="ComponentRules"/> on the application: the code is organized into the
/// components of SRS section 3.1 with one-directional dependencies, and every external system is
/// reached only through its adapter (NFR-ARC-3).
/// </summary>
public class DependencyRuleTests
{
    private static readonly Assembly Application = typeof(AccountsModule).Assembly;

    private static readonly RuleSet Rules = ComponentRules.Rules;

    private static readonly DependencyChecker Checker = new(Rules);

    public static TheoryData<string> SrsComponents => [.. ComponentRules.SrsComponents];

    [Theory]
    [MemberData(nameof(SrsComponents))]
    public void Each_SRS_component_is_a_namespace_of_the_application(string component)
    {
        var typesInComponent = Application.GetTypes()
            .Where(type => type.Namespace is { } @namespace && Checker.AreaOf(@namespace) == component);

        Assert.NotEmpty(typesInComponent);
    }

    [Fact]
    public void Every_type_sits_in_a_registered_area()
    {
        AssertNone(
            Checker.FindTypesOutsideAreas(Application),
            "These types are in the Ecnanib namespace but in no component. Move them into a component, " +
            "or register their area in ComponentRules.");
    }

    [Fact]
    public void Components_depend_only_on_the_components_they_are_allowed_to()
    {
        AssertNone(
            Checker.FindAreaViolations(Application),
            "These references point the wrong way between components. See docs/architecture.md.");
    }

    [Fact]
    public void Adapters_and_order_placement_are_used_only_where_allowed()
    {
        AssertNone(
            Checker.FindRestrictedNamespaceViolations(Application),
            "These references reach into a namespace that is closed to the code using it. " +
            "Use the component's common interface instead.");
    }

    [Fact]
    public void External_systems_are_reached_only_from_adapters()
    {
        AssertNone(
            Checker.FindNetworkViolations(Application),
            "Network client types are used outside an Adapters namespace. " +
            "Every external system is reached through its adapter.");
    }

    [Fact]
    public void Settings_reach_the_components_only_through_the_platform()
    {
        AssertNone(
            Checker.FindReservedNamespaceViolations(Application),
            "The host's configuration is used outside Platform. Take the typed settings instead, " +
            "which come from the one configuration file. See docs/configuration.md.");
    }

    [Fact]
    public void No_code_reads_environment_variables_or_command_line_arguments()
    {
        AssertNone(
            Checker.FindForbiddenCalls(Application),
            "The application reads or changes the process environment directly. Every setting " +
            "belongs in the one configuration file. See docs/configuration.md.");
    }

    [Fact]
    public void Every_rule_names_a_registered_area()
    {
        var areas = Rules.AllowedDependencies.Keys.ToHashSet();

        var unknown = Rules.AllowedDependencies.Values
            .Concat(Rules.RestrictedNamespaces.Values)
            .Concat(Rules.ReservedNamespaces.Values)
            .SelectMany(named => named)
            .Where(area => !areas.Contains(area))
            .Distinct();

        AssertNone(unknown, "ComponentRules names areas that are not registered.");
    }

    [Fact]
    public void The_allowed_dependencies_contain_no_cycle()
    {
        var cycle = FindCycle(Rules.AllowedDependencies);

        Assert.True(
            cycle is null,
            $"The allowed dependencies must be one-directional, but they contain a cycle: {string.Join(" -> ", cycle ?? [])}");
    }

    [Fact]
    public void A_cycle_in_the_allowed_dependencies_is_found()
    {
        var cyclic = new Dictionary<string, IReadOnlySet<string>>
        {
            ["A"] = new HashSet<string> { "B" },
            ["B"] = new HashSet<string> { "C" },
            ["C"] = new HashSet<string> { "A" },
        };

        Assert.NotNull(FindCycle(cyclic));
    }

    private static void AssertNone<T>(IEnumerable<T> found, string explanation)
    {
        var lines = found.Select(item => $"  {item}").ToList();

        Assert.True(lines.Count == 0, $"{explanation}{Environment.NewLine}{string.Join(Environment.NewLine, lines)}");
    }

    /// <summary>Returns the areas along one cycle, or null when the graph has none.</summary>
    private static IReadOnlyList<string>? FindCycle(IReadOnlyDictionary<string, IReadOnlySet<string>> graph)
    {
        var finished = new HashSet<string>();
        var path = new List<string>();

        foreach (var start in graph.Keys)
        {
            if (Visit(start) is { } cycle)
            {
                return cycle;
            }
        }

        return null;

        IReadOnlyList<string>? Visit(string area)
        {
            if (finished.Contains(area))
            {
                return null;
            }

            var seenAt = path.IndexOf(area);
            if (seenAt >= 0)
            {
                return [.. path[seenAt..], area];
            }

            path.Add(area);
            foreach (var next in graph.GetValueOrDefault(area) ?? new HashSet<string>())
            {
                if (Visit(next) is { } cycle)
                {
                    return cycle;
                }
            }

            path.RemoveAt(path.Count - 1);
            finished.Add(area);
            return null;
        }
    }
}
