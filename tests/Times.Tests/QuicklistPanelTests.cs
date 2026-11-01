using System.Globalization;
using Spectre.Console;
using Spectre.Console.Rendering;
using Spectre.Console.Testing;

namespace Times.Tests;

public sealed class QuicklistPanelTests
{
    private const int Width = 80;

    private static ProjectList Projects(int count) =>
        new ProjectList(Enumerable.Range(1, count).Select(i => new Project(1000 + i, $"Project {i}")));

    /// <summary>Renders <paramref name="panel"/> at <see cref="Width"/> columns and returns its lines.</summary>
    private static string[] Render(IRenderable panel)
    {
        TestConsole console = new TestConsole();
        console.Profile.Width = Width;
        console.Write(panel);
        return console.Output.TrimEnd('\n').Split('\n');
    }

    private static string RenderAnsi(IRenderable panel)
    {
        TestConsole console = new TestConsole().EmitAnsiSequences();
        console.Profile.Width = Width;
        console.Write(panel);
        return console.Output;
    }

    /// <summary>The text inside the panel's side borders, trimmed at the end.</summary>
    private static string Inner(string line)
    {
        Assert.StartsWith("│ ", line);
        Assert.EndsWith(" │", line);
        return line[2..^2].TrimEnd();
    }

    // AC-1
    [Fact]
    public void Panel_HasRoundedBorder_ProjectsHeader_AndFullWidth()
    {
        string[] lines = Render(QuicklistPanel.Render(Projects(2)));

        Assert.StartsWith("╭─Projects─", lines[0]);
        Assert.EndsWith("╮", lines[0]);
        Assert.StartsWith("╰", lines[^1]);
        Assert.EndsWith("╯", lines[^1]);
        Assert.All(lines, l => Assert.Equal(Width, l.Length));
        Assert.Equal(4, lines.Length);
    }

    // AC-1
    [Fact]
    public void Panel_BorderAndHeaderAreGrey()
    {
        string output = RenderAnsi(QuicklistPanel.Render(Projects(1)));

        // The header takes the border's grey: no other colour starts before "Projects".
        Assert.Matches(@"\x1b\[(90|38;5;8)m╭─Projects", output);
    }

    // AC-1
    [Fact]
    public void Panel_InsideFixedHeightOptions_TakesOnlyItsOwnRows()
    {
        TestConsole console = new TestConsole();
        console.Profile.Width = Width;
        RenderOptions options = RenderOptions.Create(console, console.Profile.Capabilities) with { Height = 20 };

        List<SegmentLine> lines = Segment.SplitLines(QuicklistPanel.Render(Projects(2)).Render(options, Width));

        Assert.Equal(4, lines.Count);
    }

    // AC-2
    [Fact]
    public void Rows_ShowNumberWorkItemAndTitle_InQuicklistOrder()
    {
        ProjectList list = new ProjectList([new Project(99, "Zeta"), new Project(1, "Alpha"), new Project(1234567, "Website")]);

        string[] lines = Render(QuicklistPanel.Render(list));

        Assert.Equal(["1 #99 Zeta", "2 #1 Alpha", "3 #1234567 Website"], lines[1..^1].Select(Inner));
    }

    // AC-2
    [Fact]
    public void Number_IsSteelBlue()
    {
        string output = RenderAnsi(QuicklistPanel.Render(new ProjectList([new Project(42, "Zeta")])));

        // SteelBlue1 is colour 75 in the 256-colour palette.
        Assert.Matches(@"\x1b\[38;5;75m1\x1b\[0m #42 Zeta", output);
    }

    // AC-2
    [Fact]
    public void Numbers_UseInvariantCulture()
    {
        CultureInfo before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");

            string[] lines = Render(QuicklistPanel.Render(new ProjectList([new Project(1234567, "Big")])));

            Assert.Equal("1 #1234567 Big", Inner(lines[1]));
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    // AC-3
    [Fact]
    public void LongTitle_IsCutToOneLine_EndingInEllipsis()
    {
        string title = string.Concat(Enumerable.Repeat("word ", 39)) + "words"; // 200 characters, with spaces
        Assert.Equal(200, title.Length);
        ProjectList list = new ProjectList([new Project(1, title), new Project(2, "Next")]);

        string[] lines = Render(QuicklistPanel.Render(list));

        Assert.Equal(4, lines.Length);
        Assert.Equal(Width, lines[1].Length);
        Assert.EndsWith("… │", lines[1]);
        Assert.StartsWith("│ 1 #1 word word", lines[1]);
        Assert.Equal("2 #2 Next", Inner(lines[2]));
    }

    // AC-3
    [Fact]
    public void LongTitleWithoutSpaces_IsCutToOneLine_EndingInEllipsis()
    {
        ProjectList list = new ProjectList([new Project(1, new string('x', 200))]);

        string[] lines = Render(QuicklistPanel.Render(list));

        Assert.Equal(3, lines.Length);
        Assert.Equal(Width, lines[1].Length);
        Assert.EndsWith("x… │", lines[1]);
    }

    // AC-3
    [Fact]
    public void TitleThatFitsExactly_IsNotCut()
    {
        // Inside width 80: 2 borders and 2 padding leave 76 columns; "1 #1 " takes 5.
        string title = new string('y', 71);

        string[] lines = Render(QuicklistPanel.Render(new ProjectList([new Project(1, title)])));

        Assert.Equal("1 #1 " + title, Inner(lines[1]));
    }

    // AC-3
    [Fact]
    public void LongWideCharacterTitle_IsCutToTheWidth()
    {
        ProjectList list = new ProjectList([new Project(1, new string('漢', 100))]);

        string[] lines = Render(QuicklistPanel.Render(list));

        Assert.Equal(3, lines.Length);
        Assert.EndsWith("… │", lines[1]);
        Assert.InRange(new Segment(lines[1]).CellCount(), Width - 1, Width);
    }

    // AC-4
    [Theory]
    [InlineData("[red]x[/]")]
    [InlineData("[bold]x")]
    [InlineData("a]b[c")]
    public void MarkupTitle_IsShownLiterally(string title)
    {
        string[] lines = Render(QuicklistPanel.Render(new ProjectList([new Project(7, title)])));

        Assert.Equal("1 #7 " + title, Inner(lines[1]));
    }

    // AC-5
    [Fact]
    public void EmptyList_ShowsOneGreyLine()
    {
        string[] lines = Render(QuicklistPanel.Render(ProjectList.Empty));

        Assert.Equal(3, lines.Length);
        Assert.Equal("No projects yet. Add one with addproject <work item> <title>.", Inner(lines[1]));
        Assert.Matches(@"\x1b\[(90|38;5;8)mNo projects yet\.", RenderAnsi(QuicklistPanel.Render(ProjectList.Empty)));
    }

    // AC-6
    [Fact]
    public void FiveProjects_AreAllShown()
    {
        Assert.Equal(5, QuicklistPanel.MaxRows);

        string[] lines = Render(QuicklistPanel.Render(Projects(5)));

        Assert.Equal(7, lines.Length);
        Assert.Equal(
            ["1 #1001 Project 1", "2 #1002 Project 2", "3 #1003 Project 3", "4 #1004 Project 4", "5 #1005 Project 5"],
            lines[1..^1].Select(Inner));
        Assert.DoesNotContain(lines, l => l.Contains("more", StringComparison.Ordinal));
    }

    // AC-6
    [Theory]
    [InlineData(6, 2)]
    [InlineData(7, 3)]
    [InlineData(30, 26)]
    public void SixOrMoreProjects_ShowFirstFour_ThenMoreLine(int count, int more)
    {
        string[] lines = Render(QuicklistPanel.Render(Projects(count)));

        Assert.Equal(7, lines.Length);
        Assert.Equal(
            ["1 #1001 Project 1", "2 #1002 Project 2", "3 #1003 Project 3", "4 #1004 Project 4"],
            lines[1..5].Select(Inner));
        Assert.Equal($"… {more} more. Type projects to see all.", Inner(lines[5]));
    }

    // AC-6
    [Fact]
    public void MoreLine_IsGrey()
    {
        string output = RenderAnsi(QuicklistPanel.Render(Projects(6)));

        Assert.Matches(@"\x1b\[(90|38;5;8)m… 2 more\. Type projects to see all\.", output);
    }

    // AC-8, AC-1 (059)
    [Fact]
    public void Unreadable_ShowsRedReason_ThenGreyNextStep()
    {
        IRenderable panel = QuicklistPanel.RenderUnreadable(Markup.Escape("projects.json is not [valid]."), Markup.Escape("Fix or delete [it]."));

        string[] lines = Render(panel);

        Assert.Equal(4, lines.Length);
        Assert.StartsWith("╭─Projects─", lines[0]);
        Assert.Equal("projects.json is not [valid].", Inner(lines[1]));
        Assert.Equal("Fix or delete [it].", Inner(lines[2]));
        Assert.StartsWith("╰", lines[3]);
        string output = RenderAnsi(panel);
        Assert.Matches(@"\x1b\[(91|38;5;9)mprojects\.json is not \[valid\]\.", output);
        Assert.Matches(@"\x1b\[(90|38;5;8)mFix or delete \[it\]\.", output);
    }

    // AC-8, AC-1 (059)
    [Fact]
    public void Unreadable_LongLines_EachStayOneLine()
    {
        string longText = string.Concat(Enumerable.Repeat("reason ", 40));
        IRenderable panel = QuicklistPanel.RenderUnreadable(Markup.Escape(longText), Markup.Escape(longText));

        string[] lines = Render(panel);

        Assert.Equal(4, lines.Length);
        Assert.EndsWith("… │", lines[1]);
        Assert.EndsWith("… │", lines[2]);
    }
}
