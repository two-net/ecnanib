namespace Ecnanib.Platform.Settings;

/// <summary>
/// The <c>Availability</c> section: the share of a month that the service is to be up for, and
/// what makes maintenance announced maintenance, which is left out of that measure (NFR-REL-1,
/// NFR-REL-6). See docs/reliability.md.
/// </summary>
/// <param name="TargetPercent">The availability that a month is held to, in percent: from 1 to 100.</param>
/// <param name="MaintenanceNoticeHours">
/// How long before its start maintenance is announced for it to be announced in advance: from 0
/// to 720 hours. Maintenance that is announced later is shown all the same, and what the service
/// is down for during it counts.
/// </param>
/// <param name="LongestMaintenanceHours">How long maintenance that is announced lasts at the most: from an hour to 168.</param>
public sealed record AvailabilitySettings(decimal TargetPercent, int MaintenanceNoticeHours, int LongestMaintenanceHours)
{
    /// <summary>How long before its start maintenance is announced for it to be announced in advance.</summary>
    public TimeSpan MaintenanceNotice => TimeSpan.FromHours(MaintenanceNoticeHours);

    /// <summary>How long maintenance that is announced lasts at the most.</summary>
    public TimeSpan LongestMaintenance => TimeSpan.FromHours(LongestMaintenanceHours);

    internal static AvailabilitySettings Read(SettingsReader file) =>
        new(
            file.Number("Availability:TargetPercent", minimum: 1m, maximum: 100m),

            // Nothing in advance is asked for with 0: all that is announced before it starts is
            // announced maintenance then. A month is the most.
            file.WholeNumber("Availability:MaintenanceNoticeHours", minimum: 0, maximum: 720),

            // A week at the most: an end that was mistyped leaves no month out of the measure.
            file.WholeNumber("Availability:LongestMaintenanceHours", minimum: 1, maximum: 168));
}
