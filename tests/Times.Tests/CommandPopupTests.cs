using Spectre.Console;
using Spectre.Console.Testing;

namespace Times.Tests;

public class CommandPopupTests
{
    private static Command Cmd(string name, string description, string alias = "x") => new(name, alias, description, _ => CommandResult.None);

    private static List<Command> Many(int count) =>
        Enumerable.Range(1, count).Select(i => Cmd($"c{i:00}", $"Command {i}", $"x{(char)('a' + i)}")).ToList();

    /// <summary>Renders one markup row and returns the plain text (no ANSI) and the raw ANSI output.</summary>
    private static (string Plain, string Ansi) Render(string row, int width = 80)
    {
        TestConsole plain = new TestConsole();
        plain.Profile.Width = width;
        plain.Markup(row);

        TestConsole ansi = new TestConsole().EmitAnsiSequences();
        ansi.Profile.Width = width;
        ansi.Markup(row);

        return (plain.Output, ansi.Output);
    }

    // AC-8 (041)
    [Fact]
    public void Row_ShowsNamePaddedToLongest_ThenAliasPaddedToLongest_ThenDescription()
    {
        IReadOnlyList<string> rows = CommandPopup.Rows([Cmd("help", "List all commands", "h"), Cmd("today", "Go back to today", "td")], 80, 8);

        Assert.Equal(2, rows.Count);
        Assert.StartsWith("help   h   List all commands", Render(rows[0]).Plain);
        Assert.StartsWith("today  td  Go back to today", Render(rows[1]).Plain);
    }

    // AC-8 (041)
    [Fact]
    public void Row_NameAndAliasAreSteelBlue_DescriptionIsGrey()
    {
        string row = CommandPopup.Rows([Cmd("help", "List all commands", "h")], 80, 8)[0];

        Assert.StartsWith("[steelblue1]help[/]  [steelblue1]h[/][grey]  List all commands[/]", row);
    }

    // AC-8 (041)
    [Fact]
    public void Alias_IsEscaped()
    {
        string row = CommandPopup.Rows([Cmd("help", "d", "[b]")], 80, 8)[0];

        Assert.StartsWith("help  [b]  d", Render(row).Plain);
    }

    // AC-8 (041): name, gap, alias and description are cropped from the right, one after the other.
    [Theory]
    [InlineData(1, "c")]
    [InlineData(10, "checkpoint")]
    [InlineData(12, "checkpoint  ")]
    [InlineData(13, "checkpoint  c")]
    [InlineData(14, "checkpoint  cp")]
    [InlineData(16, "checkpoint  cp  ")]
    [InlineData(17, "checkpoint  cp  B")]
    [InlineData(20, "checkpoint  cp  Book")]
    public void NarrowRow_CropsPartsInOrder(int width, string expected)
    {
        string row = CommandPopup.Rows([Cmd("checkpoint", "Book a checkpoint", "cp")], width, 8)[0];

        string plain = Render(row, width).Plain;
        Assert.Equal(width, plain.Length);
        Assert.Equal(expected.PadRight(width), plain);
    }

    // AC-7
    [Fact]
    public void MoreMatchesThanRows_ShowsEightRows_LastIsMore()
    {
        IReadOnlyList<string> rows = CommandPopup.Rows(Many(12), 80, 20);

        Assert.Equal(CommandPopup.MaxRows, rows.Count);
        Assert.Equal(8, rows.Count);
        Assert.StartsWith("c07", Render(rows[6]).Plain);
        Assert.Equal("… 5 more", Render(rows[7]).Plain.TrimEnd());
        Assert.StartsWith("[grey]", rows[7]);
    }

    // AC-7
    [Fact]
    public void ExactlyEightMatches_ShowsAllWithoutMoreRow()
    {
        IReadOnlyList<string> rows = CommandPopup.Rows(Many(8), 80, 8);

        Assert.Equal(8, rows.Count);
        Assert.DoesNotContain(rows, r => r.Contains("more", StringComparison.Ordinal));
    }

    // AC-7
    [Fact]
    public void SmallRowBudget_IsRespected()
    {
        IReadOnlyList<string> rows = CommandPopup.Rows(Many(5), 80, 3);

        Assert.Equal(3, rows.Count);
        Assert.Equal("… 3 more", Render(rows[2]).Plain.TrimEnd());
    }

    // AC-7
    [Theory]
    [InlineData(21, 8)]
    [InlineData(9, 8)]
    [InlineData(4, 3)]
    [InlineData(2, 1)]
    public void PopupArea_NeverReachesRowOne(int bottomRow, int expected)
    {
        Assert.Equal(expected, new PopupArea(bottomRow, 80, _ => { }).MaxRows);
    }

    // AC-8
    [Fact]
    public void NoMatches_ShowsOneGreyRow()
    {
        IReadOnlyList<string> rows = CommandPopup.Rows([], 80, 8);

        Assert.Single(rows);
        (string plain, string ansi) = Render(rows[0]);
        Assert.Equal("No matching commands", plain.TrimEnd());
        Assert.Contains("\u001b[38;5;8mNo matching commands", ansi);
    }

    // AC-10
    [Fact]
    public void MarkupCharacters_AreDrawnLiterally()
    {
        string row = CommandPopup.Rows([Cmd("x[y]", "Tick [x] off [/]")], 80, 8)[0];

        Assert.StartsWith("x[y]  x  Tick [x] off [/]", Render(row).Plain);
    }

    // AC-10
    [Theory]
    [InlineData(80)]
    [InlineData(30)]
    [InlineData(14)]
    [InlineData(9)]
    [InlineData(7)]
    [InlineData(4)]
    public void EveryRow_IsExactlyTheTerminalWidth(int width)
    {
        List<Command> commands = [Cmd("help", new string('d', 200), "h"), Cmd("q", "short", "qq"), Cmd("multi", "line\nbreak", "m")];

        foreach (string row in CommandPopup.Rows(commands, width, 8))
        {
            string plain = Render(row, width).Plain;
            Assert.DoesNotContain('\n', plain);
            Assert.Equal(width, plain.Length);
        }
    }

    // AC-10
    [Fact]
    public void LongDescription_IsCroppedToOneRow()
    {
        string row = CommandPopup.Rows([Cmd("help", new string('d', 200))], 40, 8)[0];

        Assert.Equal("help  x  " + new string('d', 31), Render(row, 40).Plain);
    }

    // Job 011 ------------------------------------------------------------------------------

    // AC-7
    [Fact]
    public void Footer_IsLastRow_AfterMoreRow_WithinMaxRows()
    {
        IReadOnlyList<string> rows = CommandPopup.Rows(Many(12), 80, 20, "12 commands start with c");

        Assert.Equal(8, rows.Count);
        Assert.StartsWith("c06", Render(rows[5]).Plain);
        Assert.Equal("… 6 more", Render(rows[6]).Plain.TrimEnd());
        Assert.Equal("12 commands start with c", Render(rows[7]).Plain.TrimEnd());
    }

    // AC-7
    [Theory]
    [InlineData(3, 3)]
    [InlineData(4, 4)]
    [InlineData(5, 4)]
    [InlineData(8, 4)]
    public void Footer_TakesOneRowOfTheBudget(int maxRows, int expected)
    {
        IReadOnlyList<string> rows = CommandPopup.Rows(Many(3), 80, maxRows, "footer");

        Assert.Equal(expected, rows.Count);
        Assert.Equal("footer", Render(rows[^1]).Plain.TrimEnd());
    }

    // AC-7
    [Fact]
    public void Footer_WithOneRow_IsTheOnlyRow()
    {
        IReadOnlyList<string> rows = CommandPopup.Rows(Many(3), 80, 1, "footer");

        Assert.Single(rows);
        Assert.Equal("footer", Render(rows[0]).Plain.TrimEnd());
    }

    // AC-7
    [Theory]
    [InlineData(80)]
    [InlineData(10)]
    [InlineData(2)]
    public void Footer_IsGrey_Escaped_AndExactlyTheWidth(int width)
    {
        IReadOnlyList<string> rows = CommandPopup.Rows(Many(2), width, 8, "[x]");

        (string plain, string ansi) = Render(rows[^1], width);
        Assert.StartsWith("[grey]", rows[^1]);
        Assert.Equal(width, plain.Length);
        Assert.Equal("[x]"[..Math.Min(3, width)], plain.TrimEnd());
        Assert.Contains("\u001b[38;5;8m[", ansi);
    }

    // AC-7
    [Fact]
    public void NoFooter_RowsUnchanged()
    {
        Assert.Equal(CommandPopup.Rows(Many(12), 80, 8), CommandPopup.Rows(Many(12), 80, 8, null));
    }

    // AC-6 (040)
    [Fact]
    public void RegisteredCommands_RenderWithoutSlash()
    {
        CommandRegistry registry = CommandRegistry.CreateDefault();

        IReadOnlyList<string> rows = CommandPopup.Rows(registry.Match("/"), 80, 8);

        Assert.Equal(2, rows.Count);
        Assert.StartsWith("help  h  List all commands", Render(rows[0]).Plain);
        Assert.StartsWith("quit  q  Exit Times", Render(rows[1]).Plain);
        Assert.All(rows, r => Assert.DoesNotContain("/", Render(r).Plain));
    }
}
