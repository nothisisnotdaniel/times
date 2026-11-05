using System.Text;

namespace Times.Tests;

public sealed class ReportCsvTests
{
    private static ReportRow Row(int? workItem, string name, int minutes, bool open = false) =>
        new ReportRow(workItem, [name], TimeSpan.FromMinutes(minutes), open);

    private static ProjectReport Report(params ReportRow[] rows) => new ProjectReport(rows, 0);

    // AC-2
    [Fact]
    public void ToBytes_StartsWithBom_HeaderAndCrlfAfterEveryLine()
    {
        ProjectReport report = Report(Row(99, "Call", 30), Row(1234, "Fix", 450), Row(null, "Mail", 60));
        ProjectList projects = new ProjectList([new Project(1234, "Times")]);

        byte[] bytes = ReportCsv.ToBytes(ReportCsv.Format(report, projects));

        byte[] expectedText = Encoding.UTF8.GetBytes("Work item,Title,Hours\r\n99,Call,0.50\r\n1234,Times,7.50\r\n,Mail,1.00\r\n");
        Assert.Equal([0xEF, 0xBB, 0xBF, .. expectedText], bytes);
    }

    // AC-2
    [Fact]
    public void Format_NoTotalRow_NoOpenMarker_NoHash()
    {
        string csv = ReportCsv.Format(Report(Row(7, "Plan", 0, open: true)), null);

        Assert.Equal("Work item,Title,Hours\r\n7,Plan,0.00\r\n", csv);
        Assert.DoesNotContain("Total", csv);
        Assert.DoesNotContain("open", csv);
        Assert.DoesNotContain("#", csv);
    }

    // AC-2: the title falls back to the names when the quicklist has no title (or there is none).
    [Fact]
    public void Format_FallbackTitle_IsTheNamesJoined()
    {
        ProjectReport report = Report(new ReportRow(1234, ["Review", "Fix"], TimeSpan.FromMinutes(90), false));

        Assert.Equal("Work item,Title,Hours\r\n1234,\"Review, Fix\",1.50\r\n", ReportCsv.Format(report, null));
        Assert.Equal(
            "Work item,Title,Hours\r\n1234,\"Review, Fix\",1.50\r\n",
            ReportCsv.Format(report, new ProjectList([new Project(5, "Other")])));
    }

    // AC-2: non-ASCII titles are UTF-8.
    [Fact]
    public void ToBytes_NonAscii_IsUtf8()
    {
        byte[] bytes = ReportCsv.ToBytes(ReportCsv.Format(Report(Row(null, "Bürö ❤️", 60)), null));

        Assert.Equal("\uFEFFWork item,Title,Hours\r\n,Bürö ❤️,1.00\r\n", Encoding.UTF8.GetString(bytes));
    }

    // AC-3
    [Theory]
    [InlineData("Fix \"A\", B", "\"Fix \"\"A\"\", B\"")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData(" lead", "\" lead\"")]
    [InlineData("trail ", "\"trail \"")]
    [InlineData("plain text", "plain text")]
    [InlineData("it's; fine", "it's; fine")]
    public void Format_QuotesOnlyWhenNeeded(string title, string expected)
    {
        string csv = ReportCsv.Format(Report(Row(null, title, 60)), null);

        Assert.Equal($"Work item,Title,Hours\r\n,{expected},1.00\r\n", csv);
    }

    // AC-3, AC-4: the quicklist title goes through the same rules.
    [Fact]
    public void Format_QuicklistTitle_IsQuotedAndGuarded()
    {
        ProjectList projects = new ProjectList([new Project(1, "Fix \"A\", B"), new Project(2, "=1+1")]);

        string csv = ReportCsv.Format(Report(Row(1, "x", 60), Row(2, "y", 30)), projects);

        Assert.Equal("Work item,Title,Hours\r\n1,\"Fix \"\"A\"\", B\",1.00\r\n2,'=1+1,0.50\r\n", csv);
    }

    // AC-4
    [Theory]
    [InlineData("=1+1", "'=1+1")]
    [InlineData("+49 call", "'+49 call")]
    [InlineData("-x", "'-x")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("=HYPERLINK(\"x\",\"y\")", "\"'=HYPERLINK(\"\"x\"\",\"\"y\"\")\"")]
    [InlineData("a=b", "a=b")]
    public void Format_FormulaTitles_GetALeadingApostrophe_BeforeQuoting(string title, string expected)
    {
        string csv = ReportCsv.Format(Report(Row(null, title, 60)), null);

        Assert.Equal($"Work item,Title,Hours\r\n,{expected},1.00\r\n", csv);
    }

    // AC-1
    [Fact]
    public void FileName_IsBuiltFromTheTwoDates()
    {
        Assert.Equal(
            "times-report-2026-11-02-to-2026-11-04.csv",
            ReportCsv.FileName(new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 4)));
    }
}
