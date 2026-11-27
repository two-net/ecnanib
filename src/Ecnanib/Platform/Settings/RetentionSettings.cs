namespace Ecnanib.Platform.Settings;

/// <summary>The <c>Retention</c> section: how many days each kind of record is kept (NFR-DAT-5).</summary>
/// <param name="TranscriptDays">How long what a role answered in a cycle is kept: from a day to 36500 days.</param>
/// <param name="DecisionDays">How long the record of a cycle is kept: from a day to 36500 days.</param>
/// <param name="NotificationDays">How long a notification is kept: from a day to 36500 days.</param>
/// <param name="AuditDays">How long an event of the audit trail is kept: from a day to 36500 days.</param>
public sealed record RetentionSettings(int TranscriptDays, int DecisionDays, int NotificationDays, int AuditDays)
{
    internal static RetentionSettings Read(SettingsReader file) =>
        new(
            file.WholeNumber("Retention:TranscriptDays", minimum: 1, maximum: MostDays),
            file.WholeNumber("Retention:DecisionDays", minimum: 1, maximum: MostDays),
            file.WholeNumber("Retention:NotificationDays", minimum: 1, maximum: MostDays),
            file.WholeNumber("Retention:AuditDays", minimum: 1, maximum: MostDays));

    // A hundred years. Each is counted back from now to a moment on a calendar, so none may be
    // so large that there is no such moment: a job that could not work out what is past its
    // time would fail with every run.
    private const int MostDays = 36500;
}
