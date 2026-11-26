namespace Ecnanib.Platform.Persistence;

/// <summary>
/// How much room each table of the database takes. Decision records and transcripts grow with
/// every cycle, and the sizes of the tables are how that growth is watched (FR-OPS-2, risk R7 of
/// the specification). See docs/persistence.md.
/// </summary>
public interface IDatabaseSizes
{
    /// <summary>
    /// The size of every table of the application's database, the largest first, as the database
    /// says it now. It reads no row of any table.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<IReadOnlyList<TableSize>> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>The size of one table.</summary>
/// <param name="Name">The table, as the database names it.</param>
/// <param name="Bytes">What the table takes on disk in all, with its indexes and with what it stores out of line.</param>
public sealed record TableSize(string Name, long Bytes);
