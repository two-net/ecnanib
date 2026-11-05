using Mono.Cecil;
using Mono.Cecil.Cil;
using ReflectionAssembly = System.Reflection.Assembly;

namespace Ecnanib.Tests.Architecture;

/// <summary>Something in the Risk Engine that could make an evaluation depend on more than its arguments.</summary>
internal sealed record Nondeterminism(string Source, string What)
{
    public override string ToString() => $"{Source} -> {What}";
}

/// <summary>
/// FR-RISK-01: the engine is deterministic, so the same inputs always give the same evaluation. Rule 4's test
/// keeps other modules out of it, and this keeps out what's left: clocks, randomness, I/O, threads and binary
/// floating point, which DATA-R-02 rules out for money anyway. It reads method bodies, signatures and fields, so
/// it sees lambdas and local functions. The module's entry class and its configuration reader are exempt,
/// because they wire the engine up rather than evaluate. It takes the assembly and namespace as parameters so
/// that RiskEngineDeterminismGuardTests can prove it catches real uses.
/// </summary>
internal sealed class RiskEngineDeterminism(ReflectionAssembly assembly, string engineNamespace, IReadOnlyCollection<string> exempt)
{
    private static readonly (string Reference, string What)[] Banned =
    [
        ("System.DateTime::get_Now", "reads the clock"),
        ("System.DateTime::get_UtcNow", "reads the clock"),
        ("System.DateTime::get_Today", "reads the clock"),
        ("System.DateTimeOffset::get_Now", "reads the clock"),
        ("System.DateTimeOffset::get_UtcNow", "reads the clock"),
        ("System.TimeProvider", "reads the clock"),
        ("System.Diagnostics.Stopwatch", "reads the clock"),
        ("System.Environment", "reads the machine"),
        ("System.Random", "is random"),
        ("System.Security.Cryptography.RandomNumberGenerator", "is random"),
        ("System.Guid::NewGuid", "is random"),
        ("System.Threading.Tasks.", "runs asynchronously"),
        ("System.Threading.Thread", "runs on another thread"),
        ("System.Net.", "reaches the network"),
        ("System.IO.", "reads or writes files"),
        ("Microsoft.EntityFrameworkCore", "reads the database"),
        ("Microsoft.Extensions.Configuration", "reads configuration"),
        ("System.Double", "uses binary floating point"),
        ("System.Single", "uses binary floating point"),
    ];

    private static readonly HashSet<Code> FloatingPoint = [Code.Ldc_R4, Code.Ldc_R8, Code.Conv_R4, Code.Conv_R8, Code.Conv_R_Un];

    public List<Nondeterminism> Find()
    {
        using var module = ModuleDefinition.ReadModule(assembly.Location);
        var prefix = engineNamespace + ".";
        var found = new List<Nondeterminism>();

        foreach (var type in module.GetTypes().Where(type => Scanned(type, prefix)))
        {
            foreach (var field in type.Fields)
            {
                Check(type, field.FieldType.FullName, found);
            }

            foreach (var method in type.Methods)
            {
                Check(type, method.ReturnType.FullName, found);

                foreach (var parameter in method.Parameters)
                {
                    Check(type, parameter.ParameterType.FullName, found);
                }

                if (!method.HasBody)
                {
                    continue;
                }

                foreach (var local in method.Body.Variables)
                {
                    Check(type, local.VariableType.FullName, found);
                }

                foreach (var instruction in method.Body.Instructions)
                {
                    if (FloatingPoint.Contains(instruction.OpCode.Code))
                    {
                        found.Add(new Nondeterminism(type.FullName, $"{instruction.OpCode}: uses binary floating point"));
                    }

                    if (instruction.Operand is MemberReference member)
                    {
                        Check(type, member.FullName, found);
                    }
                }
            }
        }

        return [.. found.Distinct()];
    }

    private bool Scanned(TypeDefinition type, string prefix)
    {
        // A nested type, such as a lambda's closure, belongs to the type it's declared in.
        var outer = type;

        while (outer.DeclaringType is not null)
        {
            outer = outer.DeclaringType;
        }

        return outer.FullName.StartsWith(prefix, StringComparison.Ordinal) && !exempt.Contains(outer.Name);
    }

    private static void Check(TypeDefinition type, string reference, List<Nondeterminism> found)
    {
        foreach (var (banned, what) in Banned)
        {
            if (reference.Contains(banned, StringComparison.Ordinal))
            {
                found.Add(new Nondeterminism(type.FullName, $"{reference}: {what}"));
            }
        }
    }
}

/// <summary>FR-RISK-01: the engine depends on nothing but its arguments.</summary>
public sealed class RiskEngineDeterminismTests
{
    [Fact]
    public void The_engine_reads_no_clock_randomness_io_or_floating_point()
    {
        var scan = new RiskEngineDeterminism(
            typeof(Program).Assembly,
            "Ecnanib.Modules.RiskEngine",
            ["RiskEngineModule", "RiskCapsSettings", "RiskEngineStartup"]);

        Assert.Empty(scan.Find());
    }
}

/// <summary>
/// Tests the scan against deliberately broken fixtures. It's the only automated guard on the engine's
/// determinism, so a check that silently stopped matching would be invisible without this.
/// </summary>
public sealed class RiskEngineDeterminismGuardTests
{
    private static readonly List<Nondeterminism> Found = new RiskEngineDeterminism(
        typeof(RiskEngineDeterminismGuardTests).Assembly,
        "Ecnanib.Tests.Architecture.DeterminismFixtures",
        ["Wiring"]).Find();

    [Theory]
    [InlineData("ReadsTheClock", "reads the clock")]
    [InlineData("ReadsTheClockInALambda", "reads the clock")]
    [InlineData("TakesATimeProvider", "reads the clock")]
    [InlineData("IsRandom", "is random")]
    [InlineData("MakesAGuid", "is random")]
    [InlineData("RunsAsynchronously", "runs asynchronously")]
    [InlineData("ReadsAFile", "reads or writes files")]
    [InlineData("ReadsTheMachine", "reads the machine")]
    [InlineData("StoresADouble", "binary floating point")]
    [InlineData("ConvertsToDouble", "binary floating point")]
    [InlineData("ReturnsAFloat", "binary floating point")]
    public void A_nondeterministic_use_is_found(string fixture, string what) =>
        Assert.Contains(Found, use => use.Source.Contains(fixture, StringComparison.Ordinal) && use.What.Contains(what, StringComparison.Ordinal));

    [Theory]
    [InlineData("PureDecimalMath")]
    [InlineData("Wiring")]
    public void A_deterministic_or_exempt_type_passes(string fixture) =>
        Assert.DoesNotContain(Found, use => use.Source.Contains(fixture, StringComparison.Ordinal));
}
