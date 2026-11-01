using Spectre.Console;
using Spectre.Console.Rendering;
using Spectre.Console.Testing;

namespace Times.Tests;

public class TopAreaTests
{
    private static Rows Numbered(int count) =>
        new Rows(Enumerable.Range(1, count).Select(i => new Text($"line {i}")));

    /// <summary>Renders <paramref name="content"/> fitted to <paramref name="height"/> and returns its lines.</summary>
    private static string[] Render(IRenderable content, int height, int offset = 0, int width = 80)
    {
        TestConsole console = new TestConsole();
        console.Profile.Width = width;
        console.Write(TopArea.Fit(content, height, offset));
        return console.Output.Length == 0 ? [] : console.Output.TrimEnd('\n').Split('\n');
    }

    private static string RenderAnsi(IRenderable content, int height, int offset = 0)
    {
        TestConsole console = new TestConsole().EmitAnsiSequences();
        console.Write(TopArea.Fit(content, height, offset));
        return console.Output;
    }

    // AC-1
    [Fact]
    public void Overflow_KeepsHeightMinusOneLines_ThenCue()
    {
        string[] lines = Render(Numbered(10), 4);

        Assert.Equal(4, lines.Length);
        Assert.Equal("line 1", lines[0].TrimEnd());
        Assert.Equal("line 2", lines[1].TrimEnd());
        Assert.Equal("line 3", lines[2].TrimEnd());
        Assert.Equal("↓ 7 more lines below · PgDn / Ctrl+↓", lines[3].TrimEnd());
    }

    // AC-1
    [Fact]
    public void Cue_IsGrey()
    {
        string output = RenderAnsi(Numbered(10), 4);

        // Grey is colour 8 (ESC[90m in the standard palette or ESC[38;5;8m in 256 colours).
        Assert.Matches(@"\x1b\[(90|38;5;8)m↓ 7 more lines below · PgDn / Ctrl\+↓", output);
    }

    // AC-2
    [Theory]
    [InlineData(4, 4)]
    [InlineData(3, 4)]
    [InlineData(1, 1)]
    public void FittingContent_IsUnchanged_WithoutCue(int count, int height)
    {
        TestConsole plain = new TestConsole();
        plain.Write(Numbered(count));

        string[] lines = Render(Numbered(count), height);

        Assert.Equal(plain.Output.TrimEnd('\n').Split('\n'), lines);
        Assert.DoesNotContain(lines, l => l.Contains("more", StringComparison.Ordinal));
    }

    // AC-2
    [Fact]
    public void FittingContent_KeepsItsStyles()
    {
        Markup content = new Markup("[steelblue1]blue[/]\n[grey]grey[/]");
        TestConsole plain = new TestConsole().EmitAnsiSequences();
        plain.Write(content);

        Assert.Equal(plain.Output, RenderAnsi(content, 2));
    }

    // AC-3
    [Fact]
    public void HeightOne_ShowsOnlyTheCue_CountingAllLines()
    {
        string[] lines = Render(Numbered(5), 1);

        Assert.Single(lines);
        Assert.Equal("↓ 5 more lines below · PgDn / Ctrl+↓", lines[0].TrimEnd());
    }

    // AC-3
    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void HeightZeroOrLess_RendersNothing(int height)
    {
        Assert.Empty(Render(Numbered(5), height));
        Assert.Empty(Render(Text.Empty, height));
    }

    // Scrollable output (job 037) ----------------------------------------------------------

    private static string[] Trimmed(string[] lines) => lines.Select(l => l.TrimEnd()).ToArray();

    // AC-1 (037)
    [Theory]
    [InlineData(4, 4, 2)]
    [InlineData(3, 5, 7)]
    [InlineData(5, 10, -1)]
    public void FittingContent_IgnoresTheOffset(int count, int height, int offset)
    {
        Assert.Equal(Render(Numbered(count), height), Render(Numbered(count), height, offset));
        Assert.DoesNotContain(Render(Numbered(count), height, offset), l => l.Contains("more", StringComparison.Ordinal));
    }

    // AC-2 (037)
    [Fact]
    public void OffsetZero_ShowsFirstLines_ThenBelowCue()
    {
        Assert.Equal(
            ["line 1", "line 2", "line 3", "line 4", "↓ 6 more lines below · PgDn / Ctrl+↓"],
            Trimmed(Render(Numbered(10), 5, 0)));
    }

    // AC-3 (037)
    [Fact]
    public void MiddleOffset_ShowsAboveCue_Lines_AndBelowCue()
    {
        Assert.Equal(
            ["↑ 3 more lines above · PgUp / Ctrl+↑", "line 4", "line 5", "line 6", "↓ 4 more lines below · PgDn / Ctrl+↓"],
            Trimmed(Render(Numbered(10), 5, 3)));
    }

    // AC-3 (037)
    [Fact]
    public void BothCues_AreGrey()
    {
        string output = RenderAnsi(Numbered(10), 5, 3);

        Assert.Matches(@"\x1b\[(90|38;5;8)m↑ 3 more lines above · PgUp / Ctrl\+↑", output);
        Assert.Matches(@"\x1b\[(90|38;5;8)m↓ 4 more lines below · PgDn / Ctrl\+↓", output);
    }

    // AC-4 (037)
    [Fact]
    public void MaxOffset_ShowsAboveCue_AndTheLastLines_WithoutBelowCue()
    {
        Assert.Equal(6, TopArea.MaxOffset(10, 5));
        Assert.Equal(
            ["↑ 6 more lines above · PgUp / Ctrl+↑", "line 7", "line 8", "line 9", "line 10"],
            Trimmed(Render(Numbered(10), 5, 6)));
    }

    // AC-4 (037)
    [Theory]
    [InlineData(7, 6)]
    [InlineData(100, 6)]
    [InlineData(-1, 0)]
    [InlineData(-50, 0)]
    public void OffsetsOutsideTheRange_AreClamped(int offset, int clamped)
    {
        Assert.Equal(Render(Numbered(10), 5, clamped), Render(Numbered(10), 5, offset));
    }

    // AC-4 (037)
    [Theory]
    [InlineData(10, 5, 6)]
    [InlineData(5, 5, 0)]
    [InlineData(4, 5, 0)]
    [InlineData(10, 3, 8)]
    [InlineData(10, 2, 0)]
    [InlineData(10, 1, 0)]
    [InlineData(10, 0, 0)]
    public void MaxOffset_FollowsTheRule(int lineCount, int height, int expected)
    {
        Assert.Equal(expected, TopArea.MaxOffset(lineCount, height));
    }

    // AC-6 (037)
    [Theory]
    [InlineData(5, 3)]
    [InlineData(3, 1)]
    [InlineData(2, 1)]
    [InlineData(1, 1)]
    public void PageSize_IsTheRowsBetweenTheCues(int height, int expected)
    {
        Assert.Equal(expected, TopArea.PageSize(height));
    }

    // AC-4 (037)
    [Fact]
    public void HeightTwo_ShowsFirstLine_AndBelowCue_WhateverTheOffset()
    {
        string[] expected = ["line 1", "↓ 9 more lines below · PgDn / Ctrl+↓"];
        Assert.Equal(expected, Trimmed(Render(Numbered(10), 2, 0)));
        Assert.Equal(expected, Trimmed(Render(Numbered(10), 2, 4)));
    }

    // AC-4 (037)
    [Fact]
    public void HeightOne_IgnoresTheOffset()
    {
        Assert.Equal(["↓ 5 more lines below · PgDn / Ctrl+↓"], Trimmed(Render(Numbered(5), 1, 3)));
    }

    // AC-4 (037)
    [Fact]
    public void HeightThree_MiddleOffset_ShowsOneLineBetweenTheCues()
    {
        Assert.Equal(
            ["↑ 1 more line above · PgUp / Ctrl+↑", "line 2", "↓ 3 more lines below · PgDn / Ctrl+↓"],
            Trimmed(Render(Numbered(5), 3, 1)));
    }

    // AC-4 (037)
    [Fact]
    public void Cues_AreCroppedToTheWidth()
    {
        string[] lines = Render(Numbered(10), 5, 3, width: 10);

        Assert.Equal("↑ 3 more l", lines[0]);
        Assert.Equal("↓ 4 more l", lines[^1]);
    }

    // AC-4 (037)
    [Fact]
    public void HeightZero_WithOffset_RendersNothing()
    {
        Assert.Empty(Render(Numbered(5), 0, 2));
    }

    // Scroll cue wording (job 062) ---------------------------------------------------------

    // AC-2, AC-3 (062)
    [Theory]
    [InlineData(1, true, "↑ 1 more line above · PgUp / Ctrl+↑")]
    [InlineData(1, false, "↓ 1 more line below · PgDn / Ctrl+↓")]
    [InlineData(2, true, "↑ 2 more lines above · PgUp / Ctrl+↑")]
    [InlineData(2, false, "↓ 2 more lines below · PgDn / Ctrl+↓")]
    [InlineData(12, true, "↑ 12 more lines above · PgUp / Ctrl+↑")]
    [InlineData(12, false, "↓ 12 more lines below · PgDn / Ctrl+↓")]
    public void CueText_ReadsSingularForOne_AndNamesCtrlArrow(int count, bool above, string expected)
    {
        Assert.Equal(expected, TopArea.CueText(count, above));
    }

    // AC-1, AC-3 (062)
    [Fact]
    public void Render_TakesBothCuesFromCueText()
    {
        Assert.Equal(
            [TopArea.CueText(1, above: true), "line 2", TopArea.CueText(3, above: false)],
            Trimmed(Render(Numbered(5), 3, 1)));
        Assert.Equal(
            [TopArea.CueText(3, above: true), "line 4", "line 5", "line 6", TopArea.CueText(4, above: false)],
            Trimmed(Render(Numbered(10), 5, 3)));
    }

    // AC-4 (062)
    [Fact]
    public void NewCues_AreCroppedToAWidthNarrowerThanTheirText()
    {
        const int Width = 30;
        string above = TopArea.CueText(3, above: true);
        string below = TopArea.CueText(4, above: false);
        Assert.True(above.Length > Width);
        Assert.True(below.Length > Width);

        string[] lines = Render(Numbered(10), 5, 3, width: Width);

        Assert.Equal(above[..Width], lines[0]);
        Assert.Equal(below[..Width], lines[^1]);
        Assert.All(lines, l => Assert.True(l.Length <= Width, $"row wider than {Width}: '{l}'"));
    }
}
