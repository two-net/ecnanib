using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace Ecnanib.Tests.Persistence;

/// <summary>
/// Finds what a migration drops or rewrites: a migration that does is marked as destructive, and
/// is preceded by a backup (NFR-DAT-11). See docs/backups.md.
/// </summary>
/// <remarks>
/// <para>
/// It reads the operations of a migration, and of a statement that was written by hand the words
/// that it begins with. So it does not see what a function does that a statement calls, nor a
/// statement inside another: whoever writes such a migration marks it.
/// </para>
/// <para>
/// A statement that fills a column which the same migration added counts too: the rule cannot
/// tell it from one that writes over what was stored.
/// </para>
/// </remarks>
internal static partial class DestructiveRules
{
    /// <summary>What the operations of a migration drop or rewrite, each in a few words. Empty for a migration that only adds.</summary>
    public static IReadOnlyList<string> FindIn(IEnumerable<MigrationOperation> operations) =>
        [.. operations.Select(WhatItLoses).OfType<string>()];

    private static string? WhatItLoses(MigrationOperation operation) => operation switch
    {
        DropTableOperation drop => $"drops the table {drop.Name}",
        DropColumnOperation drop => $"drops the column {drop.Table}.{drop.Name}",
        DropSchemaOperation drop => $"drops the schema {drop.Name}",
        DeleteDataOperation delete => $"deletes rows of {delete.Table}",
        UpdateDataOperation update => $"updates rows of {update.Table}",
        AlterColumnOperation alter when ChangesTheType(alter) => $"changes the type of {alter.Table}.{alter.Name}",
        SqlOperation statement when Beginning(statement.Sql) is { } words => $"sends a statement of {words}",
        _ => null,
    };

    /// <summary>Whether the values of a column are stored anew, as another type. A column that only becomes required is not.</summary>
    private static bool ChangesTheType(AlterColumnOperation alter) =>
        alter.ColumnType is { } now && alter.OldColumn.ColumnType is { } before
            ? !string.Equals(now, before, StringComparison.OrdinalIgnoreCase)
            : alter.ClrType != alter.OldColumn.ClrType;

    /// <summary>The words that say a statement drops or rewrites, or null for a statement that does neither.</summary>
    private static string? Beginning(string sql)
    {
        var statement = Comments().Replace(sql, " ").TrimStart();

        return statement switch
        {
            _ when Begins(statement, "UPDATE") => "UPDATE",
            _ when Begins(statement, "DELETE") => "DELETE",
            _ when Begins(statement, "TRUNCATE") => "TRUNCATE",
            _ when Begins(statement, @"DROP\s+TABLE") => "DROP TABLE",
            _ when Begins(statement, @"DROP\s+SCHEMA") => "DROP SCHEMA",
            _ when Begins(statement, @"ALTER\s+TABLE") && Has(statement, @"DROP\s+COLUMN") => "ALTER TABLE with DROP COLUMN",
            _ when Begins(statement, @"ALTER\s+TABLE") && Has(statement, @"ALTER\s+COLUMN\s+\S+\s+(SET\s+DATA\s+)?TYPE") => "ALTER TABLE with TYPE",

            // What a statement that reads first goes on to do, and what an insert does to the row that is in its way.
            _ when Begins(statement, "WITH") && Has(statement, "UPDATE|DELETE") => "WITH and UPDATE or DELETE",
            _ when Begins(statement, "INSERT") && Has(statement, @"ON\s+CONFLICT\b.*\bDO\s+UPDATE") => "INSERT with ON CONFLICT DO UPDATE",
            _ => null,
        };
    }

    private static bool Begins(string statement, string words) =>
        Regex.IsMatch(statement, $@"\A(?:{words})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool Has(string statement, string words) =>
        Regex.IsMatch(statement, $@"\b(?:{words})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);

    // A comment to the end of its line, and one between its marks.
    [GeneratedRegex(@"--[^\n]*|/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex Comments();
}
