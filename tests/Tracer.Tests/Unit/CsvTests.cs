using Tracer.Api.Export;

namespace Tracer.Tests.Unit;

public class CsvTests
{
    [Fact]
    public void Plain_values_are_written_bare() => Assert.Equal("hello", Csv.Field("hello"));

    [Fact]
    public void An_empty_value_is_an_empty_field() => Assert.Equal(string.Empty, Csv.Field(null));

    [Theory]
    [InlineData("a,b", "\"a,b\"")] // unquoted, this would silently become two columns
    [InlineData("line\nbreak", "\"line\nbreak\"")]
    [InlineData("carriage\rreturn", "\"carriage\rreturn\"")]
    public void Values_that_would_break_the_row_are_quoted(string value, string expected) =>
        Assert.Equal(expected, Csv.Field(value));

    [Fact]
    public void Quotes_are_doubled_inside_a_quoted_field() =>
        // RFC 4180's escape: a literal " is written "".
        Assert.Equal("\"she said \"\"hi\"\"\"", Csv.Field("she said \"hi\""));

    [Theory]
    [InlineData("=1+1")]
    [InlineData("+1")]
    [InlineData("-2+3+cmd|' /C calc'!A0")]
    [InlineData("@SUM(A1)")]
    [InlineData("\tsneaky")]
    public void Values_a_spreadsheet_would_execute_are_defused(string value)
    {
        var field = Csv.Field(value);

        // The apostrophe is the spreadsheet's own "this is text" marker. Quoting
        // alone would not do it: the CSV parser strips the quotes before the
        // formula engine ever sees the cell.
        Assert.StartsWith("'", field.TrimStart('"'));
        Assert.Contains(value.TrimStart('\t'), field);
    }

    [Fact]
    public void A_defused_value_that_also_needs_quoting_gets_both()
    {
        var field = Csv.Field("=cmd,calc");

        Assert.Equal("\"'=cmd,calc\"", field);
    }

    [Fact]
    public void Ordinary_text_is_not_defused()
    {
        // The trigger is the *leading* character; a formula-looking string in the
        // middle of a sentence is just a sentence.
        Assert.Equal("2 + 2 = 4", Csv.Field("2 + 2 = 4"));
        Assert.Equal("hello", Csv.Field("hello"));
    }

    [Fact]
    public void Render_writes_a_header_row_then_the_data_rows()
    {
        var csv = Csv.Render(["a", "b"], [["1", "2"], ["3", null]]);

        // CRLF per RFC 4180, and a null cell is an empty field rather than "null".
        Assert.Equal("a,b\r\n1,2\r\n3,\r\n", csv);
    }
}
