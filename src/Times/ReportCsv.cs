using System.Globalization;
using System.Text;

namespace Times;

/// <summary>
/// The project report as CSV (RFC 4180): a <c>Work item,Title,Hours</c> header, then one line per
/// report row, CRLF after every line, no total. Pure: it never touches a file.
/// </summary>
internal static class ReportCsv
{
    /// <summary>The first line of every export.</summary>
    public const string Header = "Work item,Title,Hours";

    private const string LineEnd = "\r\n";

    /// <summary>UTF-8 with a byte order mark, so a spreadsheet reads non-ASCII titles correctly.</summary>
    private static readonly UTF8Encoding Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    /// <summary>
    /// The CSV text for <paramref name="report"/>: the header, then the rows in report order with the
    /// work item as a plain number (empty for a name row), the title (<see cref="ReportRow.Title"/>,
    /// guarded against formulas) and the hours as <see cref="App.FormatHours"/> writes them.
    /// </summary>
    public static string Format(ProjectReport report, ProjectList? projects)
    {
        StringBuilder csv = new StringBuilder();
        csv.Append(Header).Append(LineEnd);
        foreach (ReportRow row in report.Rows)
        {
            string workItem = row.WorkItem is { } w ? w.ToString(CultureInfo.InvariantCulture) : string.Empty;
            csv.Append(Field(workItem))
                .Append(',')
                .Append(Field(GuardFormula(row.Title(projects))))
                .Append(',')
                .Append(Field(App.FormatHours(row.Total)))
                .Append(LineEnd);
        }

        return csv.ToString();
    }

    /// <summary><paramref name="csv"/> as the bytes of the file: the UTF-8 byte order mark, then the text.</summary>
    public static byte[] ToBytes(string csv)
    {
        byte[] preamble = Encoding.GetPreamble();
        byte[] text = Encoding.GetBytes(csv);
        byte[] bytes = new byte[preamble.Length + text.Length];
        preamble.CopyTo(bytes, 0);
        text.CopyTo(bytes, preamble.Length);
        return bytes;
    }

    /// <summary>The export's file name, e.g. <c>times-report-2026-11-02-to-2026-11-04.csv</c>. Built only from the two dates.</summary>
    public static string FileName(DateOnly from, DateOnly to) =>
        "times-report-" + from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        + "-to-" + to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".csv";

    /// <summary>
    /// <paramref name="value"/> in double quotes, each <c>"</c> doubled, if it holds a <c>,</c>, a
    /// <c>"</c>, a line break or a leading or trailing space; otherwise unchanged.
    /// </summary>
    internal static string Field(string value)
    {
        bool quote = value.AsSpan().IndexOfAny(",\"\r\n") >= 0
            || (value.Length > 0 && (value[0] == ' ' || value[^1] == ' '));
        return quote ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;
    }

    /// <summary>
    /// <paramref name="title"/> with a leading <c>'</c> if it starts with <c>=</c>, <c>+</c>, <c>-</c> or
    /// <c>@</c> (or Tab or CR, which title validation refuses today), so a spreadsheet shows it as text
    /// instead of running it as a formula.
    /// </summary>
    internal static string GuardFormula(string title) =>
        title.Length > 0 && title[0] is '=' or '+' or '-' or '@' or '\t' or '\r' ? "'" + title : title;
}
