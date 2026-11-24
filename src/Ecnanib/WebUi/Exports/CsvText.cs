using System.Globalization;

namespace Ecnanib.WebUi.Exports;

/// <summary>
/// The one rule of a cell and of a line of a CSV file that a user downloads (FR-RPT-4). See
/// "The files" in docs/export.md.
/// </summary>
/// <remarks>
/// <para>
/// <b>A file is for a program to read, and a person in a spreadsheet.</b> A number has every
/// digit that it has and no word beside it, a time is in UTC and says so, and a member of a
/// fixed set is its name: nothing depends on where the file is opened. What is not known is an
/// empty cell.
/// </para>
/// <para>
/// <b>No cell is a formula.</b> A spreadsheet works out a cell that starts as a formula does,
/// and a name that a user typed, or an answer that a model wrote, may start so. Such a cell is
/// given an apostrophe before it, which a spreadsheet shows as text. A number is written here,
/// and never taken as text, so a loss keeps its sign.
/// </para>
/// </remarks>
public static class CsvText
{
    // What starts a formula in a spreadsheet, or is skipped by one before what does.
    private const string StartsFormula = "=+-@\t\r\n";

    // Every digit that a number of the service can have, and no zeros after the last.
    private const string Digits = "0.############################";

    /// <summary>One line of a file: its cells with a comma between them, and the end of a line.</summary>
    /// <param name="cells">The cells, each already as <see cref="Text"/>, <see cref="Number(decimal?)"/>, and the others write it.</param>
    public static string Line(IEnumerable<string> cells)
    {
        ArgumentNullException.ThrowIfNull(cells);

        return string.Join(',', cells.Select(Quoted)) + "\r\n";
    }

    /// <summary>
    /// A cell of words that the service did not choose: a name, a symbol, an answer of a model.
    /// One that would be read as a formula is given an apostrophe before it.
    /// </summary>
    public static string Text(string? written) =>
        string.IsNullOrEmpty(written) ? string.Empty
        : StartsFormula.Contains(written[0], StringComparison.Ordinal) ? $"'{written}"
        : written;

    /// <summary>A cell of a number, with every digit that it has. Empty where it is not known.</summary>
    public static string Number(decimal? value) => value?.ToString(Digits, CultureInfo.InvariantCulture) ?? string.Empty;

    /// <summary>A cell of a whole number. Empty where it is not known.</summary>
    public static string Number(long? value) => value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    /// <summary>A cell of a moment, in UTC, to the second: <c>2026-11-12T09:00:00Z</c>. Empty where there is none.</summary>
    public static string Time(DateTimeOffset? at) =>
        at?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) ?? string.Empty;

    /// <summary>A cell of an id. Empty where there is none.</summary>
    public static string Id(Guid? id) => id?.ToString() ?? string.Empty;

    /// <summary>A cell of a member of a fixed set, by its name: <c>Demo</c>, <c>StopLoss</c>. Empty where there is none.</summary>
    public static string Name<TEnum>(TEnum? member)
        where TEnum : struct, Enum => member?.ToString() ?? string.Empty;

    /// <summary>A cell of whether something is so: <c>true</c> or <c>false</c>.</summary>
    public static string Whether(bool so) => so ? "true" : "false";

    // A cell that holds what divides cells or lines stands in quotes, and a quote in it is written twice.
    private static string Quoted(string cell) =>
        cell.AsSpan().IndexOfAny(",\"\r\n") < 0 ? cell : $"\"{cell.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}
