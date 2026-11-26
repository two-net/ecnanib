using Npgsql;

namespace Ecnanib.Platform.Persistence;

/// <summary>
/// Asks the database for the sizes of its tables (FR-OPS-2). See <see cref="IDatabaseSizes"/>.
/// </summary>
/// <remarks>
/// The statement is fixed text, takes no value, and reads the catalog of the database alone: it
/// names no table of the application, so it lists whatever tables there are, the one that keeps
/// the migrations included. It is sent on a connection of the application's pool.
/// </remarks>
internal sealed class DatabaseSizes(NpgsqlDataSource database) : IDatabaseSizes
{
    public async Task<IReadOnlyList<TableSize>> ReadAsync(CancellationToken cancellationToken)
    {
        // The ordinary tables of the schema that the application's tables are in. The size is the
        // table with its indexes and its out-of-line storage, which is what grows on the disk.
        await using var command = database.CreateCommand(
            """
            SELECT tables.relname, pg_total_relation_size(tables.oid)
            FROM pg_class AS tables
            JOIN pg_namespace AS schemas ON schemas.oid = tables.relnamespace
            WHERE schemas.nspname = current_schema() AND tables.relkind = 'r'
            ORDER BY pg_total_relation_size(tables.oid) DESC, tables.relname
            """);

        await using var rows = await command.ExecuteReaderAsync(cancellationToken);

        var sizes = new List<TableSize>();
        while (await rows.ReadAsync(cancellationToken))
        {
            sizes.Add(new TableSize(rows.GetString(0), rows.GetInt64(1)));
        }

        return sizes;
    }
}
