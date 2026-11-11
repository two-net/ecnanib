namespace Ecnanib.Platform.Settings;

/// <summary>The <c>Limits</c> section: fair-use limits that apply equally to every user (FR-ACC-7).</summary>
public sealed record LimitsSettings(int MaxAgentsPerUser)
{
    internal static LimitsSettings Read(SettingsReader file) =>
        new(file.WholeNumber("Limits:MaxAgentsPerUser"));
}
