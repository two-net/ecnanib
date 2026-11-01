using Mono.Cecil;

namespace Ecnanib.Tests.Architecture;

/// <summary>Enforces NFR-MNT-01 on the application assembly.</summary>
public sealed class ModuleBoundaryTests
{
    private static readonly ModuleBoundaries Boundaries = new(typeof(Program).Assembly, "Ecnanib", ModuleMap.Allowed);

    [Fact]
    public void Units_depend_only_on_the_contracts_the_map_allows() =>
        Assert.Empty(Boundaries.FindDependencyViolations().Select(violation => violation.ToString()));

    [Fact]
    public void Implementation_types_are_not_public() =>
        Assert.Empty(Boundaries.FindPublicInternals().Select(violation => violation.ToString()));

    [Fact]
    public void The_map_has_no_cycles() => Assert.Null(Boundaries.FindCycle());

    /// <summary>
    /// Deliberately independent of the map, so editing the map can't quietly weaken it. AI output
    /// must not reach the Risk Engine's code, and determinism is only provable with explicit
    /// inputs (FR-RISK-01, NFR-SEC-08).
    /// </summary>
    [Fact]
    public void Risk_engine_depends_only_on_the_kernel()
    {
        var offending = Boundaries.Dependencies()
            .Where(edge => edge.Source.StartsWith("Ecnanib.Modules.RiskEngine.", StringComparison.Ordinal)
                && !edge.Target.StartsWith("Ecnanib.Modules.RiskEngine.", StringComparison.Ordinal)
                && !edge.Target.StartsWith("Ecnanib.Kernel.", StringComparison.Ordinal));

        Assert.Empty(offending);
    }

    /// <summary>A module left out of Program.cs would otherwise fail only when first used.</summary>
    [Fact]
    public void Program_registers_every_module()
    {
        // Read with Cecil because ArchUnitNET drops compiler-generated methods, which include the
        // method that holds Program.cs's top-level statements.
        using var module = ModuleDefinition.ReadModule(typeof(Program).Assembly.Location);
        var called = module.EntryPoint.Body.Instructions
            .Select(instruction => instruction.Operand)
            .OfType<MethodReference>()
            .Select(method => method.DeclaringType.FullName)
            .ToHashSet();

        var missing = ModuleMap.Registered
            .Select(unit => unit == ModuleMap.Platform
                ? "Ecnanib.Platform.PlatformModule"
                : $"Ecnanib.Modules.{unit}.{unit}Module")
            .Where(entryClass => !called.Contains(entryClass));

        Assert.Empty(missing);
    }
}
