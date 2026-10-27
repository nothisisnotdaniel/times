using Spectre.Console;
using Spectre.Console.Rendering;
using Spectre.Console.Testing;

namespace Times.Tests;

public class TopAreaTests
{
    private static Rows Numbered(int count) =>
        new Rows(Enumerable.Range(1, count).Select(i => new Text($"line {i}")));

    /// <summary>Renders <paramref name="content"/> fitted to <paramref name="height"/> and returns its lines.</summary>
    private static string[] Render(IRenderable content, int height)
    {
        TestConsole console = new TestConsole();
        console.Write(TopArea.Fit(content, height));
        return console.Output.Length == 0 ? [] : console.Output.TrimEnd('\n').Split('\n');
    }

    private static string RenderAnsi(IRenderable content, int height)
    {
        TestConsole console = new TestConsole().EmitAnsiSequences();
        console.Write(TopArea.Fit(content, height));
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
        Assert.Equal("… 7 more lines", lines[3].TrimEnd());
    }

    // AC-1
    [Fact]
    public void Cue_IsGrey()
    {
        string output = RenderAnsi(Numbered(10), 4);

        // Grey is colour 8 (ESC[90m in the standard palette or ESC[38;5;8m in 256 colours).
        Assert.Matches(@"\x1b\[(90|38;5;8)m… 7 more lines", output);
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
        Assert.Equal("… 5 more lines", lines[0].TrimEnd());
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
}
