namespace Ecnanib.Platform.Settings;

/// <summary>
/// The <c>Backup</c> section: where the backups of the database go, how often one is taken, how
/// long one is kept, how often a restore is tested, and the client tools of PostgreSQL that take
/// and restore them (NFR-DAT-11). See docs/backups.md.
/// </summary>
/// <param name="Directory">
/// The folder of the backups. A path that is not a full one starts at the folder the application
/// runs from, as the path of the log file does.
/// </param>
/// <param name="IntervalHours">The time between two backups: from an hour to 24, so a day has one at the least.</param>
/// <param name="KeepDays">How long a backup is kept: from a day to 36500 days.</param>
/// <param name="RestoreTestDays">The time between two tests of a restore: from a day to 365 days.</param>
/// <param name="PgDump">The tool that takes a backup: a name that is found on the path of the process, or a full path.</param>
/// <param name="PgRestore">The tool that restores one, named in the same way.</param>
public sealed record BackupSettings(
    string Directory,
    int IntervalHours,
    int KeepDays,
    int RestoreTestDays,
    string PgDump,
    string PgRestore)
{
    internal static BackupSettings Read(SettingsReader file) =>
        new(
            file.Text("Backup:Directory"),

            // The database is backed up daily: a file can ask for more backups, and not for fewer.
            file.WholeNumber("Backup:IntervalHours", minimum: 1, maximum: 24),
            file.WholeNumber("Backup:KeepDays", minimum: 1, maximum: MostDays),
            file.WholeNumber("Backup:RestoreTestDays", minimum: 1, maximum: 365),
            file.Text("Backup:PgDump"),
            file.Text("Backup:PgRestore"));

    // A hundred years, as for what is kept of the records: it is counted back from now to a
    // moment on a calendar, so it may not be so large that there is no such moment.
    private const int MostDays = 36500;
}
