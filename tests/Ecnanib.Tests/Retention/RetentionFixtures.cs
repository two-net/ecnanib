using Ecnanib.Tests.Accounts;

namespace Ecnanib.Tests.Retention;

/// <summary>What a test of the retention jobs asks the database and the log of its application.</summary>
internal static class RetentionRows
{
    /// <summary>How many rows a table holds, or how many of them a condition holds for.</summary>
    public static long Count(this AccountsApplication application, string table, string? where = null) =>
        application.Database.Scalar<long>($"""SELECT count(*) FROM "{table}" {(where is null ? "" : "WHERE " + where)}""");

    /// <summary>
    /// The statements that deleted from a table, as the mapper logged them, in the order they
    /// were sent in. The file of the tests logs from the information level up, which is the
    /// level of a statement.
    /// </summary>
    public static IReadOnlyList<string> DeletesFrom(this AccountsApplication application, string table) =>
    [
        .. application.Events()
            .Select(logged => logged.Text("commandText"))
            .OfType<string>()
            .Where(statement => statement.Contains($"DELETE FROM {table} ", StringComparison.Ordinal)),
    ];

    /// <summary>
    /// What a retention job said that it deleted: the events of its area, each with the counts
    /// that it names. A run that deleted nothing says nothing.
    /// </summary>
    public static IReadOnlyList<LoggedSummary> Summaries(this AccountsApplication application, string area) =>
    [
        .. application.Events()
            .Where(logged => logged.Area == area)
            .Select(logged => new LoggedSummary(logged.Template, logged.Level, name => logged[name].GetInt32())),
    ];

    /// <summary>Whether the database has an index by a name, and how it defines it.</summary>
    public static string IndexDefinition(this AccountsApplication application, string table, string index) =>
        Assert.Single(application.Database.Column(
            $"SELECT indexdef FROM pg_indexes WHERE tablename = '{table}' AND indexname = '{index}'"));
}

/// <summary>An event that a retention job logged.</summary>
/// <param name="Template">What it says, with the names of its counts in it.</param>
/// <param name="Level">The level that it was logged at.</param>
/// <param name="Count">The count that it names.</param>
internal sealed record LoggedSummary(string Template, string Level, Func<string, int> Count);
