using System.Text;

namespace Tracer.Api.Export;

/// <summary>
/// Writes RFC 4180 CSV.
///
/// <para>
/// Hand-rolled rather than pulled from a package because the whole of it is the
/// two rules below, and both are rules this codebase should be able to point at
/// and test.
/// </para>
/// </summary>
public static class Csv
{
    /// <summary>
    /// Characters that make a spreadsheet treat a cell as a formula rather than
    /// as text. A leading '-' is in the list because <c>-2+3+cmd|' /C calc'!A0</c>
    /// is a formula too, and a leading tab or CR is there because Excel strips
    /// them and then reconsiders what is left.
    /// </summary>
    private static readonly char[] FormulaTriggers = ['=', '+', '-', '@', '\t', '\r'];

    /// <summary>Characters that force a field to be quoted.</summary>
    private static readonly char[] MustQuote = [',', '"', '\n', '\r'];

    public static string Render(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string?>> rows)
    {
        var csv = new StringBuilder();
        csv.Append(string.Join(",", headers.Select(Field)));
        // CRLF: RFC 4180 says so, and Excel on Windows is the consumer that cares.
        csv.Append("\r\n");

        foreach (var row in rows)
        {
            csv.Append(string.Join(",", row.Select(Field)));
            csv.Append("\r\n");
        }

        return csv.ToString();
    }

    /// <summary>
    /// Quotes a single field.
    ///
    /// A field is wrapped in quotes when it contains a comma, a quote, or a line
    /// break — otherwise a title with a comma in it silently becomes two columns
    /// — and any quote inside is doubled, which is how RFC 4180 escapes them.
    /// </summary>
    public static string Field(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var escaped = Defuse(value);

        return escaped.IndexOfAny(MustQuote) >= 0
            ? $"\"{escaped.Replace("\"", "\"\"")}\""
            : escaped;
    }

    /// <summary>
    /// Stops a spreadsheet from executing a cell.
    ///
    /// <para>
    /// An issue titled <c>=cmd|' /C calc'!A0</c> is a legitimate title — one a
    /// tracker full of shell snippets and formulas will genuinely contain — but
    /// Excel, LibreOffice, and Sheets read a leading '=' as "evaluate this", and
    /// a CSV export is precisely a file a person double-clicks. Quoting does not
    /// help: the parser strips the quotes before the formula engine ever sees the
    /// text. Prefixing with an apostrophe does, because that is the spreadsheet's
    /// own "this is literal text" marker.
    /// </para>
    /// <para>
    /// This is <b>deliberately one-way</b>. The apostrophe is a display artifact
    /// of the CSV rendering, not part of the title, and the import contract is
    /// JSON — where no such escape exists or is needed — so nothing ever has to
    /// undo it. A CSV export is a file for a spreadsheet to open, not a wire
    /// format to feed back in; treating it as both is what would force this to be
    /// reversible, and a reversible escape that a spreadsheet still honours is
    /// not a thing that exists.
    /// </para>
    /// </summary>
    private static string Defuse(string value) =>
        FormulaTriggers.Contains(value[0]) ? "'" + value : value;
}
