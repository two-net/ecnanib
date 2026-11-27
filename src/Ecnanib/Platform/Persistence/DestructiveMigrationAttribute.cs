namespace Ecnanib.Platform.Persistence;

/// <summary>
/// Marks a migration that drops or rewrites data as destructive (NFR-DAT-11). The application
/// backs the database up before it applies a migration that carries this, and does not apply it
/// when the backup fails. See docs/backups.md.
/// </summary>
/// <remarks>
/// <para>
/// It goes on the class of the migration, in the file that is read and edited, and not in the
/// designer file beside it.
/// </para>
/// <para>
/// A migration drops or rewrites data when it drops a table, a column, or a schema, deletes or
/// updates rows, changes the type of a column, or sends a statement of its own that does one of
/// these. The tests find such a migration that is not marked. A migration can be marked for
/// what they cannot see, too: a function that is called, or a statement inside another.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class DestructiveMigrationAttribute : Attribute;
