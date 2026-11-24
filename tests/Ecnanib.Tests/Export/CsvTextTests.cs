using System.Globalization;
using Ecnanib.Shared;
using Ecnanib.WebUi.Exports;

namespace Ecnanib.Tests.Export;

/// <summary>
/// A user can export their trades and decisions as CSV (FR-RPT-4). This is the rule of a cell
/// and of a line of such a file: what a program reads back is what was written, and no cell is
/// worked out by a spreadsheet as a formula. See "The files" in docs/export.md.
/// </summary>
public class CsvTextTests
{
    [Fact]
    public void A_line_is_its_cells_with_commas_between_them_and_ends_as_a_line_of_CSV_does()
    {
        Assert.Equal("a,b,,d\r\n", CsvText.Line(["a", "b", string.Empty, "d"]));
        Assert.Equal("\r\n", CsvText.Line([]));
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("with a space", "with a space")]
    [InlineData("one, two", "\"one, two\"")]
    [InlineData("says \"no\"", "\"says \"\"no\"\"\"")]
    [InlineData("two\nlines", "\"two\nlines\"")]
    [InlineData("two\r\nlines", "\"two\r\nlines\"")]
    [InlineData("{\"bias\":\"bullish\",\"confidence\":0.6}", "\"{\"\"bias\"\":\"\"bullish\"\",\"\"confidence\"\":0.6}\"")]
    public void A_cell_that_holds_what_divides_cells_or_lines_stands_in_quotes_and_a_quote_in_it_is_written_twice(string cell, string written)
    {
        Assert.Equal($"first,{written},last\r\n", CsvText.Line(["first", cell, "last"]));
    }

    [Theory]
    [InlineData("=1+1")]
    [InlineData("=HYPERLINK(\"https://example.com\",\"Open\")")]
    [InlineData("+1")]
    [InlineData("-1")]
    [InlineData("@SUM(A1:A9)")]
    [InlineData("\t=1+1")]
    [InlineData("\r=1+1")]
    [InlineData("\n=1+1")]
    public void Words_that_a_spreadsheet_would_work_out_as_a_formula_are_given_an_apostrophe_before_them(string written)
    {
        Assert.Equal($"'{written}", CsvText.Text(written));
    }

    [Theory]
    [InlineData("Momentum")]
    [InlineData("BTCUSDT")]
    [InlineData("a=b+c")]
    [InlineData("{\"action\":\"open_long\"}")]
    [InlineData(" =1+1")]
    public void Other_words_are_as_they_were_written(string written)
    {
        Assert.Equal(written, CsvText.Text(written));
    }

    [Fact]
    public void Words_that_are_not_there_are_an_empty_cell()
    {
        Assert.Equal(string.Empty, CsvText.Text(null));
        Assert.Equal(string.Empty, CsvText.Text(string.Empty));
    }

    [Fact]
    public void A_number_has_every_digit_that_it_has_and_its_sign_and_nothing_else_wherever_the_file_is_written()
    {
        var before = CultureInfo.CurrentCulture;

        try
        {
            // Where a comma divides a number, and a point its thousands.
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");

            // A loss keeps its sign: a number is no words, and is given no apostrophe.
            Assert.Equal("-10.7", CsvText.Number(-10.7m));
            Assert.Equal("100000", CsvText.Number(100_000.00000000m));
            Assert.Equal("98990.5", CsvText.Number(98_990.50m));
            Assert.Equal("0.00000001", CsvText.Number(0.00000001m));
            Assert.Equal("1234567.123456789012345678", CsvText.Number(1_234_567.123456789012345678m));
            Assert.Equal("0", CsvText.Number(0m));
            Assert.Equal("1800", CsvText.Number(1800L));
            Assert.Equal("-3", CsvText.Number(-3L));
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }

        // What is not known is an empty cell, and never a nought.
        Assert.Equal(string.Empty, CsvText.Number((decimal?)null));
        Assert.Equal(string.Empty, CsvText.Number((long?)null));
    }

    [Fact]
    public void A_moment_is_in_UTC_to_the_second_and_says_so()
    {
        Assert.Equal("2026-11-24T09:00:05Z", CsvText.Time(new DateTimeOffset(2026, 11, 24, 9, 0, 5, TimeSpan.Zero)));

        // Whatever offset it is handed with, and with what is under a second left out.
        Assert.Equal("2026-11-24T09:00:05Z", CsvText.Time(new DateTimeOffset(2026, 11, 24, 11, 0, 5, 999, TimeSpan.FromHours(2))));
        Assert.Equal(string.Empty, CsvText.Time(null));
    }

    [Fact]
    public void An_id_a_member_of_a_fixed_set_and_whether_something_is_so_are_written_one_way()
    {
        var id = Guid.Parse("0193a4f2-7c1e-7b3a-9d2f-5e6a7b8c9d0e");

        Assert.Equal("0193a4f2-7c1e-7b3a-9d2f-5e6a7b8c9d0e", CsvText.Id(id));
        Assert.Equal(string.Empty, CsvText.Id(null));

        // A mode is the word that labels it (FR-BIN-11).
        Assert.Equal("Demo", CsvText.Name<TradingMode>(TradingMode.Demo));
        Assert.Equal("Live", CsvText.Name<TradingMode>(TradingMode.Live));
        Assert.Equal(string.Empty, CsvText.Name<Market>(null));

        Assert.Equal("true", CsvText.Whether(true));
        Assert.Equal("false", CsvText.Whether(false));
    }
}
