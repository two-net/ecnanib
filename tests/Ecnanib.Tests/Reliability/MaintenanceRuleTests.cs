using System.Reflection;
using Ecnanib.Accounts;
using Ecnanib.Tests.Architecture;

namespace Ecnanib.Tests.Reliability;

/// <summary>
/// Maintenance never closes positions (NFR-REL-6). An announcement is words on every page and
/// time that is left out of a measure: nothing that trades can read it. What keeps it is a
/// namespace of the platform that only the platform itself, Accounts, which records who
/// announced what, and the Web UI, which shows it, may use. So no agent is paused, no order is
/// taken off, and no position is closed because maintenance is announced or under way: the
/// components that could do so do not know of it. What a stop of the application does to a
/// position is in <c>GracefulShutdownTests</c>. See "Announced maintenance" in
/// docs/reliability.md.
/// </summary>
public class MaintenanceRuleTests
{
    private const string Availability = "Platform.Availability";

    private static readonly Assembly Application = typeof(AccountsModule).Assembly;

    [Fact]
    public void Only_the_platform_Accounts_and_the_Web_UI_may_use_what_keeps_the_announced_maintenance()
    {
        Assert.Equal(
            [ComponentRules.Accounts, ComponentRules.Platform, ComponentRules.WebUi],
            ComponentRules.Rules.RestrictedNamespaces[Availability].Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Nothing_that_trades_reads_the_announced_maintenance()
    {
        var reaching = new DependencyChecker(ComponentRules.Rules)
            .FindRestrictedNamespaceViolations(Application)
            .Where(violation => violation.Target.StartsWith($"Ecnanib.{Availability}.", StringComparison.Ordinal))
            .ToList();

        Assert.True(
            reaching.Count == 0,
            "Maintenance never closes positions, and acts on no agent: only the platform, Accounts, and the Web UI may use " +
            $"Ecnanib.{Availability} (NFR-REL-6). These do:{Environment.NewLine}{string.Join(Environment.NewLine, reaching)}");
    }

    [Fact]
    public void The_namespace_holds_what_is_announced_and_what_is_measured()
    {
        // The rule is about a namespace that is there: a rename of it would leave the rule about nothing.
        var kept = Application.GetTypes()
            .Where(type => type.Namespace == $"Ecnanib.{Availability}")
            .Select(type => type.Name)
            .ToList();

        Assert.Contains("MaintenanceWindows", kept);
        Assert.Contains("IAnnouncedMaintenance", kept);
        Assert.Contains("IAvailability", kept);
    }
}
