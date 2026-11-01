namespace Ecnanib.Tests.Architecture;

/// <summary>
/// Tests the boundary rules themselves against deliberately broken fixtures. These rules are the
/// only guard on module boundaries, so a rule that silently stopped matching would be invisible
/// without this.
/// </summary>
public sealed class ModuleBoundaryGuardTests
{
    private const string Root = "Ecnanib.Tests.Architecture.Fixtures";
    private const string Alpha = $"{Root}.Modules.Alpha";

    private static readonly IReadOnlyDictionary<string, string[]> FixtureMap = new Dictionary<string, string[]>
    {
        ["Alpha"] = ["Beta"],
        ["Beta"] = [],
        ["Gamma"] = [],
    };

    private static readonly ModuleBoundaries Boundaries =
        new(typeof(ModuleBoundaryGuardTests).Assembly, Root, FixtureMap);

    private static readonly List<Violation> Violations = [.. Boundaries.FindDependencyViolations()];

    [Theory]
    [InlineData("ViaField")]
    [InlineData("ViaProperty")]
    [InlineData("ViaParameter")]
    [InlineData("ViaReturnType")]
    [InlineData("ViaBaseType")]
    [InlineData("ViaInterface")]
    [InlineData("ViaClassAttribute")]
    [InlineData("ViaMethodAttribute")]
    [InlineData("ViaMethodBody")]
    [InlineData("ViaStaticCall")]
    [InlineData("ViaMethodGroup")]
    [InlineData("ViaLambda")]
    [InlineData("ViaClosure")]
    [InlineData("ViaLocalFunction")]
    [InlineData("ViaAsync")]
    [InlineData("ViaIterator")]
    [InlineData("ViaGenericField")]
    [InlineData("ViaGenericMethodCall")]
    [InlineData("ViaGenericConstraint")]
    [InlineData("ViaTypeof")]
    [InlineData("ViaTypeCheck")]
    [InlineData("ViaSwitchPattern")]
    [InlineData("ViaCatchClause")]
    public void A_forbidden_dependency_is_reported(string fixture) => Assert.Contains(Violations, IsGammaEdgeFrom(fixture));

    /// <summary>
    /// The compiler inlines these, so they leave nothing to scan. They're pinned here so that
    /// the blind spots listed in docs/architecture.md stay accurate.
    /// </summary>
    [Theory]
    [InlineData("ViaConstant")]
    [InlineData("ViaEnumValue")]
    [InlineData("ViaNameof")]
    public void A_compile_time_only_reference_cannot_be_seen(string fixture) =>
        Assert.DoesNotContain(Violations, IsGammaEdgeFrom(fixture));

    [Fact]
    public void A_dependency_on_an_allowed_contract_is_not_reported() =>
        Assert.DoesNotContain(Violations, violation => violation.Source == $"{Alpha}.Clean");

    [Fact]
    public void A_dependency_on_the_internals_of_an_allowed_unit_is_reported() =>
        Assert.Contains(Violations, violation =>
            violation.Source == $"{Alpha}.UsesInternalOfAllowedUnit"
            && violation.Rule.EndsWith("is internal to Beta", StringComparison.Ordinal));

    [Fact]
    public void A_namespace_missing_from_the_map_is_reported() =>
        Assert.Contains(Violations, violation =>
            violation.Source == $"{Root}.Modules.Delta.NotInTheMap"
            && violation.Rule == "belongs to no unit in the map");

    [Fact]
    public void A_public_implementation_type_is_reported()
    {
        var reported = Boundaries.FindPublicInternals().Select(violation => violation.Source).ToList();

        Assert.Contains($"{Root}.Modules.Beta.LeakedImplementation", reported);
        Assert.DoesNotContain($"{Root}.Modules.Beta.BetaModule", reported);
        Assert.DoesNotContain($"{Root}.Modules.Gamma.Contracts.GammaThing", reported);
    }

    [Fact]
    public void A_cycle_in_the_map_is_reported()
    {
        var cyclic = new Dictionary<string, string[]> { ["A"] = ["B"], ["B"] = ["C"], ["C"] = ["A"] };
        var boundaries = new ModuleBoundaries(typeof(ModuleBoundaryGuardTests).Assembly, Root, cyclic);

        Assert.Equal(["A", "B", "C", "A"], boundaries.FindCycle());
    }

    private static Predicate<Violation> IsGammaEdgeFrom(string fixture) => violation =>
        violation.Source.StartsWith($"{Alpha}.{fixture}", StringComparison.Ordinal)
        && violation.Rule == "Alpha may not depend on Gamma";
}
