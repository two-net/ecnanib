using ArchUnitNET.Domain;
using ArchUnitNET.Loader;
using Mono.Cecil;
using ArchModel = ArchUnitNET.Domain.Architecture;
using ReflectionAssembly = System.Reflection.Assembly;

namespace Ecnanib.Tests.Architecture;

/// <summary>A dependency or declaration that breaks a boundary rule.</summary>
internal sealed record Violation(string Source, string Target, string Rule)
{
    public override string ToString() => $"{Source} -> {Target}: {Rule}";
}

/// <summary>
/// Checks compiled code against a dependency map. All modules share one assembly, so `internal`
/// separates nothing and these rules are the only boundary. They take the assembly, root namespace
/// and map as parameters so that ModuleBoundaryGuardTests can prove they catch real violations.
/// </summary>
internal sealed class ModuleBoundaries(
    ReflectionAssembly assembly,
    string rootNamespace,
    IReadOnlyDictionary<string, string[]> allowed)
{
    private readonly ArchModel _architecture = new ArchLoader().LoadAssemblies(assembly).Build();
    private readonly string _prefix = rootNamespace + ".";

    /// <summary>Every reference from a type under the root namespace to another such type.</summary>
    public IEnumerable<(string Source, string Target)> Dependencies() =>
        ScannedTypes()
            .SelectMany(type => type.Dependencies.Select(dependency => (type.FullName, dependency.Target.FullName)))
            .Concat(CatchClauses())
            .Where(edge => edge.Item2.StartsWith(_prefix, StringComparison.Ordinal))
            .Distinct();

    /// <summary>Every type outside the map, and every dependency not through an allowed Contracts namespace.</summary>
    public IEnumerable<Violation> FindDependencyViolations()
    {
        foreach (var type in ScannedTypes().Where(type => UnitOf(type.FullName) is null))
        {
            yield return new Violation(type.FullName, "-", "belongs to no unit in the map");
        }

        foreach (var (source, target) in Dependencies())
        {
            if (UnitOf(source) is not { } sourceUnit || UnitOf(target) is not { } targetUnit || sourceUnit == targetUnit)
            {
                continue;
            }

            if (targetUnit != ModuleMap.Kernel && !allowed[sourceUnit].Contains(targetUnit))
            {
                yield return new Violation(source, target, $"{sourceUnit} may not depend on {targetUnit}");
            }
            else if (!IsPublicSurface(target, targetUnit))
            {
                yield return new Violation(source, target, $"{target} is internal to {targetUnit}");
            }
        }
    }

    /// <summary>Public types in a unit that are neither in its Contracts namespace nor its entry class.</summary>
    public IEnumerable<Violation> FindPublicInternals() =>
        from type in ScannedTypes()
        where type.Visibility == Visibility.Public
        let unit = UnitOf(type.FullName)
        where unit is not (null or ModuleMap.WebUi or ModuleMap.Kernel)
            && !IsPublicSurface(type.FullName, unit)
            && !type.FullName.EndsWith($".{unit}Module", StringComparison.Ordinal)
        select new Violation(type.FullName, "-", "public type outside Contracts");

    /// <summary>One cycle in the map, as a path, or null when the map is a DAG.</summary>
    public IReadOnlyList<string>? FindCycle()
    {
        var finished = new HashSet<string>();
        var path = new List<string>();

        IReadOnlyList<string>? Visit(string unit)
        {
            if (path.IndexOf(unit) is >= 0 and var start)
            {
                return [.. path[start..], unit];
            }

            if (!finished.Add(unit))
            {
                return null;
            }

            path.Add(unit);
            var cycle = allowed.GetValueOrDefault(unit, []).Select(Visit).FirstOrDefault(found => found is not null);
            path.RemoveAt(path.Count - 1);
            return cycle;
        }

        return allowed.Keys.Select(Visit).FirstOrDefault(found => found is not null);
    }

    private string? UnitOf(string typeName)
    {
        var parts = typeName[_prefix.Length..].Split('.');
        var unit = parts switch
        {
            ["Modules", var module, _, ..] => module,
            ["Components" or "Endpoints", ..] => ModuleMap.WebUi,
            [ModuleMap.Platform or ModuleMap.Kernel, _, ..] => parts[0],
            _ => null,
        };

        return unit is not null && allowed.ContainsKey(unit) ? unit : null;
    }

    private bool IsPublicSurface(string typeName, string unit)
    {
        var contracts = unit is ModuleMap.Platform ? $"{_prefix}Platform.Contracts." : $"{_prefix}Modules.{unit}.Contracts.";
        return unit == ModuleMap.Kernel || typeName.StartsWith(contracts, StringComparison.Ordinal);
    }

    private IEnumerable<IType> ScannedTypes() =>
        _architecture.Types.Where(type => type.FullName.StartsWith(_prefix, StringComparison.Ordinal));

    // ArchUnitNET reads a method's instructions, but a catch clause lives in its exception table.
    private List<(string Source, string Target)> CatchClauses()
    {
        using var module = ModuleDefinition.ReadModule(assembly.Location);

        return
        [
            .. from type in module.GetTypes()
               where type.FullName.StartsWith(_prefix, StringComparison.Ordinal)
               from method in type.Methods
               where method.HasBody
               from handler in method.Body.ExceptionHandlers
               where handler.CatchType is not null
               select (type.FullName.Replace('/', '+'), handler.CatchType.FullName.Replace('/', '+')),
        ];
    }
}
