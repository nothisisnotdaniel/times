using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Times.Tests;

public sealed class TimesheetHtmlTests : IDisposable
{
    private static readonly TimeSpan EightHours = TimeSpan.FromHours(8);

    private static readonly DateTime Created = new DateTime(2026, 10, 27, 18, 30, 0);

    private const string Hostile = "<script>alert(1)</script> & \"x\"";

    private readonly TempFolder data = new();

    public void Dispose() => data.Dispose();

    private static DayEntry At(EntryKind kind, string time) =>
        new DayEntry(kind, TimeOnly.ParseExact(time, "HH:mm", CultureInfo.InvariantCulture));

    private Timesheet October(Func<DateOnly, string?>? holiday = null)
    {
        DayStore store = new DayStore(data.Path);
        store.Save(new DateOnly(2026, 10, 1), new DayRecord(
            [At(EntryKind.Arrival, "08:00"), At(EntryKind.BreakStart, "12:00"), At(EntryKind.BreakEnd, "12:30"), At(EntryKind.Leaving, "17:00")]));
        store.Save(new DateOnly(2026, 10, 2), new DayRecord([], AbsenceKind.Vacation));
        store.Save(new DateOnly(2026, 10, 5), new DayRecord([], AbsenceKind.Sick));
        store.Save(new DateOnly(2026, 10, 6), new DayRecord([], AbsenceKind.PublicHoliday));
        store.Save(new DateOnly(2026, 10, 7), new DayRecord(
            [At(EntryKind.Arrival, "09:00"), At(EntryKind.Leaving, "15:00")], null,
            [new BalanceCorrection(-30, Hostile), new BalanceCorrection(90, "Trip")]));
        File.WriteAllText(store.PathFor(new DateOnly(2026, 10, 8)), "{not json");
        return Timesheet.Calculate(store, 2026, 10, EightHours,
            holiday ?? (day => PublicHolidays.NameOf(day, PublicHolidays.Germany, PublicHolidays.None)));
    }

    private static List<string[]> Rows(string html)
    {
        string body = html[html.IndexOf("<tbody>", StringComparison.Ordinal)..html.IndexOf("</tbody>", StringComparison.Ordinal)];
        return Regex.Matches(body, "<tr[^>]*>(.*?)</tr>")
            .Select(row => Regex.Matches(row.Groups[1].Value, "<td[^>]*>(.*?)</td>").Select(c => c.Groups[1].Value).ToArray())
            .ToList();
    }

    // AC-3
    [Fact]
    public void FileName_IsTimesTimesheetYearMonth()
    {
        Assert.Equal("times-timesheet-2026-10.html", TimesheetHtml.FileName(2026, 10));
        Assert.Equal("times-timesheet-0987-01.html", TimesheetHtml.FileName(987, 1));
        Assert.Equal("October 2026", TimesheetHtml.MonthName(2026, 10));
    }

    // AC-3
    [Theory]
    [InlineData(2028, 2, 29)]
    [InlineData(2027, 2, 28)]
    [InlineData(2026, 10, 31)]
    [InlineData(2026, 9, 30)]
    public void Format_HasOneRowPerCalendarDay(int year, int month, int count)
    {
        Timesheet sheet = Timesheet.Calculate(new DayStore(data.Path), year, month, EightHours, _ => null);

        List<string[]> rows = Rows(TimesheetHtml.Format(sheet, Created));

        Assert.Equal(count, rows.Count);
        for (int i = 0; i < count; i++)
        {
            DateOnly day = new DateOnly(year, month, i + 1);
            Assert.Equal(day.ToString("yyyy-MM-dd ddd", CultureInfo.InvariantCulture), rows[i][0]);
            Assert.Equal(["", "", "", "", "", ""], rows[i][1..]);
        }
    }

    // AC-3, AC-4, AC-6
    [Fact]
    public void Format_ShowsTheCellTexts()
    {
        List<string[]> rows = Rows(TimesheetHtml.Format(October(), Created));

        Assert.Equal(["2026-10-01 Thu", "08:00", "17:00", "0:30", "8:30", "", ""], rows[0]);
        Assert.Equal(["2026-10-02 Fri", "", "", "", "", "Vacation", ""], rows[1]);
        Assert.Equal(["2026-10-03 Sat", "", "", "", "", "Public holiday · German Unity Day", ""], rows[2]);
        Assert.Equal(["2026-10-05 Mon", "", "", "", "", "Sick", ""], rows[4]);
        Assert.Equal(["2026-10-06 Tue", "", "", "", "", "Public holiday", ""], rows[5]);
        Assert.Equal("6:00", rows[6][4]);
        Assert.Equal(
            "+1:00 · &lt;script&gt;alert(1)&lt;/script&gt; &amp; &quot;x&quot;; Trip", rows[6][6]);
        Assert.Equal(["2026-10-08 Thu", "", "", "", "", "Unreadable", ""], rows[7]);
    }

    // AC-4: a stored public holiday is named too, and a booked holiday is not shown as an absence.
    [Fact]
    public void Format_NamesAStoredPublicHoliday()
    {
        List<string[]> rows = Rows(TimesheetHtml.Format(October(day => day.Day is 6 or 1 ? "Some Day" : null), Created));

        Assert.Equal("Public holiday · Some Day", rows[5][5]);
        Assert.Equal("", rows[0][5]);
    }

    // AC-5, AC-6
    [Fact]
    public void Format_ShowsTheTotals_AndTheSkippedNote()
    {
        Timesheet sheet = October();

        string html = TimesheetHtml.Format(sheet, Created);

        // 8:30 + 6:00; +0:30 + 0 + 0 + 0 - 2:00 + 1:00.
        Assert.Contains("<p>Total 14:30</p>", html);
        Assert.Contains("<p>Month balance -0:30</p>", html);
        Assert.Contains($"<p>Balance at end of month {DayView.FormatSigned(sheet.EndBalance.Total)}</p>", html);
        Assert.Contains("! 1 day file could not be read and is left out of the totals.", html);
        Assert.Contains("<p>Daily target 8:00</p>", html);
    }

    // AC-7
    [Fact]
    public void Format_EscapesEverything_AndHasNoActiveContent()
    {
        string html = TimesheetHtml.Format(October(_ => "<b>\"Fest\" & 'Tag'</b>"), Created);

        Assert.DoesNotContain(Hostile, html);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt; &amp; &quot;x&quot;", html);
        Assert.Contains("&lt;b&gt;&quot;Fest&quot; &amp; &#39;Tag&#39;&lt;/b&gt;", html);
        Assert.DoesNotContain("<b>", html);
        foreach (string forbidden in new[] { "<script", "src=", "href=", "url(", "@import" })
        {
            Assert.DoesNotContain(forbidden, html, StringComparison.OrdinalIgnoreCase);
        }
    }

    // AC-7
    [Fact]
    public void Format_HasTheCspCharsetStyleAndFooter()
    {
        string html = TimesheetHtml.Format(October(), Created);

        Assert.StartsWith("<!DOCTYPE html>\n", html);
        Assert.Contains("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'\">", html);
        Assert.Contains("<meta charset=\"utf-8\">", html);
        Assert.Single(Regex.Matches(html, "<style"));
        string style = html[html.IndexOf("<style>", StringComparison.Ordinal)..html.IndexOf("</style>", StringComparison.Ordinal)];
        Assert.Contains("@media print", style);
        Assert.Contains("<footer>Created 2026-10-27 18:30</footer>", html);
        Assert.Contains("tr class=\"weekend\"", html);
    }

    // AC-7
    [Fact]
    public void ToBytes_IsUtf8WithoutBom()
    {
        byte[] bytes = TimesheetHtml.ToBytes("ü<");

        Assert.Equal(Encoding.UTF8.GetBytes("ü<"), bytes);
        Assert.NotEqual(0xEF, bytes[0]);
    }
}
