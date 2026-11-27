using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecnanib.Platform.Persistence;

/// <summary>
/// How a test of a restore ended. A member is stored by its name, so renaming one changes
/// stored data and needs a migration.
/// </summary>
internal enum RestoreTestResult
{
    /// <summary>The backup was restored, and the database that came of it holds what the backup says.</summary>
    Succeeded,

    /// <summary>The backup could not be relied on to bring the database back.</summary>
    Failed,
}

/// <summary>
/// Why a test of a restore failed, in a word of the application's own. A member is stored by
/// its name, so renaming one changes stored data and needs a migration.
/// </summary>
internal enum RestoreTestFailure
{
    /// <summary>
    /// The backup was not restored: the database to restore it to could not be made, or the
    /// tool that restores ended with an error. The log says which.
    /// </summary>
    NotRestored,

    /// <summary>
    /// The backup was restored, and the database that came of it is not what the backup says:
    /// it is at another migration, lacks a table, or holds other rows. The log says what differs.
    /// </summary>
    Differs,
}

/// <summary>
/// One test of a restore, as a row of the <c>restore_tests</c> table: which backup was restored,
/// when, and how it ended (NFR-DAT-11). See docs/backups.md.
/// </summary>
/// <remarks>
/// A row says that a test failed and, in one word of the application's own, why. It holds no
/// text of an error and nothing that a tool wrote: those are in the log (NFR-DAT-7).
/// </remarks>
/// <param name="id">Assigned by the application.</param>
/// <param name="backup">The backup that was restored, by the name of its file.</param>
/// <param name="backupTakenAt">When that backup was taken.</param>
/// <param name="startedAt">When the test started, before a database was made to restore to.</param>
/// <param name="finishedAt">When the test ended.</param>
/// <param name="result">How it ended.</param>
/// <param name="failure">Why it failed, or null when it did not.</param>
/// <param name="tables">How many tables the restored database had, and zero when the backup was not restored.</param>
/// <param name="rows">How many rows those tables held together.</param>
internal sealed class RestoreTest(
    Guid id,
    string backup,
    DateTimeOffset backupTakenAt,
    DateTimeOffset startedAt,
    DateTimeOffset finishedAt,
    RestoreTestResult result,
    RestoreTestFailure? failure,
    int tables,
    long rows)
{
    public Guid Id { get; } = id;

    public string Backup { get; } = backup;

    public DateTimeOffset BackupTakenAt { get; } = backupTakenAt;

    public DateTimeOffset StartedAt { get; } = startedAt;

    public DateTimeOffset FinishedAt { get; } = finishedAt;

    public RestoreTestResult Result { get; } = result;

    public RestoreTestFailure? Failure { get; } = failure;

    public int Tables { get; } = tables;

    public long Rows { get; } = rows;
}

internal sealed class RestoreTestConfiguration : IEntityTypeConfiguration<RestoreTest>
{
    public void Configure(EntityTypeBuilder<RestoreTest> builder)
    {
        builder.ToTable("restore_tests");
        builder.HasKey(test => test.Id);

        // The application assigns the id, so a row can be written without asking the database for one.
        builder.Property(test => test.Id).ValueGeneratedNever();
        builder.Property(test => test.Backup);
        builder.Property(test => test.BackupTakenAt);
        builder.Property(test => test.StartedAt);
        builder.Property(test => test.FinishedAt);
        builder.Property(test => test.Result);
        builder.Property(test => test.Failure);
        builder.Property(test => test.Tables);
        builder.Property(test => test.Rows);

        // The latest test, and the tests in order.
        builder.HasIndex(test => test.StartedAt);
    }
}
