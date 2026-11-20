using System.Globalization;
using System.Text;

namespace Times;

/// <summary>
/// Formats a <see cref="Timesheet"/> as one printable, self-contained HTML page: escaped text only, one inline
/// style sheet, no scripts, no links and no external resources, and a Content-Security-Policy that allows nothing
/// but the inline style.
/// </summary>
internal static class TimesheetHtml
{
    /// <summary>The policy in the page's <c>meta</c> tag: nothing may load or run, only the inline style applies.</summary>
    internal const string ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'";

    private const string Style = """
        body { font-family: -apple-system, "Segoe UI", Helvetica, Arial, sans-serif; font-size: 10pt; color: #000; margin: 2em; }
        h1 { font-size: 16pt; margin: 0 0 0.2em 0; }
        p { margin: 0.2em 0; }
        table { border-collapse: collapse; width: 100%; margin: 1em 0; }
        th, td { border: 1px solid #999; padding: 2px 6px; text-align: left; vertical-align: top; }
        th { background: #e8e8e8; }
        td.time { text-align: right; font-variant-numeric: tabular-nums; }
        tr.weekend td { background: #f3f3f3; }
        tr.unreadable td { color: #a00; }
        .sums p { font-size: 11pt; }
        .note { color: #a00; }
        footer { margin-top: 2em; color: #555; font-size: 9pt; }
        @media print {
          @page { size: A4 portrait; margin: 12mm; }
          body { margin: 0; font-size: 9pt; }
          tr { page-break-inside: avoid; }
          th, tr.weekend td { -webkit-print-color-adjust: exact; print-color-adjust: exact; }
        }
        """;

    /// <summary>The file name for a month's sheet: <c>times-timesheet-yyyy-MM.html</c>.</summary>
    public static string FileName(int year, int month) =>
        "times-timesheet-" + new DateOnly(year, month, 1).ToString("yyyy-MM", CultureInfo.InvariantCulture) + ".html";

    /// <summary>The month's English name and year, e.g. <c>October 2026</c>.</summary>
    public static string MonthName(int year, int month) =>
        new DateOnly(year, month, 1).ToString("MMMM yyyy", CultureInfo.InvariantCulture);

    /// <summary>The page for <paramref name="sheet"/>, with a footer saying it was created at <paramref name="created"/>.</summary>
    public static string Format(Timesheet sheet, DateTime created)
    {
        string month = MonthName(sheet.Year, sheet.Month);
        StringBuilder html = new StringBuilder();
        html.Append("<!DOCTYPE html>\n");
        html.Append("<html lang=\"en\">\n<head>\n");
        html.Append("<meta charset=\"utf-8\">\n");
        html.Append("<meta http-equiv=\"Content-Security-Policy\" content=\"").Append(ContentSecurityPolicy).Append("\">\n");
        html.Append("<title>Timesheet ").Append(Escape(month)).Append("</title>\n");
        html.Append("<style>\n").Append(Style).Append("\n</style>\n");
        html.Append("</head>\n<body>\n");
        html.Append("<h1>Timesheet ").Append(Escape(month)).Append("</h1>\n");
        html.Append("<p>Daily target ").Append(Escape(DayView.FormatTimeLeft(sheet.Target))).Append("</p>\n");
        html.Append("<table>\n<thead>\n<tr><th scope=\"col\">Date</th><th scope=\"col\">Arrival</th><th scope=\"col\">Leaving</th>")
            .Append("<th scope=\"col\">Breaks</th><th scope=\"col\">Work time</th><th scope=\"col\">Absence</th>")
            .Append("<th scope=\"col\">Correction</th></tr>\n</thead>\n<tbody>\n");
        foreach (TimesheetDay day in sheet.Days)
        {
            AppendRow(html, day);
        }

        html.Append("</tbody>\n</table>\n");
        html.Append("<div class=\"sums\">\n");
        html.Append("<p>Total ").Append(Escape(DayView.FormatTimeLeft(sheet.MonthWork))).Append("</p>\n");
        html.Append("<p>Month balance ").Append(Escape(DayView.FormatSigned(sheet.MonthBalance))).Append("</p>\n");
        html.Append("<p>Balance at end of month ").Append(Escape(DayView.FormatSigned(sheet.EndBalance.Total))).Append("</p>\n");
        html.Append("</div>\n");
        if (sheet.SkippedFiles > 0)
        {
            string files = sheet.SkippedFiles == 1 ? "day file" : "day files";
            html.Append("<p class=\"note\">! ").Append(sheet.SkippedFiles.ToString(CultureInfo.InvariantCulture)).Append(' ')
                .Append(files).Append(" could not be read and ").Append(sheet.SkippedFiles == 1 ? "is" : "are")
                .Append(" left out of the totals.</p>\n");
        }

        html.Append("<footer>Created ").Append(Escape(created.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)))
            .Append("</footer>\n");
        html.Append("</body>\n</html>\n");
        return html.ToString();
    }

    /// <summary>The page as UTF-8 without a byte order mark.</summary>
    public static byte[] ToBytes(string html) => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(html);

    /// <summary>
    /// <paramref name="text"/> safe for HTML element content and quoted attribute values: <c>&amp;</c>, <c>&lt;</c>,
    /// <c>&gt;</c>, <c>"</c> and <c>'</c> become character references; nothing else changes.
    /// </summary>
    internal static string Escape(string text)
    {
        StringBuilder escaped = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            escaped.Append(c switch
            {
                '&' => "&amp;",
                '<' => "&lt;",
                '>' => "&gt;",
                '"' => "&quot;",
                '\'' => "&#39;",
                _ => c.ToString(),
            });
        }

        return escaped.ToString();
    }

    /// <summary>The Absence cell's text for <paramref name="day"/>, or the empty string.</summary>
    internal static string AbsenceText(TimesheetDay day)
    {
        if (day.Unreadable)
        {
            return "Unreadable";
        }

        switch (day.Absence)
        {
            case AbsenceKind.Vacation:
                return "Vacation";
            case AbsenceKind.Sick:
                return "Sick";
            case AbsenceKind.PublicHoliday:
                return day.Holiday is { } stored ? "Public holiday · " + stored : "Public holiday";
        }

        return day.IsEmpty && day.Holiday is { } name ? "Public holiday · " + name : string.Empty;
    }

    /// <summary>The Correction cell's text: the signed sum, then the reasons joined with <c>; </c>, or the empty string.</summary>
    internal static string CorrectionText(TimesheetDay day) =>
        day.CorrectionReasons.Count == 0
            ? string.Empty
            : DayView.FormatSigned(day.CorrectionTotal) + " · " + string.Join("; ", day.CorrectionReasons);

    private static void AppendRow(StringBuilder html, TimesheetDay day)
    {
        bool weekend = day.Date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
        string classes = (weekend, day.Unreadable) switch
        {
            (true, true) => " class=\"weekend unreadable\"",
            (true, false) => " class=\"weekend\"",
            (false, true) => " class=\"unreadable\"",
            _ => string.Empty,
        };
        string date = day.Date.ToString("yyyy-MM-dd ddd", CultureInfo.InvariantCulture);
        html.Append("<tr").Append(classes).Append('>');
        Cell(html, date, null);
        Cell(html, day.Arrival?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? string.Empty, "time");
        Cell(html, day.Leaving?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? string.Empty, "time");
        Cell(html, day.BreakTime is { } pause ? DayView.FormatTimeLeft(pause) : string.Empty, "time");
        Cell(html, day.WorkTime is { } work ? DayView.FormatTimeLeft(work) : string.Empty, "time");
        Cell(html, AbsenceText(day), null);
        Cell(html, CorrectionText(day), null);
        html.Append("</tr>\n");
    }

    private static void Cell(StringBuilder html, string text, string? cssClass)
    {
        html.Append(cssClass is null ? "<td>" : "<td class=\"" + cssClass + "\">").Append(Escape(text)).Append("</td>");
    }
}
