using Spectre.Console;
using Spectre.Console.Testing;

namespace Times.Tests;

public class LineEditorTests
{
    private static (TestConsole Console, LineEditor Editor) Create()
    {
        TestConsole console = new TestConsole().Interactive();
        return (console, new LineEditor(console, CommandRegistry.CreateDefault()));
    }

    private static string? Read(LineEditor editor) => editor.ReadLine(5, 23, 70);

    [Fact]
    public void PrintableCharacters_AndEnter_ReturnTheLine()
    {
        (TestConsole console, LineEditor editor) = Create();
        console.Input.PushTextWithEnter("help me");

        Assert.Equal("help me", Read(editor));
    }

    [Fact]
    public void TypedText_IsEchoed()
    {
        (TestConsole console, LineEditor editor) = Create();
        console.Input.PushTextWithEnter("hel");

        Read(editor);

        Assert.Contains("hel", console.Output);
    }

    // AC-10
    [Fact]
    public void Backspace_RemovesLastCharacter()
    {
        (TestConsole console, LineEditor editor) = Create();
        console.Input.PushText("quiz");
        console.Input.PushKey(ConsoleKey.Backspace);
        console.Input.PushTextWithEnter("t");

        Assert.Equal("quit", Read(editor));
    }

    [Fact]
    public void Backspace_OnEmptyLine_DoesNothing()
    {
        (TestConsole console, LineEditor editor) = Create();
        console.Input.PushKey(ConsoleKey.Backspace);
        console.Input.PushTextWithEnter("q");

        Assert.Equal("q", Read(editor));
    }

    [Fact]
    public void Escape_ClearsTheLine()
    {
        (TestConsole console, LineEditor editor) = Create();
        console.Input.PushText("abc");
        console.Input.PushKey(ConsoleKey.Escape);
        console.Input.PushTextWithEnter("quit");

        Assert.Equal("quit", Read(editor));
    }

    // AC-11
    [Fact]
    public void Tab_CompletesUniquePrefix()
    {
        (TestConsole console, LineEditor editor) = Create();
        console.Input.PushText("q");
        console.Input.PushKey(ConsoleKey.Tab);
        console.Input.PushKey(ConsoleKey.Enter);

        Assert.Equal("quit", Read(editor));
    }

    [Theory]
    [InlineData("x")]  // no match
    [InlineData("/")]   // matches help and quit
    public void Tab_LeavesNoOrAmbiguousMatchUnchanged(string prefix)
    {
        (TestConsole console, LineEditor editor) = Create();
        console.Input.PushText(prefix);
        console.Input.PushKey(ConsoleKey.Tab);
        console.Input.PushKey(ConsoleKey.Enter);

        Assert.Equal(prefix, Read(editor));
    }

    [Fact]
    public void Tab_AfterArguments_DoesNothing()
    {
        (TestConsole console, LineEditor editor) = Create();
        console.Input.PushText("q x");
        console.Input.PushKey(ConsoleKey.Tab);
        console.Input.PushKey(ConsoleKey.Enter);

        Assert.Equal("q x", Read(editor));
    }

    // AC-12
    [Fact]
    public void Up_RecallsPreviousLine()
    {
        (TestConsole console, LineEditor editor) = Create();
        console.Input.PushTextWithEnter("help");
        console.Input.PushKey(ConsoleKey.UpArrow);
        console.Input.PushKey(ConsoleKey.Enter);

        Assert.Equal("help", Read(editor));
        Assert.Equal("help", Read(editor));
    }

    [Fact]
    public void Up_PastOldest_StaysOnOldest()
    {
        (TestConsole console, LineEditor editor) = Create();
        console.Input.PushTextWithEnter("one");
        console.Input.PushTextWithEnter("two");
        console.Input.PushKey(ConsoleKey.UpArrow);
        console.Input.PushKey(ConsoleKey.UpArrow);
        console.Input.PushKey(ConsoleKey.UpArrow);
        console.Input.PushKey(ConsoleKey.UpArrow);
        console.Input.PushKey(ConsoleKey.Enter);

        Read(editor);
        Read(editor);
        Assert.Equal("one", Read(editor));
    }

    [Fact]
    public void Down_PastNewest_GivesEmptyLine()
    {
        (TestConsole console, LineEditor editor) = Create();
        console.Input.PushTextWithEnter("one");
        console.Input.PushTextWithEnter("two");
        console.Input.PushKey(ConsoleKey.UpArrow);
        console.Input.PushKey(ConsoleKey.UpArrow);
        console.Input.PushKey(ConsoleKey.DownArrow);
        console.Input.PushKey(ConsoleKey.DownArrow);
        console.Input.PushKey(ConsoleKey.DownArrow);
        console.Input.PushKey(ConsoleKey.Enter);

        Read(editor);
        Read(editor);
        Assert.Equal("", Read(editor));
    }

    [Fact]
    public void Down_MovesToNewerEntry()
    {
        (TestConsole console, LineEditor editor) = Create();
        console.Input.PushTextWithEnter("one");
        console.Input.PushTextWithEnter("two");
        console.Input.PushKey(ConsoleKey.UpArrow);
        console.Input.PushKey(ConsoleKey.UpArrow);
        console.Input.PushKey(ConsoleKey.DownArrow);
        console.Input.PushKey(ConsoleKey.Enter);

        Read(editor);
        Read(editor);
        Assert.Equal("two", Read(editor));
    }

    [Fact]
    public void Up_WithNoHistory_LeavesLineEmpty()
    {
        (TestConsole console, LineEditor editor) = Create();
        console.Input.PushKey(ConsoleKey.UpArrow);
        console.Input.PushKey(ConsoleKey.Enter);

        Assert.Equal("", Read(editor));
    }

    [Fact]
    public void EmptyLines_AreNotAddedToHistory()
    {
        (TestConsole console, LineEditor editor) = Create();
        console.Input.PushTextWithEnter("help");
        console.Input.PushTextWithEnter("");
        console.Input.PushKey(ConsoleKey.UpArrow);
        console.Input.PushKey(ConsoleKey.Enter);

        Read(editor);
        Read(editor);
        Assert.Equal("help", Read(editor));
    }

    // Command popup (job 006) --------------------------------------------------------------

    private const int HintRow = 21;

    private sealed class PopupRun
    {
        public PopupRun(int bottomRow = HintRow, params string[] extra)
        {
            Console = new TestConsole().Interactive().EmitAnsiSequences();
            CommandRegistry registry = CommandRegistry.CreateDefault();
            registry.Register("prev", "p", "Go to the previous day", _ => CommandResult.None);
            registry.Register("next", "n", "Go to the next day", _ => CommandResult.None);
            registry.Register("today", "t", "Go back to today", _ => CommandResult.None);
            int aliasIndex = 0;
            foreach (string name in extra)
            {
                registry.Register(name, $"j{(char)('a' + aliasIndex++)}", $"Dummy {name}", _ => CommandResult.None);
            }

            Editor = new LineEditor(Console, registry);
            Area = new PopupArea(bottomRow, 80, row =>
            {
                Restored.Add(row);
                Console.Cursor.SetPosition(1, row);
                Console.Write(new Spectre.Console.Text($"restored {row}"));
            });
        }

        public TestConsole Console { get; }

        public LineEditor Editor { get; }

        public PopupArea Area { get; }

        public List<int> Restored { get; } = [];

        public string? Read() => Editor.ReadLine(5, 23, 70, Area);

        /// <summary>The text last written at each row, like a screen.</summary>
        public Dictionary<int, string> Screen()
        {
            Dictionary<int, string> screen = [];
            foreach (AnsiWrite write in AnsiWrites.Parse(Console.Output).Where(w => w.Column == 1))
            {
                screen[write.Row] = write.Text;
            }

            return screen;
        }

        /// <summary>
        /// The text at each row (column 1) after the editor handled its <paramref name="key"/>-th key
        /// (1-based): every write before that key's input-line redraw.
        /// </summary>
        public Dictionary<int, string> ScreenAfterKey(int key)
        {
            Dictionary<int, string> screen = [];
            int redraws = 0;
            foreach (AnsiWrite write in AnsiWrites.Parse(Console.Output))
            {
                if (write.Row == 23 && write.Column == 5)
                {
                    redraws++;
                    if (redraws == key)
                    {
                        break;
                    }
                }
                else if (write.Column == 1)
                {
                    screen[write.Row] = write.Text.TrimEnd();
                }
            }

            return screen;
        }

        public int PopupDraws() => AnsiWrites.AtRow(Console.Output, HintRow).Count(t => !t.StartsWith("restored", StringComparison.Ordinal));
    }

    // AC-3
    [Fact]
    public void Slash_OpensPopup_EndingOnHintRow_ListingEveryCommand()
    {
        PopupRun run = new PopupRun();
        run.Console.Input.PushText("/");
        run.Console.Input.PushKey(ConsoleKey.Enter);

        run.Read();

        string output = run.Console.Output;
        Assert.Contains("\u001b[21;1H", output);
        List<AnsiWrite> rows = AnsiWrites.Parse(output).Where(w => w.Column == 1 && !w.Text.StartsWith("restored", StringComparison.Ordinal)).ToList();
        Assert.Equal([17, 18, 19, 20, 21], rows.Select(r => r.Row));
        Assert.StartsWith("help   h  List all commands", rows[0].Text);
        Assert.StartsWith("next   n  Go to the next day", rows[1].Text);
        Assert.StartsWith("prev   p  Go to the previous day", rows[2].Text);
        Assert.StartsWith("quit   q  Exit Times", rows[3].Text);
        Assert.StartsWith("today  t  Go back to today", rows[4].Text);
    }

    [Fact]
    public void NoPopupArea_DrawsNoPopup()
    {
        TestConsole console = new TestConsole().Interactive().EmitAnsiSequences();
        LineEditor editor = new LineEditor(console, CommandRegistry.CreateDefault());
        console.Input.PushTextWithEnter("he");

        Assert.Equal("he", editor.ReadLine(5, 23, 70));
        Assert.DoesNotContain("List all commands", console.Output);
    }

    [Fact]
    public void LineStartingWithSpace_OpensNoPopup()
    {
        PopupRun run = new PopupRun();
        run.Console.Input.PushTextWithEnter(" help");

        run.Read();

        Assert.Equal(0, run.PopupDraws());
        Assert.DoesNotContain("List all commands", run.Console.Output);
    }

    // AC-4
    [Fact]
    public void EachKey_RedrawsPopup_WithCurrentMatch()
    {
        PopupRun run = new PopupRun();
        run.Console.Input.PushText("tdoay");

        run.Console.Input.PushKey(ConsoleKey.Enter);
        run.Read();

        Assert.Equal(5, run.PopupDraws());
        Assert.StartsWith("today", AnsiWrites.AtRow(run.Console.Output, HintRow).Last(t => !t.StartsWith("restored", StringComparison.Ordinal)));
    }

    // AC-4
    [Fact]
    public void ShrinkingPopup_RestoresRowsItNoLongerUses()
    {
        PopupRun run = new PopupRun();
        run.Console.Input.PushText("/t");
        run.Console.Input.PushKey(ConsoleKey.Escape);
        run.Console.Input.PushKey(ConsoleKey.Enter);

        run.Read();

        // "/" fills rows 17-21, "t" only row 21: rows 17-20 come back first, row 21 on Esc.
        Assert.Equal([17, 18, 19, 20, 21], run.Restored);
        Dictionary<int, string> screen = run.Screen();
        Assert.Equal("restored 17", screen[17]);
        Assert.Equal("restored 21", screen[21]);
    }

    // AC-4
    [Fact]
    public void Backspace_RedrawsPopup()
    {
        PopupRun run = new PopupRun();
        run.Console.Input.PushText("/t");
        run.Console.Input.PushKey(ConsoleKey.Backspace);
        run.Console.Input.PushKey(ConsoleKey.Escape);
        run.Console.Input.PushKey(ConsoleKey.Enter);

        run.Read();

        // After Backspace the popup lists all five commands again, starting on row 17.
        Assert.Equal(3, run.PopupDraws());
        Assert.Equal(2, AnsiWrites.AtRow(run.Console.Output, 17).Count(t => t.StartsWith("help", StringComparison.Ordinal)));
    }

    // AC-4
    [Fact]
    public void TabCompletion_RedrawsPopup()
    {
        PopupRun run = new PopupRun();
        run.Console.Input.PushText("q");
        run.Console.Input.PushKey(ConsoleKey.Tab);
        run.Console.Input.PushKey(ConsoleKey.Enter);

        Assert.Equal("quit", run.Read());
        Assert.Equal(2, run.PopupDraws());
        Assert.Contains(AnsiWrites.Parse(run.Console.Output), w => w.Row == 23 && w.Text.StartsWith("quit", StringComparison.Ordinal));
    }

    // AC-4
    [Fact]
    public void CursorReturnsToEndOfInputLine_AfterEveryPopupRedraw()
    {
        PopupRun run = new PopupRun();
        run.Console.Input.PushText("he");
        run.Console.Input.PushKey(ConsoleKey.Enter);

        run.Read();

        List<AnsiWrite> writes = AnsiWrites.Parse(run.Console.Output);
        Assert.Equal(new AnsiWrite(23, 7, string.Empty), writes[^1]);
        // Every popup draw on row 21 is followed, eventually, by a cursor move back to row 23.
        for (int i = 0; i < writes.Count; i++)
        {
            if (writes[i].Row == HintRow)
            {
                Assert.Contains(writes.Skip(i + 1), w => w.Row == 23);
            }
        }
    }

    // AC-5
    [Fact]
    public void BackspaceOnSlash_ClosesPopup_AndRestoresEveryRow()
    {
        PopupRun run = new PopupRun();
        run.Console.Input.PushText("/");
        run.Console.Input.PushKey(ConsoleKey.Backspace);
        run.Console.Input.PushKey(ConsoleKey.Enter);

        Assert.Equal("", run.Read());
        Assert.Equal([17, 18, 19, 20, 21], run.Restored);
        Dictionary<int, string> screen = run.Screen();
        for (int row = 17; row <= 21; row++)
        {
            Assert.Equal($"restored {row}", screen[row]);
        }
    }

    // AC-5
    [Fact]
    public void Enter_ClosesPopup_AndReturnsTypedLine()
    {
        PopupRun run = new PopupRun();
        run.Console.Input.PushTextWithEnter("/tdoay x");

        Assert.Equal("/tdoay x", run.Read());
        // "/" covered rows 17-21; "t" shrank it to row 21, and Enter gives row 21 back.
        Assert.Equal([17, 18, 19, 20, 21], run.Restored);
        Dictionary<int, string> screen = run.Screen();
        for (int row = 17; row <= 21; row++)
        {
            Assert.Equal($"restored {row}", screen[row]);
        }
    }

    // AC-5
    [Fact]
    public void Enter_WithoutPopup_RestoresNothing()
    {
        PopupRun run = new PopupRun();
        run.Console.Input.PushTextWithEnter(" abc");

        Assert.Equal(" abc", run.Read());
        Assert.Empty(run.Restored);
    }

    // AC-6
    [Fact]
    public void Escape_WithPopupOpen_ClosesIt_AndKeepsText()
    {
        PopupRun run = new PopupRun();
        run.Console.Input.PushText("he");
        run.Console.Input.PushKey(ConsoleKey.Escape);
        run.Console.Input.PushKey(ConsoleKey.Enter);

        Assert.Equal("he", run.Read());
        // "he" matches help and next (rows 20-21); Esc gives both rows back.
        Assert.Equal([20, 21], run.Restored.TakeLast(2));
        Dictionary<int, string> screen = run.Screen();
        for (int row = 20; row <= 21; row++)
        {
            Assert.Equal($"restored {row}", screen[row]);
        }
    }

    // AC-6
    [Fact]
    public void SecondEscape_ClearsTheLine()
    {
        PopupRun run = new PopupRun();
        run.Console.Input.PushText("he");
        run.Console.Input.PushKey(ConsoleKey.Escape);
        run.Console.Input.PushKey(ConsoleKey.Escape);
        run.Console.Input.PushKey(ConsoleKey.Enter);

        Assert.Equal("", run.Read());
    }

    // AC-6
    [Fact]
    public void AfterEscape_PopupStaysClosed_UntilLineIsEmpty()
    {
        PopupRun run = new PopupRun();
        run.Console.Input.PushText("he");
        run.Console.Input.PushKey(ConsoleKey.Escape);
        run.Console.Input.PushText("l");
        run.Console.Input.PushKey(ConsoleKey.Backspace);
        run.Console.Input.PushKey(ConsoleKey.Backspace);
        run.Console.Input.PushKey(ConsoleKey.Backspace);
        run.Console.Input.PushKey(ConsoleKey.Backspace);
        run.Console.Input.PushText("t");
        run.Console.Input.PushKey(ConsoleKey.Enter);

        Assert.Equal("t", run.Read());
        // "h", "he" drew the popup; "l" and the Backspaces down to "h" did not; the new "t" did.
        Assert.Equal(3, run.PopupDraws());
        Assert.StartsWith("today", AnsiWrites.AtRow(run.Console.Output, HintRow).Last(t => !t.StartsWith("restored", StringComparison.Ordinal)));
    }

    // AC-6
    [Fact]
    public void AfterSecondEscape_TypingSlash_OpensPopupAgain()
    {
        PopupRun run = new PopupRun();
        run.Console.Input.PushText("he");
        run.Console.Input.PushKey(ConsoleKey.Escape);
        run.Console.Input.PushKey(ConsoleKey.Escape);
        run.Console.Input.PushText("/");
        run.Console.Input.PushKey(ConsoleKey.Enter);

        Assert.Equal("/", run.Read());
        Assert.Equal(3, run.PopupDraws());
    }

    // AC-7
    [Fact]
    public void SmallTerminal_PopupNeverCoversRowOne()
    {
        PopupRun run = new PopupRun(bottomRow: 3);
        run.Console.Input.PushText("/");
        run.Console.Input.PushKey(ConsoleKey.Enter);

        run.Read();

        List<AnsiWrite> rows = AnsiWrites.Parse(run.Console.Output).Where(w => w.Column == 1).ToList();
        Assert.DoesNotContain(rows, w => w.Row < 2);
        Assert.Contains(rows, w => w.Row == 2 && w.Text.StartsWith("help", StringComparison.Ordinal));
        Assert.Contains(rows, w => w.Row == 3 && w.Text.StartsWith("… 4 more", StringComparison.Ordinal));
    }

    // AC-9
    [Fact]
    public void Typing_NeverClearsTheScreen()
    {
        PopupRun run = new PopupRun();
        run.Console.Input.PushText("tdoay");
        run.Console.Input.PushKey(ConsoleKey.Backspace);
        run.Console.Input.PushKey(ConsoleKey.Escape);
        run.Console.Input.PushKey(ConsoleKey.Escape);
        run.Console.Input.PushKey(ConsoleKey.Enter);

        run.Read();

        Assert.DoesNotContain("\u001b[2J", run.Console.Output);
    }

    // Tab feedback (job 011) ---------------------------------------------------------------

    private static readonly string[] Prefixed = ["print", "project", "ps"];

    private static PopupRun PrefixRun() => new(HintRow, Prefixed);

    // AC-3
    [Theory]
    [InlineData("p", "p")]     // prev, print, project, ps share only "p"
    [InlineData("pr", "pr")]   // prev, print, project share "pr": not extended further
    [InlineData("PRO", "project")]
    public void Tab_ExtendsToCommonPrefix(string typed, string expected)
    {
        PopupRun run = PrefixRun();
        run.Console.Input.PushText(typed);
        run.Console.Input.PushKey(ConsoleKey.Tab);
        run.Console.Input.PushKey(ConsoleKey.Enter);

        Assert.Equal(expected, run.Read());
    }

    // AC-3
    [Fact]
    public void Tab_ExtendsShortPrefix_ToCommonPrefix()
    {
        PopupRun run = new PopupRun(HintRow, "print", "project");
        run.Console.Input.PushText("p");
        run.Console.Input.PushKey(ConsoleKey.Tab);
        run.Console.Input.PushKey(ConsoleKey.Enter);

        Assert.Equal("pr", run.Read());
        Assert.Contains(AnsiWrites.Parse(run.Console.Output), w => w.Row == 23 && w.Text.StartsWith("pr", StringComparison.Ordinal));
    }

    // AC-3
    [Fact]
    public void Tab_WithoutPopupArea_OnlyExtendsTheLine()
    {
        TestConsole console = new TestConsole().Interactive().EmitAnsiSequences();
        CommandRegistry registry = CommandRegistry.CreateDefault();
        registry.Register("prev", "p", "d", _ => CommandResult.None);
        registry.Register("print", "pi", "d", _ => CommandResult.None);
        LineEditor editor = new LineEditor(console, registry);
        console.Input.PushText("p");
        console.Input.PushKey(ConsoleKey.Tab);
        console.Input.PushKey(ConsoleKey.Enter);

        Assert.Equal("pr", editor.ReadLine(5, 23, 70));
        Assert.DoesNotContain("commands start with", console.Output);
        Assert.DoesNotContain("prev", console.Output);
    }

    // AC-4
    [Fact]
    public void AmbiguousTab_ListsOnlyPrefixMatches_WithFooterOnHintRow()
    {
        PopupRun run = PrefixRun();
        run.Console.Input.PushText("pr");
        run.Console.Input.PushKey(ConsoleKey.Tab);
        run.Console.Input.PushKey(ConsoleKey.Enter);

        Assert.Equal("pr", run.Read());

        // Before Tab the fuzzy list includes ps (one edit from "pr").
        Dictionary<int, string> before = run.ScreenAfterKey(2);
        Assert.Contains(before.Values, t => t.StartsWith("ps ", StringComparison.Ordinal));

        Dictionary<int, string> screen = run.ScreenAfterKey(3);
        Assert.Equal("3 commands start with pr", screen[HintRow]);
        Assert.StartsWith("prev", screen[18]);
        Assert.StartsWith("print", screen[19]);
        Assert.StartsWith("project", screen[20]);
        // The popup never reached row 17; the Tab list does not cover it either.
        Assert.False(screen.ContainsKey(17));
        Assert.DoesNotContain(screen.Values, t => t.StartsWith("ps ", StringComparison.Ordinal));
        Assert.Contains("\u001b[38;5;8m3 commands start with pr", run.Console.Output);
    }

    // AC-5
    [Fact]
    public void AmbiguousTab_AfterEscape_ReopensPopup()
    {
        PopupRun run = PrefixRun();
        run.Console.Input.PushText("pr");
        run.Console.Input.PushKey(ConsoleKey.Escape);
        run.Console.Input.PushKey(ConsoleKey.Tab);
        run.Console.Input.PushKey(ConsoleKey.Enter);

        Assert.Equal("pr", run.Read());

        Assert.Equal("restored 21", run.ScreenAfterKey(3)[HintRow]);
        Dictionary<int, string> screen = run.ScreenAfterKey(4);
        Assert.Equal("3 commands start with pr", screen[HintRow]);
        Assert.StartsWith("prev", screen[18]);
        Assert.StartsWith("print", screen[19]);
        Assert.StartsWith("project", screen[20]);
    }

    // AC-6
    [Fact]
    public void AfterTabList_Backspace_GoesBackToFuzzyMatch()
    {
        PopupRun run = PrefixRun();
        run.Console.Input.PushText("pr");
        run.Console.Input.PushKey(ConsoleKey.Tab);
        run.Console.Input.PushKey(ConsoleKey.Backspace);
        run.Console.Input.PushKey(ConsoleKey.Enter);

        Assert.Equal("p", run.Read());

        // "p" fuzzy: prev, print, project, ps on rows 18-21, no footer.
        Dictionary<int, string> screen = run.ScreenAfterKey(4);
        Assert.StartsWith("prev", screen[18]);
        Assert.StartsWith("ps", screen[HintRow]);
        Assert.DoesNotContain(screen.Values, t => t.Contains("commands start with", StringComparison.Ordinal));
    }

    // AC-6
    [Fact]
    public void AfterTabList_TypingCharacter_GoesBackToFuzzyMatch()
    {
        PopupRun run = PrefixRun();
        run.Console.Input.PushText("pr");
        run.Console.Input.PushKey(ConsoleKey.Tab);
        run.Console.Input.PushText("o");
        run.Console.Input.PushKey(ConsoleKey.Enter);

        Assert.Equal("pro", run.Read());

        // "pro" fuzzy: project (0), then prev and print (1 edit) on rows 19-21; row 18 (footer list top) restored.
        Dictionary<int, string> screen = run.ScreenAfterKey(4);
        Assert.StartsWith("project", screen[19]);
        Assert.StartsWith("prev", screen[20]);
        Assert.StartsWith("print", screen[HintRow]);
        Assert.Equal("restored 18", screen[18]);
        Assert.DoesNotContain(screen.Values, t => t.Contains("commands start with", StringComparison.Ordinal));
    }

    // AC-7
    [Fact]
    public void TabList_WithMoreMatchesThanFit_ShowsMoreRowAndFooter()
    {
        PopupRun run = new PopupRun(bottomRow: 5, "print", "project", "ps");
        run.Console.Input.PushText("p");
        run.Console.Input.PushKey(ConsoleKey.Tab);
        run.Console.Input.PushKey(ConsoleKey.Enter);

        run.Read();

        // Rows 2-5 hold 4 rows: two matches, "… 2 more", then the footer.
        Dictionary<int, string> screen = run.ScreenAfterKey(2);
        Assert.StartsWith("prev", screen[2]);
        Assert.StartsWith("print", screen[3]);
        Assert.Equal("… 2 more", screen[4]);
        Assert.Equal("4 commands start with p", screen[5]);
        Assert.DoesNotContain(AnsiWrites.Parse(run.Console.Output), w => w.Row < 2);
    }

    // AC-8
    [Fact]
    public void Tab_WithNoPrefixMatch_LeavesLineAndPopup()
    {
        PopupRun run = PrefixRun();
        run.Console.Input.PushText("x");
        run.Console.Input.PushKey(ConsoleKey.Tab);
        run.Console.Input.PushKey(ConsoleKey.Enter);

        Assert.Equal("x", run.Read());
        Assert.Equal(run.ScreenAfterKey(1), run.ScreenAfterKey(2));
        Assert.Equal("No matching commands", run.ScreenAfterKey(2)[HintRow]);
    }

    // AC-8
    [Fact]
    public void Tab_WithNoPrefixMatch_AfterEscape_KeepsPopupClosed()
    {
        PopupRun run = PrefixRun();
        run.Console.Input.PushText("x");
        run.Console.Input.PushKey(ConsoleKey.Escape);
        run.Console.Input.PushKey(ConsoleKey.Tab);
        run.Console.Input.PushKey(ConsoleKey.Enter);

        Assert.Equal("x", run.Read());
        Assert.Equal("restored 21", run.ScreenAfterKey(3)[HintRow]);
    }

    // AC-8
    [Fact]
    public void Tab_AfterWhitespace_LeavesLineAndPopup()
    {
        PopupRun run = PrefixRun();
        run.Console.Input.PushText("p x");
        run.Console.Input.PushKey(ConsoleKey.Tab);
        run.Console.Input.PushKey(ConsoleKey.Enter);

        Assert.Equal("p x", run.Read());
        Assert.Equal(run.ScreenAfterKey(3), run.ScreenAfterKey(4));
        Assert.DoesNotContain("commands start with", run.Console.Output);
    }

    // AC-8
    [Fact]
    public void Tab_UniquePrefix_StillCompletes_WithExtraCommands()
    {
        PopupRun run = PrefixRun();
        run.Console.Input.PushText("q");
        run.Console.Input.PushKey(ConsoleKey.Tab);
        run.Console.Input.PushKey(ConsoleKey.Enter);

        Assert.Equal("quit", run.Read());
    }

    // AC-8
    [Fact]
    public void AmbiguousTab_NeverClearsScreen_AndCursorEndsOnInputLine()
    {
        PopupRun run = PrefixRun();
        run.Console.Input.PushText("pr");
        run.Console.Input.PushKey(ConsoleKey.Tab);
        run.Console.Input.PushKey(ConsoleKey.Enter);

        run.Read();

        Assert.DoesNotContain("\u001b[2J", run.Console.Output);
        List<AnsiWrite> writes = AnsiWrites.Parse(run.Console.Output);
        Assert.Equal(new AnsiWrite(23, 7, string.Empty), writes[^1]);
        // After the Tab-list draw, the cursor goes back to the end of "pr".
        int footer = writes.FindIndex(w => w.Text.StartsWith("3 commands", StringComparison.Ordinal));
        Assert.Contains(writes.Skip(footer + 1), w => w == new AnsiWrite(23, 7, string.Empty));
    }

    // Input placeholder (job 014) ----------------------------------------------------------

    private const string Placeholder = "Type help for commands";

    private const string Grey = @"\x1b\[(90|38;5;8)m";

    private static (TestConsole Console, LineEditor Editor) CreateAnsi()
    {
        TestConsole console = new TestConsole().Interactive().EmitAnsiSequences();
        return (console, new LineEditor(console, CommandRegistry.CreateDefault()));
    }

    private static List<AnsiWrite> InputRow(TestConsole console) =>
        AnsiWrites.Parse(console.Output).Where(w => w.Row == 23).ToList();

    // AC-3
    [Fact]
    public void Placeholder_FirstCharacter_IsWrittenAtColumn_AndBlanksThePlaceholder()
    {
        (TestConsole console, LineEditor editor) = CreateAnsi();
        console.Input.PushText("x");
        console.Input.PushKey(ConsoleKey.Enter);

        Assert.Equal("x", editor.ReadLine(5, 23, 70, null, Placeholder));

        AnsiWrite first = InputRow(console).First(w => w.Text.Length > 0);
        Assert.Equal(5, first.Column);
        Assert.StartsWith("x", first.Text);
        Assert.True(first.Text.Length >= Placeholder.Length);
        Assert.Equal(string.Empty, first.Text.Trim()[1..]);
        Assert.DoesNotContain("Type help", first.Text);
    }

    // AC-3
    [Fact]
    public void Placeholder_IsNeverPartOfTheReturnedLine()
    {
        (TestConsole console, LineEditor editor) = CreateAnsi();
        console.Input.PushKey(ConsoleKey.Enter);
        console.Input.PushTextWithEnter("he");

        Assert.Equal(string.Empty, editor.ReadLine(5, 23, 70, null, Placeholder));
        Assert.Equal("he", editor.ReadLine(5, 23, 70, null, Placeholder));
    }

    // AC-4
    [Fact]
    public void Placeholder_ReturnsAfterBackspace_InGrey_WithCursorAtColumn()
    {
        (TestConsole console, LineEditor editor) = CreateAnsi();
        console.Input.PushText("/");
        console.Input.PushKey(ConsoleKey.Backspace);
        console.Input.PushKey(ConsoleKey.Enter);

        editor.ReadLine(5, 23, 70, null, Placeholder);

        List<AnsiWrite> row = InputRow(console);
        AnsiWrite last = row.Last(w => w.Text.Length > 0);
        Assert.Equal(5, last.Column);
        Assert.Equal(Placeholder, last.Text);
        Assert.Matches(Grey + Placeholder, console.Output);
        Assert.Equal(new AnsiWrite(23, 5, string.Empty), AnsiWrites.Parse(console.Output)[^1]);
        Assert.EndsWith("\u001b[23;5H", console.Output);
    }

    // AC-4
    [Fact]
    public void Placeholder_ReturnsAfterEscape_InGrey_WithCursorAtColumn()
    {
        (TestConsole console, LineEditor editor) = CreateAnsi();
        console.Input.PushText("x");
        console.Input.PushKey(ConsoleKey.Escape);
        console.Input.PushKey(ConsoleKey.Enter);

        Assert.Equal(string.Empty, editor.ReadLine(5, 23, 70, null, Placeholder));

        AnsiWrite last = InputRow(console).Last(w => w.Text.Length > 0);
        Assert.Equal(new AnsiWrite(23, 5, Placeholder), last);
        Assert.Matches(Grey + Placeholder, console.Output);
        Assert.EndsWith("\u001b[23;5H", console.Output);
    }

    // AC-4
    [Fact]
    public void Placeholder_WithPopup_SlashThenBackspace_ShowsPlaceholderAtColumn()
    {
        PopupRun run = new PopupRun();
        run.Console.Input.PushText("/");
        run.Console.Input.PushKey(ConsoleKey.Backspace);
        run.Console.Input.PushKey(ConsoleKey.Enter);

        Assert.Equal(string.Empty, run.Editor.ReadLine(5, 23, 70, run.Area, Placeholder));

        Assert.Equal(new AnsiWrite(23, 5, Placeholder), AnsiWrites.Parse(run.Console.Output).Last(w => w.Row == 23 && w.Text.Length > 0));
        Assert.EndsWith("\u001b[23;5H", run.Console.Output);
    }

    // AC-5
    [Fact]
    public void Placeholder_UpShowsHistoryWithoutLeftovers_DownShowsPlaceholderAgain()
    {
        (TestConsole second, LineEditor next) = CreateAnsi();
        second.Input.PushTextWithEnter("help");
        next.ReadLine(5, 23, 70, null, Placeholder);
        int before = AnsiWrites.Parse(second.Output).Count;

        second.Input.PushKey(ConsoleKey.UpArrow);
        second.Input.PushKey(ConsoleKey.DownArrow);
        second.Input.PushKey(ConsoleKey.Enter);
        Assert.Equal(string.Empty, next.ReadLine(5, 23, 70, null, Placeholder));

        List<AnsiWrite> writes = AnsiWrites.Parse(second.Output).Skip(before).Where(w => w.Row == 23 && w.Text.Length > 0).ToList();
        Assert.Equal(2, writes.Count);
        // Up: the history entry, padded over the whole placeholder, with no placeholder text left.
        Assert.StartsWith("help", writes[0].Text);
        Assert.True(writes[0].Text.Length >= Placeholder.Length);
        Assert.Equal("help", writes[0].Text.TrimEnd());
        // Down to the empty line: the placeholder again, cursor at the column.
        Assert.Equal(new AnsiWrite(23, 5, Placeholder), writes[1]);
        Assert.EndsWith("\u001b[23;5H", second.Output);
    }

    // AC-6
    [Fact]
    public void Placeholder_IsCroppedToTheRoom()
    {
        (TestConsole console, LineEditor editor) = CreateAnsi();
        console.Input.PushText("ab");
        console.Input.PushKey(ConsoleKey.Escape);
        console.Input.PushKey(ConsoleKey.Enter);

        editor.ReadLine(5, 23, 10, null, Placeholder);

        List<AnsiWrite> row = InputRow(console).Where(w => w.Text.Length > 0).ToList();
        // Room is width - 1 = 9. Typing blanks only those 9 cells, and the placeholder is cropped to them.
        Assert.All(row, w => Assert.True(w.Column + w.Text.Length - 1 <= 5 + 9 - 1));
        Assert.Equal("Type help", row[^1].Text);
        Assert.Equal("a" + new string(' ', 8), row[0].Text);
        Assert.DoesNotContain("Type help ", console.Output);
    }

    // AC-7
    [Fact]
    public void Placeholder_NeverClearsTheScreen_AndPopupStillOpens()
    {
        PopupRun run = new PopupRun();
        run.Console.Input.PushText("/");
        run.Console.Input.PushKey(ConsoleKey.Backspace);
        run.Console.Input.PushText("x");
        run.Console.Input.PushKey(ConsoleKey.Escape);
        run.Console.Input.PushKey(ConsoleKey.Enter);

        run.Editor.ReadLine(5, 23, 70, run.Area, Placeholder);

        Assert.DoesNotContain("\u001b[2J", run.Console.Output);
        Assert.Contains(AnsiWrites.Parse(run.Console.Output), w => w.Row == HintRow && w.Text.StartsWith("today", StringComparison.Ordinal));
    }

    // AC-7
    [Fact]
    public void NoPlaceholder_EmptyLineStaysBlank()
    {
        (TestConsole console, LineEditor editor) = CreateAnsi();
        console.Input.PushText("x");
        console.Input.PushKey(ConsoleKey.Backspace);
        console.Input.PushKey(ConsoleKey.Enter);

        editor.ReadLine(5, 23, 70);

        Assert.DoesNotContain("Type help", console.Output);
        Assert.Equal(new AnsiWrite(23, 5, " "), AnsiWrites.Parse(console.Output).Last(w => w.Row == 23 && w.Text.Length > 0));
    }

    // Long input cue (job 016) ------------------------------------------------------------

    private const string Ellipsis = "\u2026";

    // Every non-empty write on the input row (row 23).
    private static List<AnsiWrite> InputWrites(TestConsole console) =>
        InputRow(console).Where(w => w.Text.Length > 0).ToList();

    private static void AssertInsideRoom(IEnumerable<AnsiWrite> writes, int column, int room) =>
        Assert.All(writes, w => Assert.True(w.Column + w.Text.Length - 1 <= column + room - 1, $"'{w.Text}' at {w.Column} goes past the room"));

    // AC-1
    [Fact]
    public void LongInput_LineThatFits_IsShownInFull_WithoutCue()
    {
        (TestConsole console, LineEditor editor) = CreateAnsi();
        console.Input.PushTextWithEnter("abcdefghij");

        Assert.Equal("abcdefghij", editor.ReadLine(5, 23, 11));

        Assert.DoesNotContain(Ellipsis, console.Output);
        Assert.Equal(new AnsiWrite(23, 5, "abcdefghij"), InputWrites(console)[^1]);
        Assert.EndsWith("\u001b[23;15H", console.Output);
    }

    // AC-2
    [Fact]
    public void LongInput_EleventhCharacter_ShowsCueAndLastNine()
    {
        (TestConsole console, LineEditor editor) = CreateAnsi();
        console.Input.PushText("abcdefghijk");
        console.Input.PushKey(ConsoleKey.Enter);

        editor.ReadLine(5, 23, 11);

        List<AnsiWrite> writes = InputWrites(console);
        Assert.Equal(new AnsiWrite(23, 5, Ellipsis + "cdefghijk"), writes[^1]);
        AssertInsideRoom(writes, 5, 10);
        Assert.Equal("\u001b[23;15H", System.Text.RegularExpressions.Regex.Matches(console.Output, @"\u001b\[\d+;\d+H")[^1].Value);
        Assert.EndsWith("\u001b[23;15H", console.Output);
    }

    // AC-2
    [Fact]
    public void LongInput_EveryLaterCharacter_KeepsCueAndTail()
    {
        (TestConsole console, LineEditor editor) = CreateAnsi();
        console.Input.PushText("abcdefghijklmnop");
        console.Input.PushKey(ConsoleKey.Enter);

        editor.ReadLine(5, 23, 11);

        List<AnsiWrite> writes = InputWrites(console);
        Assert.Equal(16, writes.Count);
        for (int typed = 11; typed <= 16; typed++)
        {
            string expected = Ellipsis + "abcdefghijklmnop"[(typed - 9)..typed];
            Assert.Equal(new AnsiWrite(23, 5, expected), writes[typed - 1]);
        }

        for (int typed = 1; typed <= 10; typed++)
        {
            Assert.DoesNotContain(Ellipsis, writes[typed - 1].Text);
        }

        AssertInsideRoom(writes, 5, 10);
        Assert.EndsWith("\u001b[23;15H", console.Output);
    }

    // AC-3
    [Fact]
    public void LongInput_Cue_IsGrey_AndTailIsNot()
    {
        (TestConsole console, LineEditor editor) = CreateAnsi();
        console.Input.PushText("abcdefghijk");
        console.Input.PushKey(ConsoleKey.Enter);

        editor.ReadLine(5, 23, 11);

        string output = console.Output;
        Assert.Matches(Grey + Ellipsis + @"\x1b\[0m", output);
        int tail = output.LastIndexOf("cdefghijk", StringComparison.Ordinal);
        string beforeTail = output[..tail];
        Assert.DoesNotMatch(Grey + "$", beforeTail);
        Assert.EndsWith("\u001b[0m", beforeTail);
    }

    // AC-4
    [Fact]
    public void LongInput_BackspaceToRoom_RemovesCue()
    {
        (TestConsole console, LineEditor editor) = CreateAnsi();
        console.Input.PushText("abcdefghijk");
        console.Input.PushKey(ConsoleKey.Backspace);
        console.Input.PushKey(ConsoleKey.Enter);

        Assert.Equal("abcdefghij", editor.ReadLine(5, 23, 11));

        Assert.Equal(new AnsiWrite(23, 5, "abcdefghij"), InputWrites(console)[^1]);
        Assert.EndsWith("\u001b[23;15H", console.Output);
    }

    // AC-5
    [Fact]
    public void LongInput_Enter_ReturnsFullLine_AndAddsItToHistoryInFull()
    {
        (TestConsole console, LineEditor editor) = CreateAnsi();
        console.Input.PushTextWithEnter("checkin something long");

        Assert.Equal("checkin something long", editor.ReadLine(5, 23, 11));
        Assert.Equal(["checkin something long"], editor.History);
    }

    // AC-5
    [Fact]
    public void LongInput_Enter_WithPopup_ReturnsFullLine_CursorAtRoomEnd()
    {
        PopupRun run = new PopupRun();
        run.Console.Input.PushTextWithEnter("checkin something long");

        Assert.Equal("checkin something long", run.Editor.ReadLine(5, 23, 11, run.Area));
        Assert.Equal(["checkin something long"], run.Editor.History);
        Assert.EndsWith("\u001b[23;15H", run.Console.Output);
    }

    // AC-6
    [Fact]
    public void LongInput_UpToLongHistoryEntry_ShowsCueAndTail()
    {
        (TestConsole console, LineEditor editor) = CreateAnsi();
        console.Input.PushTextWithEnter("abcdefghijklmn");
        editor.ReadLine(5, 23, 11, null, Placeholder);
        int before = AnsiWrites.Parse(console.Output).Count;

        console.Input.PushKey(ConsoleKey.UpArrow);
        console.Input.PushKey(ConsoleKey.Enter);
        Assert.Equal("abcdefghijklmn", editor.ReadLine(5, 23, 11, null, Placeholder));

        List<AnsiWrite> writes = AnsiWrites.Parse(console.Output).Skip(before).Where(w => w.Row == 23 && w.Text.Length > 0).ToList();
        Assert.Equal([new AnsiWrite(23, 5, Ellipsis + "fghijklmn")], writes);
        Assert.EndsWith("\u001b[23;15H", console.Output);
    }

    // AC-6
    [Fact]
    public void LongInput_EscapeOnOverflowingLine_BringsPlaceholderBack_WithoutLeftovers()
    {
        (TestConsole console, LineEditor editor) = CreateAnsi();
        console.Input.PushText("abcdefghijklmn");
        console.Input.PushKey(ConsoleKey.Escape);
        console.Input.PushKey(ConsoleKey.Enter);

        Assert.Equal(string.Empty, editor.ReadLine(5, 23, 10, null, Placeholder));

        // The cropped placeholder fills all 9 cells, so nothing of the cue or tail can remain.
        AnsiWrite last = InputWrites(console)[^1];
        Assert.Equal(new AnsiWrite(23, 5, "Type help"), last);
        Assert.DoesNotContain(Ellipsis, last.Text);
        Assert.Matches(Grey + "Type help", console.Output);
        Assert.EndsWith("\u001b[23;5H", console.Output);
    }

    // AC-6
    [Fact]
    public void LongInput_EscapeOnOverflowingLine_WithoutPlaceholder_BlanksTheRoom()
    {
        (TestConsole console, LineEditor editor) = CreateAnsi();
        console.Input.PushText("abcdefghijklmn");
        console.Input.PushKey(ConsoleKey.Escape);
        console.Input.PushKey(ConsoleKey.Enter);

        Assert.Equal(string.Empty, editor.ReadLine(5, 23, 11));

        Assert.Equal(new AnsiWrite(23, 5, new string(' ', 10)), InputWrites(console)[^1]);
    }

    // AC-7
    [Fact]
    public void LongInput_CommandLine_WithPopup_PopupRedrawnPerKey_CursorAtRoomEnd_NoClear()
    {
        PopupRun run = new PopupRun();
        run.Console.Input.PushText("checkin_x_y_z");
        run.Console.Input.PushKey(ConsoleKey.Enter);

        run.Editor.ReadLine(5, 23, 11, run.Area);

        string output = run.Console.Output;
        Assert.DoesNotContain("\u001b[2J", output);
        Assert.Equal(13, run.PopupDraws());
        List<AnsiWrite> writes = AnsiWrites.Parse(output).Where(w => w.Row == 23 && w.Text.Length > 0).ToList();
        Assert.Equal(new AnsiWrite(23, 5, Ellipsis + "kin_x_y_z"), writes[^1]);
        AssertInsideRoom(writes, 5, 10);
        Assert.EndsWith("\u001b[23;15H", output);
    }

    // AC-8
    [Fact]
    public void LongInput_RoomBelowTwo_NeverShowsCue_AndShowsLastCharacter()
    {
        (TestConsole console, LineEditor editor) = CreateAnsi();
        console.Input.PushText("abc");
        console.Input.PushKey(ConsoleKey.Enter);

        Assert.Equal("abc", editor.ReadLine(5, 23, 2));

        Assert.DoesNotContain(Ellipsis, console.Output);
        Assert.Equal(new AnsiWrite(23, 5, "c"), InputWrites(console)[^1]);
        Assert.EndsWith("\u001b[23;6H", console.Output);
    }

    // Scrollable output (job 037) ----------------------------------------------------------

    public static TheoryData<ConsoleKey, bool, bool, int> ScrollKeys => new()
    {
        { ConsoleKey.PageDown, false, false, 1 },
        { ConsoleKey.PageDown, true, false, 1 },
        { ConsoleKey.DownArrow, false, true, 1 },
        { ConsoleKey.PageUp, false, false, -1 },
        { ConsoleKey.PageUp, true, false, -1 },
        { ConsoleKey.UpArrow, false, true, -1 },
    };

    private static ConsoleKeyInfo Key(ConsoleKey key, bool shift = false, bool control = false) =>
        new ConsoleKeyInfo('\0', key, shift, false, control);

    // AC-5 (037)
    [Theory]
    [MemberData(nameof(ScrollKeys))]
    public void ScrollKeys_CallTheCallback_AndKeepTheLine(ConsoleKey key, bool shift, bool control, int direction)
    {
        (TestConsole console, LineEditor editor) = Create();
        List<int> calls = [];
        console.Input.PushTextWithEnter("help");
        console.Input.PushText("to");
        console.Input.PushKey(Key(key, shift, control));
        console.Input.PushTextWithEnter("day");

        Assert.Equal("help", editor.ReadLine(5, 23, 70, scroll: (d, _) => { calls.Add(d); return true; }));
        Assert.Equal("today", editor.ReadLine(5, 23, 70, scroll: (d, _) => { calls.Add(d); return true; }));

        Assert.Equal([direction], calls);
        Assert.Equal(["help", "today"], editor.History);
    }

    // AC-5 (037)
    [Theory]
    [MemberData(nameof(ScrollKeys))]
    public void ScrollKeys_DoNotMoveTheHistoryPosition(ConsoleKey key, bool shift, bool control, int direction)
    {
        (TestConsole console, LineEditor editor) = Create();
        List<int> calls = [];
        console.Input.PushTextWithEnter("prev");
        console.Input.PushTextWithEnter("next");
        console.Input.PushKey(ConsoleKey.UpArrow);
        console.Input.PushKey(Key(key, shift, control));
        console.Input.PushKey(Key(key, shift, control));
        console.Input.PushKey(ConsoleKey.UpArrow);
        console.Input.PushKey(ConsoleKey.Enter);

        Func<int, ScrollStep, bool> scroll = (d, _) => { calls.Add(d); return false; };
        editor.ReadLine(5, 23, 70, scroll: scroll);
        editor.ReadLine(5, 23, 70, scroll: scroll);

        // Up, two scroll keys, Up: two steps back through history, as if the scroll keys were not there.
        Assert.Equal("prev", editor.ReadLine(5, 23, 70, scroll: scroll));
        Assert.Equal([direction, direction], calls);
    }

    // AC-5 (037)
    [Fact]
    public void PlainUpAndDown_StayHistory_AndNeverScroll()
    {
        (TestConsole console, LineEditor editor) = Create();
        List<int> calls = [];
        console.Input.PushTextWithEnter("help");
        console.Input.PushKey(ConsoleKey.UpArrow);
        console.Input.PushKey(ConsoleKey.DownArrow);
        console.Input.PushKey(ConsoleKey.UpArrow);
        console.Input.PushKey(ConsoleKey.Enter);

        Func<int, ScrollStep, bool> scroll = (d, _) => { calls.Add(d); return true; };
        editor.ReadLine(5, 23, 70, scroll: scroll);

        Assert.Equal("help", editor.ReadLine(5, 23, 70, scroll: scroll));
        Assert.Empty(calls);
    }

    // AC-5 (037)
    [Theory]
    [MemberData(nameof(ScrollKeys))]
    public void ScrollKeys_WithoutCallback_DoNothing(ConsoleKey key, bool shift, bool control, int direction)
    {
        _ = direction;
        (TestConsole console, LineEditor editor) = Create();
        console.Input.PushTextWithEnter("help");
        console.Input.PushText("x");
        console.Input.PushKey(Key(key, shift, control));
        console.Input.PushKey(ConsoleKey.Enter);

        Read(editor);

        Assert.Equal("x", Read(editor));
        Assert.Equal(["help", "x"], editor.History);
    }

    // AC-7 (037)
    [Fact]
    public void Scroll_RedrawsTheInputLine_WithCursorAfterTheText()
    {
        (TestConsole console, LineEditor editor) = CreateAnsi();
        console.Input.PushText("abc");
        console.Input.PushKey(ConsoleKey.PageDown);
        console.Input.PushKey(ConsoleKey.Enter);

        int before = 0;
        editor.ReadLine(5, 23, 70, scroll: (_, _) =>
        {
            before = console.Output.Length;
            console.Cursor.SetPosition(1, 1);
            console.Write(new Spectre.Console.Text("scrolled"));
            return true;
        });

        string after = console.Output[before..];
        Assert.DoesNotContain("\u001b[2J", console.Output);
        Assert.Equal(new AnsiWrite(23, 5, "abc"), AnsiWrites.Parse(after).Last(w => w.Text.Length > 0));
        Assert.EndsWith("\u001b[23;8H", console.Output);
    }

    // AC-6 (037)
    [Fact]
    public void Scroll_ThatChangesNothing_WritesNothing()
    {
        (TestConsole console, LineEditor editor) = CreateAnsi();
        console.Input.PushText("abc");
        console.Input.PushKey(ConsoleKey.PageDown);
        console.Input.PushKey(ConsoleKey.Enter);

        int before = -1;
        editor.ReadLine(5, 23, 70, scroll: (_, _) =>
        {
            before = console.Output.Length;
            return false;
        });

        Assert.Equal(before, console.Output.Length);
    }

    // AC-3, AC-4 (064)
    [Fact]
    public void Scroll_ThatChangesNothing_WithTabListOpen_WritesNothing_AndKeepsTheTabList()
    {
        PopupRun run = PrefixRun();
        run.Console.Input.PushText("pr");
        run.Console.Input.PushKey(ConsoleKey.Tab);
        run.Console.Input.PushKey(ConsoleKey.PageDown);
        run.Console.Input.PushKey(ConsoleKey.PageDown);
        run.Console.Input.PushKey(ConsoleKey.Enter);

        int calls = 0;
        int afterFirst = -1;
        int beforeSecond = -1;
        string? line = run.Editor.ReadLine(5, 23, 70, run.Area, scroll: (_, _) =>
        {
            calls++;
            if (calls == 1)
            {
                afterFirst = run.Console.Output.Length;
                return false;
            }

            beforeSecond = run.Console.Output.Length;
            return true;
        });

        Assert.Equal("pr", line);
        Assert.Equal(2, calls);
        Assert.Equal("3 commands start with pr", run.ScreenAfterKey(3)[HintRow]);

        // AC-3: the no-op scroll wrote no bytes and nothing cleared the screen.
        Assert.Equal(afterFirst, beforeSecond);
        Assert.DoesNotContain("\u001b[2J", run.Console.Output);

        // AC-4: the scroll that changed something redraws the popup as the Tab list, before the
        // input line (after it, Enter closes the popup and restores its rows).
        Dictionary<int, string> screen = [];
        foreach (AnsiWrite write in AnsiWrites.Parse(run.Console.Output[beforeSecond..]))
        {
            if (write.Row == 23 && write.Column == 5)
            {
                break;
            }

            if (write.Column == 1)
            {
                screen[write.Row] = write.Text.TrimEnd();
            }
        }

        Assert.Equal("3 commands start with pr", screen[HintRow]);
        Assert.StartsWith("prev", screen[18]);
        Assert.StartsWith("print", screen[19]);
        Assert.StartsWith("project", screen[20]);
        Assert.DoesNotContain(screen.Values, t => t.StartsWith("ps ", StringComparison.Ordinal));
    }

    // AC-8 (037)
    [Fact]
    public void Scroll_WithPopupOpen_DrawsThePopupAgain_AfterTheScroll()
    {
        PopupRun run = new PopupRun();
        run.Console.Input.PushText("/");
        run.Console.Input.PushKey(ConsoleKey.PageDown);
        run.Console.Input.PushKey(ConsoleKey.Enter);

        int scrolledAt = -1;
        run.Editor.ReadLine(5, 23, 70, run.Area, scroll: (_, _) =>
        {
            run.Console.Cursor.SetPosition(1, HintRow);
            run.Console.Write(new Spectre.Console.Text("scrolled"));
            scrolledAt = run.Console.Output.Length;
            return true;
        });

        List<AnsiWrite> after = AnsiWrites.Parse(run.Console.Output[scrolledAt..]);
        Assert.Contains(after, w => w.Row == HintRow && w.StartsWithCommand);
        Assert.True(after.FindIndex(w => w.Row == HintRow && w.StartsWithCommand) < after.FindIndex(w => w.Row == 23 && w.Column == 5));
    }

    // Mouse wheel (job 038) -----------------------------------------------------------------

    private static readonly ConsoleKeyInfo Esc = new('\u001b', ConsoleKey.Escape, false, false, false);

    /// <summary>Pushes Esc (with <c>KeyChar</c> ESC) and then <paramref name="rest"/> as plain characters.</summary>
    private static void PushSequence(TestConsole console, string rest)
    {
        console.Input.PushKey(Esc);
        console.Input.PushText(rest);
    }

    private const string WheelUp = "[<64;3;4M";
    private const string WheelDown = "[<65;3;4M";

    // AC-6
    [Fact]
    public void Wheel_CallsTheCallbackOnce_PerNotch_AsAWheelStep()
    {
        (TestConsole console, LineEditor editor) = Create();
        List<(int, ScrollStep)> calls = [];
        console.Input.PushText("to");
        PushSequence(console, WheelUp);
        PushSequence(console, WheelDown);
        PushSequence(console, WheelDown);
        console.Input.PushTextWithEnter("day");

        string? line = editor.ReadLine(5, 23, 70, scroll: (d, step) => { calls.Add((d, step)); return true; });

        Assert.Equal("today", line);
        Assert.Equal([(-1, ScrollStep.Wheel), (1, ScrollStep.Wheel), (1, ScrollStep.Wheel)], calls);
        Assert.Equal(["today"], editor.History);
    }

    // AC-6
    [Theory]
    [InlineData("[<80;1;1M", -1)]
    [InlineData("[<81;1;1M", 1)]
    public void Wheel_WithModifiers_ScrollsToo(string sequence, int direction)
    {
        (TestConsole console, LineEditor editor) = Create();
        List<(int, ScrollStep)> calls = [];
        PushSequence(console, sequence);
        console.Input.PushKey(ConsoleKey.Enter);

        Assert.Equal(string.Empty, editor.ReadLine(5, 23, 70, scroll: (d, step) => { calls.Add((d, step)); return false; }));
        Assert.Equal([(direction, ScrollStep.Wheel)], calls);
    }

    // AC-6 (and AC-5 of 037: scroll keys are page steps)
    [Fact]
    public void ScrollKeys_ArePageSteps()
    {
        (TestConsole console, LineEditor editor) = Create();
        List<ScrollStep> steps = [];
        console.Input.PushKey(ConsoleKey.PageDown);
        console.Input.PushKey(ConsoleKey.PageUp);
        console.Input.PushKey(ConsoleKey.Enter);

        editor.ReadLine(5, 23, 70, scroll: (_, step) => { steps.Add(step); return false; });

        Assert.Equal([ScrollStep.Page, ScrollStep.Page], steps);
    }

    // AC-6
    [Fact]
    public void Wheel_DoesNotMoveTheHistoryPosition()
    {
        (TestConsole console, LineEditor editor) = Create();
        console.Input.PushTextWithEnter("prev");
        console.Input.PushTextWithEnter("next");
        console.Input.PushKey(ConsoleKey.UpArrow);
        PushSequence(console, WheelUp);
        PushSequence(console, WheelDown);
        console.Input.PushKey(ConsoleKey.UpArrow);
        console.Input.PushKey(ConsoleKey.Enter);

        Func<int, ScrollStep, bool> scroll = (_, _) => true;
        editor.ReadLine(5, 23, 70, scroll: scroll);
        editor.ReadLine(5, 23, 70, scroll: scroll);

        // Up, two notches, Up: two steps back through history, as if the notches were not there.
        Assert.Equal("prev", editor.ReadLine(5, 23, 70, scroll: scroll));
        Assert.Equal(["prev", "next", "prev"], editor.History);
    }

    // AC-6
    [Fact]
    public void Wheel_KeepsTheTabList_AndDrawsItAgainOnTop()
    {
        PopupRun run = new PopupRun();
        run.Console.Input.PushText("/");
        run.Console.Input.PushKey(ConsoleKey.Tab);
        PushSequence(run.Console, WheelDown);
        run.Console.Input.PushKey(ConsoleKey.Enter);

        int scrolledAt = -1;
        string? line = run.Editor.ReadLine(5, 23, 70, run.Area, scroll: (_, _) =>
        {
            scrolledAt = run.Console.Output.Length;
            return true;
        });

        Assert.Equal("/", line);
        Assert.True(scrolledAt > 0);
        List<AnsiWrite> after = AnsiWrites.Parse(run.Console.Output[scrolledAt..]);
        Assert.Contains(after, w => w.Row == HintRow && w.Text.Contains("commands start with /", StringComparison.Ordinal));
        Assert.DoesNotContain(after, w => w.Text.Contains("64;", StringComparison.Ordinal));
    }

    // AC-6
    [Fact]
    public void Wheel_KeepsADismissedPopupClosed()
    {
        PopupRun run = new PopupRun();
        run.Console.Input.PushText("t");
        run.Console.Input.PushKey(ConsoleKey.Escape);
        PushSequence(run.Console, WheelDown);
        run.Console.Input.PushText("o");
        run.Console.Input.PushKey(ConsoleKey.Enter);

        int scrolledAt = -1;
        string? line = run.Editor.ReadLine(5, 23, 70, run.Area, scroll: (_, _) =>
        {
            scrolledAt = run.Console.Output.Length;
            return true;
        });

        Assert.Equal("to", line);
        List<AnsiWrite> after = AnsiWrites.Parse(run.Console.Output[scrolledAt..]);
        Assert.DoesNotContain(after, w => w.Column == 1 && w.StartsWithCommand);
    }

    // AC-7
    [Theory]
    [InlineData("[<0;3;4M")]   // click
    [InlineData("[<0;3;4m")]   // release
    [InlineData("[<32;3;4M")]  // drag
    [InlineData("[<66;3;4M")]  // horizontal wheel
    [InlineData("[<67;3;4M")]
    [InlineData("[A")]         // unknown sequences
    [InlineData("[1;5D")]
    [InlineData("[<64;3;4~")]
    [InlineData("[<111111111111111111111111111111~")] // over the 32-key limit (066: read to its end)
    public void OtherSequences_AreIgnoredSilently(string sequence)
    {
        string Run(bool withSequence)
        {
            PopupRun run = new PopupRun();
            List<int> calls = [];
            run.Console.Input.PushTextWithEnter("help");
            run.Console.Input.PushText("t");
            if (withSequence)
            {
                PushSequence(run.Console, sequence);
            }

            run.Console.Input.PushText("o");
            run.Console.Input.PushTextWithEnter("day");
            run.Console.Input.PushKey(ConsoleKey.UpArrow);
            if (withSequence)
            {
                PushSequence(run.Console, sequence);
            }

            run.Console.Input.PushKey(ConsoleKey.UpArrow);
            run.Console.Input.PushKey(ConsoleKey.Enter);

            Assert.Equal("help", run.Editor.ReadLine(5, 23, 70, run.Area, scroll: (d, _) => { calls.Add(d); return true; }));
            Assert.Equal("today", run.Editor.ReadLine(5, 23, 70, run.Area, scroll: (d, _) => { calls.Add(d); return true; }));
            Assert.Equal("help", run.Editor.ReadLine(5, 23, 70, run.Area, scroll: (d, _) => { calls.Add(d); return true; }));
            Assert.Empty(calls);
            Assert.Equal(["help", "today", "help"], run.Editor.History);
            return run.Console.Output;
        }

        // The screen is written exactly as if the sequence had never come in.
        Assert.Equal(Run(withSequence: false), Run(withSequence: true));
    }

    // AC-6, AC-7
    [Fact]
    public void Sequence_CutOffByTheEndOfInput_EndsTheLine()
    {
        PausingInput input = new PausingInput();
        input.Push("ab");
        input.Push(Esc);
        input.Push("[<64;3");
        LineEditor editor = new LineEditor(new InputOverrideConsole(new TestConsole().Interactive(), input), CommandRegistry.CreateDefault());
        List<int> calls = [];

        Assert.Null(editor.ReadLine(5, 23, 70, scroll: (d, _) => { calls.Add(d); return true; }));
        Assert.Empty(calls);
    }

    /// <summary>Console input that says no key is available right after the keys marked as followed by a pause.</summary>
    private sealed class PausingInput : IAnsiConsoleInput
    {
        private readonly Queue<(ConsoleKeyInfo Key, bool Pause)> keys = new();
        private bool paused;

        public void Push(ConsoleKeyInfo key, bool pause = false) => keys.Enqueue((key, pause));

        public void Push(string text)
        {
            foreach (char c in text)
            {
                Push(new ConsoleKeyInfo(c, default, false, false, false));
            }
        }

        public bool IsKeyAvailable() => !paused && keys.Count > 0;

        public ConsoleKeyInfo? ReadKey(bool intercept)
        {
            if (keys.Count == 0)
            {
                return null;
            }

            (ConsoleKeyInfo key, bool pause) = keys.Dequeue();
            paused = pause;
            return key;
        }

        public Task<ConsoleKeyInfo?> ReadKeyAsync(bool intercept, CancellationToken cancellationToken) =>
            Task.FromResult(ReadKey(intercept));
    }

    private static readonly ConsoleKeyInfo Enter = new('\r', ConsoleKey.Enter, false, false, false);

    // AC-8
    [Fact]
    public void LoneEsc_WithoutPopup_ClearsTheLine()
    {
        PausingInput input = new PausingInput();
        input.Push("abc");
        input.Push(Esc, pause: true);
        input.Push("x");
        input.Push(Enter);
        LineEditor editor = new LineEditor(new InputOverrideConsole(new TestConsole().Interactive(), input), CommandRegistry.CreateDefault());

        Assert.Equal("x", editor.ReadLine(5, 23, 70, scroll: (_, _) => true));
    }

    // AC-8
    [Fact]
    public void LoneEsc_WithPopupOpen_ClosesIt_AndKeepsTheLine()
    {
        TestConsole console = new TestConsole().Interactive().EmitAnsiSequences();
        PausingInput input = new PausingInput();
        input.Push("t");
        input.Push(Esc, pause: true);
        input.Push("o");
        input.Push(Enter);
        List<int> restored = [];
        PopupArea area = new PopupArea(HintRow, 80, restored.Add);
        LineEditor editor = new LineEditor(new InputOverrideConsole(console, input), CommandRegistry.CreateDefault());

        Assert.Equal("to", editor.ReadLine(5, 23, 70, area, scroll: (_, _) => true));
        Assert.Contains(HintRow, restored);
    }

    // AC-8
    [Fact]
    public void LoneEsc_KeyCharOnly_IsAnEscToo()
    {
        PausingInput input = new PausingInput();
        input.Push("abc");
        input.Push(new ConsoleKeyInfo('\u001b', default, false, false, false), pause: true);
        input.Push("x");
        input.Push(Enter);
        LineEditor editor = new LineEditor(new InputOverrideConsole(new TestConsole().Interactive(), input), CommandRegistry.CreateDefault());

        Assert.Equal("x", editor.ReadLine(5, 23, 70));
    }

    // AC-8
    [Fact]
    public void Esc_FollowedByAnotherKey_IsEscThenThatKey()
    {
        (TestConsole console, LineEditor editor) = Create();
        List<int> calls = [];
        console.Input.PushText("xyz");
        console.Input.PushKey(Esc);
        console.Input.PushTextWithEnter("a");

        Assert.Equal("a", editor.ReadLine(5, 23, 70, scroll: (d, _) => { calls.Add(d); return true; }));
        Assert.Empty(calls);
    }

    // AC-8
    [Fact]
    public void Esc_FollowedByEsc_ThenAWheelSequence_ClearsAndScrolls()
    {
        (TestConsole console, LineEditor editor) = Create();
        List<int> calls = [];
        console.Input.PushText("xyz");
        console.Input.PushKey(Esc);
        PushSequence(console, WheelDown);
        console.Input.PushTextWithEnter("a");

        Assert.Equal("a", editor.ReadLine(5, 23, 70, scroll: (d, _) => { calls.Add(d); return true; }));
        Assert.Equal([1], calls);
    }

    // AC-8
    [Fact]
    public void Esc_FollowedByEnter_ClearsAndReturns()
    {
        (TestConsole console, LineEditor editor) = Create();
        console.Input.PushText("xyz");
        console.Input.PushKey(Esc);
        console.Input.PushKey(ConsoleKey.Enter);

        Assert.Equal(string.Empty, Read(editor));
    }

    // Commands without slash (job 040) ------------------------------------------------------

    // AC-6 (040)
    [Fact]
    public void FirstCharacter_OpensPopup_ListingBareNames()
    {
        PopupRun run = new PopupRun();
        run.Console.Input.PushText("t");
        run.Console.Input.PushKey(ConsoleKey.Enter);

        Assert.Equal("t", run.Read());
        Assert.Equal(1, run.PopupDraws());
        Assert.Equal("today  t  Go back to today", AnsiWrites.AtRow(run.Console.Output, HintRow)[0].TrimEnd());
        Assert.DoesNotContain(AnsiWrites.Parse(run.Console.Output), w => w.Column == 1 && w.Text.StartsWith('/'));
    }

    // AC-6 (040)
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(" t")]
    public void EmptyLine_OrLeadingWhitespace_DrawsNoPopup(string typed)
    {
        PopupRun run = new PopupRun();
        run.Console.Input.PushText(typed);
        run.Console.Input.PushKey(ConsoleKey.Enter);

        run.Read();

        Assert.Equal(0, run.PopupDraws());
        Assert.Empty(run.Restored);
    }

    // AC-7 (040)
    [Fact]
    public void Escape_KeepsPopupClosedWhileTyping_UntilLineIsClearedByBackspace()
    {
        PopupRun run = new PopupRun();
        run.Console.Input.PushText("t");
        run.Console.Input.PushKey(ConsoleKey.Escape);
        run.Console.Input.PushText("oday");
        run.Console.Input.PushKey(ConsoleKey.Backspace);
        run.Console.Input.PushKey(ConsoleKey.Backspace);
        run.Console.Input.PushKey(ConsoleKey.Backspace);
        run.Console.Input.PushKey(ConsoleKey.Backspace);
        run.Console.Input.PushKey(ConsoleKey.Backspace);
        run.Console.Input.PushText("n");
        run.Console.Input.PushKey(ConsoleKey.Enter);

        Assert.Equal("n", run.Read());
        // "t" drew it; Esc closed it; "oday" and the Backspaces down to "" did not; "n" did again.
        Assert.Equal(2, run.PopupDraws());
        Assert.Equal("restored 21", run.ScreenAfterKey(2)[HintRow]);
        Assert.StartsWith("next", AnsiWrites.AtRow(run.Console.Output, HintRow).Last(t => !t.StartsWith("restored", StringComparison.Ordinal)));
    }

    // AC-8 (040)
    [Theory]
    [InlineData("he", "help")]
    [InlineData("/he", "/help")]
    [InlineData("HE", "help")]
    [InlineData("/He", "/help")]
    public void Tab_CompletesBareName_KeepingTheUsersSlash(string typed, string expected)
    {
        PopupRun run = new PopupRun();
        run.Console.Input.PushText(typed);
        run.Console.Input.PushKey(ConsoleKey.Tab);
        run.Console.Input.PushKey(ConsoleKey.Enter);

        Assert.Equal(expected, run.Read());
    }

    // AC-8 (040)
    [Fact]
    public void Tab_OnB_ExtendsToBreak_AndShowsTheTabList()
    {
        PopupRun run = new PopupRun(HintRow, "break", "breakend");
        run.Console.Input.PushText("b");
        run.Console.Input.PushKey(ConsoleKey.Tab);
        run.Console.Input.PushKey(ConsoleKey.Enter);

        Assert.Equal("break", run.Read());
        Dictionary<int, string> screen = run.ScreenAfterKey(2);
        Assert.Equal("2 commands start with break", screen[HintRow]);
        Assert.StartsWith("break ", screen[19]);
        Assert.StartsWith("breakend", screen[20]);
    }

    // AC-8 (040)
    [Fact]
    public void Tab_OnEmptyLine_DoesNothing_AndOpensNoPopup()
    {
        PopupRun run = new PopupRun();
        run.Console.Input.PushKey(ConsoleKey.Tab);
        run.Console.Input.PushKey(ConsoleKey.Enter);

        Assert.Equal(string.Empty, run.Read());
        Assert.Equal(0, run.PopupDraws());
        Assert.Empty(run.Restored);
    }

    // AC-6 (040): a lone '/' still opens the popup with every command, and Tab gives the full Tab list.
    [Fact]
    public void Tab_OnLoneSlash_ListsEveryCommand()
    {
        PopupRun run = new PopupRun();
        run.Console.Input.PushText("/");
        run.Console.Input.PushKey(ConsoleKey.Tab);
        run.Console.Input.PushKey(ConsoleKey.Enter);

        Assert.Equal("/", run.Read());
        Assert.Equal("5 commands start with /", run.ScreenAfterKey(2)[HintRow]);
    }

    // Long escape sequences and bracketed paste (job 066) ------------------------------------

    private static readonly string FortyDigits = new string('7', 40);

    /// <summary>Pushes a bracketed paste: Esc <c>[200~</c>, the keys, Esc <c>[201~</c>.</summary>
    private static void PushPaste(TestConsole console, params ConsoleKeyInfo[] keys)
    {
        PushSequence(console, "[200~");
        foreach (ConsoleKeyInfo key in keys)
        {
            console.Input.PushKey(key);
        }

        PushSequence(console, "[201~");
    }

    private static ConsoleKeyInfo[] Keys(string text) =>
        text.Select(c => new ConsoleKeyInfo(c, default, false, false, false)).ToArray();

    private static readonly ConsoleKeyInfo EnterKeyLf = new('\n', ConsoleKey.Enter, false, false, false);
    private static readonly ConsoleKeyInfo CharCr = new('\r', default, false, false, false);
    private static readonly ConsoleKeyInfo CharLf = new('\n', default, false, false, false);

    // AC-1
    [Fact]
    public void OverLongSequence_LeavesNothingInTheLine()
    {
        (TestConsole console, LineEditor editor) = Create();
        PushSequence(console, "[" + FortyDigits + "~");
        console.Input.PushTextWithEnter("quit");

        Assert.Equal("quit", Read(editor));
        Assert.Equal(["quit"], editor.History);
        Assert.DoesNotContain("7", console.Output);
        Assert.DoesNotContain("~", console.Output);
        Assert.DoesNotContain("[", console.Output);
    }

    // AC-1
    [Fact]
    public void OverLongSequence_IsNotTypedIntoTheInputLine()
    {
        PopupRun run = new PopupRun();
        PushSequence(run.Console, "[" + FortyDigits + "~");
        run.Console.Input.PushTextWithEnter("quit");

        Assert.Equal("quit", run.Read());
        Assert.DoesNotContain(AnsiWrites.Parse(run.Console.Output), w => w.Text.Contains('7') || w.Text.Contains('~'));
    }

    // AC-2
    [Fact]
    public void KeysAfterAnOverLongSequence_AreTypedNormally()
    {
        (TestConsole console, LineEditor editor) = Create();
        PushSequence(console, "[" + new string('1', 40) + "~");
        console.Input.PushTextWithEnter("ab");

        Assert.Equal("ab", Read(editor));
    }

    // AC-2: a letter ends an unterminated sequence, so no more than that is swallowed.
    [Fact]
    public void OverLongSequence_EndsAtTheFirstFinalCharacter()
    {
        (TestConsole console, LineEditor editor) = Create();
        PushSequence(console, "[" + FortyDigits + "xab");
        console.Input.PushKey(ConsoleKey.Enter);

        Assert.Equal("ab", Read(editor));
    }

    // AC-3
    [Fact]
    public void OverLongWheelSequence_DoesNotScroll()
    {
        (TestConsole console, LineEditor editor) = Create();
        List<int> calls = [];
        PushSequence(console, "[<64;" + FortyDigits + "M");
        console.Input.PushKey(ConsoleKey.Enter);

        Assert.Equal(string.Empty, editor.ReadLine(5, 23, 70, scroll: (d, _) => { calls.Add(d); return true; }));
        Assert.Empty(calls);
    }

    // AC-4
    [Fact]
    public void InputEnding_InsideAnOverLongSequence_ReturnsNull()
    {
        PausingInput input = new PausingInput();
        input.Push("ab");
        input.Push(Esc);
        input.Push("[" + FortyDigits);
        LineEditor editor = new LineEditor(new InputOverrideConsole(new TestConsole().Interactive(), input), CommandRegistry.CreateDefault());

        Assert.Null(editor.ReadLine(5, 23, 70, scroll: (_, _) => true));
        Assert.Empty(editor.History);
    }

    // AC-6
    [Fact]
    public void Paste_PutsTheTextInTheLine_AndEnterReturnsIt()
    {
        (TestConsole console, LineEditor editor) = Create();
        PushPaste(console, Keys("arrive 8:15"));
        console.Input.PushKey(ConsoleKey.Enter);

        Assert.Equal("arrive 8:15", Read(editor));
        Assert.Equal(["arrive 8:15"], editor.History);
        Assert.Contains("arrive 8:15", console.Output);
    }

    // AC-6
    [Fact]
    public void Paste_IsNotSubmitted_WithoutEnter()
    {
        PausingInput input = new PausingInput();
        input.Push(Esc);
        input.Push("[200~arrive 8:15");
        input.Push(Enter);
        input.Push(Esc);
        input.Push("[201~");
        LineEditor editor = new LineEditor(new InputOverrideConsole(new TestConsole().Interactive(), input), CommandRegistry.CreateDefault());

        Assert.Null(editor.ReadLine(5, 23, 70));
        Assert.Empty(editor.History);
    }

    // AC-7
    [Fact]
    public void Paste_LineBreaks_BecomeOneSpaceEach()
    {
        (TestConsole console, LineEditor editor) = Create();
        PushPaste(console, [.. Keys("a"), Enter, CharLf, .. Keys("b"), EnterKeyLf, .. Keys("c")]);
        console.Input.PushKey(ConsoleKey.Enter);

        Assert.Equal("a b c", Read(editor));
        Assert.Equal(["a b c"], editor.History);
    }

    // AC-7
    [Fact]
    public void Paste_LineBreaksAsKeyChars_CrLfCrAndLf_BecomeOneSpaceEach()
    {
        (TestConsole console, LineEditor editor) = Create();
        PushPaste(console, [.. Keys("a"), CharCr, CharLf, .. Keys("b"), CharCr, .. Keys("c"), CharLf, .. Keys("d"), CharLf, CharCr, .. Keys("e")]);
        console.Input.PushKey(ConsoleKey.Enter);

        Assert.Equal("a b c d  e", Read(editor));
    }

    // AC-7
    [Fact]
    public void Paste_Tab_BecomesASpace_AndOtherControlCharactersAreDropped()
    {
        (TestConsole console, LineEditor editor) = Create();
        PushPaste(console, [
            .. Keys("a"),
            new ConsoleKeyInfo('\t', ConsoleKey.Tab, false, false, false),
            .. Keys("b\tc\u0007d\u0000e"),
            new ConsoleKeyInfo('\b', ConsoleKey.Backspace, false, false, false),
            .. Keys("f"),
        ]);
        console.Input.PushKey(ConsoleKey.Enter);

        Assert.Equal("a b cdef", Read(editor));
    }

    // AC-8
    [Fact]
    public void Paste_AppendsToWhatWasTyped()
    {
        (TestConsole console, LineEditor editor) = Create();
        console.Input.PushText("arr");
        PushPaste(console, Keys("ive 8:15"));
        console.Input.PushKey(ConsoleKey.Enter);

        Assert.Equal("arrive 8:15", Read(editor));
    }

    /// <summary>
    /// Plays the writes of <paramref name="output"/> onto a screen whose rows 1 to 22 start out as
    /// <c>row N</c> (what <see cref="BackgroundArea"/> restores): the text at each row.
    /// </summary>
    private static Dictionary<int, string> FinalScreen(string output)
    {
        Dictionary<int, char[]> rows = [];
        for (int row = 1; row <= 23; row++)
        {
            rows[row] = (row < 23 ? $"row {row}" : string.Empty).PadRight(200).ToCharArray();
        }

        foreach (AnsiWrite write in AnsiWrites.Parse(output))
        {
            for (int i = 0; i < write.Text.Length; i++)
            {
                rows[write.Row][write.Column - 1 + i] = write.Text[i];
            }
        }

        return rows.ToDictionary(r => r.Key, r => new string(r.Value).TrimEnd());
    }

    /// <summary>A popup area whose restored rows read <c>row N</c>, padded to its width, like the screen <see cref="FinalScreen"/> starts with.</summary>
    private static PopupArea BackgroundArea(TestConsole console) => new PopupArea(HintRow, 80, row =>
    {
        console.Cursor.SetPosition(1, row);
        console.Write(new Text($"row {row}".PadRight(80)));
    });

    private static string LastCursor(string output) =>
        System.Text.RegularExpressions.Regex.Matches(output, @"\u001b\[\d+;\d+H")[^1].Value;

    // AC-8
    [Theory]
    [InlineData("", "t", 70)]                                   // placeholder blanked, popup opens
    [InlineData("", "today", 70)]
    [InlineData("to", "day", 70)]
    [InlineData("", " x", 70)]                                  // leading space: no popup
    [InlineData("arr", "ive 8:15 and a much longer tail", 20)]  // … tail cue
    [InlineData("", "help me with a long line", 12)]
    public void Paste_DrawsTheLine_LikeTheSameTextTyped(string before, string pasted, int width)
    {
        TestConsole typed = new TestConsole().Interactive().EmitAnsiSequences();
        typed.Input.PushText(before + pasted);
        typed.Input.PushKey(ConsoleKey.Enter);
        TestConsole paste = new TestConsole().Interactive().EmitAnsiSequences();
        paste.Input.PushText(before);
        PushPaste(paste, Keys(pasted));
        paste.Input.PushKey(ConsoleKey.Enter);

        string? typedLine = new LineEditor(typed, CommandRegistry.CreateDefault()).ReadLine(5, 23, width, BackgroundArea(typed), Placeholder);
        string? pastedLine = new LineEditor(paste, CommandRegistry.CreateDefault()).ReadLine(5, 23, width, BackgroundArea(paste), Placeholder);

        Assert.Equal(before + pasted, pastedLine);
        Assert.Equal(typedLine, pastedLine);
        Assert.Equal(FinalScreen(typed.Output), FinalScreen(paste.Output));
        Assert.Equal(LastCursor(typed.Output), LastCursor(paste.Output));
    }

    /// <summary>
    /// Reads until <paramref name="input"/> runs out, with the default commands and a popup, and
    /// returns the output up to the last input-line redraw (before the end of input closes the popup).
    /// </summary>
    private static string ReadUntilTheEnd(PausingInput input)
    {
        TestConsole console = new TestConsole().Interactive().EmitAnsiSequences();
        LineEditor editor = new LineEditor(new InputOverrideConsole(console, input), CommandRegistry.CreateDefault());
        Assert.Null(editor.ReadLine(5, 23, 70, BackgroundArea(console), Placeholder));
        string output = console.Output;
        int lastLine = output.LastIndexOf("\u001b[23;5H", StringComparison.Ordinal);
        System.Text.RegularExpressions.Match close = System.Text.RegularExpressions.Regex.Match(output[lastLine..], @"\u001b\[\d+;1H");
        return close.Success ? output[..(lastLine + close.Index)] : output;
    }

    // AC-8: before Enter, the open popup is the same too.
    [Fact]
    public void Paste_OpensThePopup_LikeTyping()
    {
        PausingInput typed = new PausingInput();
        typed.Push("he");
        PausingInput pasted = new PausingInput();
        pasted.Push(Esc);
        pasted.Push("[200~he");
        pasted.Push(Esc);
        pasted.Push("[201~");

        Dictionary<int, string> typedScreen = FinalScreen(ReadUntilTheEnd(typed));
        Dictionary<int, string> pastedScreen = FinalScreen(ReadUntilTheEnd(pasted));

        Assert.Equal(typedScreen, pastedScreen);
        Assert.StartsWith("help", pastedScreen[HintRow]);
    }

    // AC-9
    [Fact]
    public void Paste_OtherSequencesInside_AreDropped()
    {
        (TestConsole console, LineEditor editor) = Create();
        List<int> calls = [];
        PushSequence(console, "[200~");
        console.Input.PushText("a");
        PushSequence(console, "[<65;3;4M");
        console.Input.PushText("b");
        PushSequence(console, "[A");
        PushSequence(console, "[200~");
        PushSequence(console, "[" + FortyDigits + "~");
        console.Input.PushText("c");
        PushSequence(console, "[201~");
        console.Input.PushKey(ConsoleKey.Enter);

        Assert.Equal("abc", editor.ReadLine(5, 23, 70, scroll: (d, _) => { calls.Add(d); return true; }));
        Assert.Empty(calls);
    }

    // AC-9
    [Fact]
    public void Paste_StrayEsc_IsDropped()
    {
        (TestConsole console, LineEditor editor) = Create();
        PushPaste(console, [.. Keys("a"), Esc, .. Keys("b"), Esc, Esc, .. Keys("c")]);
        console.Input.PushKey(ConsoleKey.Enter);

        Assert.Equal("abc", Read(editor));
    }

    // AC-9
    [Theory]
    [InlineData("[200~abc")]
    [InlineData("[200~abc\u001b")]
    [InlineData("[200~abc\u001b[20")]
    public void InputEnding_InsideAPaste_ReturnsNull(string rest)
    {
        PausingInput input = new PausingInput();
        input.Push("x");
        input.Push(Esc);
        input.Push(rest);
        LineEditor editor = new LineEditor(new InputOverrideConsole(new TestConsole().Interactive(), input), CommandRegistry.CreateDefault());

        Assert.Null(editor.ReadLine(5, 23, 70));
        Assert.Empty(editor.History);
    }

    // AC-6: an end marker outside a paste is just an unknown sequence.
    [Fact]
    public void PasteEnd_OutsideAPaste_IsDropped()
    {
        (TestConsole console, LineEditor editor) = Create();
        PushSequence(console, "[201~");
        console.Input.PushTextWithEnter("ab");

        Assert.Equal("ab", Read(editor));
    }

    // Job 079, AC-7 --------------------------------------------------------------------------

    private static string? ReadValue(LineEditor editor, out bool cancelled) =>
        editor.ReadValue(5, 23, 70, "a whole number", out cancelled);

    [Fact]
    public void ReadValue_ReturnsTheTypedValue_WithoutPopupOrHistory()
    {
        (TestConsole console, LineEditor editor) = Create();
        console.Input.PushTextWithEnter("he");
        console.Input.PushKey(ConsoleKey.UpArrow);
        console.Input.PushKey(ConsoleKey.Tab);
        console.Input.PushKey(ConsoleKey.Enter);
        console.Input.PushKey(ConsoleKey.UpArrow);
        console.Input.PushKey(ConsoleKey.Enter);

        Assert.Equal("he", ReadValue(editor, out bool cancelled));
        Assert.False(cancelled);
        Assert.DoesNotContain("help", console.Output);
        // The value was not added to the history: Up on the next command line finds nothing.
        Assert.Equal(string.Empty, Read(editor));
    }

    [Fact]
    public void ReadValue_Esc_Cancels()
    {
        (TestConsole console, LineEditor editor) = Create();
        console.Input.PushText("12");
        console.Input.PushKey(ConsoleKey.Escape);

        Assert.Equal(string.Empty, ReadValue(editor, out bool cancelled));
        Assert.True(cancelled);
    }

    // Job 079, AC-5 --------------------------------------------------------------------------

    [Fact]
    public void ReadKey_ReturnsKeys_DropsMouseAndPasteSequences_AndNormalizesEsc()
    {
        (TestConsole console, LineEditor editor) = Create();
        console.Input.PushKey(ConsoleKey.DownArrow);
        console.Input.PushKey(new ConsoleKeyInfo('\u001b', ConsoleKey.Escape, false, false, false));
        console.Input.PushText("[<65;10;5M");
        console.Input.PushKey(new ConsoleKeyInfo('\u001b', ConsoleKey.Escape, false, false, false));
        console.Input.PushText("[200~ x");
        console.Input.PushKey(new ConsoleKeyInfo('\u001b', ConsoleKey.Escape, false, false, false));
        console.Input.PushText("[201~");
        console.Input.PushKey(new ConsoleKeyInfo('\u001b', default, false, false, false));
        console.Input.PushText("q");
        console.Input.PushKey(ConsoleKey.Enter);

        Assert.Equal(ConsoleKey.DownArrow, editor.ReadKey()?.Key);
        Assert.Equal(ConsoleKey.Escape, editor.ReadKey()?.Key);
        Assert.Equal('q', editor.ReadKey()?.KeyChar);
        Assert.Equal(ConsoleKey.Enter, editor.ReadKey()?.Key);
    }

    [Fact]
    public void ReadKey_ReturnsNull_WhenTheInputEnds()
    {
        PausingFreeInput input = new PausingFreeInput();
        LineEditor editor = new LineEditor(new InputOverrideConsole(new TestConsole().Interactive(), input), CommandRegistry.CreateDefault());

        Assert.Null(editor.ReadKey());
    }

    private sealed class PausingFreeInput : IAnsiConsoleInput
    {
        public bool IsKeyAvailable() => false;

        public ConsoleKeyInfo? ReadKey(bool intercept) => null;

        public Task<ConsoleKeyInfo?> ReadKeyAsync(bool intercept, CancellationToken cancellationToken) =>
            Task.FromResult<ConsoleKeyInfo?>(null);
    }
}
