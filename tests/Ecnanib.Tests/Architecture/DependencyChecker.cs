using System.Collections.Concurrent;
using System.Reflection;
using ArchUnitNET.Domain.Dependencies;
using ArchUnitNET.Loader;
using IType = ArchUnitNET.Domain.IType;
using TypeGraph = ArchUnitNET.Domain.Architecture;

namespace Ecnanib.Tests.Architecture;

/// <summary>A set of dependency rules over the namespaces below one root namespace.</summary>
/// <param name="RootNamespace">The namespace that contains the areas, for example <c>Ecnanib</c>.</param>
/// <param name="AllowedDependencies">For each area, the other areas it may reference.</param>
/// <param name="RestrictedNamespaces">
/// Namespaces, relative to the root, that only the listed areas may reference.
/// </param>
/// <param name="OutboundNetworkNamespaces">
/// Namespaces of network client types, usable only inside an <c>Adapters</c> namespace.
/// </param>
/// <param name="ReservedNamespaces">
/// Namespaces outside the root that only the listed areas may reference.
/// </param>
/// <param name="ForbiddenCalls">
/// Methods that no code may call, each written as <c>Namespace.Type::Method</c>. A name also
/// covers every method that starts with it.
/// </param>
internal sealed record RuleSet(
    string RootNamespace,
    IReadOnlyDictionary<string, IReadOnlySet<string>> AllowedDependencies,
    IReadOnlyDictionary<string, IReadOnlySet<string>> RestrictedNamespaces,
    IReadOnlyList<string> OutboundNetworkNamespaces,
    IReadOnlyDictionary<string, IReadOnlySet<string>> ReservedNamespaces,
    IReadOnlyList<string> ForbiddenCalls);

/// <summary>One reference from a type to a type it is not allowed to use.</summary>
internal sealed record Violation(string Origin, string Target, string Reason)
{
    public override string ToString() => $"{Origin} -> {Target}: {Reason}";
}

/// <summary>Finds the references in an assembly that break a <see cref="RuleSet"/>.</summary>
internal sealed class DependencyChecker(RuleSet rules)
{
    private const string AdaptersSegment = "Adapters";

    private static readonly ConcurrentDictionary<Assembly, TypeGraph> Loaded = new();

    private readonly string _rootPrefix = rules.RootNamespace + ".";

    /// <summary>The area a namespace belongs to, or null when it is outside every area.</summary>
    public string? AreaOf(string @namespace)
    {
        if (!@namespace.StartsWith(_rootPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var relative = @namespace[_rootPrefix.Length..];
        return rules.AllowedDependencies.Keys.FirstOrDefault(area => IsWithin(relative, area));
    }

    /// <summary>Types under the root namespace that sit in no registered area.</summary>
    public IReadOnlyList<string> FindTypesOutsideAreas(Assembly assembly) =>
    [
        .. Load(assembly).Types
            .Where(type => IsWithin(type.Namespace.FullName, rules.RootNamespace))
            .Where(type => AreaOf(type.Namespace.FullName) is null)
            .Select(type => type.FullName)
            .Distinct()
            .Order(StringComparer.Ordinal),
    ];

    /// <summary>References from one area to an area that is not on its allow-list.</summary>
    public IReadOnlyList<Violation> FindAreaViolations(Assembly assembly) => Find(assembly, (origin, target) =>
    {
        var originArea = AreaOf(origin.Namespace.FullName);
        var targetArea = AreaOf(target.Namespace.FullName);
        if (originArea is null || targetArea is null || originArea == targetArea)
        {
            return null;
        }

        return rules.AllowedDependencies[originArea].Contains(targetArea)
            ? null
            : $"{originArea} may not depend on {targetArea}";
    });

    /// <summary>
    /// References into a restricted namespace, or into a component's <c>Adapters</c> namespace,
    /// from code that is not allowed to use it.
    /// </summary>
    public IReadOnlyList<Violation> FindRestrictedNamespaceViolations(Assembly assembly) => Find(assembly, (origin, target) =>
    {
        var targetArea = AreaOf(target.Namespace.FullName);
        if (targetArea is null)
        {
            return null;
        }

        var originArea = AreaOf(origin.Namespace.FullName);
        var targetNamespace = target.Namespace.FullName[_rootPrefix.Length..];

        if (IsWithin(targetNamespace, $"{targetArea}.{AdaptersSegment}") && originArea != targetArea)
        {
            return $"adapters of {targetArea} are private to it";
        }

        foreach (var (restricted, audience) in rules.RestrictedNamespaces)
        {
            if (IsWithin(targetNamespace, restricted) && (originArea is null || !audience.Contains(originArea)))
            {
                return $"only {string.Join(", ", audience.Order(StringComparer.Ordinal))} may use {restricted}";
            }
        }

        return null;
    });

    /// <summary>Uses of network client types outside an <c>Adapters</c> namespace.</summary>
    public IReadOnlyList<Violation> FindNetworkViolations(Assembly assembly) => Find(assembly, (origin, target) =>
    {
        if (IsInAdapters(origin.Namespace.FullName))
        {
            return null;
        }

        var network = rules.OutboundNetworkNamespaces.FirstOrDefault(ns => IsWithin(target.Namespace.FullName, ns));
        return network is null ? null : $"{network} may be used only inside an {AdaptersSegment} namespace";
    });

    /// <summary>References to a reserved namespace from an area that is not allowed to use it.</summary>
    public IReadOnlyList<Violation> FindReservedNamespaceViolations(Assembly assembly) => Find(assembly, (origin, target) =>
    {
        var originArea = AreaOf(origin.Namespace.FullName);

        foreach (var (reserved, audience) in rules.ReservedNamespaces)
        {
            if (IsWithin(target.Namespace.FullName, reserved) && (originArea is null || !audience.Contains(originArea)))
            {
                return $"only {string.Join(", ", audience.Order(StringComparer.Ordinal))} may use {reserved}";
            }
        }

        return null;
    });

    /// <summary>Calls to a method that no code may call.</summary>
    public IReadOnlyList<Violation> FindForbiddenCalls(Assembly assembly) =>
    [
        .. Load(assembly).Types
            .SelectMany(origin => origin.Dependencies.OfType<MethodCallDependency>()
                .Select(call => (origin, method: $"{call.Target.FullName}::{call.TargetMember.Name}")))
            .Where(call => rules.ForbiddenCalls.Any(forbidden => call.method.StartsWith(forbidden, StringComparison.Ordinal)))
            .Select(call => new Violation(call.origin.FullName, call.method, "no code may call this method"))
            .Distinct()
            .OrderBy(violation => violation.Origin, StringComparer.Ordinal)
            .ThenBy(violation => violation.Target, StringComparer.Ordinal),
    ];

    private bool IsInAdapters(string @namespace)
    {
        var area = AreaOf(@namespace);
        return area is not null && IsWithin(@namespace[_rootPrefix.Length..], $"{area}.{AdaptersSegment}");
    }

    private static IReadOnlyList<Violation> Find(Assembly assembly, Func<IType, IType, string?> reasonFor) =>
    [
        .. Load(assembly).Types
            .SelectMany(origin => origin.Dependencies.Select(dependency => (origin, target: dependency.Target)))

            // A generic parameter is a target without a namespace. It is a placeholder, not a
            // type that code depends on, so no rule applies to it.
            .Where(pair => pair.target.Namespace is not null)
            .Select(pair => (pair.origin, pair.target, reason: reasonFor(pair.origin, pair.target)))
            .Where(found => found.reason is not null)
            .Select(found => new Violation(found.origin.FullName, found.target.FullName, found.reason!))
            .Distinct()
            .OrderBy(violation => violation.Origin, StringComparer.Ordinal)
            .ThenBy(violation => violation.Target, StringComparer.Ordinal),
    ];

    private static TypeGraph Load(Assembly assembly) =>
        Loaded.GetOrAdd(assembly, toLoad => new ArchLoader().LoadAssemblies(toLoad).Build());

    /// <summary>True when <paramref name="namespace"/> is <paramref name="parent"/> or is nested in it.</summary>
    private static bool IsWithin(string @namespace, string parent) =>
        @namespace == parent || @namespace.StartsWith(parent + ".", StringComparison.Ordinal);
}
