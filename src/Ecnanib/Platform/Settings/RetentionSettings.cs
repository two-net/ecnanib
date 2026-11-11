namespace Ecnanib.Platform.Settings;

/// <summary>The <c>Retention</c> section: how many days each kind of record is kept (NFR-DAT-5).</summary>
public sealed record RetentionSettings(int TranscriptDays, int DecisionDays, int NotificationDays, int AuditDays)
{
    internal static RetentionSettings Read(SettingsReader file) =>
        new(
            file.WholeNumber("Retention:TranscriptDays"),
            file.WholeNumber("Retention:DecisionDays"),
            file.WholeNumber("Retention:NotificationDays"),
            file.WholeNumber("Retention:AuditDays"));
}
