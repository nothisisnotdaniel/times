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
        console.Input.PushTextWithEnter("/help me");

        Assert.Equal("/help me", Read(editor));
    }

    [Fact]
    public void TypedText_IsEchoed()
    {
        (TestConsole console, LineEditor editor) = Create();
        console.Input.PushTextWithEnter("/hel");

        Read(editor);

        Assert.Contains("/hel", console.Output);
    }

    // AC-10
    [Fact]
    public void Backspace_RemovesLastCharacter()
    {
        (TestConsole console, LineEditor editor) = Create();
        console.Input.PushText("/quiz");
        console.Input.PushKey(ConsoleKey.Backspace);
        console.Input.PushTextWithEnter("t");

        Assert.Equal("/quit", Read(editor));
    }

    [Fact]
    public void Backspace_OnEmptyLine_DoesNothing()
    {
        (TestConsole console, LineEditor editor) = Create();
        console.Input.PushKey(ConsoleKey.Backspace);
        console.Input.PushTextWithEnter("/q");

        Assert.Equal("/q", Read(editor));
    }

    [Fact]
    public void Escape_ClearsTheLine()
    {
        (TestConsole console, LineEditor editor) = Create();
        console.Input.PushText("abc");
        console.Input.PushKey(ConsoleKey.Escape);
        console.Input.PushTextWithEnter("/quit");

        Assert.Equal("/quit", Read(editor));
    }

    // AC-11
    [Fact]
    public void Tab_CompletesUniquePrefix()
    {
        (TestConsole console, LineEditor editor) = Create();
        console.Input.PushText("/q");
        console.Input.PushKey(ConsoleKey.Tab);
        console.Input.PushKey(ConsoleKey.Enter);

        Assert.Equal("/quit", Read(editor));
    }

    [Theory]
    [InlineData("/x")]  // no match
    [InlineData("/")]   // matches /help and /quit
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
        console.Input.PushText("/q x");
        console.Input.PushKey(ConsoleKey.Tab);
        console.Input.PushKey(ConsoleKey.Enter);

        Assert.Equal("/q x", Read(editor));
    }

    // AC-12
    [Fact]
    public void Up_RecallsPreviousLine()
    {
        (TestConsole console, LineEditor editor) = Create();
        console.Input.PushTextWithEnter("/help");
        console.Input.PushKey(ConsoleKey.UpArrow);
        console.Input.PushKey(ConsoleKey.Enter);

        Assert.Equal("/help", Read(editor));
        Assert.Equal("/help", Read(editor));
    }

    [Fact]
    public void Up_PastOldest_StaysOnOldest()
    {
        (TestConsole console, LineEditor editor) = Create();
        console.Input.PushTextWithEnter("/one");
        console.Input.PushTextWithEnter("/two");
        console.Input.PushKey(ConsoleKey.UpArrow);
        console.Input.PushKey(ConsoleKey.UpArrow);
        console.Input.PushKey(ConsoleKey.UpArrow);
        console.Input.PushKey(ConsoleKey.UpArrow);
        console.Input.PushKey(ConsoleKey.Enter);

        Read(editor);
        Read(editor);
        Assert.Equal("/one", Read(editor));
    }

    [Fact]
    public void Down_PastNewest_GivesEmptyLine()
    {
        (TestConsole console, LineEditor editor) = Create();
        console.Input.PushTextWithEnter("/one");
        console.Input.PushTextWithEnter("/two");
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
        console.Input.PushTextWithEnter("/one");
        console.Input.PushTextWithEnter("/two");
        console.Input.PushKey(ConsoleKey.UpArrow);
        console.Input.PushKey(ConsoleKey.UpArrow);
        console.Input.PushKey(ConsoleKey.DownArrow);
        console.Input.PushKey(ConsoleKey.Enter);

        Read(editor);
        Read(editor);
        Assert.Equal("/two", Read(editor));
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
        console.Input.PushTextWithEnter("/help");
        console.Input.PushTextWithEnter("");
        console.Input.PushKey(ConsoleKey.UpArrow);
        console.Input.PushKey(ConsoleKey.Enter);

        Read(editor);
        Read(editor);
        Assert.Equal("/help", Read(editor));
    }

    // Command popup (job 006) --------------------------------------------------------------

    private const int HintRow = 21;

    private sealed class PopupRun
    {
        public PopupRun(int bottomRow = HintRow, params string[] extra)
        {
            Console = new TestConsole().Interactive().EmitAnsiSequences();
            CommandRegistry registry = CommandRegistry.CreateDefault();
            registry.Register("/prev", "Go to the previous day", _ => CommandResult.None);
            registry.Register("/next", "Go to the next day", _ => CommandResult.None);
            registry.Register("/today", "Go back to today", _ => CommandResult.None);
            foreach (string name in extra)
            {
                registry.Register(name, $"Dummy {name}", _ => CommandResult.None);
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
        Assert.StartsWith("/help   List all commands", rows[0].Text);
        Assert.StartsWith("/next   Go to the next day", rows[1].Text);
        Assert.StartsWith("/prev   Go to the previous day", rows[2].Text);
        Assert.StartsWith("/quit   Exit Times", rows[3].Text);
        Assert.StartsWith("/today  Go back to today", rows[4].Text);
    }

    [Fact]
    public void NoPopupArea_DrawsNoPopup()
    {
        TestConsole console = new TestConsole().Interactive().EmitAnsiSequences();
        LineEditor editor = new LineEditor(console, CommandRegistry.CreateDefault());
        console.Input.PushTextWithEnter("/he");

        Assert.Equal("/he", editor.ReadLine(5, 23, 70));
        Assert.DoesNotContain("List all commands", console.Output);
    }

    [Fact]
    public void TextWithoutSlash_OpensNoPopup()
    {
        PopupRun run = new PopupRun();
        run.Console.Input.PushTextWithEnter("help");

        run.Read();

        Assert.Equal(0, run.PopupDraws());
        Assert.DoesNotContain("List all commands", run.Console.Output);
    }

    // AC-4
    [Fact]
    public void EachKey_RedrawsPopup_WithCurrentMatch()
    {
        PopupRun run = new PopupRun();
        run.Console.Input.PushText("/tdoay");

        run.Console.Input.PushKey(ConsoleKey.Enter);
        run.Read();

        Assert.Equal(6, run.PopupDraws());
        Assert.StartsWith("/today", AnsiWrites.AtRow(run.Console.Output, HintRow).Last(t => !t.StartsWith("restored", StringComparison.Ordinal)));
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

        // "/" fills rows 17-21, "/t" only row 21: rows 17-20 come back first, row 21 on Esc.
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
        Assert.Equal(2, AnsiWrites.AtRow(run.Console.Output, 17).Count(t => t.StartsWith("/help", StringComparison.Ordinal)));
    }

    // AC-4
    [Fact]
    public void TabCompletion_RedrawsPopup()
    {
        PopupRun run = new PopupRun();
        run.Console.Input.PushText("/q");
        run.Console.Input.PushKey(ConsoleKey.Tab);
        run.Console.Input.PushKey(ConsoleKey.Enter);

        Assert.Equal("/quit", run.Read());
        Assert.Equal(3, run.PopupDraws());
        Assert.Contains(AnsiWrites.Parse(run.Console.Output), w => w.Row == 23 && w.Text.StartsWith("/quit", StringComparison.Ordinal));
    }

    // AC-4
    [Fact]
    public void CursorReturnsToEndOfInputLine_AfterEveryPopupRedraw()
    {
        PopupRun run = new PopupRun();
        run.Console.Input.PushText("/he");
        run.Console.Input.PushKey(ConsoleKey.Enter);

        run.Read();

        List<AnsiWrite> writes = AnsiWrites.Parse(run.Console.Output);
        Assert.Equal(new AnsiWrite(23, 8, string.Empty), writes[^1]);
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
        // "/" covered rows 17-21; "/t" shrank it to row 21, and Enter gives row 21 back.
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
        run.Console.Input.PushTextWithEnter("abc");

        Assert.Equal("abc", run.Read());
        Assert.Empty(run.Restored);
    }

    // AC-6
    [Fact]
    public void Escape_WithPopupOpen_ClosesIt_AndKeepsText()
    {
        PopupRun run = new PopupRun();
        run.Console.Input.PushText("/he");
        run.Console.Input.PushKey(ConsoleKey.Escape);
        run.Console.Input.PushKey(ConsoleKey.Enter);

        Assert.Equal("/he", run.Read());
        // "/he" matches /help and /next (rows 20-21); Esc gives both rows back.
        Assert.Equal([20, 21], run.Restored.TakeLast(2));
        Dictionary<int, string> screen = run.Screen();
        for (int row = 17; row <= 21; row++)
        {
            Assert.Equal($"restored {row}", screen[row]);
        }
    }

    // AC-6
    [Fact]
    public void SecondEscape_ClearsTheLine()
    {
        PopupRun run = new PopupRun();
        run.Console.Input.PushText("/he");
        run.Console.Input.PushKey(ConsoleKey.Escape);
        run.Console.Input.PushKey(ConsoleKey.Escape);
        run.Console.Input.PushKey(ConsoleKey.Enter);

        Assert.Equal("", run.Read());
    }

    // AC-6
    [Fact]
    public void AfterEscape_PopupStaysClosed_UntilLineStopsStartingWithSlash()
    {
        PopupRun run = new PopupRun();
        run.Console.Input.PushText("/he");
        run.Console.Input.PushKey(ConsoleKey.Escape);
        run.Console.Input.PushText("l");
        run.Console.Input.PushKey(ConsoleKey.Backspace);
        run.Console.Input.PushKey(ConsoleKey.Backspace);
        run.Console.Input.PushKey(ConsoleKey.Backspace);
        run.Console.Input.PushKey(ConsoleKey.Backspace);
        run.Console.Input.PushText("/");
        run.Console.Input.PushKey(ConsoleKey.Enter);

        Assert.Equal("/", run.Read());
        // "/", "/h", "/he" drew the popup; "l" and the Backspaces did not; the new "/" did.
        Assert.Equal(4, run.PopupDraws());
        Assert.Equal(2, AnsiWrites.AtRow(run.Console.Output, 17).Count(t => t.StartsWith("/help", StringComparison.Ordinal)));
    }

    // AC-6
    [Fact]
    public void AfterSecondEscape_TypingSlash_OpensPopupAgain()
    {
        PopupRun run = new PopupRun();
        run.Console.Input.PushText("/he");
        run.Console.Input.PushKey(ConsoleKey.Escape);
        run.Console.Input.PushKey(ConsoleKey.Escape);
        run.Console.Input.PushText("/");
        run.Console.Input.PushKey(ConsoleKey.Enter);

        Assert.Equal("/", run.Read());
        Assert.Equal(4, run.PopupDraws());
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
        Assert.Contains(rows, w => w.Row == 2 && w.Text.StartsWith("/help", StringComparison.Ordinal));
        Assert.Contains(rows, w => w.Row == 3 && w.Text.StartsWith("… 4 more", StringComparison.Ordinal));
    }

    // AC-9
    [Fact]
    public void Typing_NeverClearsTheScreen()
    {
        PopupRun run = new PopupRun();
        run.Console.Input.PushText("/tdoay");
        run.Console.Input.PushKey(ConsoleKey.Backspace);
        run.Console.Input.PushKey(ConsoleKey.Escape);
        run.Console.Input.PushKey(ConsoleKey.Escape);
        run.Console.Input.PushKey(ConsoleKey.Enter);

        run.Read();

        Assert.DoesNotContain("\u001b[2J", run.Console.Output);
    }

    // Tab feedback (job 011) ---------------------------------------------------------------

    private static readonly string[] Prefixed = ["/print", "/project", "/ps"];

    private static PopupRun PrefixRun() => new(HintRow, Prefixed);

    // AC-3
    [Theory]
    [InlineData("/p", "/p")]     // /prev, /print, /project, /ps share only "/p"
    [InlineData("/pr", "/pr")]   // /prev, /print, /project share "/pr": not extended further
    [InlineData("/PRO", "/project")]
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
        PopupRun run = new PopupRun(HintRow, "/print", "/project");
        run.Console.Input.PushText("/p");
        run.Console.Input.PushKey(ConsoleKey.Tab);
        run.Console.Input.PushKey(ConsoleKey.Enter);

        Assert.Equal("/pr", run.Read());
        Assert.Contains(AnsiWrites.Parse(run.Console.Output), w => w.Row == 23 && w.Text.StartsWith("/pr", StringComparison.Ordinal));
    }

    // AC-3
    [Fact]
    public void Tab_WithoutPopupArea_OnlyExtendsTheLine()
    {
        TestConsole console = new TestConsole().Interactive().EmitAnsiSequences();
        CommandRegistry registry = CommandRegistry.CreateDefault();
        registry.Register("/prev", "d", _ => CommandResult.None);
        registry.Register("/print", "d", _ => CommandResult.None);
        LineEditor editor = new LineEditor(console, registry);
        console.Input.PushText("/p");
        console.Input.PushKey(ConsoleKey.Tab);
        console.Input.PushKey(ConsoleKey.Enter);

        Assert.Equal("/pr", editor.ReadLine(5, 23, 70));
        Assert.DoesNotContain("commands start with", console.Output);
        Assert.DoesNotContain("/prev", console.Output);
    }

    // AC-4
    [Fact]
    public void AmbiguousTab_ListsOnlyPrefixMatches_WithFooterOnHintRow()
    {
        PopupRun run = PrefixRun();
        run.Console.Input.PushText("/pr");
        run.Console.Input.PushKey(ConsoleKey.Tab);
        run.Console.Input.PushKey(ConsoleKey.Enter);

        Assert.Equal("/pr", run.Read());

        // Before Tab the fuzzy list includes /ps (one edit from "pr").
        Dictionary<int, string> before = run.ScreenAfterKey(3);
        Assert.Contains(before.Values, t => t.StartsWith("/ps ", StringComparison.Ordinal));

        Dictionary<int, string> screen = run.ScreenAfterKey(4);
        Assert.Equal("3 commands start with /pr", screen[HintRow]);
        Assert.StartsWith("/prev", screen[18]);
        Assert.StartsWith("/print", screen[19]);
        Assert.StartsWith("/project", screen[20]);
        // Rows above 18 were given back when "/p" shrank the popup; the Tab list does not cover them.
        Assert.Equal("restored 17", screen[17]);
        Assert.DoesNotContain(screen.Values, t => t.StartsWith("/ps ", StringComparison.Ordinal));
        Assert.Contains("\u001b[38;5;8m3 commands start with /pr", run.Console.Output);
    }

    // AC-5
    [Fact]
    public void AmbiguousTab_AfterEscape_ReopensPopup()
    {
        PopupRun run = PrefixRun();
        run.Console.Input.PushText("/pr");
        run.Console.Input.PushKey(ConsoleKey.Escape);
        run.Console.Input.PushKey(ConsoleKey.Tab);
        run.Console.Input.PushKey(ConsoleKey.Enter);

        Assert.Equal("/pr", run.Read());

        Assert.Equal("restored 21", run.ScreenAfterKey(4)[HintRow]);
        Dictionary<int, string> screen = run.ScreenAfterKey(5);
        Assert.Equal("3 commands start with /pr", screen[HintRow]);
        Assert.StartsWith("/prev", screen[18]);
        Assert.StartsWith("/print", screen[19]);
        Assert.StartsWith("/project", screen[20]);
    }

    // AC-6
    [Fact]
    public void AfterTabList_Backspace_GoesBackToFuzzyMatch()
    {
        PopupRun run = PrefixRun();
        run.Console.Input.PushText("/pr");
        run.Console.Input.PushKey(ConsoleKey.Tab);
        run.Console.Input.PushKey(ConsoleKey.Backspace);
        run.Console.Input.PushKey(ConsoleKey.Enter);

        Assert.Equal("/p", run.Read());

        // "/p" fuzzy: /prev, /print, /project, /ps on rows 18-21, no footer.
        Dictionary<int, string> screen = run.ScreenAfterKey(5);
        Assert.StartsWith("/prev", screen[18]);
        Assert.StartsWith("/ps", screen[HintRow]);
        Assert.DoesNotContain(screen.Values, t => t.Contains("commands start with", StringComparison.Ordinal));
    }

    // AC-6
    [Fact]
    public void AfterTabList_TypingCharacter_GoesBackToFuzzyMatch()
    {
        PopupRun run = PrefixRun();
        run.Console.Input.PushText("/pr");
        run.Console.Input.PushKey(ConsoleKey.Tab);
        run.Console.Input.PushText("o");
        run.Console.Input.PushKey(ConsoleKey.Enter);

        Assert.Equal("/pro", run.Read());

        // "/pro" fuzzy: /project (0), then /prev and /print (1 edit) on rows 19-21; row 18 (footer list top) restored.
        Dictionary<int, string> screen = run.ScreenAfterKey(5);
        Assert.StartsWith("/project", screen[19]);
        Assert.StartsWith("/prev", screen[20]);
        Assert.StartsWith("/print", screen[HintRow]);
        Assert.Equal("restored 18", screen[18]);
        Assert.DoesNotContain(screen.Values, t => t.Contains("commands start with", StringComparison.Ordinal));
    }

    // AC-7
    [Fact]
    public void TabList_WithMoreMatchesThanFit_ShowsMoreRowAndFooter()
    {
        PopupRun run = new PopupRun(bottomRow: 5, "/print", "/project", "/ps");
        run.Console.Input.PushText("/p");
        run.Console.Input.PushKey(ConsoleKey.Tab);
        run.Console.Input.PushKey(ConsoleKey.Enter);

        run.Read();

        // Rows 2-5 hold 4 rows: two matches, "… 2 more", then the footer.
        Dictionary<int, string> screen = run.ScreenAfterKey(3);
        Assert.StartsWith("/prev", screen[2]);
        Assert.StartsWith("/print", screen[3]);
        Assert.Equal("… 2 more", screen[4]);
        Assert.Equal("4 commands start with /p", screen[5]);
        Assert.DoesNotContain(AnsiWrites.Parse(run.Console.Output), w => w.Row < 2);
    }

    // AC-8
    [Fact]
    public void Tab_WithNoPrefixMatch_LeavesLineAndPopup()
    {
        PopupRun run = PrefixRun();
        run.Console.Input.PushText("/x");
        run.Console.Input.PushKey(ConsoleKey.Tab);
        run.Console.Input.PushKey(ConsoleKey.Enter);

        Assert.Equal("/x", run.Read());
        Assert.Equal(run.ScreenAfterKey(2), run.ScreenAfterKey(3));
        Assert.Equal("No matching commands", run.ScreenAfterKey(3)[HintRow]);
    }

    // AC-8
    [Fact]
    public void Tab_WithNoPrefixMatch_AfterEscape_KeepsPopupClosed()
    {
        PopupRun run = PrefixRun();
        run.Console.Input.PushText("/x");
        run.Console.Input.PushKey(ConsoleKey.Escape);
        run.Console.Input.PushKey(ConsoleKey.Tab);
        run.Console.Input.PushKey(ConsoleKey.Enter);

        Assert.Equal("/x", run.Read());
        Assert.Equal("restored 21", run.ScreenAfterKey(4)[HintRow]);
    }

    // AC-8
    [Fact]
    public void Tab_AfterWhitespace_LeavesLineAndPopup()
    {
        PopupRun run = PrefixRun();
        run.Console.Input.PushText("/p x");
        run.Console.Input.PushKey(ConsoleKey.Tab);
        run.Console.Input.PushKey(ConsoleKey.Enter);

        Assert.Equal("/p x", run.Read());
        Assert.Equal(run.ScreenAfterKey(4), run.ScreenAfterKey(5));
        Assert.DoesNotContain("commands start with", run.Console.Output);
    }

    // AC-8
    [Fact]
    public void Tab_UniquePrefix_StillCompletes_WithExtraCommands()
    {
        PopupRun run = PrefixRun();
        run.Console.Input.PushText("/q");
        run.Console.Input.PushKey(ConsoleKey.Tab);
        run.Console.Input.PushKey(ConsoleKey.Enter);

        Assert.Equal("/quit", run.Read());
    }

    // AC-8
    [Fact]
    public void AmbiguousTab_NeverClearsScreen_AndCursorEndsOnInputLine()
    {
        PopupRun run = PrefixRun();
        run.Console.Input.PushText("/pr");
        run.Console.Input.PushKey(ConsoleKey.Tab);
        run.Console.Input.PushKey(ConsoleKey.Enter);

        run.Read();

        Assert.DoesNotContain("\u001b[2J", run.Console.Output);
        List<AnsiWrite> writes = AnsiWrites.Parse(run.Console.Output);
        Assert.Equal(new AnsiWrite(23, 8, string.Empty), writes[^1]);
        // After the Tab-list draw, the cursor goes back to the end of "/pr".
        int footer = writes.FindIndex(w => w.Text.StartsWith("3 commands", StringComparison.Ordinal));
        Assert.Contains(writes.Skip(footer + 1), w => w == new AnsiWrite(23, 8, string.Empty));
    }
}
