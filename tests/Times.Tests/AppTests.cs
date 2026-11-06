using System.Text;
using System.Text.RegularExpressions;
using Spectre.Console;
using Spectre.Console.Testing;

namespace Times.Tests;

public sealed class AppTests : IDisposable
{
    private const string Heading = "Tuesday, 2026-10-27";

    /// <summary>
    /// The description of the first help row (absence), which is on help's first page at 24 rows
    /// whatever commands come later in the alphabet.
    /// </summary>
    private const string HelpFirstRow = "Mark this day as vacation";

    /// <summary>Rows the quicklist panel takes with no projects (border, one line, border): the day heading is on the next row.</summary>
    private const int EmptyPanelRows = 3;

    /// <summary>Rows of the top area on the default 24-row terminal: 24 minus the hint row and the 3-line input box.</summary>
    private const int TopHeight = 20;

    /// <summary>More PageDown presses than help needs to reach the bottom at 24 rows; the extra ones change nothing.</summary>
    private const int HelpPages = 5;

    /// <summary>Pushes <paramref name="count"/> PageDown keys.</summary>
    private static void PageDown(TestConsole console, int count)
    {
        for (int i = 0; i < count; i++)
        {
            console.Input.PushKey(ConsoleKey.PageDown);
        }
    }

    private readonly TempFolder data = new();

    // Where tests that make the data folder unwritable (or point it at a folder that must not be created)
    // keep their instance lock, so start-up still succeeds and the test reaches what it is about (117).
    private readonly TempFolder lockFolder = new();

    // Set by UseSideLock: the lock the helpers pass to App instead of App's own one on the data folder.
    private InstanceLock? sideLock;

    public void Dispose()
    {
        sideLock?.Dispose();
        data.Dispose();
        lockFolder.Dispose();
    }

    /// <summary>Makes the helpers (and <see cref="SideLock"/> users) keep the instance lock outside the data folder.</summary>
    private void UseSideLock() => sideLock = SideLock();

    /// <summary>A fresh instance lock on a folder of its own, not the data folder.</summary>
    private InstanceLock SideLock() => new InstanceLock(lockFolder.Path);

    /// <summary>
    /// Start-up creates a missing data folder for <c>times.lock</c> (117) and removes the lock on exit, so
    /// after a run that saved nothing the folder is either missing or empty.
    /// </summary>
    private static void AssertLeftNothing(string folder)
    {
        Assert.True(!Directory.Exists(folder) || !Directory.EnumerateFileSystemEntries(folder).Any(),
            $"{folder} holds files");
    }

    private (int ExitCode, string Output) Run(Action<TestConsole> input, CommandRegistry? registry = null, TimeProvider? time = null, string? dataFolder = null)
    {
        TestConsole console = new TestConsole().Interactive();
        input(console);
        int exitCode = new App(console, time ?? FixedTimeProvider.At20261027(), new DayStore(dataFolder ?? data.Path), registry,
            instanceLock: sideLock).Run([]);
        return (exitCode, console.Output);
    }

    private (int ExitCode, string Output) RunCommands(params string[] commands) => RunCommands(null, commands);

    private (int ExitCode, string Output) RunCommands(TimeProvider? time, params string[] commands) =>
        Run(c =>
        {
            foreach (string command in commands)
            {
                c.Input.PushTextWithEnter(command);
            }
        }, time: time);

    /// <summary>The heading of the last drawn frame.</summary>
    private static string LastHeading(string output) =>
        Regex.Matches(output, @"[A-Z][a-z]+day, \d{4}-\d{2}-\d{2}").Last().Value;

    /// <summary>Everything from the last drawn heading on: the last frame.</summary>
    private static string LastFrame(string output) => output[output.LastIndexOf(LastHeading(output), StringComparison.Ordinal)..];

    /// <summary>
    /// Splits the output into full frames (a scroll rewrites rows in place without clearing, so it
    /// starts no frame). Each frame runs from row 1 (right after the clear-screen
    /// sequence: the quicklist panel's top border, or the day heading while help or projects hides
    /// the panel) to the bottom border of the input panel (the <c>╰</c> line right after the
    /// <c>│ > </c> line), split on <c>\n</c>.
    /// </summary>
    private static List<string[]> Frames(string output)
    {
        List<string[]> frames = new List<string[]>();
        List<int> starts = Regex.Matches(output, @"\x1b\[2J(?:\x1b\[3J)?\x1b\[1;1H").Select(m => m.Index + m.Length).ToList();
        foreach (int start in starts)
        {
            string[] lines = output[start..].Split('\n');
            for (int i = 0; i + 1 < lines.Length; i++)
            {
                if (lines[i].StartsWith("│ > ", StringComparison.Ordinal) && lines[i + 1].StartsWith('╰'))
                {
                    string last = lines[i + 1];
                    lines[i + 1] = last[..(last.IndexOf('╯') + 1)];
                    frames.Add(lines[..(i + 2)]);
                    break;
                }
            }
        }

        return frames;
    }

    /// <summary>Asserts the 24-line layout: the hint row at <c>frame[^4]</c>, the input panel on the last 3 lines.</summary>
    private static void AssertLayout(string[] frame)
    {
        Assert.Equal(24, frame.Length);
        Assert.StartsWith("╭", frame[^3]);
        Assert.StartsWith("│ > ", frame[^2]);
        Assert.StartsWith("╰", frame[^1]);
    }

    /// <summary>Asserts that <paramref name="text"/> sits on the hint row and nowhere above it.</summary>
    private static void AssertOnHintRow(string[] frame, string text)
    {
        AssertLayout(frame);
        Assert.Contains(text, frame[^4]);
        Assert.DoesNotContain(frame[..^4], l => l.Contains(text, StringComparison.Ordinal));
    }

    /// <summary>
    /// The hint rows above the input panel, trimmed: from the lowest row above <c>frame[^3]</c> that
    /// contains <paramref name="start"/> (the start of the hint) down to <c>frame[^4]</c>.
    /// </summary>
    private static string[] HintRowsOf(string[] frame, string start)
    {
        int first = Array.FindLastIndex(frame[..^3], l => l.Contains(start, StringComparison.Ordinal));
        Assert.True(first >= 0, $"no hint row holds {start}");
        return frame[first..^3].Select(l => l.Trim()).ToArray();
    }

    /// <summary>The hint rows joined with one space, as the hint reads.</summary>
    private static string HintText(string[] frame, string start) => string.Join(' ', HintRowsOf(frame, start));

    private static int Count(string text, string value)
    {
        int count = 0;
        for (int i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    // AC-1
    [Fact]
    public void Quit_ReturnsZero_AndShowsDayHeadingAndEmptyState()
    {
        (int exitCode, string output) = Run(c => c.Input.PushTextWithEnter("quit"));

        Assert.Equal(0, exitCode);
        Assert.Contains(Heading, output);
        Assert.Contains("No entries yet.", output);
    }

    // AC-2
    [Fact]
    public void StarterOutput_IsGone()
    {
        (_, string output) = Run(c => c.Input.PushTextWithEnter("quit"));

        Assert.DoesNotContain("_____", output); // Figlet "Times" banner
        Assert.DoesNotContain("Zone", output);
        Assert.DoesNotContain("18:30:00", output);
    }

    // AC-3
    [Fact]
    public void InputBox_IsRoundedPanel_BelowDayView_AtBottom()
    {
        (_, string output) = Run(c => c.Input.PushTextWithEnter("quit"));

        int heading = output.IndexOf(Heading, StringComparison.Ordinal);
        int top = output.IndexOf('╭', heading);
        int bottom = output.IndexOf('╰', top);
        Assert.True(heading >= 0 && top > heading && bottom > top);

        // The layout fills the 24-line test terminal; the panel occupies the last 3 lines.
        string[] frame = output[..(output.IndexOf('╯', bottom) + 1)].Split('\n');
        Assert.Equal(24, frame.Length);
        Assert.StartsWith("╭", frame[^3]);
        Assert.StartsWith("│ > ", frame[^2]);
        Assert.StartsWith("╰", frame[^1]);
    }

    // AC-4
    [Fact]
    public void Help_ListsEveryRegisteredCommand_IncludingOnesAddedInTest()
    {
        CommandRegistry registry = CommandRegistry.CreateDefault();
        registry.Register("demo", "dm", "A command from the test", _ => CommandResult.None);

        (int exitCode, string output) = Run(c =>
        {
            c.Input.PushTextWithEnter("help");
            c.Input.PushTextWithEnter("quit");
        }, registry);

        Assert.Equal(0, exitCode);
        Assert.Contains("help", output);
        Assert.Contains("List all commands", output);
        Assert.Contains("quit", output);
        Assert.Contains("Exit Times", output);
        Assert.Contains("demo", output);
        Assert.Contains("A command from the test", output);
    }

    // AC-5
    [Fact]
    public void UnknownCommand_ShowsHint_AndLoopContinues()
    {
        (int exitCode, string output) = Run(c =>
        {
            c.Input.PushTextWithEnter("nope");
            c.Input.PushTextWithEnter("quit");
        });

        Assert.Equal(0, exitCode);
        Assert.Contains("Unknown command nope", output);
        Assert.Contains("Type help to see all commands.", output);
    }

    [Fact]
    public void UnknownCommand_HintIsRed()
    {
        TestConsole console = new TestConsole().Interactive().EmitAnsiSequences();
        console.Input.PushTextWithEnter("nope");
        console.Input.PushTextWithEnter("quit");

        new App(console, FixedTimeProvider.At20261027(), new DayStore(data.Path)).Run([]);

        // Red foreground: SGR 31 (standard) or 91 (bright) or 38;5;9.
        Assert.Matches(@"\x1b\[(31|91|38;5;9)(;1)?m(\x1b\[[0-9;]*m)*Unknown command", console.Output);
    }

    // AC-6
    [Theory]
    [InlineData("/[bad]", "Unknown command /[bad]")]
    [InlineData("/[red]x[/]", "Unknown command /[red]x[/]")]
    public void MarkupInCommandName_IsShownLiterally(string typed, string expected)
    {
        (int exitCode, string output) = Run(c =>
        {
            c.Input.PushTextWithEnter(typed);
            c.Input.PushTextWithEnter("quit");
        });

        Assert.Equal(0, exitCode);
        Assert.Contains(expected, output);
    }

    [Fact]
    public void MarkupInNonCommandInput_DoesNotThrow()
    {
        (int exitCode, string output) = Run(c =>
        {
            c.Input.PushTextWithEnter("[red]x");
            c.Input.PushTextWithEnter("quit");
        });

        Assert.Equal(0, exitCode);
        Assert.Contains("Unknown command [red]x. Type help to see all commands.", output);
    }

    [Fact]
    public void HandlerException_ShowsRedHint_NotStackTrace()
    {
        CommandRegistry registry = CommandRegistry.CreateDefault();
        registry.Register("boom", "bm", "Throws", _ => throw new InvalidOperationException("bad [thing]"));

        (int exitCode, string output) = Run(c =>
        {
            c.Input.PushTextWithEnter("boom");
            c.Input.PushTextWithEnter("quit");
        }, registry);

        Assert.Equal(0, exitCode);
        Assert.Contains("boom failed: bad [thing]", output);
        Assert.DoesNotContain("   at ", output);
    }

    // AC-7
    [Fact]
    public void UnknownWord_ShowsHint_AndLoopContinues()
    {
        (int exitCode, string output) = Run(c =>
        {
            c.Input.PushTextWithEnter("hello");
            c.Input.PushTextWithEnter("quit");
        });

        Assert.Equal(0, exitCode);
        Assert.Contains("Unknown command hello. Type help to see all commands.", output);
    }

    [Fact]
    public void EmptyInput_ShowsNoHint_AndLoopContinues()
    {
        (int exitCode, string output) = Run(c =>
        {
            c.Input.PushTextWithEnter("");
            c.Input.PushTextWithEnter("   ");
            c.Input.PushTextWithEnter("quit");
        });

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain("Commands start with", output);
        Assert.DoesNotContain("Unknown command", output);
        Assert.Equal(3, Count(output, Heading));
    }

    // AC-8
    [Fact]
    public void CommandNames_MatchCaseInsensitively()
    {
        (int exitCode, _) = Run(c => c.Input.PushTextWithEnter("QUIT"));

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public void TextAfterName_IsPassedToHandler()
    {
        string? received = null;
        CommandRegistry registry = CommandRegistry.CreateDefault();
        registry.Register("echo", "ec", "Echo", a => { received = a; return CommandResult.None; });

        Run(c =>
        {
            c.Input.PushTextWithEnter("Echo  hello [world] ");
            c.Input.PushTextWithEnter("quit");
        }, registry);

        Assert.Equal("hello [world]", received);
    }

    // AC-9
    [Fact]
    public void Screen_IsRedrawnAfterEachCommand()
    {
        (_, string output) = Run(c =>
        {
            c.Input.PushTextWithEnter("nope");
            c.Input.PushTextWithEnter("quit");
        });

        Assert.True(Count(output, Heading) >= 2);
    }

    [Fact]
    public void Message_StaysInTopArea_UntilNextCommand()
    {
        (_, string output) = Run(c =>
        {
            c.Input.PushTextWithEnter("help");
            c.Input.PushTextWithEnter("nope");
            c.Input.PushTextWithEnter("");
            c.Input.PushTextWithEnter("quit");
        });

        string[] frames = output.Split(Heading).Skip(1).ToArray();
        Assert.Equal(4, frames.Length);
        Assert.Contains(HelpFirstRow, frames[1]);           // after help
        Assert.DoesNotContain(HelpFirstRow, frames[2]);     // replaced by the hint
        Assert.Contains("Unknown command nope", frames[2]);
        Assert.DoesNotContain("Unknown command", frames[3]);       // cleared by the empty line

        // The message sits above the input panel.
        Assert.True(frames[1].IndexOf(HelpFirstRow, StringComparison.Ordinal) < frames[1].IndexOf("│ > ", StringComparison.Ordinal));

        // Job 004: help stays in the top area and the hint row stays blank (AC-6);
        // the hint is on the hint row and gone after the next command (AC-7).
        List<string[]> lines = Frames(output);
        Assert.Equal(4, lines.Count);
        AssertLayout(lines[1]);
        Assert.Contains(lines[1][..^4], l => l.Contains(HelpFirstRow, StringComparison.Ordinal));
        Assert.True(string.IsNullOrWhiteSpace(lines[1][^4]));
        AssertOnHintRow(lines[2], "Unknown command nope");
        AssertLayout(lines[3]);
        Assert.True(string.IsNullOrWhiteSpace(lines[3][^4]));
    }

    // Hint position (job 004) -------------------------------------------------------------

    // AC-1, AC-3
    [Fact]
    public void UnknownCommand_HintSitsDirectlyAboveInputBox()
    {
        (_, string output) = RunCommands("nope", "quit");

        string[] frame = Frames(output)[1];
        AssertOnHintRow(frame, "Unknown command nope");
        Assert.DoesNotContain(frame[..^4], l => l.Contains("Unknown command", StringComparison.Ordinal));
        Assert.Contains(Heading, frame[EmptyPanelRows]);
    }

    // AC-2, AC-3
    [Theory]
    [InlineData("hello", "Unknown command hello.")]
    [InlineData("next x", "next takes no arguments.")]
    [InlineData("prev [red]", "prev takes no arguments.")]
    public void Hints_SitDirectlyAboveInputBox(string typed, string expected)
    {
        (int exitCode, string output) = RunCommands(typed, "quit");

        Assert.Equal(0, exitCode);
        string[] frame = Frames(output)[1];
        AssertOnHintRow(frame, expected);
        Assert.Contains(Heading, frame[EmptyPanelRows]);
    }

    // AC-2, AC-3
    [Fact]
    public void BoundaryHint_SitsDirectlyAboveInputBox()
    {
        (int exitCode, string output) =
            RunCommands(new SettableTimeProvider(DateTimeOffset.MaxValue), "next", "quit");

        Assert.Equal(0, exitCode);
        string[] frame = Frames(output)[1];
        AssertOnHintRow(frame, "There is no day after this one.");
        Assert.Contains("Friday, 9999-12-31", frame[EmptyPanelRows]);
    }

    // AC-2, AC-3
    [Fact]
    public void HandlerExceptionHint_SitsDirectlyAboveInputBox()
    {
        CommandRegistry registry = CommandRegistry.CreateDefault();
        registry.Register("boom", "bm", "Throws", _ => throw new InvalidOperationException("bad [thing]"));

        (int exitCode, string output) = Run(c =>
        {
            c.Input.PushTextWithEnter("boom");
            c.Input.PushTextWithEnter("quit");
        }, registry);

        Assert.Equal(0, exitCode);
        string[] frame = Frames(output)[1];
        AssertOnHintRow(frame, "boom failed: bad [thing]");
        Assert.Contains(Heading, frame[EmptyPanelRows]);
    }

    // AC-4
    [Fact]
    public void HintRow_IsBlank_WhenThereIsNoHint()
    {
        (_, string output) = RunCommands("nope", "", "quit");

        List<string[]> frames = Frames(output);
        Assert.Equal(3, frames.Count);
        foreach (string[] frame in new[] { frames[0], frames[2] })
        {
            AssertLayout(frame);
            Assert.True(string.IsNullOrWhiteSpace(frame[^4]));
            Assert.Contains(Heading, frame[EmptyPanelRows]);
        }

        Assert.DoesNotContain(frames[2], l => l.Contains("Unknown command", StringComparison.Ordinal));
    }

    // AC-7
    [Fact]
    public void Hint_IsReplacedByHelp_OnNextCommand()
    {
        (_, string output) = RunCommands("nope", "help", "quit");

        string[] frame = Frames(output)[2];
        AssertLayout(frame);
        Assert.Contains(frame[..^4], l => l.Contains(HelpFirstRow, StringComparison.Ordinal));
        Assert.True(string.IsNullOrWhiteSpace(frame[^4]));
        Assert.DoesNotContain(frame, l => l.Contains("Unknown command", StringComparison.Ordinal));
    }

    // AC-8; since job 096 a long hint wraps onto at most 3 rows.
    [Fact]
    public void LongHint_IsCroppedToThreeRows_AndDoesNotMoveInputBox()
    {
        CommandRegistry registry = CommandRegistry.CreateDefault();
        string text = string.Concat(Enumerable.Repeat("word ", 60)); // 300 characters
        registry.Register("long", "lo", "Long hint", _ => CommandResult.Error(Markup.Escape(text)));

        (int exitCode, string output) = Run(c =>
        {
            c.Input.PushTextWithEnter("long");
            c.Input.PushTextWithEnter("quit");
        }, registry);

        Assert.Equal(0, exitCode);
        string[] frame = Frames(output)[1];
        AssertLayout(frame);
        Assert.All(frame[^6..^3], l => Assert.StartsWith("word", l));
        Assert.EndsWith("\u2026", frame[^4].TrimEnd());
        Assert.DoesNotContain(frame[..^6], l => l.Contains("word", StringComparison.Ordinal));
        Assert.Contains(Heading, frame[EmptyPanelRows]);
    }

    // AC-10
    [Fact]
    public void Backspace_EditsTheLine()
    {
        (int exitCode, string output) = Run(c =>
        {
            c.Input.PushText("quiz");
            c.Input.PushKey(ConsoleKey.Backspace);
            c.Input.PushTextWithEnter("t");
        });

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain("Unknown command", output);
    }

    [Fact]
    public void Escape_ClearsTheLine()
    {
        (int exitCode, string output) = Run(c =>
        {
            c.Input.PushText("abc");
            c.Input.PushKey(ConsoleKey.Escape); // closes the popup
            c.Input.PushKey(ConsoleKey.Escape); // clears the line
            c.Input.PushTextWithEnter("quit");
        });

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain("Commands start with", output);
        Assert.DoesNotContain("Unknown command", output);
    }

    // AC-11
    [Fact]
    public void Tab_CompletesCommand()
    {
        (int exitCode, string output) = Run(c =>
        {
            c.Input.PushText("q");
            c.Input.PushKey(ConsoleKey.Tab);
            c.Input.PushKey(ConsoleKey.Enter);
        });

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain("Unknown command", output);
    }

    // AC-12
    [Fact]
    public void UpArrow_RecallsPreviousCommand()
    {
        (int exitCode, string output) = Run(c =>
        {
            c.Input.PushTextWithEnter("help");
            c.Input.PushKey(ConsoleKey.UpArrow);
            c.Input.PushKey(ConsoleKey.Enter);
            c.Input.PushTextWithEnter("quit");
        });

        Assert.Equal(0, exitCode);
        string[] frames = output.Split(Heading).Skip(1).ToArray();
        Assert.Equal(3, frames.Length);
        Assert.Contains(HelpFirstRow, frames[1]);
        Assert.Contains(HelpFirstRow, frames[2]);
    }

    // Day navigation (job 002) ------------------------------------------------------------

    // AC-1
    [Fact]
    public void Prev_MovesToPreviousDay()
    {
        (int exitCode, string output) = RunCommands("prev", "quit");

        Assert.Equal(0, exitCode);
        Assert.Equal("Monday, 2026-10-26", LastHeading(output));
    }

    // AC-2
    [Fact]
    public void Next_MovesToNextDay()
    {
        (int exitCode, string output) = RunCommands("next", "quit");

        Assert.Equal(0, exitCode);
        Assert.Equal("Wednesday, 2026-10-28", LastHeading(output));
    }

    // AC-3
    [Fact]
    public void Today_JumpsBackToToday()
    {
        (int exitCode, string output) = RunCommands("prev", "prev", "today", "quit");

        Assert.Equal(0, exitCode);
        Assert.Contains("Sunday, 2026-10-25", output);
        Assert.Equal(Heading, LastHeading(output));
    }

    // AC-4
    [Theory]
    [InlineData(2026, 1, 1, "prev", "Wednesday, 2025-12-31")]
    [InlineData(2026, 2, 28, "next", "Sunday, 2026-03-01")]
    public void Navigation_CrossesMonthAndYearBoundaries(int year, int month, int day, string command, string expected)
    {
        SettableTimeProvider time =
            new SettableTimeProvider(new DateTimeOffset(year, month, day, 12, 0, 0, TimeSpan.Zero));

        (int exitCode, string output) = RunCommands(time, command, "quit");

        Assert.Equal(0, exitCode);
        Assert.Equal(expected, LastHeading(output));
    }

    // AC-5
    [Fact]
    public void Today_UsesTheClockWhenTheCommandRuns()
    {
        SettableTimeProvider time = new SettableTimeProvider(new DateTimeOffset(2026, 10, 27, 23, 0, 0, TimeSpan.Zero));
        CommandRegistry registry = CommandRegistry.CreateDefault();
        registry.Register("tick", "tk", "Advance the test clock", _ =>
        {
            time.UtcNow = time.UtcNow.AddHours(2);
            return CommandResult.None;
        });

        (int exitCode, string output) = Run(c =>
        {
            c.Input.PushTextWithEnter("tick");
            c.Input.PushTextWithEnter("today");
            c.Input.PushTextWithEnter("quit");
        }, registry, time);

        Assert.Equal(0, exitCode);
        Assert.Equal("Wednesday, 2026-10-28", LastHeading(output));
    }

    // AC-6
    [Fact]
    public void DayWithoutFile_ShowsHeadingAndEmptyState()
    {
        (_, string output) = RunCommands("next", "quit");

        string frame = LastFrame(output);
        Assert.StartsWith("Wednesday, 2026-10-28", frame);
        Assert.Contains("No entries yet.", frame);
    }

    // AC-7
    [Fact]
    public void Browsing_LeavesExistingFolderEmpty()
    {
        (int exitCode, _) = RunCommands("prev", "next", "next", "today", "quit");

        Assert.Equal(0, exitCode);
        Assert.True(Directory.Exists(data.Path));
        Assert.Empty(Directory.EnumerateFileSystemEntries(data.Path));
    }

    // AC-7
    [Fact]
    public void Browsing_DoesNotCreateMissingFolder()
    {
        using TempFolder missing = new TempFolder(create: false);

        (int exitCode, _) = Run(c =>
        {
            foreach (string command in new[] { "prev", "next", "next", "today", "quit" })
            {
                c.Input.PushTextWithEnter(command);
            }
        }, dataFolder: missing.Path);

        Assert.Equal(0, exitCode);
        AssertLeftNothing(missing.Path);
    }

    // AC-8
    [Fact]
    public void Help_ListsNavigationCommands()
    {
        (_, string output) = Run(c =>
        {
            c.Input.PushTextWithEnter("help");
            PageDown(c, HelpPages);
            c.Input.PushTextWithEnter("quit");
        });

        Assert.Contains("prev", output);
        Assert.Contains("Go to the previous day", output);
        Assert.Contains("next", output);
        Assert.Contains("Go to the next day", output);
        Assert.Contains("today", output);
        Assert.Contains("Go back to today", output);
    }

    // AC-8
    [Fact]
    public void NavigationCommands_MatchCaseInsensitively()
    {
        (int exitCode, string output) = RunCommands("NEXT", "quit");

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain("Unknown command", output);
        Assert.Equal("Wednesday, 2026-10-28", LastHeading(output));
    }

    // AC-8
    [Theory]
    [InlineData("pre", "Monday, 2026-10-26")]
    [InlineData("n", "Wednesday, 2026-10-28")]
    [InlineData("t", Heading)]
    public void Tab_CompletesNavigationCommands(string prefix, string expected)
    {
        (int exitCode, string output) = Run(c =>
        {
            if (prefix == "t")
            {
                c.Input.PushTextWithEnter("next"); // so today has somewhere to come back from
            }

            c.Input.PushText(prefix);
            c.Input.PushKey(ConsoleKey.Tab);
            c.Input.PushKey(ConsoleKey.Enter);
            c.Input.PushTextWithEnter("quit");
        });

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain("Unknown command", output);
        Assert.Equal(expected, LastHeading(output));
    }

    // AC-9
    [Theory]
    [InlineData("next 3", "next")]
    [InlineData("prev x", "prev")]
    [InlineData("today [red]", "today")]
    public void NavigationWithArgument_ShowsHint_AndKeepsDay(string typed, string name)
    {
        (int exitCode, string output) = RunCommands(typed, "quit");

        Assert.Equal(0, exitCode);
        Assert.Equal(Heading, LastHeading(output));
        Assert.Contains($"{name} takes no arguments.", LastFrame(output));
    }

    // AC-9
    [Fact]
    public void NavigationWithArgument_HintIsRed_AndLoopContinues()
    {
        TestConsole console = new TestConsole().Interactive().EmitAnsiSequences();
        console.Input.PushTextWithEnter("next 3");
        console.Input.PushTextWithEnter("prev");
        console.Input.PushTextWithEnter("quit");

        int exitCode = new App(console, FixedTimeProvider.At20261027(), new DayStore(data.Path)).Run([]);

        Assert.Equal(0, exitCode);
        Assert.Matches(@"\x1b\[(1;)?(31|91|38;5;9)(;1)?m(\x1b\[[0-9;]*m)*next", console.Output);
        Assert.Matches(@"\x1b\[(31|91|38;5;9)m takes no arguments\.", console.Output);
        Assert.Contains("takes no arguments.", console.Output);
        Assert.Contains("Monday, 2026-10-26", console.Output); // the loop went on to prev
    }

    // AC-10
    [Fact]
    public void Prev_AtMinValue_ShowsHint_AndStaysPut()
    {
        (int exitCode, string output) =
            RunCommands(new SettableTimeProvider(DateTimeOffset.MinValue), "prev", "quit");

        Assert.Equal(0, exitCode);
        Assert.Equal("Monday, 0001-01-01", LastHeading(output));
        Assert.Contains("There is no day before this one.", LastFrame(output));
        Assert.DoesNotContain("   at ", output);
        Assert.DoesNotContain("failed", output);
    }

    // AC-10
    [Fact]
    public void Next_AtMaxValue_ShowsHint_AndStaysPut()
    {
        (int exitCode, string output) =
            RunCommands(new SettableTimeProvider(DateTimeOffset.MaxValue), "next", "quit");

        Assert.Equal(0, exitCode);
        Assert.Equal("Friday, 9999-12-31", LastHeading(output));
        Assert.Contains("There is no day after this one.", LastFrame(output));
        Assert.DoesNotContain("   at ", output);
        Assert.DoesNotContain("failed", output);
    }

    // AC-10
    [Fact]
    public void BoundaryHint_IsRed()
    {
        TestConsole console = new TestConsole().Interactive().EmitAnsiSequences();
        console.Input.PushTextWithEnter("next");
        console.Input.PushTextWithEnter("quit");

        new App(console, new SettableTimeProvider(DateTimeOffset.MaxValue), new DayStore(data.Path)).Run([]);

        Assert.Matches(@"\x1b\[(31|91|38;5;9)(;1)?m(\x1b\[[0-9;]*m)*There is no day after this one\.", console.Output);
    }

    // Command popup (job 006) --------------------------------------------------------------

    private const string ClearScreen = "\u001b[2J";

    private string RunAnsi(Action<TestConsole> input, CommandRegistry? registry = null)
    {
        TestConsole console = new TestConsole().Interactive().EmitAnsiSequences();
        input(console);
        Assert.Equal(0, new App(console, FixedTimeProvider.At20261027(), new DayStore(data.Path), registry, instanceLock: sideLock).Run([]));
        return console.Output;
    }

    /// <summary>Each full frame (written at 1;1 right after a clear), split into its 24 screen rows.</summary>
    private static List<string[]> AnsiFrames(string output) =>
        AnsiWrites.Parse(output)
            .Where(w => w.Row == 1 && w.Column == 1 && w.Text.Contains('╰'))
            .Select(w => w.Text.Split('\n'))
            .ToList();

    /// <summary>The writes at column 1 between frame <paramref name="index"/> and the next one: what the editor drew while typing.</summary>
    private static List<AnsiWrite> TypingWrites(string output, int index)
    {
        List<AnsiWrite> writes = AnsiWrites.Parse(output);
        List<int> frameStarts = writes
            .Select((w, i) => (w, i))
            .Where(x => x.w.Row == 1 && x.w.Column == 1 && x.w.Text.Contains('╰'))
            .Select(x => x.i)
            .ToList();
        int end = index + 1 < frameStarts.Count ? frameStarts[index + 1] : writes.Count;
        return writes.Skip(frameStarts[index] + 1).Take(end - frameStarts[index] - 1).Where(w => w.Column == 1).ToList();
    }

    private static CommandRegistry ManyCommands()
    {
        CommandRegistry registry = CommandRegistry.CreateDefault();
        for (int i = 1; i <= 12; i++)
        {
            registry.Register($"c{i:00}", $"x{(char)('a' + i)}", $"Dummy command {i}", _ => CommandResult.None);
        }

        return registry;
    }

    // AC-3
    [Fact]
    public void Slash_OpensPopup_OnRowsEndingAtHintRow()
    {
        string output = RunAnsi(c =>
        {
            c.Input.PushText("/");
            c.Input.PushKey(ConsoleKey.Backspace);
            c.Input.PushTextWithEnter("quit");
        });

        Assert.Contains("\u001b[21;1H", output);
        List<AnsiWrite> popup = TypingWrites(output, 0).Take(8).ToList();
        Assert.Equal([14, 15, 16, 17, 18, 19, 20, 21], popup.Select(w => w.Row));
        // Thirty-one commands: the first seven by name, then a row counting the other twenty-four.
        Assert.Equal(
            ["absence", "addproject", "arrive", "backup", "booked", "break", "breakend", "…"],
            popup.Select(w => w.Text.Split(' ')[0]));
        Assert.StartsWith("… 26 more", popup[^1].Text);
        Assert.Contains("Book the end of a break", popup[6].Text);
        Assert.Equal(80, popup[0].Text.Length);
    }

    // AC-4
    [Fact]
    public void Typo_ShowsTodayFirst()
    {
        string output = RunAnsi(c =>
        {
            c.Input.PushTextWithEnter("tdoay");
            c.Input.PushTextWithEnter("quit");
        });

        List<AnsiWrite> writes = TypingWrites(output, 0);
        AnsiWrite lastPopupRow = writes.Last(w => w.Row == 21 && w.StartsWithCommand);
        Assert.StartsWith("today", lastPopupRow.Text);
    }

    // AC-4, AC-5
    [Fact]
    public void ClosedOrShrunkPopup_RestoresTheFramesRows()
    {
        // help fills the top area with a long table, so the popup covers real content.
        string output = RunAnsi(c =>
        {
            c.Input.PushTextWithEnter("help");
            c.Input.PushText("c");
            c.Input.PushKey(ConsoleKey.Backspace);
            c.Input.PushKey(ConsoleKey.Backspace);
            c.Input.PushKey(ConsoleKey.Enter);
            c.Input.PushTextWithEnter("quit");
        }, ManyCommands());

        string[] frame = AnsiFrames(output)[1];
        List<AnsiWrite> writes = TypingWrites(output, 1);

        // "/" and "c" both fill 8 rows (14-21); Backspace to empty gives every row back.
        // "c" is the alias of change, so change is listed first (041).
        Assert.Contains(writes, w => w.Row == 14 && w.Text.StartsWith("change", StringComparison.Ordinal));
        Dictionary<int, string> screen = [];
        foreach (AnsiWrite write in writes)
        {
            screen[write.Row] = write.Text;
        }

        for (int row = 14; row <= 21; row++)
        {
            Assert.Equal(frame[row - 1], screen[row]);
        }

        Assert.Contains("│", screen[14]);
    }

    // AC-5
    [Fact]
    public void ClosingPopup_BringsBackTheRedHint()
    {
        string output = RunAnsi(c =>
        {
            c.Input.PushTextWithEnter("nope");
            c.Input.PushText("/");
            c.Input.PushKey(ConsoleKey.Backspace);
            c.Input.PushText("/");
            c.Input.PushKey(ConsoleKey.Escape);
            c.Input.PushKey(ConsoleKey.Escape);
            c.Input.PushTextWithEnter("quit");
        });

        List<string> hintRow = TypingWrites(output, 1).Where(w => w.Row == 21).Select(w => w.Text).ToList();

        // popup, restored (Backspace), popup, restored (Esc), popup (quit typing), ...
        Assert.StartsWith("… 26 more", hintRow[0]);
        Assert.Contains("Unknown command nope", hintRow[1]);
        Assert.StartsWith("… 26 more", hintRow[2]);
        Assert.Contains("Unknown command nope", hintRow[3]);
        Assert.Contains("Unknown command nope", hintRow[^1]);
        Assert.Contains("\u001b[38;5;9mUnknown command", output[output.LastIndexOf("\u001b[21;1H", StringComparison.Ordinal)..]);
    }

    // AC-5
    [Fact]
    public void Enter_StillDispatchesTheTypedLine()
    {
        string output = RunAnsi(c =>
        {
            c.Input.PushTextWithEnter("prev");
            c.Input.PushTextWithEnter("quit");
        });

        Assert.Contains("Monday, 2026-10-26", AnsiFrames(output)[1][EmptyPanelRows]);
    }

    // AC-7
    [Fact]
    public void ManyMatches_PopupShowsEightRows_AndMore()
    {
        string output = RunAnsi(c =>
        {
            c.Input.PushText("/");
            c.Input.PushKey(ConsoleKey.Escape);
            c.Input.PushKey(ConsoleKey.Escape);
            c.Input.PushTextWithEnter("quit");
        }, ManyCommands());

        List<AnsiWrite> popup = TypingWrites(output, 0).Take(8).ToList();
        Assert.Equal(Enumerable.Range(14, 8), popup.Select(w => w.Row));
        // 39 commands: 7 shown, the eighth row counts the other 32.
        Assert.StartsWith("… 38 more", popup[^1].Text);
        // Nothing is drawn on row 1 while typing (the final clear on quit homes the cursor there, writing nothing).
        Assert.DoesNotContain(TypingWrites(output, 0), w => w.Row == 1 && w.Text.Length > 0);
    }

    // AC-9
    [Fact]
    public void Typing_NeverClearsOrRedrawsTheLayout()
    {
        string output = RunAnsi(c =>
        {
            c.Input.PushText("tdoay");
            c.Input.PushKey(ConsoleKey.Backspace);
            c.Input.PushKey(ConsoleKey.Tab);
            c.Input.PushKey(ConsoleKey.Escape);
            c.Input.PushKey(ConsoleKey.Escape);
            c.Input.PushText("he");
            c.Input.PushKey(ConsoleKey.Tab);
            c.Input.PushKey(ConsoleKey.Enter);
            c.Input.PushTextWithEnter("quit");
        });

        // One clear per frame (start, after help) plus one on quit; one heading per frame.
        Assert.Equal(3, Count(output, ClearScreen));
        Assert.Equal(2, AnsiFrames(output).Count);
        Assert.Equal(2, Count(output, Heading));
    }

    // Arrival and leaving (job 008) --------------------------------------------------------

    private string DayFile(string date) => Path.Combine(data.Path, date + ".json");

    private string ReadDay(string date) => File.ReadAllText(DayFile(date));

    /// <summary>The last drawn frame, split into its 24 screen rows.</summary>
    private static string[] LastLayout(string output) => Frames(output)[^1];

    private static string[] WorkingDirectoryEntries() =>
        Directory.EnumerateFileSystemEntries(Directory.GetCurrentDirectory()).Order(StringComparer.Ordinal).ToArray();

    /// <summary>A day file in the entries format, e.g. <c>Entries("arrival 08:15", "leaving 12:00")</c>.</summary>
    private static string Entries(params string[] entries) =>
        "{\"entries\":[" + string.Join(',', entries.Select(e =>
        {
            string[] parts = e.Split(' ');
            return $"{{\"kind\":\"{parts[0]}\",\"time\":\"{parts[1]}\"}}";
        })) + "]}";

    // AC-1
    [Fact]
    public void Arrive_WithTime_CreatesDayFile_AndShowsIt()
    {
        (int exitCode, string output) = RunCommands("arrive 8:15", "quit");

        Assert.Equal(0, exitCode);
        Assert.Equal(["2026-10-27.json"], Directory.EnumerateFiles(data.Path).Select(Path.GetFileName));
        Assert.Equal(Entries("arrival 08:15"), ReadDay("2026-10-27"));
        string frame = LastFrame(output);
        Assert.Matches(@"1 +AR +08:15", frame);
        Assert.DoesNotContain(" LE ", frame);
        Assert.DoesNotContain("No entries yet.", frame);
    }

    // AC-2
    [Fact]
    public void Leave_WithTime_StoresLeaving_AndShowsIt()
    {
        (int exitCode, string output) = RunCommands("leave 17:30", "quit");

        Assert.Equal(0, exitCode);
        Assert.Equal(Entries("leaving 17:30"), ReadDay("2026-10-27"));
        Assert.Matches(@"1 +LE +17:30", LastFrame(output));
    }

    // AC-2
    [Theory]
    [InlineData("arrive", "{\"entries\":[{\"kind\":\"arrival\",\"time\":\"18:30\"}]}")]
    [InlineData("leave", "{\"entries\":[{\"kind\":\"leaving\",\"time\":\"18:30\"}]}")]
    public void WithoutTime_BooksClockTime_SecondsDropped(string command, string expected)
    {
        FixedTimeProvider clock =
            new FixedTimeProvider(new DateTimeOffset(2026, 10, 27, 18, 30, 59, TimeSpan.Zero), TimeZoneInfo.Utc);

        (int exitCode, string output) = RunCommands(clock, command, "quit");

        Assert.Equal(0, exitCode);
        Assert.Equal(expected, ReadDay("2026-10-27"));
        Assert.Contains("18:30", LastFrame(output));
    }

    // AC-2
    [Fact]
    public void WithoutTime_UsesLocalWallClock()
    {
        // 16:30 UTC is 18:30 in UTC+2.
        TimeZoneInfo zone = TimeZoneInfo.CreateCustomTimeZone("Plus2", TimeSpan.FromHours(2), "Plus2", "Plus2");
        FixedTimeProvider clock = new FixedTimeProvider(new DateTimeOffset(2026, 10, 27, 16, 30, 0, TimeSpan.Zero), zone);

        RunCommands(clock, "arrive", "quit");

        Assert.Equal(Entries("arrival 18:30"), ReadDay("2026-10-27"));
    }

    // AC-3
    [Fact]
    public void Booking_GoesToTheViewedDay()
    {
        (int exitCode, string output) = RunCommands("prev", "arrive 9:00", "quit");

        Assert.Equal(0, exitCode);
        Assert.Equal(["2026-10-26.json"], Directory.EnumerateFiles(data.Path).Select(Path.GetFileName));
        Assert.Equal(Entries("arrival 09:00"), ReadDay("2026-10-26"));
        Assert.StartsWith("Monday, 2026-10-26", LastFrame(output));
        Assert.Matches(@"1 +AR +09:00", LastFrame(output));
    }

    // AC-2 (017)
    [Theory]
    [InlineData("arrive", "arrival")]
    [InlineData("leave", "leaving")]
    public void BookingAgain_AddsAnEntry_AndKeepsTheEarlierOne(string command, string kind)
    {
        (int exitCode, string output) = RunCommands(command + " 8:15", command + " 13:00", "quit");

        Assert.Equal(0, exitCode);
        Assert.Equal(Entries(kind + " 08:15", kind + " 13:00"), ReadDay("2026-10-27"));
        string frame = LastFrame(output);
        string label = kind == "arrival" ? "AR" : "LE";
        Assert.Matches($@"1 +{label} +08:15", frame);
        Assert.Matches($@"2 +{label} +13:00", frame);
    }

    // AC-2 (017)
    [Fact]
    public void Booking_KeepsAllEntries_AndReopenedAppLoadsThem()
    {
        RunCommands("arrive 8:15", "leave 12:00", "arrive 13:00", "leave 17:30", "quit");

        Assert.Equal(
            Entries("arrival 08:15", "leaving 12:00", "arrival 13:00", "leaving 17:30"),
            ReadDay("2026-10-27"));

        (int exitCode, string output) = RunCommands("quit");
        Assert.Equal(0, exitCode);
        Assert.Matches(@"1 +AR +08:15", output);
        Assert.Matches(@"2 +LE +12:00", output);
        Assert.Matches(@"3 +AR +13:00", output);
        Assert.Matches(@"4 +LE +17:30", output);
    }

    // AC-6 (019)
    [Fact]
    public void ArriveAndLeave_ShowTimeLeftPerEntry()
    {
        (int exitCode, string output) = RunCommands("arrive 8:00", "leave 17:00", "quit");

        Assert.Equal(0, exitCode);
        string frame = LastFrame(output);
        Assert.Contains("left 8:00", frame);
        Assert.Contains("left -1:00", frame);
        Assert.Matches(@"1 +AR +08:00 +left 8:00", frame);
        Assert.Matches(@"2 +LE +17:00 +left -1:00", frame);
    }

    // AC-3 (017)
    [Fact]
    public void Booking_KeepsEntriesInTimeOrder_WhateverTheBookingOrder()
    {
        (_, string output) = RunCommands("leave 17:00", "arrive 8:00", "quit");

        Assert.Equal(Entries("arrival 08:00", "leaving 17:00"), ReadDay("2026-10-27"));
        string frame = LastFrame(output);
        Assert.Matches(@"1 +AR +08:00", frame);
        Assert.Matches(@"2 +LE +17:00", frame);
    }

    // AC-3 (017)
    [Fact]
    public void Booking_SameTime_KeepsBookingOrder_AndAllowsDuplicates()
    {
        RunCommands("leave 12:00", "arrive 12:00", "arrive 12:00", "quit");

        Assert.Equal(Entries("leaving 12:00", "arrival 12:00", "arrival 12:00"), ReadDay("2026-10-27"));
    }

    // AC-6
    [Theory]
    [InlineData("arrive 25:00", "arrive: 25:00 is not a time.")]
    [InlineData("leave abc", "leave: abc is not a time.")]
    [InlineData("arrive [bold]", "arrive: [bold] is not a time.")]
    public void InvalidTime_ShowsHintOnHintRow_AndWritesNothing(string typed, string expected)
    {
        (int exitCode, string output) = RunCommands(typed, "help", typed, "quit");

        Assert.Equal(0, exitCode);
        string[] frame = LastLayout(output);
        AssertOnHintRow(frame, expected);
        AssertOnHintRow(frame, "Use H:mm, now, -<n>m or +<n>m.");
        Assert.Empty(Directory.EnumerateFileSystemEntries(data.Path));
    }

    // AC-6
    [Fact]
    public void InvalidTime_LeavesExistingFileUnchanged()
    {
        File.WriteAllText(DayFile("2026-10-27"), "{\"arrival\":\"08:00\"}");
        byte[] before = File.ReadAllBytes(DayFile("2026-10-27"));

        (int exitCode, _) = RunCommands("arrive 25:00", "leave abc", "arrive [bold]", "quit");

        Assert.Equal(0, exitCode);
        Assert.Equal(before, File.ReadAllBytes(DayFile("2026-10-27")));
    }

    // AC-6
    [Fact]
    public void InvalidTime_HintIsRed()
    {
        TestConsole console = new TestConsole().Interactive().EmitAnsiSequences();
        console.Input.PushTextWithEnter("arrive 25:00");
        console.Input.PushTextWithEnter("quit");

        new App(console, FixedTimeProvider.At20261027(), new DayStore(data.Path)).Run([]);

        Assert.Matches(@"\x1b\[(31|91|38;5;9)marrive: ", console.Output);
    }

    // AC-4 (017)
    [Theory]
    [InlineData("arrive 9:00", "leave 8:00", "leaving 08:00", "arrival 09:00")]
    [InlineData("arrive 9:00", "leave 9:00", "arrival 09:00", "leaving 09:00")]
    [InlineData("leave 17:00", "arrive 18:00", "leaving 17:00", "arrival 18:00")]
    [InlineData("leave 17:00", "arrive 17:00", "leaving 17:00", "arrival 17:00")]
    [InlineData("arrive 8:00", "arrive 9:00", "arrival 08:00", "arrival 09:00")]
    [InlineData("leave 12:00", "leave 17:00", "leaving 12:00", "leaving 17:00")]
    public void AnyOrder_IsAccepted_WithoutHint(string first, string second, string entry1, string entry2)
    {
        (int exitCode, string output) = RunCommands(first, second, "quit");

        Assert.Equal(0, exitCode);
        Assert.Equal(Entries(entry1, entry2), ReadDay("2026-10-27"));
        string[] frame = LastLayout(output);
        AssertLayout(frame);
        Assert.Equal(string.Empty, frame[^4].Trim());
        Assert.DoesNotContain("must be after", output);
    }

    // AC-4 (017)
    [Fact]
    public void LeavingWithoutArrival_IsAllowed()
    {
        (_, string output) = RunCommands("leave 0:00", "quit");

        Assert.Equal(Entries("leaving 00:00"), ReadDay("2026-10-27"));
        Assert.DoesNotContain("must be after", output);
    }

    // AC-6, AC-7 (017)
    [Fact]
    public void LegacyFile_IsShownAsEntries_AndBookingConvertsIt()
    {
        File.WriteAllText(DayFile("2026-10-27"), "{\"arrival\":\"08:15\",\"leaving\":\"17:30\"}");

        (int exitCode, string output) = RunCommands("arrive 13:00", "quit");

        Assert.Equal(0, exitCode);
        string first = string.Join('\n', Frames(output)[0]);
        Assert.Matches(@"1 +AR +08:15", first);
        Assert.Matches(@"2 +LE +17:30", first);
        Assert.Equal(Entries("arrival 08:15", "arrival 13:00", "leaving 17:30"), ReadDay("2026-10-27"));
        string frame = LastFrame(output);
        Assert.Matches(@"1 +AR +08:15", frame);
        Assert.Matches(@"2 +AR +13:00", frame);
        Assert.Matches(@"3 +LE +17:30", frame);
    }

    // AC-6 (017)
    [Fact]
    public void LegacyFile_LeavingBeforeArrival_IsShownInTimeOrder()
    {
        File.WriteAllText(DayFile("2026-10-27"), "{\"arrival\":\"09:00\",\"leaving\":\"08:00\"}");

        (_, string output) = RunCommands("quit");

        Assert.Matches(@"1 +LE +08:00", output);
        Assert.Matches(@"2 +AR +09:00", output);
    }

    // AC-7 (017)
    [Fact]
    public void LegacyFile_IsNotRewritten_ByLoadingOrBrowsing()
    {
        string path = DayFile("2026-10-26");
        File.WriteAllText(path, "{\"arrival\":\"08:15\"}");
        DateTime stamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, stamp);
        byte[] before = File.ReadAllBytes(path);

        (int exitCode, string output) = RunCommands("prev", "next", "prev", "quit");

        Assert.Equal(0, exitCode);
        Assert.Matches(@"1 +AR +08:15", LastFrame(output));
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));
        Assert.Equal(["2026-10-26.json"], Directory.EnumerateFiles(data.Path).Select(Path.GetFileName));
    }

    // AC-9 (017)
    [Fact]
    public void EmptyEntriesFile_ShowsNoEntriesYet()
    {
        File.WriteAllText(DayFile("2026-10-27"), "{\"entries\":[]}");

        (int exitCode, string output) = RunCommands("quit");

        Assert.Equal(0, exitCode);
        Assert.Contains("No entries yet.", LastFrame(output));
    }

    // AC-8 (017)
    [Theory]
    [InlineData("{\"entries\":[{\"kind\":\"lunch\",\"time\":\"08:15\"}]}")]
    [InlineData("{\"entries\":[{\"kind\":\"arrival\",\"time\":\"8:15\"}]}")]
    [InlineData("{\"entries\":[{\"kind\":\"arrival\"}]}")]
    [InlineData("{\"entries\":{}}")]
    [InlineData("{\"entries\":[],\"arrival\":\"08:15\"}")]
    [InlineData("{\"entries\":[{\"kind\":\"checkpoint\",\"time\":\"09:00\",\"name\":\"abc\u202Edef\"}]}")] // AC-8 (056)
    [InlineData("{\"entries\":[{\"kind\":\"checkpoint\",\"time\":\"09:00\",\"name\":\"abc\\u202edef\"}]}")]
    [InlineData("{\"entries\":[{\"kind\":\"checkpoint\",\"time\":\"09:00\",\"name\":\"abc\\ud800def\"}]}")]
    [InlineData("{\"entries\":[{\"kind\":\"checkpoint\",\"time\":\"09:00\",\"name\":\"abc\\udc00def\"}]}")]
    public void InvalidEntriesFile_ShowsHint_AndNothingIsBooked(string contents)
    {
        File.WriteAllText(DayFile("2026-10-27"), contents);
        byte[] before = File.ReadAllBytes(DayFile("2026-10-27"));

        (int exitCode, string output) = RunCommands("arrive 9:00", "quit");

        Assert.Equal(0, exitCode);
        Assert.Equal(before, File.ReadAllBytes(DayFile("2026-10-27")));
        string[] frame = LastLayout(output);
        AssertOnHintRow(frame, "is not a valid day file. Nothing was booked.");
        Assert.Contains("is not a valid day file.", string.Join('\n', frame[..^4]));
    }

    // AC-8
    [Theory]
    [InlineData("", "the app-data folder could not be found (the path is empty)")]
    [InlineData("   ", "the app-data folder could not be found (the path is empty)")]
    [InlineData("Times", "the data folder Times is not an absolute path")]
    [InlineData("./data", "the data folder ./data is not an absolute path")]
    [InlineData("[red]x", "the data folder [red]x is not an absolute path")]
    public void InvalidDataFolder_ReturnsTwo_WithRedError_AndCreatesNothing(string folder, string named)
    {
        string[] before = WorkingDirectoryEntries();
        TestConsole console = new TestConsole().Interactive().EmitAnsiSequences();
        console.Input.PushTextWithEnter("arrive 8:15");

        int exitCode = new App(console, FixedTimeProvider.At20261027(), new DayStore(folder)).Run([]);

        Assert.Equal(2, exitCode);
        Assert.Matches(@"\x1b\[(31|91|38;5;9)mTimes cannot start", console.Output);
        Assert.Contains(named, Regex.Replace(console.Output, @"\x1b\[[0-9;]*m", string.Empty));
        Assert.DoesNotContain(Heading, console.Output);
        Assert.Equal(before, WorkingDirectoryEntries());
    }

    /// <summary>True if <paramref name="text"/> holds a code point of Unicode category Format (Cf).</summary>
    private static bool HasFormatCodePoint(string text) =>
        text.EnumerateRunes().Any(r => Rune.GetUnicodeCategory(r) == System.Globalization.UnicodeCategory.Format);

    /// <summary>Runs an invalid data folder and returns the exit code and the ANSI-stripped output, checking the red prefix and the working directory.</summary>
    private static (int ExitCode, string Plain) RunInvalidFolder(string folder, bool folderFromEnvironment)
    {
        string[] before = WorkingDirectoryEntries();
        TestConsole console = new TestConsole().Interactive().EmitAnsiSequences();
        console.Profile.Width = 300;
        console.Input.PushTextWithEnter("arrive 8:15");

        int exitCode = new App(console, FixedTimeProvider.At20261027(), new DayStore(folder), folderFromEnvironment: folderFromEnvironment).Run([]);

        Assert.Matches(@"\x1b\[(31|91|38;5;9)mTimes cannot start", console.Output);
        Assert.DoesNotContain(Heading, console.Output);
        Assert.Equal(before, WorkingDirectoryEntries());
        return (exitCode, Regex.Replace(console.Output, @"\x1b\[[0-9;]*m", string.Empty));
    }

    // 085 AC-5
    [Theory]
    [InlineData("\u202Etimes")]
    [InlineData("ti\u200Bmes")]
    [InlineData("times\U000E0041")]
    [InlineData("\u001b\u202Eti\u200Bmes")]
    public void InvalidOverride_WithFormatCharacters_ShowsCleanedValue(string folder)
    {
        (int exitCode, string plain) = RunInvalidFolder(folder, folderFromEnvironment: true);

        Assert.Equal(2, exitCode);
        Assert.Contains("the data folder \"times\" from TIMES_DATA_DIR is not an absolute path", plain);
        Assert.False(HasFormatCodePoint(plain));
        Assert.False(ProjectList.HasInvisibleOrBrokenCharacters(plain));
    }

    // 085 AC-6
    [Theory]
    [InlineData(" /a\u200Bb")]
    [InlineData(" /a\u202Eb")]
    public void EdgeWhitespaceOverride_WithFormatCharacters_ShowsCleanedValue(string folder)
    {
        (int exitCode, string plain) = RunInvalidFolder(folder, folderFromEnvironment: true);

        Assert.Equal(2, exitCode);
        Assert.Contains("the data folder \" /ab\" from TIMES_DATA_DIR starts or ends with whitespace", plain);
        Assert.False(HasFormatCodePoint(plain));
    }

    // 085 AC-7
    [Theory]
    [InlineData("Ti\u202Emes", "\u202E")]
    [InlineData("Ti\u001bmes", "\u001b")]
    [InlineData("Ti\u200Bmes", "\u200B")]
    public void InvalidDefaultFolder_WithFormatOrControlCharacters_ShowsCleanedValue(string folder, string removed)
    {
        (int exitCode, string plain) = RunInvalidFolder(folder, folderFromEnvironment: false);

        Assert.Equal(2, exitCode);
        Assert.Contains("the data folder Times is not an absolute path", plain);
        Assert.DoesNotContain(removed, plain, StringComparison.Ordinal);
        Assert.False(HasFormatCodePoint(plain));
    }

    // AC-9
    [Fact]
    public void MissingFolder_IsCreatedOnlyByTheFirstBooking()
    {
        using TempFolder missing = new TempFolder(create: false);

        (int exitCode, _) = Run(c =>
        {
            foreach (string command in new[] { "prev", "next", "arrive 25:00", "leave x", "quit" })
            {
                c.Input.PushTextWithEnter(command);
            }
        }, dataFolder: missing.Path);

        Assert.Equal(0, exitCode);
        AssertLeftNothing(missing.Path);

        Run(c =>
        {
            c.Input.PushTextWithEnter("arrive 8:15");
            c.Input.PushTextWithEnter("quit");
        }, dataFolder: missing.Path);

        Assert.Equal(
            [Path.Combine(missing.Path, "2026-10-27.json")],
            Directory.EnumerateFileSystemEntries(missing.Path));
    }

    // AC-9
    [Fact]
    public void BookingOnAnotherDay_CreatesNoOtherFile()
    {
        File.WriteAllText(DayFile("2026-10-26"), "{\"arrival\":\"09:00\"}");

        RunCommands("prev", "leave 8:00", "next", "quit");

        Assert.Equal(["2026-10-26.json"], Directory.EnumerateFiles(data.Path).Select(Path.GetFileName));
    }

    // AC-10
    [Fact]
    public void CorruptDayFile_ShowsRedHint_AndIsNotOverwritten()
    {
        File.WriteAllText(DayFile("2026-10-27"), "{not json");
        byte[] before = File.ReadAllBytes(DayFile("2026-10-27"));

        (int exitCode, string output) = RunCommands("arrive 8:00", "leave 17:00", "quit");

        Assert.Equal(0, exitCode);
        Assert.Equal(before, File.ReadAllBytes(DayFile("2026-10-27")));
        AssertOnHintRow(LastLayout(output), "is not a valid day file. Nothing was booked.");
        Assert.DoesNotContain("   at ", output);
        Assert.DoesNotContain("failed:", output);
        Assert.Equal(["2026-10-27.json"], Directory.EnumerateFiles(data.Path).Select(Path.GetFileName));
    }

    // AC-10
    [Fact]
    public void CorruptDayFile_HintIsRed()
    {
        File.WriteAllText(DayFile("2026-10-27"), "{not json");
        TestConsole console = new TestConsole().Interactive().EmitAnsiSequences();
        console.Input.PushTextWithEnter("arrive 8:00");
        console.Input.PushTextWithEnter("quit");

        Assert.Equal(0, new App(console, FixedTimeProvider.At20261027(), new DayStore(data.Path)).Run([]));
        Assert.Matches(@"\x1b\[(31|91|38;5;9)marrive: The file for 2026-10-27", console.Output);
    }

    // AC-11
    [Fact]
    public void Help_ListsArriveAndLeave_WithOptionalTime()
    {
        // The help table is taller than one page: page down so every row has been drawn.
        (_, string output) = Run(c =>
        {
            c.Input.PushTextWithEnter("help");
            PageDown(c, HelpPages);
            c.Input.PushTextWithEnter("quit");
        });

        Assert.Matches(@"arrive +│ a +│ Book your arrival: now, or at an optional H:mm", output);
        Assert.Matches(@"leave +│ l +│ Book your leaving: now, or at an optional H:mm", output);
    }

    // AC-11
    [Theory]
    [InlineData("ar", "{\"entries\":[{\"kind\":\"arrival\",\"time\":\"18:30\"}]}")]
    [InlineData("l", "{\"entries\":[{\"kind\":\"leaving\",\"time\":\"18:30\"}]}")]
    public void Tab_CompletesArriveAndLeave(string prefix, string expected)
    {
        (int exitCode, string output) = Run(c =>
        {
            c.Input.PushText(prefix);
            c.Input.PushKey(ConsoleKey.Tab);
            c.Input.PushKey(ConsoleKey.Enter);
            c.Input.PushTextWithEnter("quit");
        });

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain("Unknown command", output);
        Assert.Equal(expected, ReadDay("2026-10-27"));
    }

    // AC-11
    [Theory]
    [InlineData("ar", "arrive")]
    [InlineData("lea", "leave")]
    [InlineData("arive", "arrive")]
    public void Popup_ShowsArriveAndLeave(string typed, string expected)
    {
        string output = RunAnsi(c =>
        {
            c.Input.PushText(typed);
            c.Input.PushKey(ConsoleKey.Escape);
            c.Input.PushKey(ConsoleKey.Escape);
            c.Input.PushTextWithEnter("quit");
        });

        // One popup draw per typed key, each ending on row 21. The draw after the last key lists
        // the best match on its top row.
        List<AnsiWrite> writes = TypingWrites(output, 0);
        List<int> bottoms = writes
            .Select((w, i) => (w, i))
            .Where(x => x.w.Row == 21 && (x.w.StartsWithCommand || x.w.Text.StartsWith('…')))
            .Select(x => x.i)
            .ToList();
        int top = bottoms[typed.Length - 1];
        while (top > 0 && writes[top - 1].Row == writes[top].Row - 1 && writes[top - 1].StartsWithCommand)
        {
            top--;
        }

        AnsiWrite row = writes[top];
        Assert.StartsWith(expected, row.Text);
        Assert.Contains("optional H:mm", row.Text);
    }

    // Top area overflow (job 009) ----------------------------------------------------------

    /// <summary>Asserts the bottom of a <paramref name="height"/>-line layout: hint row at <c>frame[^4]</c>, input panel on the last 3 lines.</summary>
    private static void AssertLayout(string[] frame, int height)
    {
        Assert.Equal(height, frame.Length);
        Assert.DoesNotContain("more lines", frame[^4]);
        Assert.StartsWith("╭", frame[^3]);
        Assert.StartsWith("│ > ", frame[^2]);
        Assert.StartsWith("╰", frame[^1]);
    }

    /// <summary>The help frame drawn on a terminal tall enough that nothing is cut: the unclipped top content lines.</summary>
    private string[] UnclippedHelpTop()
    {
        (_, string output) = Run(c =>
        {
            c.Profile.Height = 90;
            c.Input.PushTextWithEnter("help");
            c.Input.PushTextWithEnter("quit");
        }, ManyCommands());

        string[] frame = Frames(output)[1];
        Assert.Equal(90, frame.Length);
        Assert.DoesNotContain(frame, l => l.Contains("more lines", StringComparison.Ordinal));
        string[] top = frame[..^4];
        int last = Array.FindLastIndex(top, l => !string.IsNullOrWhiteSpace(l));
        return top[..(last + 1)];
    }

    // AC-4, AC-7
    [Fact]
    public void ShortTerminal_Help_ShowsFirstLines_ThenMoreCue()
    {
        string[] full = UnclippedHelpTop();

        (int exitCode, string output) = Run(c =>
        {
            c.Profile.Height = 12;
            c.Input.PushTextWithEnter("help");
            c.Input.PushTextWithEnter("quit");
        }, ManyCommands());

        Assert.Equal(0, exitCode);
        List<string[]> frames = Frames(output);
        Assert.Equal(2, frames.Count);
        foreach (string[] each in frames)
        {
            AssertLayout(each, 12);
        }

        string[] frame = frames[1];
        Assert.Contains(Heading, frame[0]);
        Assert.Equal(full[..7], frame[..7]);
        Assert.Equal($"↓ {full.Length - 7} more lines below · PgDn / Ctrl+↓", frame[^5].TrimEnd());
        Assert.True(full.Length - 7 > 10);
    }

    // AC-4
    [Fact]
    public void ShortTerminal_Cue_IsGrey()
    {
        string output = RunAnsi(c =>
        {
            c.Profile.Height = 12;
            c.Input.PushTextWithEnter("help");
            c.Input.PushTextWithEnter("quit");
        }, ManyCommands());

        Assert.Matches(@"\x1b\[(90|38;5;8)m↓ \d+ more lines below · PgDn / Ctrl\+↓", output);
    }

    // AC-6, AC-7
    [Fact]
    public void ShortTerminal_PopupThenEsc_RestoresTheCueRow()
    {
        string output = RunAnsi(c =>
        {
            c.Profile.Height = 12;
            c.Input.PushTextWithEnter("help");
            c.Input.PushText("/");
            c.Input.PushKey(ConsoleKey.Escape);
            c.Input.PushKey(ConsoleKey.Escape);
            c.Input.PushTextWithEnter("quit");
        }, ManyCommands());

        List<string[]> frames = AnsiFrames(output);
        Assert.Equal(2, frames.Count);
        foreach (string[] each in frames)
        {
            AssertLayout(each, 12);
        }

        string[] frame = frames[1];
        Assert.Contains("more lines", frame[^5]);

        List<AnsiWrite> writes = TypingWrites(output, 1);
        int popupAtCue = writes.FindIndex(w => w.Row == 8 && w.StartsWithCommand);
        Assert.True(popupAtCue >= 0, "the popup should cover the cue row");
        AnsiWrite restored = writes.Skip(popupAtCue + 1).First(w => w.Row == 8);
        Assert.Equal(frame[7], restored.Text);
        Assert.StartsWith("↓ ", restored.Text);

        // The hint row (9) is never written with anything but the popup or its own restore.
        Assert.DoesNotContain(writes, w => w.Row > 9 && w.Text.Contains("more lines", StringComparison.Ordinal));
    }

    // Input placeholder (job 014) ----------------------------------------------------------

    private const string Placeholder = "Type help for commands";

    // AC-1
    [Fact]
    public void FirstFrame_InputRow_ShowsGreyPlaceholderAfterPrompt()
    {
        string output = RunAnsi(c => c.Input.PushTextWithEnter("quit"));

        string[] frame = AnsiFrames(output)[0];
        Assert.Equal(24, frame.Length);
        Assert.Contains("> " + Placeholder, frame[^2]);

        // The raw input row: the prompt in steelblue1, then the placeholder in grey (colour 8).
        string raw = output.Split('\n').First(l => l.Contains(Placeholder, StringComparison.Ordinal));
        Assert.Matches(@"\x1b\[38;5;75m> \x1b\[0m\x1b\[(90|38;5;8)m" + Placeholder, raw);
        Assert.DoesNotMatch(@"\x1b\[38;5;75m[^\x1b]*" + Placeholder, raw);
    }

    // AC-1
    [Fact]
    public void Placeholder_DoesNotMoveTheCursor_TypingStartsAfterPrompt()
    {
        string output = RunAnsi(c => c.Input.PushTextWithEnter("quit"));

        List<AnsiWrite> writes = AnsiWrites.Parse(output);
        // Row height - 1 = 23; column 3 + "> ".Length = 5.
        Assert.Contains(writes, w => w.Row == 23 && w.Column == 5 && w.Text.StartsWith("quit", StringComparison.Ordinal));
        Assert.Contains("\u001b[23;5H", output);
    }

    // AC-2
    [Fact]
    public void EveryRedrawnFrame_ShowsThePlaceholderAgain()
    {
        string output = RunAnsi(c =>
        {
            c.Input.PushTextWithEnter("help");
            c.Input.PushTextWithEnter("nope");
            c.Input.PushKey(ConsoleKey.Enter);
            c.Input.PushTextWithEnter("hello");
            c.Input.PushTextWithEnter("quit");
        });

        List<string[]> frames = AnsiFrames(output);
        Assert.Equal(5, frames.Count);
        foreach (string[] frame in frames)
        {
            Assert.Contains("> " + Placeholder, frame[^2]);
        }

        // Each frame writes it in grey.
        Assert.True(Regex.Matches(output, @"\x1b\[(90|38;5;8)m" + Placeholder).Count >= frames.Count);
    }

    // AC-2
    [Fact]
    public void Placeholder_IsNeverDispatched()
    {
        (int exitCode, string output) = RunCommands(string.Empty, "quit");

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain("Commands start with", output);
        Assert.DoesNotContain("Unknown command", output);
    }

    // Remove entry (job 021) ---------------------------------------------------------------

    private const string ThreeEntries = "{\"entries\":[{\"kind\":\"arrival\",\"time\":\"08:00\"},{\"kind\":\"leaving\",\"time\":\"12:00\"},{\"kind\":\"arrival\",\"time\":\"13:00\"}]}";

    /// <summary>Runs the commands on an 80-column console, or a wider one when a long hint must fit.</summary>
    private (int ExitCode, string Output) RunWide(int width, params string[] commands) =>
        Run(c =>
        {
            c.Profile.Width = width;
            foreach (string command in commands)
            {
                c.Input.PushTextWithEnter(command);
            }
        });

    // AC-1 (021)
    [Fact]
    public void Remove_MiddleEntry_SavesTheRest_RenumbersAndRecomputesTimeLeft()
    {
        File.WriteAllText(DayFile("2026-10-27"), ThreeEntries);

        (int exitCode, string output) = RunCommands("remove 2", "quit");

        Assert.Equal(0, exitCode);
        Assert.Equal(Entries("arrival 08:00", "arrival 13:00"), ReadDay("2026-10-27"));
        Assert.Equal(
            [new DayEntry(EntryKind.Arrival, new TimeOnly(8, 0)), new DayEntry(EntryKind.Arrival, new TimeOnly(13, 0))],
            new DayStore(data.Path).Load(new DateOnly(2026, 10, 27)).Entries);

        // Before: the leaving at 12:00 closed 4 hours, so the 13:00 arrival had 4:00 left.
        string first = string.Join('\n', Frames(output)[0]);
        Assert.Matches(@"3 +AR +13:00 +left 4:00", first);

        string[] frame = LastLayout(output);
        string top = string.Join('\n', frame[..^4]);
        Assert.Matches(@"1 +AR +08:00 +left 8:00", top);

        // After (045): the 08:00 span is still open, so the 13:00 arrival shows the 5 hours worked so far.
        Assert.Matches(@"2 +AR +13:00 +left 3:00", top);
        Assert.DoesNotContain("12:00", top);
        Assert.DoesNotMatch(@"\b3 +(AR|LE)", top);
        AssertLayout(frame);
        Assert.Equal(string.Empty, frame[^4].Trim());
    }

    // AC-1 (021)
    [Fact]
    public void Remove_AfterBookingInTheSameSession_UsesTheShownNumbers()
    {
        (int exitCode, string output) = RunCommands("arrive 8:00", "leave 12:00", "arrive 13:00", "remove 2", "quit");

        Assert.Equal(0, exitCode);
        Assert.Equal(Entries("arrival 08:00", "arrival 13:00"), ReadDay("2026-10-27"));
        Assert.Matches(@"2 +AR +13:00", LastFrame(output));
    }

    // AC-2 (021)
    [Fact]
    public void Remove_ActsOnTheViewedDay_AndLeavesTodayUntouched()
    {
        File.WriteAllText(DayFile("2026-10-27"), ThreeEntries);
        File.WriteAllText(DayFile("2026-10-26"), Entries("arrival 09:00", "leaving 17:00"));
        byte[] today = File.ReadAllBytes(DayFile("2026-10-27"));

        (int exitCode, string output) = RunCommands("prev", "remove 1", "quit");

        Assert.Equal(0, exitCode);
        Assert.Equal(Entries("leaving 17:00"), ReadDay("2026-10-26"));
        Assert.Equal(today, File.ReadAllBytes(DayFile("2026-10-27")));
        Assert.StartsWith("Monday, 2026-10-26", LastFrame(output));
        Assert.Matches(@"1 +LE +17:00", LastFrame(output));
    }

    // AC-3 (021)
    [Theory]
    [InlineData("remove 0", "remove: there is no entry 0.")]
    [InlineData("remove 4", "remove: there is no entry 4.")]
    [InlineData("remove 99", "remove: there is no entry 99.")]
    [InlineData("remove 007", "remove: there is no entry 7.")]
    public void Remove_UnknownNumber_ShowsHintAboveInputBox_AndChangesNothing(string typed, string expected)
    {
        File.WriteAllText(DayFile("2026-10-27"), ThreeEntries);
        byte[] before = File.ReadAllBytes(DayFile("2026-10-27"));

        (int exitCode, string output) = RunCommands("help", typed, "quit");

        Assert.Equal(0, exitCode);
        AssertOnHintRow(LastLayout(output), expected);
        Assert.Equal(before, File.ReadAllBytes(DayFile("2026-10-27")));
        Assert.Matches(@"3 +AR +13:00", LastFrame(output));
    }

    // AC-4 (021)
    [Theory]
    [InlineData("remove", "remove: say which entry to remove.")]
    [InlineData("remove abc", "remove: abc is not an entry number.")]
    [InlineData("remove -1", "remove: -1 is not an entry number.")]
    [InlineData("remove +1", "remove: +1 is not an entry number.")]
    [InlineData("remove 1.5", "remove: 1.5 is not an entry number.")]
    [InlineData("remove 2 3", "remove: 2 3 is not an entry number.")]
    [InlineData("remove 99999999999", "remove: 99999999999 is not an entry number.")]
    [InlineData("remove [bold]", "remove: [bold] is not an entry number.")]
    [InlineData("remove [red]x[/]", "remove: [red]x[/] is not an entry number.")]
    public void Remove_BadArgument_ShowsUsageHint_AndChangesNothing(string typed, string expected)
    {
        File.WriteAllText(DayFile("2026-10-27"), ThreeEntries);
        byte[] before = File.ReadAllBytes(DayFile("2026-10-27"));

        (int exitCode, string output) = RunCommands("help", typed, "quit");

        Assert.Equal(0, exitCode);
        string[] frame = LastLayout(output);
        AssertOnHintRow(frame, expected);
        AssertOnHintRow(frame, "Use remove <number>.");
        Assert.Equal(before, File.ReadAllBytes(DayFile("2026-10-27")));
        Assert.DoesNotContain("failed:", output);
    }

    // AC-3, AC-4 (021)
    [Theory]
    [InlineData("remove 4")]
    [InlineData("remove abc")]
    [InlineData("remove")]
    public void Remove_Hints_AreRed(string typed)
    {
        File.WriteAllText(DayFile("2026-10-27"), ThreeEntries);
        TestConsole console = new TestConsole().Interactive().EmitAnsiSequences();
        console.Input.PushTextWithEnter(typed);
        console.Input.PushTextWithEnter("quit");

        Assert.Equal(0, new App(console, FixedTimeProvider.At20261027(), new DayStore(data.Path)).Run([]));
        Assert.Matches(@"\x1b\[(31|91|38;5;9)mremove: ", console.Output);
    }

    // AC-5 (021)
    [Fact]
    public void Remove_OnDayWithoutFile_ShowsHint_AndCreatesNothing()
    {
        using TempFolder missing = new TempFolder(create: false);

        (int exitCode, string output) = Run(c =>
        {
            c.Input.PushTextWithEnter("remove 1");
            c.Input.PushTextWithEnter("quit");
        }, dataFolder: missing.Path);

        Assert.Equal(0, exitCode);
        AssertOnHintRow(LastLayout(output), "remove: there is no entry 1.");
        AssertLeftNothing(missing.Path);

        (exitCode, output) = RunCommands("remove 1", "quit");

        Assert.Equal(0, exitCode);
        AssertOnHintRow(LastLayout(output), "remove: there is no entry 1.");
        Assert.Empty(Directory.EnumerateFileSystemEntries(data.Path));
    }

    // AC-6 (021)
    [Fact]
    public void Remove_OnlyEntry_KeepsFileWithEmptyEntries_AndShowsNoEntriesYet()
    {
        (int exitCode, string output) = RunCommands("arrive 8:00", "remove 1", "quit");

        Assert.Equal(0, exitCode);
        Assert.Equal("{\"entries\":[]}", ReadDay("2026-10-27"));
        Assert.Contains("No entries yet.", LastFrame(output));
        Assert.Equal(string.Empty, LastLayout(output)[^4].Trim());
    }

    // AC-7 (021)
    [Theory]
    [InlineData("{not json", "is not a valid day file. Nothing was removed.")]
    [InlineData("{\"entries\":[{\"kind\":\"lunch\",\"time\":\"08:15\"}]}", "is not a valid day file. Nothing was removed.")]
    public void Remove_UnreadableFile_ShowsHint_AndLeavesFileUnchanged(string contents, string expected)
    {
        File.WriteAllText(DayFile("2026-10-27"), contents);
        byte[] before = File.ReadAllBytes(DayFile("2026-10-27"));

        (int exitCode, string output) = RunCommands("remove 1", "quit");

        Assert.Equal(0, exitCode);
        Assert.Equal(before, File.ReadAllBytes(DayFile("2026-10-27")));
        AssertOnHintRow(LastLayout(output), "remove: The file for 2026-10-27 " + expected);
        Assert.DoesNotContain("   at ", output);
        Assert.DoesNotContain("failed:", output);
        Assert.Equal(["2026-10-27.json"], Directory.EnumerateFileSystemEntries(data.Path).Select(Path.GetFileName));
    }

    // AC-7 (021)
    [Fact]
    public void Remove_TooLargeFile_ShowsHint_AndLeavesFileUnchanged()
    {
        File.WriteAllText(DayFile("2026-10-27"), new string(' ', (int)DayStore.MaxFileBytes + 1) + ThreeEntries);
        byte[] before = File.ReadAllBytes(DayFile("2026-10-27"));

        // The hint is longer than 80 columns, so widen the terminal to see all of it.
        (int exitCode, string output) = RunWide(120, "remove 1", "quit");

        Assert.Equal(0, exitCode);
        Assert.Equal(before, File.ReadAllBytes(DayFile("2026-10-27")));
        string[] frame = LastLayout(output);
        AssertOnHintRow(frame, "remove: The file for 2026-10-27 is too large to be a day file. Nothing was removed.");
    }

    // AC-7 (021)
    [Fact]
    public void Remove_UnreadableFile_HintIsRed()
    {
        File.WriteAllText(DayFile("2026-10-27"), "{not json");
        TestConsole console = new TestConsole().Interactive().EmitAnsiSequences();
        console.Input.PushTextWithEnter("remove 1");
        console.Input.PushTextWithEnter("quit");

        Assert.Equal(0, new App(console, FixedTimeProvider.At20261027(), new DayStore(data.Path)).Run([]));
        Assert.Matches(@"\x1b\[(31|91|38;5;9)mremove: The file for 2026-10-27", console.Output);
    }

    // AC-7 (021)
    [Fact]
    public void Remove_SaveFailure_ShowsRedHint_NotStackTrace()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.WriteAllText(DayFile("2026-10-27"), ThreeEntries);
        byte[] before = File.ReadAllBytes(DayFile("2026-10-27"));
        UnixFileMode mode = File.GetUnixFileMode(data.Path);
        File.SetUnixFileMode(data.Path, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            TestConsole console = new TestConsole().Interactive().EmitAnsiSequences();
            console.Profile.Width = 200;
            console.Input.PushTextWithEnter("remove 2");
            console.Input.PushTextWithEnter("quit");

            Assert.Equal(0, new App(console, FixedTimeProvider.At20261027(), new DayStore(data.Path), instanceLock: SideLock()).Run([]));
            Assert.Matches(@"\x1b\[(31|91|38;5;9)mremove: could not save the day\.", console.Output);
            Assert.DoesNotContain("   at ", console.Output);
            Assert.DoesNotContain("failed:", console.Output);
        }
        finally
        {
            File.SetUnixFileMode(data.Path, mode);
        }

        Assert.Equal(before, File.ReadAllBytes(DayFile("2026-10-27")));
    }

    // AC-8 (021)
    [Fact]
    public void Help_ListsRemove_WithDescription()
    {
        // With the Alias column (041) the help table no longer fits 24 rows, so scroll to its end.
        (_, string output) = Run(c =>
        {
            c.Input.PushTextWithEnter("help");
            PageDown(c, HelpPages);
            c.Input.PushTextWithEnter("quit");
        });

        Assert.Matches(@"remove +│ r +│ Remove an entry from this day by its number", output);
    }

    // AC-8 (021)
    [Fact]
    public void Tab_CompletesRemove()
    {
        File.WriteAllText(DayFile("2026-10-27"), ThreeEntries);

        (int exitCode, string output) = Run(c =>
        {
            c.Input.PushText("rem");
            c.Input.PushKey(ConsoleKey.Tab);
            c.Input.PushTextWithEnter(" 3");
            c.Input.PushTextWithEnter("quit");
        });

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain("Unknown command", output);
        Assert.Equal(Entries("arrival 08:00", "leaving 12:00"), ReadDay("2026-10-27"));
    }

    // AC-8 (021)
    [Fact]
    public void Popup_ShowsRemove_AfterTypingRem()
    {
        string output = RunAnsi(c =>
        {
            c.Input.PushText("rem");
            c.Input.PushKey(ConsoleKey.Escape);
            c.Input.PushKey(ConsoleKey.Escape);
            c.Input.PushTextWithEnter("quit");
        });

        List<AnsiWrite> writes = TypingWrites(output, 0);
        List<int> bottoms = writes
            .Select((w, i) => (w, i))
            .Where(x => x.w.Row == 21 && (x.w.StartsWithCommand || x.w.Text.StartsWith('…')))
            .Select(x => x.i)
            .ToList();
        int top = bottoms[2];
        while (top > 0 && writes[top - 1].Row == writes[top].Row - 1 && writes[top - 1].StartsWithCommand)
        {
            top--;
        }

        AnsiWrite row = writes[top];
        Assert.StartsWith("remove", row.Text);
        Assert.Contains("Remove an entry from this day by its number", row.Text);
    }

    // Change entry (job 023) ---------------------------------------------------------------

    // AC-1 (023)
    [Fact]
    public void Change_MiddleEntry_SavesNewTime_KeepsKind_AndRecomputesTimeLeft()
    {
        File.WriteAllText(DayFile("2026-10-27"), ThreeEntries);

        (int exitCode, string output) = RunCommands("change 2 12:30", "quit");

        Assert.Equal(0, exitCode);
        Assert.Equal(Entries("arrival 08:00", "leaving 12:30", "arrival 13:00"), ReadDay("2026-10-27"));
        Assert.Equal(
            [
                new DayEntry(EntryKind.Arrival, new TimeOnly(8, 0)),
                new DayEntry(EntryKind.Leaving, new TimeOnly(12, 30)),
                new DayEntry(EntryKind.Arrival, new TimeOnly(13, 0)),
            ],
            new DayStore(data.Path).Load(new DateOnly(2026, 10, 27)).Entries);

        // Before: the 12:00 leaving closed 4 hours. After: 4:30, so 3:30 is left.
        string first = string.Join('\n', Frames(output)[0]);
        Assert.Matches(@"2 +LE +12:00 +left 4:00", first);

        string[] frame = LastLayout(output);
        string top = string.Join('\n', frame[..^4]);
        Assert.Matches(@"1 +AR +08:00 +left 8:00", top);
        Assert.Matches(@"2 +LE +12:30 +left 3:30", top);
        Assert.Matches(@"3 +AR +13:00 +left 3:30", top);
        Assert.DoesNotContain("12:00", top);
        AssertLayout(frame);
        Assert.Equal(string.Empty, frame[^4].Trim());
    }

    // AC-2 (023)
    [Fact]
    public void Change_PastOtherEntries_ReSortsAndRenumbers()
    {
        File.WriteAllText(DayFile("2026-10-27"), ThreeEntries);

        (int exitCode, string output) = RunCommands("change 1 14:00", "quit");

        Assert.Equal(0, exitCode);
        Assert.Equal(Entries("leaving 12:00", "arrival 13:00", "arrival 14:00"), ReadDay("2026-10-27"));
        string[] frame = LastLayout(output);
        string top = string.Join('\n', frame[..^4]);
        Assert.Matches(@"1 +LE +12:00 +left 8:00", top);
        Assert.Matches(@"2 +AR +13:00 +left 8:00", top);

        // The 13:00 arrival opens a span (045), so the second arrival at 14:00 shows an hour worked.
        Assert.Matches(@"3 +AR +14:00 +left 7:00", top);
        Assert.DoesNotContain("08:00", top);
        Assert.Equal(string.Empty, frame[^4].Trim());
    }

    // AC-1, AC-2 (023)
    [Fact]
    public void Change_AfterBookingInTheSameSession_UsesTheShownNumbers()
    {
        (int exitCode, string output) = RunCommands("arrive 8:00", "leave 12:00", "arrive 13:00", "change 3 12:45", "quit");

        Assert.Equal(0, exitCode);
        Assert.Equal(Entries("arrival 08:00", "leaving 12:00", "arrival 12:45"), ReadDay("2026-10-27"));
        Assert.Matches(@"3 +AR +12:45", LastFrame(output));
    }

    // AC-3 (023)
    [Theory]
    [InlineData("change 1 7:05")]
    [InlineData("change 1 07:05")]
    [InlineData("change   1    7:05")]
    [InlineData("  change 1 07:05   ")]
    [InlineData("change    1  07:05    ")]
    public void Change_AcceptsShortAndLongHours_AndExtraSpaces(string typed)
    {
        File.WriteAllText(DayFile("2026-10-27"), ThreeEntries);

        (int exitCode, string output) = RunCommands(typed, "quit");

        Assert.Equal(0, exitCode);
        Assert.Equal(Entries("arrival 07:05", "leaving 12:00", "arrival 13:00"), ReadDay("2026-10-27"));
        Assert.Equal(string.Empty, LastLayout(output)[^4].Trim());
    }

    // AC-4 (023)
    [Fact]
    public void Change_ActsOnTheViewedDay_AndLeavesTodayUntouched()
    {
        File.WriteAllText(DayFile("2026-10-27"), ThreeEntries);
        File.WriteAllText(DayFile("2026-10-26"), Entries("arrival 08:30", "leaving 17:00"));
        byte[] today = File.ReadAllBytes(DayFile("2026-10-27"));

        (int exitCode, string output) = RunCommands("prev", "change 1 9:00", "quit");

        Assert.Equal(0, exitCode);
        Assert.Equal(Entries("arrival 09:00", "leaving 17:00"), ReadDay("2026-10-26"));
        Assert.Equal(today, File.ReadAllBytes(DayFile("2026-10-27")));
        Assert.StartsWith("Monday, 2026-10-26", LastFrame(output));
        Assert.Matches(@"1 +AR +09:00", LastFrame(output));
    }

    // AC-5 (023)
    [Theory]
    [InlineData("change 0 8:00", "change: there is no entry 0.")]
    [InlineData("change 4 8:00", "change: there is no entry 4.")]
    [InlineData("change 99 8:00", "change: there is no entry 99.")]
    [InlineData("change 004 8:00", "change: there is no entry 4.")]
    public void Change_UnknownNumber_ShowsHintAboveInputBox_AndChangesNothing(string typed, string expected)
    {
        File.WriteAllText(DayFile("2026-10-27"), ThreeEntries);
        byte[] before = File.ReadAllBytes(DayFile("2026-10-27"));

        (int exitCode, string output) = RunCommands("help", typed, "quit");

        Assert.Equal(0, exitCode);
        AssertOnHintRow(LastLayout(output), expected);
        Assert.Equal(before, File.ReadAllBytes(DayFile("2026-10-27")));
        Assert.Matches(@"3 +AR +13:00", LastFrame(output));
    }

    // AC-6 (023)
    [Theory]
    [InlineData("change 2 25:00", "change: 25:00 is not a time.")]
    [InlineData("change 2 8:5", "change: 8:5 is not a time.")]
    [InlineData("change 2 8.15", "change: 8.15 is not a time.")]
    [InlineData("change 2 abc", "change: abc is not a time.")]
    [InlineData("change 2 8:15:00", "change: 8:15:00 is not a time.")]
    [InlineData("change 2 [red]x", "change: [red]x is not a time.")]
    public void Change_InvalidTime_ShowsHintWithFormat_AndChangesNothing(string typed, string expected)
    {
        File.WriteAllText(DayFile("2026-10-27"), ThreeEntries);
        byte[] before = File.ReadAllBytes(DayFile("2026-10-27"));

        (int exitCode, string output) = RunCommands("help", typed, "quit");

        Assert.Equal(0, exitCode);
        string[] frame = LastLayout(output);
        AssertOnHintRow(frame, expected);
        AssertOnHintRow(frame, "Use H:mm, now, -<n>m or +<n>m.");
        Assert.Equal(before, File.ReadAllBytes(DayFile("2026-10-27")));
        Assert.DoesNotContain("failed:", output);
    }

    // AC-7 (023)
    [Theory]
    [InlineData("change", "change: say which entry and its new time.")]
    [InlineData("change 2", "change: say which entry and its new time.")]
    [InlineData("change 2 8:00 9:00", "change: say which entry and its new time.")]
    [InlineData("change abc 8:00", "change: abc is not an entry number.")]
    [InlineData("change -1 8:00", "change: -1 is not an entry number.")]
    [InlineData("change +1 8:00", "change: +1 is not an entry number.")]
    [InlineData("change 99999999999 8:00", "change: 99999999999 is not an entry number.")]
    [InlineData("change [bold] 8:00", "change: [bold] is not an entry number.")]
    [InlineData("change [red]x[/] 8:00", "change: [red]x[/] is not an entry number.")]
    public void Change_MissingOrMalformedArgument_ShowsUsageHint_AndChangesNothing(string typed, string expected)
    {
        File.WriteAllText(DayFile("2026-10-27"), ThreeEntries);
        byte[] before = File.ReadAllBytes(DayFile("2026-10-27"));

        (int exitCode, string output) = RunCommands("help", typed, "quit");

        Assert.Equal(0, exitCode);
        string[] frame = LastLayout(output);
        AssertLayout(frame);
        Assert.Equal(expected + " Use change <number> <H:mm|now|-10m|+5m>.", HintText(frame, "change:"));
        Assert.Equal(before, File.ReadAllBytes(DayFile("2026-10-27")));
        Assert.DoesNotContain("failed:", output);
    }

    // AC-5, AC-6, AC-7 (023)
    [Theory]
    [InlineData("change 4 8:00")]
    [InlineData("change 2 25:00")]
    [InlineData("change abc 8:00")]
    [InlineData("change")]
    public void Change_Hints_AreRed(string typed)
    {
        File.WriteAllText(DayFile("2026-10-27"), ThreeEntries);
        TestConsole console = new TestConsole().Interactive().EmitAnsiSequences();
        console.Input.PushTextWithEnter(typed);
        console.Input.PushTextWithEnter("quit");

        Assert.Equal(0, new App(console, FixedTimeProvider.At20261027(), new DayStore(data.Path)).Run([]));
        Assert.Matches(@"\x1b\[(31|91|38;5;9)mchange: ", console.Output);
    }

    // AC-8 (023)
    [Fact]
    public void Change_OnDayWithoutFile_ShowsHint_AndCreatesNothing()
    {
        using TempFolder missing = new TempFolder(create: false);

        (int exitCode, string output) = Run(c =>
        {
            c.Input.PushTextWithEnter("change 1 8:00");
            c.Input.PushTextWithEnter("quit");
        }, dataFolder: missing.Path);

        Assert.Equal(0, exitCode);
        AssertOnHintRow(LastLayout(output), "change: there is no entry 1.");
        AssertLeftNothing(missing.Path);

        (exitCode, output) = RunCommands("change 1 8:00", "quit");

        Assert.Equal(0, exitCode);
        AssertOnHintRow(LastLayout(output), "change: there is no entry 1.");
        Assert.Empty(Directory.EnumerateFileSystemEntries(data.Path));
    }

    // AC-9 (023)
    [Theory]
    [InlineData("{not json")]
    [InlineData("{\"entries\":[{\"kind\":\"lunch\",\"time\":\"08:15\"}]}")]
    public void Change_UnreadableFile_ShowsHint_AndLeavesFileUnchanged(string contents)
    {
        File.WriteAllText(DayFile("2026-10-27"), contents);
        byte[] before = File.ReadAllBytes(DayFile("2026-10-27"));

        (int exitCode, string output) = RunCommands("change 1 8:00", "quit");

        Assert.Equal(0, exitCode);
        Assert.Equal(before, File.ReadAllBytes(DayFile("2026-10-27")));
        AssertOnHintRow(LastLayout(output), "change: The file for 2026-10-27 is not a valid day file. Nothing was changed.");
        Assert.DoesNotContain("   at ", output);
        Assert.DoesNotContain("failed:", output);
        Assert.Equal(["2026-10-27.json"], Directory.EnumerateFileSystemEntries(data.Path).Select(Path.GetFileName));
    }

    // AC-9 (023)
    [Fact]
    public void Change_UnreadableFile_HintIsRed()
    {
        File.WriteAllText(DayFile("2026-10-27"), "{not json");
        TestConsole console = new TestConsole().Interactive().EmitAnsiSequences();
        console.Input.PushTextWithEnter("change 1 8:00");
        console.Input.PushTextWithEnter("quit");

        Assert.Equal(0, new App(console, FixedTimeProvider.At20261027(), new DayStore(data.Path)).Run([]));
        Assert.Matches(@"\x1b\[(31|91|38;5;9)mchange: The file for 2026-10-27", console.Output);
    }

    // AC-9 (023)
    [Fact]
    public void Change_SaveFailure_ShowsRedHint_NotStackTrace()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.WriteAllText(DayFile("2026-10-27"), ThreeEntries);
        byte[] before = File.ReadAllBytes(DayFile("2026-10-27"));
        UnixFileMode mode = File.GetUnixFileMode(data.Path);
        File.SetUnixFileMode(data.Path, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            TestConsole console = new TestConsole().Interactive().EmitAnsiSequences();
            console.Profile.Width = 200;
            console.Input.PushTextWithEnter("change 2 12:30");
            console.Input.PushTextWithEnter("quit");

            Assert.Equal(0, new App(console, FixedTimeProvider.At20261027(), new DayStore(data.Path), instanceLock: SideLock()).Run([]));
            Assert.Matches(@"\x1b\[(31|91|38;5;9)mchange: could not save the day\.", console.Output);
            Assert.DoesNotContain("   at ", console.Output);
            Assert.DoesNotContain("failed:", console.Output);
        }
        finally
        {
            File.SetUnixFileMode(data.Path, mode);
        }

        Assert.Equal(before, File.ReadAllBytes(DayFile("2026-10-27")));
    }

    // AC-10 (023)
    [Fact]
    public void Help_ListsChange_WithDescription()
    {
        (_, string output) = RunWide(120, "help", "quit");

        Assert.Matches(@"change +│ c +│ Change the time of an entry on this day: <number> <H:mm\|now\|-10m\|\+5m>", output);
    }

    // AC-10 (023)
    [Fact]
    public void Tab_CompletesChange()
    {
        File.WriteAllText(DayFile("2026-10-27"), ThreeEntries);

        (int exitCode, string output) = Run(c =>
        {
            c.Input.PushText("cha");
            c.Input.PushKey(ConsoleKey.Tab);
            c.Input.PushTextWithEnter(" 2 12:30");
            c.Input.PushTextWithEnter("quit");
        });

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain("Unknown command", output);
        Assert.Equal(Entries("arrival 08:00", "leaving 12:30", "arrival 13:00"), ReadDay("2026-10-27"));
    }

    // AC-10 (023)
    [Fact]
    public void Popup_ShowsChange_AfterTypingCha()
    {
        string output = RunAnsi(c =>
        {
            c.Input.PushText("cha");
            c.Input.PushKey(ConsoleKey.Escape);
            c.Input.PushKey(ConsoleKey.Escape);
            c.Input.PushTextWithEnter("quit");
        });

        List<AnsiWrite> writes = TypingWrites(output, 0);
        List<int> bottoms = writes
            .Select((w, i) => (w, i))
            .Where(x => x.w.Row == 21 && (x.w.StartsWithCommand || x.w.Text.StartsWith('…')))
            .Select(x => x.i)
            .ToList();
        int top = bottoms[2];
        while (top > 0 && writes[top - 1].Row == writes[top].Row - 1 && writes[top - 1].StartsWithCommand)
        {
            top--;
        }

        AnsiWrite row = writes[top];
        Assert.StartsWith("change", row.Text);
        Assert.Contains("Change the time of an entry on this day: <number> <H:mm|now|-10m", row.Text);
    }

    private static (int ExitCode, string Output) RunWithArgs(string folder, params string[] args)
    {
        // No input is queued: if the app tried to read a line, the test console would throw instead of hanging.
        TestConsole console = new TestConsole().Interactive();
        int exitCode = new App(console, FixedTimeProvider.At20261027(), new DayStore(folder)).Run(args);
        return (exitCode, console.Output);
    }

    [Fact]
    public void VersionFlag_PrintsVersionAndReturnsZero()
    {
        (int exitCode, string output) = RunWithArgs(data.Path, "--version");

        Assert.Equal(0, exitCode);
        Assert.Equal($"Times {AppVersion.Current}", output.Trim());
        Assert.DoesNotContain('\n', output.Trim());
    }

    [Fact]
    public void VersionFlag_NeverDrawsTheMainScreen()
    {
        (_, string output) = RunWithArgs(data.Path, "--version");

        Assert.DoesNotContain(Placeholder, output);
        Assert.DoesNotContain("╭", output);
        Assert.DoesNotContain("│ > ", output);
        Assert.DoesNotContain(Heading, output);
    }

    [Fact]
    public void VersionFlag_LeavesTheDataFolderEmpty()
    {
        (int exitCode, _) = RunWithArgs(data.Path, "--version");

        Assert.Equal(0, exitCode);
        Assert.Empty(Directory.EnumerateFileSystemEntries(data.Path));
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative")]
    public void VersionFlag_WorksWithAnInvalidDataFolder(string folder)
    {
        (int exitCode, string output) = RunWithArgs(folder, "--version");

        Assert.Equal(0, exitCode);
        Assert.Equal($"Times {AppVersion.Current}", output.Trim());
    }

    [Fact]
    public void VersionFlag_IsRecognisedInAnyPosition()
    {
        (int exitCode, string output) = RunWithArgs(data.Path, "x", "--version");

        Assert.Equal(0, exitCode);
        Assert.Equal($"Times {AppVersion.Current}", output.Trim());
        Assert.Empty(Directory.EnumerateFileSystemEntries(data.Path));
    }

    [Theory]
    [InlineData("--Version")]
    [InlineData("-v")]
    public void VersionFlag_MatchesExactlyAndOtherwiseStartsNormally(string arg)
    {
        TestConsole console = new TestConsole().Interactive();
        console.Input.PushTextWithEnter("quit");

        int exitCode = new App(console, FixedTimeProvider.At20261027(), new DayStore(data.Path)).Run([arg]);

        Assert.Equal(0, exitCode);
        Assert.Contains(Placeholder, console.Output);
        Assert.Contains(Heading, console.Output);
        Assert.DoesNotContain($"Times {AppVersion.Current}", console.Output);
    }

    [Fact]
    public void VersionFlag_WritesTheVersionAsPlainText()
    {
        TestConsole console = new TestConsole();

        App.WriteVersion(console, "1.0[red]x[/] [");

        Assert.Equal("Times 1.0[red]x[/] [", console.Output.Trim());
    }

    // Data folder location (job 027) -------------------------------------------------------

    private static string StripAnsi(string output) => Regex.Replace(output, @"\x1b\[[0-9;]*m", string.Empty);

    /// <summary>Runs <c>help</c>, scrolls to its bottom, then an empty line and <c>quit</c> on a terminal wide enough that the data-folder line never wraps.</summary>
    private static string RunHelp(string folder, bool folderFromEnvironment, bool ansi = false, InstanceLock? instanceLock = null)
    {
        TestConsole console = new TestConsole().Interactive();
        if (ansi)
        {
            console.EmitAnsiSequences();
        }

        console.Profile.Width = 300;
        console.Input.PushTextWithEnter("help");
        PageDown(console, HelpPages);
        console.Input.PushKey(ConsoleKey.Enter);
        console.Input.PushTextWithEnter("quit");
        int exitCode = new App(console, FixedTimeProvider.At20261027(), new DayStore(folder), folderFromEnvironment: folderFromEnvironment,
            instanceLock: instanceLock).Run([]);
        Assert.Equal(0, exitCode);
        return console.Output;
    }

    // AC-4
    [Theory]
    [InlineData("relative/dir")]
    [InlineData("~/times")]
    [InlineData("   ")]
    [InlineData("[red]x")]
    public void InvalidOverride_ReturnsTwo_NamesTheVariable_AndCreatesNothing(string folder)
    {
        string[] before = WorkingDirectoryEntries();
        TestConsole console = new TestConsole().Interactive().EmitAnsiSequences();
        console.Profile.Width = 300; // keep the error on one line, so the text can be matched as a whole
        console.Input.PushTextWithEnter("arrive 8:15");

        int exitCode = new App(console, FixedTimeProvider.At20261027(), new DayStore(folder), folderFromEnvironment: true).Run([]);

        Assert.Equal(2, exitCode);
        Assert.Matches(@"\x1b\[(31|91|38;5;9)mTimes cannot start", console.Output);
        string text = StripAnsi(console.Output).TrimEnd();
        Assert.Contains($"the data folder \"{folder}\" from TIMES_DATA_DIR is not an absolute path", text);
        Assert.EndsWith("Nothing was saved.", text);
        Assert.DoesNotContain("app-data folder could not be found", text);
        Assert.DoesNotContain(Heading, console.Output);
        Assert.Equal(before, WorkingDirectoryEntries());
    }

    // 051 AC-2, AC-3
    [Theory]
    [InlineData("", " ", true)]
    [InlineData(" ", "", true)]
    [InlineData("", "\u00a0", true)]
    [InlineData(" ", " ", false)]
    public void OverrideWithEdgeWhitespace_ReturnsTwo_NamesTheCause_AndCreatesNothing(string before, string after, bool absolute)
    {
        using TempFolder temp = new TempFolder(create: false);
        string folder = before + (absolute ? temp.Path : "rel") + after;
        string[] entries = WorkingDirectoryEntries();
        string cwd = Directory.GetCurrentDirectory();
        TestConsole console = new TestConsole().Interactive().EmitAnsiSequences();
        console.Profile.Width = 300; // keep the error on one line, so the text can be matched as a whole
        console.Input.PushTextWithEnter("arrive 8:15");

        int exitCode = new App(console, FixedTimeProvider.At20261027(), new DayStore(folder), folderFromEnvironment: true).Run([]);

        Assert.Equal(2, exitCode);
        Assert.Matches(@"^\s*\x1b\[(31|91|38;5;9)mTimes cannot start", console.Output);
        string text = StripAnsi(console.Output).TrimEnd();
        Assert.Contains($"the data folder \"{folder}\" from TIMES_DATA_DIR starts or ends with whitespace", text);
        Assert.EndsWith("Nothing was saved.", text);
        Assert.DoesNotContain("is not an absolute path", text);
        Assert.DoesNotContain(Heading, console.Output);
        Assert.Equal(entries, WorkingDirectoryEntries());
        Assert.Equal(cwd, Directory.GetCurrentDirectory());
        Assert.False(Directory.Exists(folder));
        Assert.False(Directory.Exists(temp.Path));
    }

    // 051 AC-2 (a tab is removed from the shown value, but the cause is still named)
    [Fact]
    public void OverrideWithTrailingTab_NamesTheCause()
    {
        using TempFolder temp = new TempFolder(create: false);
        TestConsole console = new TestConsole().Interactive();
        console.Profile.Width = 300;

        int exitCode = new App(console, FixedTimeProvider.At20261027(), new DayStore(temp.Path + "\t"), folderFromEnvironment: true).Run([]);

        Assert.Equal(2, exitCode);
        Assert.Contains($"the data folder \"{temp.Path}\" from TIMES_DATA_DIR starts or ends with whitespace", console.Output);
        Assert.False(Directory.Exists(temp.Path + "\t"));
    }

    // 051 AC-4
    [Theory]
    [InlineData("", " ", true)]
    [InlineData(" ", "", true)]
    [InlineData(" ", " ", false)]
    public void DefaultFolderWithEdgeWhitespace_ReturnsTwo_NamesTheCause_AndCreatesNothing(string before, string after, bool absolute)
    {
        using TempFolder temp = new TempFolder(create: false);
        string folder = before + (absolute ? temp.Path : "rel") + after;
        string[] entries = WorkingDirectoryEntries();
        TestConsole console = new TestConsole().Interactive().EmitAnsiSequences();
        console.Profile.Width = 300;
        console.Input.PushTextWithEnter("arrive 8:15");

        int exitCode = new App(console, FixedTimeProvider.At20261027(), new DayStore(folder), folderFromEnvironment: false).Run([]);

        Assert.Equal(2, exitCode);
        Assert.Matches(@"\x1b\[(31|91|38;5;9)mTimes cannot start", console.Output);
        string text = StripAnsi(console.Output).TrimEnd();
        Assert.Contains($"the data folder {folder} starts or ends with whitespace", text);
        Assert.EndsWith("Nothing was saved.", text);
        Assert.DoesNotContain("app-data folder could not be found", text);
        Assert.DoesNotContain("is not an absolute path", text);
        Assert.DoesNotContain("TIMES_DATA_DIR", text);
        Assert.DoesNotContain(Heading, console.Output);
        Assert.Equal(entries, WorkingDirectoryEntries());
        Assert.False(Directory.Exists(folder));
        Assert.False(Directory.Exists(temp.Path));
    }

    // 051 AC-7
    [Fact]
    public void ValidOverrideWithoutEdgeWhitespace_StartsNormally()
    {
        using TempFolder temp = new TempFolder(create: false);
        TestConsole console = new TestConsole().Interactive();
        console.Profile.Width = 300;
        console.Input.PushTextWithEnter("quit");

        int exitCode = new App(console, FixedTimeProvider.At20261027(), new DayStore(Path.Combine(temp.Path, "my times")), folderFromEnvironment: true).Run([]);

        Assert.Equal(0, exitCode);
        Assert.Contains(Heading, console.Output);
        Assert.DoesNotContain("Times cannot start", console.Output);
    }

    // 051 AC-7
    [Theory]
    [InlineData("", " ")]
    [InlineData(" ", "")]
    public void VersionFlag_WorksWithAnOverrideWithEdgeWhitespace(string before, string after)
    {
        using TempFolder temp = new TempFolder(create: false);
        string folder = before + temp.Path + after;
        TestConsole console = new TestConsole().Interactive();

        int exitCode = new App(console, FixedTimeProvider.At20261027(), new DayStore(folder), folderFromEnvironment: true).Run(["--version"]);

        Assert.Equal(0, exitCode);
        Assert.Equal($"Times {AppVersion.Current}", console.Output.Trim());
        Assert.False(Directory.Exists(folder));
    }

    // AC-4
    [Fact]
    public void InvalidOverride_ShowsMarkupInTheValueLiterally()
    {
        TestConsole console = new TestConsole().Interactive();
        console.Profile.Width = 300;

        int exitCode = new App(console, FixedTimeProvider.At20261027(), new DayStore("[red]x[/]"), folderFromEnvironment: true).Run([]);

        Assert.Equal(2, exitCode);
        Assert.Contains("the data folder \"[red]x[/]\" from TIMES_DATA_DIR", console.Output);
    }

    // 050 AC-5
    [Fact]
    public void InvalidOverride_MarkupAndControlCharacters_DoesNotThrow_AndShowsTheBracket()
    {
        TestConsole console = new TestConsole().Interactive();
        console.Profile.Width = 300;

        int exitCode = new App(console, FixedTimeProvider.At20261027(), new DayStore("[\u0007"), folderFromEnvironment: true).Run([]);

        Assert.Equal(2, exitCode);
        Assert.Contains("the data folder \"[\" from TIMES_DATA_DIR", console.Output);
        Assert.DoesNotContain("\u0007", console.Output, StringComparison.Ordinal);
    }

    // 050 AC-4
    [Fact]
    public void InvalidOverride_RemovesControlCharactersFromTheShownValue()
    {
        TestConsole console = new TestConsole().Interactive().EmitAnsiSequences();
        console.Profile.Width = 300;

        int exitCode = new App(console, FixedTimeProvider.At20261027(), new DayStore("rel\u001b[2Jdir\nx\ty"), folderFromEnvironment: true).Run([]);

        Assert.Equal(2, exitCode);
        Assert.Contains("the data folder \"rel[2Jdirxy\" from TIMES_DATA_DIR", StripAnsi(console.Output));
        string raw = console.Output;
        Assert.DoesNotContain("\u001b[2J", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("\t", raw, StringComparison.Ordinal);
        int start = raw.IndexOf("Times cannot start", StringComparison.Ordinal);
        int end = raw.IndexOf("Nothing was saved.", StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        string line = raw[start..end];
        Assert.DoesNotContain("\n", line, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", line, StringComparison.Ordinal);
    }

    // AC-5
    [Fact]
    public void Help_ShowsDefaultDataFolder_WithoutSource()
    {
        string output = RunHelp(data.Path, folderFromEnvironment: false, ansi: true);

        List<string[]> screens = TopScreens(output, 1);
        string[] bottom = screens[^1];
        int line = Array.FindIndex(bottom, l => l.Contains("Data folder:", StringComparison.Ordinal));
        Assert.True(line > 0);
        Assert.Equal($"Data folder: {data.Path}", bottom[line]);
        Assert.Equal($"Version: {AppVersion.Current}", bottom[line - 1]);
        Assert.Equal(DayViewCodesLine, bottom[line - 2]);
        Assert.StartsWith("╰", bottom[line - 3]);
        string shown = string.Join('\n', screens.SelectMany(screen => screen));
        foreach (Command command in CommandRegistry.CreateDefault().Commands.Concat([new Command("arrive", "a", "", _ => CommandResult.None)]))
        {
            Assert.Contains(command.Name, shown);
        }

        Assert.DoesNotContain("(from TIMES_DATA_DIR)", output);
    }

    // AC-7 (070)
    [Fact]
    public void Help_ShowsGreyVersion_DirectlyAboveDataFolder_WhichStaysLast()
    {
        string ansi = RunHelp(data.Path, folderFromEnvironment: false, ansi: true);

        string[] bottom = TopScreens(ansi, 1)[^1];
        int last = Array.FindLastIndex(bottom, l => l.Length > 0);
        Assert.True(last > 0);
        Assert.Equal($"Data folder: {data.Path}", bottom[last]);
        Assert.Equal($"Version: {AppVersion.Current}", bottom[last - 1]);
        Assert.Matches(@"\x1b\[(90|38;5;8)mVersion:\x1b\[0m " + Regex.Escape(AppVersion.Current), ansi);

        string plain = RunHelp(data.Path, folderFromEnvironment: false);
        int version = plain.IndexOf($"Version: {AppVersion.Current}", StringComparison.Ordinal);
        int folder = plain.IndexOf("Data folder:", StringComparison.Ordinal);
        Assert.True(version >= 0, "help shows no Version line");
        Assert.True(version < folder, "Version must come before Data folder");
    }

    private const string DayViewCodesLine =
        "Day view codes: AR arrival · LE leaving · BS break start · BE break end · CP checkpoint · ✓ booked";

    // AC-7 (076)
    [Fact]
    public void Help_ShowsDayViewCodes_BetweenTableAndVersion_DataFolderStaysLast()
    {
        string ansi = RunHelp(data.Path, folderFromEnvironment: false, ansi: true);

        string[] bottom = TopScreens(ansi, 1)[^1];
        int last = Array.FindLastIndex(bottom, l => l.Length > 0);
        Assert.True(last > 2);
        Assert.Equal($"Data folder: {data.Path}", bottom[last]);
        Assert.Equal($"Version: {AppVersion.Current}", bottom[last - 1]);
        Assert.Equal(DayViewCodesLine, bottom[last - 2]);
        Assert.StartsWith("╰", bottom[last - 3]);

        Assert.Matches(@"\x1b\[(90|38;5;8)mDay view codes:\x1b\[0m ", ansi);
        foreach (string code in new[] { "AR", "LE", "BS", "BE", "CP" })
        {
            Assert.Contains($"\u001b[38;5;75m{code}\u001b[0m", ansi);
        }

        string plain = RunHelp(data.Path, folderFromEnvironment: false);
        int codes = plain.IndexOf(DayViewCodesLine, StringComparison.Ordinal);
        int version = plain.IndexOf($"Version: {AppVersion.Current}", StringComparison.Ordinal);
        Assert.True(codes >= 0, "help shows no day view codes line");
        Assert.True(codes < version, "the codes line must come before Version");
    }

    // AC-5
    [Fact]
    public void Help_ShowsOverrideDataFolder_WithGreySource()
    {
        string output = RunHelp(data.Path, folderFromEnvironment: true);

        Assert.Contains($"Data folder: {data.Path} (from TIMES_DATA_DIR)", output);

        string ansi = RunHelp(data.Path, folderFromEnvironment: true, ansi: true);
        Assert.Contains(TopScreens(ansi, 1)[^1], l => l == $"Data folder: {data.Path} (from TIMES_DATA_DIR)");
        Assert.Matches(@"\x1b\[(90|38;5;8)mData folder:", ansi);
        Assert.Matches(@"\x1b\[(90|38;5;8)m\(from TIMES_DATA_DIR\)", ansi);
    }

    // AC-5
    [Fact]
    public void Help_DefaultDataFolder_LabelIsGrey_AndPathIsNot()
    {
        string ansi = RunHelp(data.Path, folderFromEnvironment: false, ansi: true);

        Assert.Matches(@"\x1b\[(90|38;5;8)mData folder:\x1b\[0m " + Regex.Escape(data.Path), ansi);
    }

    // AC-5
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Help_DataFolderWithBrackets_RendersLiterally(bool folderFromEnvironment)
    {
        string folder = Path.Combine(data.Path, "[x]");

        string output = RunHelp(folder, folderFromEnvironment);

        Assert.Contains($"Data folder: {folder}", output);
        AssertLeftNothing(folder);
    }

    // 085 AC-8
    [Theory]
    [InlineData(false, "")]
    [InlineData(true, " (from TIMES_DATA_DIR)")]
    public void Help_DataFolderWithFormatCharacter_ShowsCleanedPath(bool folderFromEnvironment, string suffix)
    {
        string folder = Path.Combine(data.Path, "a\u202Eb");

        string ansi = RunHelp(folder, folderFromEnvironment, ansi: true);

        string last = TopScreens(ansi, 1)[^1].Last(l => l.Length > 0);
        Assert.Equal($"Data folder: {Path.Combine(data.Path, "ab")}{suffix}", last);
        Assert.DoesNotContain("\u202E", ansi, StringComparison.Ordinal);
        Assert.DoesNotContain("\u202E", RunHelp(folder, folderFromEnvironment), StringComparison.Ordinal);
    }

    // 085 AC-8
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DataFolderWithFormatCharacter_BooksIntoTheRealFolder(bool folderFromEnvironment)
    {
        string folder = Path.Combine(data.Path, "a\u202Eb");
        TestConsole console = new TestConsole().Interactive();
        console.Input.PushTextWithEnter("arrive 8:15");
        console.Input.PushTextWithEnter("quit");

        int exitCode = new App(console, FixedTimeProvider.At20261027(), new DayStore(folder), folderFromEnvironment: folderFromEnvironment).Run([]);

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(Path.Combine(folder, "2026-10-27.json")));
        Assert.False(Directory.Exists(Path.Combine(data.Path, "ab")));
    }

    // AC-6
    [Fact]
    public void ValidOverride_BooksIntoThatFolder_AndHelpShowsIt()
    {
        using TempFolder overrideFolder = new TempFolder(create: false);
        string[] before = WorkingDirectoryEntries();
        TestConsole console = new TestConsole().Interactive();
        console.Profile.Width = 300;
        console.Input.PushTextWithEnter("arrive 8:15");
        console.Input.PushTextWithEnter("help");
        PageDown(console, HelpPages);
        console.Input.PushTextWithEnter("quit");

        int exitCode = new App(console, FixedTimeProvider.At20261027(), new DayStore(overrideFolder.Path), folderFromEnvironment: true).Run([]);

        Assert.Equal(0, exitCode);
        Assert.Equal(
            [Path.Combine(overrideFolder.Path, "2026-10-27.json")],
            Directory.EnumerateFileSystemEntries(overrideFolder.Path));
        Assert.Empty(Directory.EnumerateFileSystemEntries(data.Path));
        Assert.Equal(before, WorkingDirectoryEntries());
        Assert.Contains($"Data folder: {overrideFolder.Path} (from TIMES_DATA_DIR)", console.Output);
    }

    // AC-7
    [Theory]
    [InlineData("")]
    [InlineData("relative/dir")]
    [InlineData("   ")]
    public void VersionFlag_WorksWithAnInvalidOverride(string folder)
    {
        TestConsole console = new TestConsole().Interactive();

        int exitCode = new App(console, FixedTimeProvider.At20261027(), new DayStore(folder), folderFromEnvironment: true).Run(["--version"]);

        Assert.Equal(0, exitCode);
        Assert.Equal($"Times {AppVersion.Current}", console.Output.Trim());
    }

    // Project quicklist (job 030) ---------------------------------------------------------

    private string ProjectsFile => Path.Combine(data.Path, "projects.json");

    private ProjectList ReadProjects() => ProjectList.FromJson(File.ReadAllText(ProjectsFile));

    /// <summary>Runs <paramref name="command"/> on an ANSI console and returns its output, to check colours.</summary>
    private string RunAnsi(string command)
    {
        TestConsole console = new TestConsole().Interactive().EmitAnsiSequences();
        console.Profile.Width = 200;
        console.Input.PushTextWithEnter(command);
        console.Input.PushTextWithEnter("quit");
        Assert.Equal(0, new App(console, FixedTimeProvider.At20261027(), new DayStore(data.Path), instanceLock: sideLock).Run([]));
        return console.Output;
    }

    // AC-1 (030)
    [Fact]
    public void AddProject_SavesProject_AndConfirmsInTopArea()
    {
        (int exitCode, string output) = RunCommands("addproject 1234 Website relaunch", "quit");

        Assert.Equal(0, exitCode);
        Assert.Equal([new Project(1234, "Website relaunch")], ReadProjects().Projects);
        Assert.Equal("{\"projects\":[{\"workItem\":1234,\"title\":\"Website relaunch\"}]}", File.ReadAllText(ProjectsFile));
        string[] frame = LastLayout(output);
        AssertLayout(frame);
        Assert.Contains(frame[..^4], l => l.Contains("Added project 1: #1234 Website relaunch", StringComparison.Ordinal));
        Assert.Equal(string.Empty, frame[^4].Trim());
    }

    // AC-2 (030)
    [Fact]
    public void AddProject_NumbersInOrder_AndKeepsNumbersAfterRestart()
    {
        (_, string first) = RunCommands("addproject 500 First", "addproject 20 Second", "quit");

        Assert.Contains("Added project 1: #500 First", first);
        Assert.Contains("Added project 2: #20 Second", first);

        (_, string restarted) = RunCommands("addproject 7 Third", "projects", "quit");

        Assert.Contains("Added project 3: #7 Third", restarted);
        string frame = string.Join('\n', LastLayout(restarted));
        Assert.Matches(@"│ 1 +│ #500 +│ First +│[\s\S]*│ 2 +│ #20 +│ Second +│[\s\S]*│ 3 +│ #7 +│ Third +│", frame);
        Assert.Equal([new Project(500, "First"), new Project(20, "Second"), new Project(7, "Third")], ReadProjects().Projects);
    }

    // AC-3 (030)
    [Fact]
    public void Projects_ShowsTable_WithHeaders_InQuicklistOrder()
    {
        File.WriteAllText(ProjectsFile, "{\"projects\":[{\"workItem\":99,\"title\":\"Zeta\"},{\"workItem\":1,\"title\":\"Alpha\"}]}");

        (int exitCode, string output) = RunCommands("projects", "quit");

        Assert.Equal(0, exitCode);
        string[] frame = LastLayout(output);
        AssertLayout(frame);
        Assert.Contains(frame, l => Regex.IsMatch(l, @"│ # +│ Work item +│ Title +│"));
        int zeta = Array.FindIndex(frame, l => Regex.IsMatch(l, @"│ 1 +│ #99 +│ Zeta +│"));
        int alpha = Array.FindIndex(frame, l => Regex.IsMatch(l, @"│ 2 +│ #1 +│ Alpha +│"));
        Assert.True(zeta > 0 && alpha > zeta);
        Assert.Contains(frame, l => l.StartsWith('╭'));
        Assert.Equal(string.Empty, frame[^4].Trim());
    }

    // AC-3 (030)
    [Fact]
    public void Projects_TableHeadersAreGrey()
    {
        File.WriteAllText(ProjectsFile, "{\"projects\":[{\"workItem\":99,\"title\":\"Zeta\"}]}");

        string output = RunAnsi("projects");

        Assert.Matches(@"\x1b\[(90|38;5;8)m#\x1b\[0m", output);
        Assert.Matches(@"\x1b\[(90|38;5;8)mWork item\x1b\[0m", output);
        Assert.Matches(@"\x1b\[(90|38;5;8)mTitle\x1b\[0m", output);
    }

    // AC-3 (030)
    [Fact]
    public void Projects_WithNoFile_ShowsGreyNote_AndCreatesNothing()
    {
        using TempFolder missing = new TempFolder(create: false);

        (int exitCode, string output) = Run(c =>
        {
            c.Input.PushTextWithEnter("projects");
            c.Input.PushTextWithEnter("quit");
        }, dataFolder: missing.Path);

        Assert.Equal(0, exitCode);
        Assert.Contains("No projects yet. Add one with addproject <work item> <title>.", output);
        AssertLeftNothing(missing.Path);

        (_, string existing) = RunCommands("projects", "quit");
        Assert.Contains("No projects yet.", existing);
        Assert.Empty(Directory.EnumerateFileSystemEntries(data.Path));
    }

    // AC-3 (030)
    [Fact]
    public void Projects_WithEmptyList_ShowsGreyNote_AndLeavesFileUnchanged()
    {
        File.WriteAllText(ProjectsFile, "{\"projects\":[]}");

        (_, string output) = RunCommands("projects", "quit");

        Assert.Contains("No projects yet. Add one with addproject <work item> <title>.", output);
        Assert.Equal("{\"projects\":[]}", File.ReadAllText(ProjectsFile));
        Assert.Matches(@"\x1b\[(90|38;5;8)mNo projects yet\.", RunAnsi("projects"));
    }

    // AC-3 (030)
    [Fact]
    public void Projects_WithArgument_ShowsHint()
    {
        (_, string output) = RunCommands("projects x", "quit");

        AssertOnHintRow(LastLayout(output), "projects takes no arguments.");
    }

    // AC-4 (030)
    [Theory]
    [InlineData("addproject 42 [red]Fix[/]", "[red]Fix[/]")]
    [InlineData("addproject 42    Two  inner   spaces   ", "Two  inner   spaces")]
    [InlineData("addproject 42 [bold]x", "[bold]x")]
    [InlineData("addproject 42 a]b[c", "a]b[c")]
    public void AddProject_TitleIsTrimmed_ShownLiterally_AndRoundTrips(string command, string title)
    {
        (int exitCode, string output) = RunCommands(command, "projects", "quit");

        Assert.Equal(0, exitCode);
        Assert.Equal([new Project(42, title)], ReadProjects().Projects);
        Assert.Contains($"Added project 1: #42 {title}", output);
        Assert.Contains(LastLayout(output), l => l.Contains($"│ #42 ", StringComparison.Ordinal) && l.Contains(title, StringComparison.Ordinal));
        Assert.DoesNotContain("failed:", output);
    }

    // AC-4 (030)
    [Fact]
    public void AddProject_MarkupTitle_IsNotRenderedAsMarkup()
    {
        string output = RunAnsi("addproject 42 [red]Fix[/]");

        Assert.Contains("#42 [red]Fix[/]", output);
    }

    // AC-5 (030)
    [Theory]
    [InlineData("addproject", "addproject: say the work item number and the title. Use addproject <work item> <title>.")] // AC-5 (057)
    [InlineData("addproject   ", "addproject: say the work item number and the title. Use addproject <work item> <title>.")] // AC-5 (057)
    [InlineData("addproject 1234", "addproject: the title is missing. Use addproject <work item> <title>.")] // AC-1 (057)
    [InlineData("addproject 1234   ", "addproject: the title is missing. Use addproject <work item> <title>.")] // AC-4 (057)
    [InlineData("ap 1234", "addproject: the title is missing. Use addproject <work item> <title>.")] // AC-4 (057)
    [InlineData("addproject abc", "addproject: abc is not a work item number. Use addproject <work item> <title>.")] // AC-2 (057)
    [InlineData("addproject -5", "addproject: -5 is not a work item number.")] // AC-3 (057)
    [InlineData("addproject 0", "addproject: 0 is not a work item number.")] // AC-3 (057)
    [InlineData("addproject 12.5", "addproject: 12.5 is not a work item number.")] // AC-3 (057)
    [InlineData("addproject +7", "addproject: +7 is not a work item number.")] // AC-3 (057)
    [InlineData("addproject 2147483648", "addproject: 2147483648 is not a work item number.")] // AC-3 (057)
    [InlineData("addproject 99999999999999999999", "addproject: 99999999999999999999 is not a work item number.")] // AC-3 (057)
    [InlineData("addproject [red]1[/]", "addproject: [red]1[/] is not a work item number.")] // AC-3 (057)
    [InlineData("addproject abc Title", "addproject: abc is not a work item number.")]
    [InlineData("addproject -5 Title", "addproject: -5 is not a work item number.")]
    [InlineData("addproject 0 Title", "addproject: 0 is not a work item number.")]
    [InlineData("addproject 12.5 Title", "addproject: 12.5 is not a work item number.")]
    [InlineData("addproject +7 Title", "addproject: +7 is not a work item number.")]
    [InlineData("addproject 2147483648 Title", "addproject: 2147483648 is not a work item number.")]
    [InlineData("addproject 99999999999999999999 Title", "addproject: 99999999999999999999 is not a work item number.")]
    [InlineData("addproject [red]1[/] Title", "addproject: [red]1[/] is not a work item number.")]
    public void AddProject_InvalidInput_ShowsHint_AndWritesNothing(string command, string expected)
    {
        (int exitCode, string output) = RunWide(120, command, "quit");

        Assert.Equal(0, exitCode);
        AssertOnHintRow(LastLayout(output), expected);
        Assert.False(File.Exists(ProjectsFile));
        Assert.Empty(Directory.EnumerateFileSystemEntries(data.Path));
    }

    // AC-5 (030): the line editor drops C0/C1 controls as they are typed, so a line separator
    // (Unicode category Zl, not a control char) is what can reach the handler.
    [Fact]
    public void AddProject_TitleWithLineSeparator_ShowsHint_AndWritesNothing()
    {
        (_, string output) = Run(c =>
        {
            c.Profile.Width = 120;
            c.Input.PushText("addproject 12 a");
            c.Input.PushKey(new ConsoleKeyInfo('\u2028', ConsoleKey.NoName, false, false, false));
            c.Input.PushTextWithEnter("b");
            c.Input.PushTextWithEnter("quit");
        });

        AssertOnHintRow(LastLayout(output), "addproject: the title must not contain control characters. Retype it without the hidden characters.");
        Assert.False(File.Exists(ProjectsFile));
    }

    // AC-5 (030)
    [Fact]
    public void AddProject_TitleTooLong_ShowsHint_AndWritesNothing()
    {
        (_, string output) = RunWide(120, "addproject 12 " + new string('x', 201), "quit");

        AssertOnHintRow(LastLayout(output), "addproject: the title is longer than 200 characters.");
        Assert.False(File.Exists(ProjectsFile));

        (_, string ok) = RunCommands("addproject 12 " + new string('x', 200), "quit");
        Assert.Equal(200, ReadProjects().Projects[0].Title.Length);
        Assert.DoesNotContain("longer than", ok);
    }

    // AC-5 (030)
    [Fact]
    public void AddProject_DuplicateWorkItem_NamesExistingProject_AndLeavesFileUnchanged()
    {
        File.WriteAllText(ProjectsFile, "{\"projects\":[{\"workItem\":1,\"title\":\"A\"},{\"workItem\":1234,\"title\":\"B\"}]}");
        byte[] before = File.ReadAllBytes(ProjectsFile);

        (int exitCode, string output) = RunCommands("addproject 1234 Other title", "quit");

        Assert.Equal(0, exitCode);
        AssertOnHintRow(LastLayout(output), "addproject: work item #1234 is already project 2.");
        Assert.Equal(before, File.ReadAllBytes(ProjectsFile));
    }

    // AC-5 (030)
    [Theory]
    [InlineData("addproject abc Title")]
    [InlineData("addproject 1234")] // AC-7 (057)
    [InlineData("addproject abc")] // AC-7 (057)
    public void AddProject_InvalidInput_HintIsRed(string command)
    {
        Assert.Matches(@"\x1b\[(31|91|38;5;9)maddproject: ", RunAnsi(command));
    }

    // AC-7 (030)
    [Theory]
    [InlineData("{not json")]
    [InlineData("{\"projects\":{}}")]
    [InlineData("{\"projects\":[{\"workItem\":1,\"title\":\"A\"},{\"workItem\":1,\"title\":\"B\"}]}")]
    [InlineData("{\"projects\":[{\"workItem\":1,\"title\":\"abc\u202Edef\"}]}")] // AC-7 (056)
    [InlineData("{\"projects\":[{\"workItem\":1,\"title\":\"abc\\u200bdef\"}]}")]
    [InlineData("{\"projects\":[{\"workItem\":1,\"title\":\"abc\\ud800def\"}]}")]
    public void UnreadableProjectsFile_ShowsHint_AndAddProjectLeavesBytesUnchanged(string contents)
    {
        File.WriteAllText(ProjectsFile, contents);
        byte[] before = File.ReadAllBytes(ProjectsFile);

        (int exitCode, string output) = RunWide(200, "addproject 5 New", "quit");

        Assert.Equal(0, exitCode);
        AssertOnHintRow(LastLayout(output), "addproject: the project list cannot be read: projects.json is not valid. Nothing was added. Fix or delete projects.json in the data folder (help shows where).");
        Assert.Equal(before, File.ReadAllBytes(ProjectsFile));

        (_, string listed) = RunWide(200, "projects", "quit");
        AssertOnHintRow(LastLayout(listed), "projects: the project list cannot be read: projects.json is not valid. Fix or delete projects.json in the data folder (help shows where).");
        Assert.DoesNotContain("   at ", output + listed);
        Assert.Equal(before, File.ReadAllBytes(ProjectsFile));
    }

    // AC-7 (030)
    [Fact]
    public void TooLargeProjectsFile_ShowsHint_AndLeavesBytesUnchanged()
    {
        File.WriteAllText(ProjectsFile, new string(' ', (int)ProjectStore.MaxFileBytes + 1) + "{\"projects\":[]}");
        byte[] before = File.ReadAllBytes(ProjectsFile);

        (_, string output) = RunWide(200, "addproject 5 New", "projects", "quit");

        Assert.Contains("addproject: the project list cannot be read: projects.json is too large. Nothing was added. Fix or delete projects.json in the data folder (help shows where).", output);
        AssertOnHintRow(LastLayout(output), "projects: the project list cannot be read: projects.json is too large. Fix or delete projects.json in the data folder (help shows where).");
        Assert.Equal(before, File.ReadAllBytes(ProjectsFile));
    }

    // AC-7 (030)
    [Fact]
    public void UnreadableProjectsFile_HintIsRed()
    {
        File.WriteAllText(ProjectsFile, "{not json");

        Assert.Matches(@"\x1b\[(31|91|38;5;9)mprojects: the project list cannot be read: projects\.json is not valid\. Fix or delete projects\.json in the data folder \(help shows where\)\.", RunAnsi("projects"));
        Assert.Matches(@"\x1b\[(31|91|38;5;9)maddproject: the project list cannot be read: projects\.json is not valid\. Nothing was added\. Fix or delete", RunAnsi("addproject 5 New"));
        Assert.Matches(@"\x1b\[(31|91|38;5;9)mcheckpoint: the project list cannot be read: projects\.json is not valid\. Nothing was booked\. Fix or delete", RunAnsi("checkpoint 9:30 #1 Standup"));
    }

    // AC-7 (059)
    [Fact]
    public void UnreadableProjectsFile_PanelAndAllHints_ShareOneNextStep()
    {
        File.WriteAllText(ProjectsFile, "{not json");
        const string Next = "Fix or delete projects.json in the data folder (help shows where).";
        Assert.Equal(Next, App.UnreadableProjectsNextStep);

        (_, string listed) = RunWide(200, "projects", "quit");
        (_, string added) = RunWide(200, "addproject 5 New", "quit");
        (_, string booked) = RunWide(200, "checkpoint 9:30 #1 Standup", "quit");

        Assert.Contains(Next, PanelRows(LastLayout(added))[2]);
        Assert.EndsWith("valid. " + Next, LastLayout(listed)[^4].TrimEnd());
        Assert.EndsWith("added. " + Next, LastLayout(added)[^4].TrimEnd());
        Assert.EndsWith("booked. " + Next, LastLayout(booked)[^4].TrimEnd());
        Assert.False(File.Exists(DayFile("2026-10-27")));
        Assert.Equal("{not json", File.ReadAllText(ProjectsFile));
    }

    // AC-6 (059)
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(7)]
    public void ReadableProjectsFile_ShowsNoNextStep(int count)
    {
        WriteProjects(count);

        (_, string output) = RunWide(200, "projects", "addproject 999 New", "checkpoint 9:30 #1 Standup", "quit");

        Assert.DoesNotContain("Fix or delete", output);
        Assert.DoesNotContain("cannot be read", output);
    }

    // AC-7 (030)
    [Fact]
    public void AddProject_SaveFailure_ShowsRedHint_NotStackTrace()
    {
        UseSideLock();
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.WriteAllText(ProjectsFile, "{\"projects\":[]}");
        byte[] before = File.ReadAllBytes(ProjectsFile);
        UnixFileMode mode = File.GetUnixFileMode(data.Path);
        File.SetUnixFileMode(data.Path, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        string output;
        try
        {
            output = RunAnsi("addproject 5 New");
        }
        finally
        {
            File.SetUnixFileMode(data.Path, mode);
        }

        Assert.Matches(@"\x1b\[(31|91|38;5;9)maddproject: could not save the project list\.", output);
        Assert.DoesNotContain("   at ", output);
        Assert.DoesNotContain("failed:", output);
        Assert.DoesNotContain("Added project", output);
        Assert.Equal(before, File.ReadAllBytes(ProjectsFile));
    }

    // AC-8 (030)
    [Fact]
    public void AddProject_CreatesNoDayFile_AndDayViewIgnoresProjects()
    {
        (_, string output) = RunCommands("addproject 1234 Website relaunch", "prev", "next", "quit");

        Assert.Equal(["projects.json"], Directory.EnumerateFileSystemEntries(data.Path).Select(Path.GetFileName));
        Assert.Equal(Heading, LastHeading(output));
        Assert.Contains("No entries yet.", LastFrame(output));
        Assert.DoesNotContain("not a valid day file", output);
    }

    // AC-8 (030)
    [Fact]
    public void ProjectsFile_DoesNotAffectBookings()
    {
        File.WriteAllText(ProjectsFile, "{\"projects\":[{\"workItem\":1,\"title\":\"A\"}]}");

        (_, string output) = RunCommands("arrive 8:00", "quit");

        Assert.Equal("{\"entries\":[{\"kind\":\"arrival\",\"time\":\"08:00\"}]}", ReadDay("2026-10-27"));
        Assert.Equal("{\"projects\":[{\"workItem\":1,\"title\":\"A\"}]}", File.ReadAllText(ProjectsFile));
        Assert.Equal(string.Empty, LastLayout(output)[^4].Trim());
    }

    // AC-8 (030)
    [Fact]
    public void Help_ListsProjectCommands_WithDescriptions()
    {
        // The help table is taller than one page: page down so every row has been drawn.
        (_, string output) = Run(c =>
        {
            c.Profile.Width = 120;
            c.Input.PushTextWithEnter("help");
            PageDown(c, HelpPages);
            c.Input.PushTextWithEnter("quit");
        });

        Assert.Matches(@"addproject +│ ap +│ Add a project to the quicklist: <work item> <title>", output);
        Assert.Matches(@"projects +│ pr +│ List the projects on the quicklist", output);
    }

    // Quicklist panel (job 033) ------------------------------------------------------------

    private void WriteProjects(int count) =>
        File.WriteAllText(ProjectsFile, new ProjectList(Enumerable.Range(1, count).Select(i => new Project(100 + i, $"Project {i}"))).ToJson());

    /// <summary>The rows of the quicklist panel at the top of <paramref name="frame"/>, from its top border to its bottom border.</summary>
    private static string[] PanelRows(string[] frame)
    {
        Assert.StartsWith("╭─Projects", frame[0]);
        int bottom = Array.FindIndex(frame, l => l.StartsWith('╰'));
        return frame[..(bottom + 1)];
    }

    // AC-1 (033)
    [Fact]
    public void QuicklistPanel_IsOnRow1_AboveTheDayHeading()
    {
        WriteProjects(2);

        (int exitCode, string output) = RunCommands("quit");

        Assert.Equal(0, exitCode);
        Assert.True(output.IndexOf("╭─Projects", StringComparison.Ordinal) < output.IndexOf(Heading, StringComparison.Ordinal));
        string[] frame = LastLayout(output);
        AssertLayout(frame);
        string[] panel = PanelRows(frame);
        Assert.Equal(4, panel.Length);
        Assert.All(panel, l => Assert.Equal(80, l.Length));
        Assert.Contains(Heading, frame[panel.Length]);
    }

    // AC-1, AC-5 (033)
    [Fact]
    public void QuicklistPanel_WithNoFile_ShowsHowToAddAProject()
    {
        (_, string output) = RunCommands("quit");

        string[] panel = PanelRows(LastLayout(output));
        Assert.Equal(EmptyPanelRows, panel.Length);
        Assert.Contains("No projects yet. Add one with addproject <work item> <title>.", panel[1]);
    }

    // AC-2 (033)
    [Fact]
    public void QuicklistPanel_ListsProjectsInOrder()
    {
        File.WriteAllText(ProjectsFile, "{\"projects\":[{\"workItem\":99,\"title\":\"Zeta\"},{\"workItem\":1,\"title\":\"[red]Alpha[/]\"}]}");

        (_, string output) = RunCommands("quit");

        string[] panel = PanelRows(LastLayout(output));
        Assert.Equal("│ 1 #99 Zeta", panel[1].TrimEnd('│', ' '));
        Assert.Equal("│ 2 #1 [red]Alpha[/]", panel[2].TrimEnd('│', ' '));
    }

    // AC-7, AC-11 (033)
    [Fact]
    public void AddProject_ShowsInPanel_OnTheNextFrame()
    {
        WriteProjects(1);

        (_, string output) = RunCommands("addproject 1234 Website", "quit");

        List<string[]> frames = Frames(output);
        Assert.Equal(2, frames.Count);
        Assert.DoesNotContain(frames[0], l => l.Contains("#1234", StringComparison.Ordinal));
        string[] frame = frames[1];
        AssertLayout(frame);
        string[] panel = PanelRows(frame);
        Assert.Equal(4, panel.Length);
        Assert.Equal("│ 2 #1234 Website", panel[2].TrimEnd('│', ' '));
        Assert.Contains(frame[panel.Length..^4], l => l.Contains("Added project 2: #1234 Website", StringComparison.Ordinal));
    }

    // AC-7 (033)
    [Fact]
    public void AddProject_FirstProject_ReplacesTheEmptyLine()
    {
        (_, string output) = RunCommands("addproject 1234 Website", "quit");

        string[] panel = PanelRows(LastLayout(output));
        Assert.Equal("│ 1 #1234 Website", panel[1].TrimEnd('│', ' '));
        Assert.DoesNotContain(panel, l => l.Contains("No projects yet", StringComparison.Ordinal));
    }

    // AC-7 (033)
    [Theory]
    [InlineData("addproject abc Title")]
    [InlineData("addproject 101 Duplicate")]
    [InlineData("addproject 5")]
    public void FailedAddProject_LeavesPanelUnchanged(string command)
    {
        WriteProjects(2);

        (_, string output) = RunCommands(command, "quit");

        List<string[]> frames = Frames(output);
        Assert.Equal(2, frames.Count);
        Assert.Equal(PanelRows(frames[0]), PanelRows(frames[1]));
        Assert.Contains("addproject", frames[1][^4]);
    }

    // AC-8 (033)
    [Theory]
    [InlineData("{not json")]
    [InlineData("{\"projects\":[{\"workItem\":1,\"title\":\"A\"},{\"workItem\":1,\"title\":\"B\"}]}")]
    [InlineData("{\"projects\":[{\"workItem\":1,\"title\":\"abc\u202Edef\"}]}")] // AC-7 (056)
    [InlineData("{\"projects\":[{\"workItem\":1,\"title\":\"abc\\u200bdef\"}]}")]
    [InlineData("{\"projects\":[{\"workItem\":1,\"title\":\"abc\\ud800def\"}]}")]
    public void CorruptProjectsFile_ShowsRedReasonInPanel_AndBookingStillWorks(string contents)
    {
        File.WriteAllText(ProjectsFile, contents);

        (int exitCode, string output) = RunCommands("arrive 8:00", "next", "prev", "quit");

        Assert.Equal(0, exitCode);
        List<string[]> frames = Frames(output);
        Assert.Equal(4, frames.Count);
        foreach (string[] frame in frames)
        {
            AssertLayout(frame);
            string[] panel = PanelRows(frame);
            Assert.Equal(4, panel.Length);
            Assert.Contains("The project list cannot be read: projects.json is not valid.", panel[1]);
            Assert.Contains(App.UnreadableProjectsNextStep, panel[2]);
        }

        Assert.Equal("{\"entries\":[{\"kind\":\"arrival\",\"time\":\"08:00\"}]}", ReadDay("2026-10-27"));
        Assert.Matches(@"1 +AR +08:00", string.Join('\n', frames[^1]));
        Assert.Equal(contents, File.ReadAllText(ProjectsFile));
        Assert.DoesNotContain("   at ", output);
    }

    // AC-8 (033)
    [Fact]
    public void CorruptProjectsFile_ReasonIsRed()
    {
        File.WriteAllText(ProjectsFile, "{not json");

        string output = RunAnsi("quit");

        Assert.Matches(@"\x1b\[(91|38;5;9)mThe project list cannot be read: projects\.json is not valid\.", output);
        Assert.Matches(@"\x1b\[(90|38;5;8)mFix or delete projects\.json in the data folder \(help shows where\)\.", output);
    }

    // AC-8 (033)
    [Fact]
    public void TooLargeProjectsFile_ShowsReasonInPanel()
    {
        File.WriteAllText(ProjectsFile, new string(' ', (int)ProjectStore.MaxFileBytes + 1) + "{\"projects\":[]}");

        (int exitCode, string output) = RunCommands("quit");

        Assert.Equal(0, exitCode);
        string[] panel = PanelRows(LastLayout(output));
        Assert.Equal(4, panel.Length);
        Assert.Contains("The project list cannot be read: projects.json is too large.", panel[1]);
        Assert.Contains(App.UnreadableProjectsNextStep, panel[2]);
    }

    // AC-6 (058)
    [Fact]
    public void FifoProjectsFile_AppStartsWithoutHanging_AndShowsReasonInPanel()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        SpecialFiles.MakeFifo(ProjectsFile);
        int exitCode = -1;
        string output = string.Empty;

        Assert.Null(SpecialFiles.RunWithin(TimeSpan.FromSeconds(10), () => (exitCode, output) = RunCommands("quit")));

        Assert.Equal(0, exitCode);
        string[] panel = PanelRows(LastLayout(output));
        Assert.Equal(4, panel.Length);
        Assert.Contains("The project list cannot be read: projects.json is not a regular file.", panel[1]);
        Assert.Contains(App.UnreadableProjectsNextStep, panel[2]);
        Assert.Matches(@"\x1b\[(91|38;5;9)mThe project list cannot be read: projects\.json is not a regular file\.", RunAnsi("quit"));
        Assert.True(SpecialFiles.IsFifo(ProjectsFile));
    }

    // AC-7 (058)
    [Fact]
    public void FifoProjectsFile_CommandsShowHint_AndChangeNothing()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        SpecialFiles.MakeFifo(ProjectsFile);
        string listed = string.Empty;
        string added = string.Empty;
        string booked = string.Empty;

        Assert.Null(SpecialFiles.RunWithin(TimeSpan.FromSeconds(10), () =>
        {
            (_, listed) = RunWide(200, "projects", "quit");
            (_, added) = RunWide(200, "addproject 1234 Foo", "quit");
            (_, booked) = RunWide(200, "checkpoint #1 Foo", "quit");
        }));

        AssertOnHintRow(LastLayout(listed), "projects: the project list cannot be read: projects.json is not a regular file. Fix or delete projects.json in the data folder (help shows where).");
        AssertOnHintRow(LastLayout(added), "addproject: the project list cannot be read: projects.json is not a regular file. Nothing was added. Fix or delete projects.json in the data folder (help shows where).");
        AssertOnHintRow(LastLayout(booked), "checkpoint: the project list cannot be read: projects.json is not a regular file. Nothing was booked. Fix or delete projects.json in the data folder (help shows where).");
        Assert.DoesNotContain("   at ", listed + added + booked);
        Assert.True(SpecialFiles.IsFifo(ProjectsFile));
        Assert.Equal([ProjectsFile], Directory.EnumerateFileSystemEntries(data.Path));
    }

    // AC-8 (033)
    [Fact]
    public void UnreadableProjectsFile_AccessDenied_ShowsReasonInPanel()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        WriteProjects(1);
        File.SetUnixFileMode(ProjectsFile, UnixFileMode.None);
        try
        {
            (int exitCode, string output) = RunCommands("arrive 8:00", "quit");

            Assert.Equal(0, exitCode);
            string[] panel = PanelRows(LastLayout(output));
            Assert.Equal(4, panel.Length);
            Assert.Contains("The project list cannot be read:", panel[1]);
            Assert.Contains(App.UnreadableProjectsNextStep, panel[2]);
            Assert.True(File.Exists(DayFile("2026-10-27")));
        }
        finally
        {
            File.SetUnixFileMode(ProjectsFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    // AC-9 (033)
    [Fact]
    public void DrawingThePanel_WritesNothing()
    {
        (int exitCode, string output) = RunCommands("prev", "next", "quit");

        Assert.Equal(0, exitCode);
        Assert.Equal(3, Frames(output).Count);
        Assert.False(File.Exists(ProjectsFile));
        Assert.Empty(Directory.EnumerateFileSystemEntries(data.Path));
    }

    // AC-9 (033)
    [Fact]
    public void DrawingThePanel_WithMissingFolder_CreatesNothing()
    {
        using TempFolder missing = new TempFolder(create: false);

        (int exitCode, _) = Run(c =>
        {
            c.Input.PushTextWithEnter("prev");
            c.Input.PushTextWithEnter("next");
            c.Input.PushTextWithEnter("quit");
        }, dataFolder: missing.Path);

        Assert.Equal(0, exitCode);
        AssertLeftNothing(missing.Path);
    }

    // AC-6 (033)
    [Fact]
    public void ManyProjects_PanelShowsFirstFour_AndMoreLine()
    {
        WriteProjects(9);

        (_, string output) = RunCommands("quit");

        string[] frame = LastLayout(output);
        AssertLayout(frame);
        string[] panel = PanelRows(frame);
        Assert.Equal(7, panel.Length);
        Assert.Equal("│ 4 #104 Project 4", panel[4].TrimEnd('│', ' '));
        Assert.Equal("│ … 5 more. Type projects to see all.", panel[5].TrimEnd('│', ' '));
        Assert.Contains(Heading, frame[7]);
    }

    // AC-10 (033)
    [Fact]
    public void ShortTerminal_PanelIsKept_DayViewIsCutWithCue()
    {
        WriteProjects(5);

        (int exitCode, string output) = Run(c =>
        {
            c.Profile.Height = 12;
            c.Input.PushTextWithEnter("arrive 8:00");
            c.Input.PushTextWithEnter("quit");
        });

        Assert.Equal(0, exitCode);
        List<string[]> frames = Frames(output);
        Assert.Equal(2, frames.Count);
        foreach (string[] frame in frames)
        {
            AssertLayout(frame, 12);
            Assert.Equal(7, PanelRows(frame).Length);
            Assert.Matches(@"^↓ \d+ more lines below · PgDn / Ctrl\+↓", frame[^5]);
        }

        Assert.Equal("{\"entries\":[{\"kind\":\"arrival\",\"time\":\"08:00\"}]}", ReadDay("2026-10-27"));
    }

    // AC-11 (033, round 2)
    [Fact]
    public void ProjectsCommand_StillShowsTable_AndHidesThePanelUntilTheNextCommand()
    {
        WriteProjects(2);

        (_, string output) = RunCommands("projects", "", "quit");

        List<string[]> frames = Frames(output);
        Assert.Equal(3, frames.Count);
        string[] listed = frames[1];
        AssertLayout(listed);
        Assert.DoesNotContain(listed, l => l.StartsWith("╭─Projects", StringComparison.Ordinal));
        Assert.Contains(Heading, listed[0]);
        Assert.Contains(listed, l => Regex.IsMatch(l, @"│ # +│ Work item +│ Title +│"));
        Assert.Contains(listed, l => Regex.IsMatch(l, @"│ 2 +│ #102 +│ Project 2 +│"));
        Assert.Equal(4, PanelRows(frames[2]).Length);
    }

    // Feedback (033, round 2)
    [Theory]
    [InlineData(5)]
    [InlineData(9)]
    public void Help_AtDefaultHeight_WithManyProjects_ScrollsToTodayAndDataFolder(int projects)
    {
        WriteProjects(projects);
        TestConsole console = new TestConsole().Interactive().EmitAnsiSequences();
        console.Profile.Width = 300;
        console.Input.PushTextWithEnter("help");
        PageDown(console, HelpPages);
        console.Input.PushKey(ConsoleKey.Enter);
        console.Input.PushTextWithEnter("quit");

        Assert.Equal(0, new App(console, FixedTimeProvider.At20261027(), new DayStore(data.Path)).Run([]));

        string[] frame = AnsiFrames(console.Output)[1];
        AssertLayout(frame);
        Assert.Contains(Heading, frame[0]);
        Assert.DoesNotContain(frame, l => l.StartsWith("╭─Projects", StringComparison.Ordinal));
        List<string[]> screens = TopScreens(console.Output, 1);
        Assert.Contains(screens.SelectMany(screen => screen), l => l.Contains("today", StringComparison.Ordinal));
        Assert.Contains(screens[^1], l => l == $"Data folder: {data.Path}");
        Assert.DoesNotContain(screens[^1], l => l.Contains("more lines below", StringComparison.Ordinal));
    }

    // Feedback (033, round 2)
    [Fact]
    public void Panel_ComesBack_AfterHelp()
    {
        WriteProjects(5);

        (_, string output) = RunCommands("help", "next", "quit");

        List<string[]> frames = Frames(output);
        Assert.Equal(3, frames.Count);
        Assert.Equal(7, PanelRows(frames[0]).Length);
        Assert.DoesNotContain(frames[1], l => l.StartsWith("╭─Projects", StringComparison.Ordinal));
        Assert.Equal(7, PanelRows(frames[2]).Length);
    }

    // Feedback (033, round 2)
    [Theory]
    [InlineData("projects x")]
    [InlineData("nope")]
    public void FailedOrOtherCommands_KeepThePanel(string command)
    {
        WriteProjects(2);

        (_, string output) = RunCommands(command, "addproject 7 Seven", "quit");

        foreach (string[] frame in Frames(output))
        {
            Assert.StartsWith("╭─Projects", frame[0]);
        }
    }

    // Breaks and checkpoints (job 036) -------------------------------------------------------

    private const string CheckpointUsage = "Use checkpoint [H:mm|now|-10m|+5m] [#project] <name>, where #project is the project's number on the quicklist.";

    private void WriteTwoProjects() =>
        File.WriteAllText(ProjectsFile, "{\"projects\":[{\"workItem\":1234,\"title\":\"Website\"},{\"workItem\":5678,\"title\":\"Controlling\"}]}");

    private DayRecord LoadDay() => new DayStore(data.Path).Load(new DateOnly(2026, 10, 27));

    private static DayEntry Checkpoint(int hour, int minute, string name, int? workItem = null) =>
        new(EntryKind.Checkpoint, new TimeOnly(hour, minute), name, workItem);

    private static FixedTimeProvider At183059() =>
        new(new DateTimeOffset(2026, 10, 27, 18, 30, 59, TimeSpan.Zero), TimeZoneInfo.Utc);

    // AC-1 (036)
    [Theory]
    [InlineData("break", "breakStart", "BS")]
    [InlineData("breakend", "breakEnd", "BE")]
    public void Break_WithoutTime_BooksCurrentMinute(string command, string kind, string label)
    {
        (int exitCode, string output) = RunCommands(At183059(), command, "quit");

        Assert.Equal(0, exitCode);
        Assert.Equal(Entries(kind + " 18:30"), ReadDay("2026-10-27"));
        Assert.Matches($@"1 +{label} +18:30", LastFrame(output));
    }

    // AC-1 (036)
    [Theory]
    [InlineData("break", "breakStart")]
    [InlineData("breakend", "breakEnd")]
    public void Break_WithTime_BooksThatTime_OnTheViewedDay(string command, string kind)
    {
        (int exitCode, _) = RunCommands("prev", command + " 9:05", "quit");

        Assert.Equal(0, exitCode);
        Assert.Equal(["2026-10-26.json"], Directory.EnumerateFiles(data.Path).Select(Path.GetFileName));
        Assert.Equal(Entries(kind + " 09:05"), ReadDay("2026-10-26"));
    }

    // AC-1 (036)
    [Theory]
    [InlineData("break 25:00", "break: 25:00 is not a time.")]
    [InlineData("breakend abc", "breakend: abc is not a time.")]
    [InlineData("break [bold]", "break: [bold] is not a time.")]
    public void Break_InvalidTime_ShowsHint_AndWritesNothing(string typed, string expected)
    {
        (int exitCode, string output) = RunCommands(typed, "quit");

        Assert.Equal(0, exitCode);
        AssertOnHintRow(LastLayout(output), expected);
        AssertOnHintRow(LastLayout(output), "Use H:mm, now, -<n>m or +<n>m.");
        Assert.Empty(Directory.EnumerateFileSystemEntries(data.Path));
    }

    // AC-1, AC-4 (036)
    [Theory]
    [InlineData("break 25:00")]
    [InlineData("checkpoint #abc Standup")]
    public void BreakAndCheckpoint_Hints_AreRed(string typed)
    {
        Assert.Matches(@"\x1b\[(31|91|38;5;9)m(break|checkpoint): ", RunAnsi(typed));
    }

    // AC-2 (036)
    [Fact]
    public void Checkpoint_NameOnly_BooksCurrentMinute_WithoutWorkItem()
    {
        (int exitCode, string output) = RunCommands(At183059(), "checkpoint Standup", "quit");

        Assert.Equal(0, exitCode);
        Assert.Equal([Checkpoint(18, 30, "Standup")], LoadDay().Entries);
        Assert.Equal("{\"entries\":[{\"kind\":\"checkpoint\",\"time\":\"18:30\",\"name\":\"Standup\"}]}", ReadDay("2026-10-27"));
        Assert.Matches(@"1 +CP +18:30 +Standup", LastFrame(output));
    }

    // AC-2 (036)
    [Theory]
    [InlineData("checkpoint 9:30 Standup", "Standup")]
    [InlineData("checkpoint 09:30 Standup", "Standup")]
    [InlineData("checkpoint   9:30   Daily   stand up   ", "Daily   stand up")]
    [InlineData("checkpoint 9:30 10:00 sync", "10:00 sync")]
    public void Checkpoint_WithTime_BooksThatTime_AndKeepsInnerSpaces(string typed, string name)
    {
        (int exitCode, _) = RunCommands(typed, "quit");

        Assert.Equal(0, exitCode);
        Assert.Equal([Checkpoint(9, 30, name)], LoadDay().Entries);
    }

    // AC-3 (036)
    [Theory]
    [InlineData("checkpoint 9:30 #2 Standup", 9, 30)]
    [InlineData("checkpoint #2 Standup", 18, 30)]
    public void Checkpoint_ProjectNumber_StoresTheProjectsWorkItem(string typed, int hour, int minute)
    {
        WriteTwoProjects();

        (int exitCode, string output) = RunCommands(typed, "quit");

        Assert.Equal(0, exitCode);
        Assert.Equal([Checkpoint(hour, minute, "Standup", 5678)], LoadDay().Entries);
        Assert.Contains("\"workItem\":5678", ReadDay("2026-10-27"));
        Assert.DoesNotContain("\"workItem\":2", ReadDay("2026-10-27"));
        Assert.Matches(@"1 +CP +\d\d:\d\d +Standup #5678", LastFrame(output));
    }

    // AC-3 (036)
    [Theory]
    [InlineData("checkpoint 2024 review", 18, 30)]
    [InlineData("checkpoint 9:30 2024 review", 9, 30)]
    public void Checkpoint_DigitWordWithoutHash_IsPartOfTheName(string typed, int hour, int minute)
    {
        WriteTwoProjects();

        RunCommands(typed, "quit");

        Assert.Equal([Checkpoint(hour, minute, "2024 review")], LoadDay().Entries);
    }

    // AC-3 (036)
    [Fact]
    public void Checkpoint_HashWordLaterInTheName_IsPartOfTheName()
    {
        WriteTwoProjects();

        RunCommands("checkpoint 9:30 Review #2 done", "quit");

        Assert.Equal([Checkpoint(9, 30, "Review #2 done")], LoadDay().Entries);
    }

    // AC-4 (036)
    [Theory]
    [InlineData("checkpoint 9:30 #0 Standup", "checkpoint: there is no project #0 on the quicklist.")]
    [InlineData("checkpoint #3 Standup", "checkpoint: there is no project #3 on the quicklist.")]
    [InlineData("checkpoint #99 Standup", "checkpoint: there is no project #99 on the quicklist.")]
    [InlineData("checkpoint #99999999999 Standup", "checkpoint: there is no project #99999999999 on the quicklist.")]
    [InlineData("checkpoint # Standup", "checkpoint: # is not a project number. " + CheckpointUsage)]
    [InlineData("checkpoint #abc Standup", "checkpoint: #abc is not a project number. " + CheckpointUsage)]
    [InlineData("checkpoint 9:30 #2x Standup", "checkpoint: #2x is not a project number. " + CheckpointUsage)]
    [InlineData("checkpoint #-1 Standup", "checkpoint: #-1 is not a project number. " + CheckpointUsage)]
    [InlineData("checkpoint #[red] Standup", "checkpoint: #[red] is not a project number. " + CheckpointUsage)]
    [InlineData("checkpoint #２ Standup", "checkpoint: #２ is not a project number. " + CheckpointUsage)]
    [InlineData("checkpoint", "checkpoint: say the checkpoint's name. " + CheckpointUsage)]
    [InlineData("checkpoint 9:30", "checkpoint: say the checkpoint's name. " + CheckpointUsage)]
    [InlineData("checkpoint #2", "checkpoint: say the checkpoint's name. " + CheckpointUsage)]
    [InlineData("checkpoint 9:30 #2", "checkpoint: say the checkpoint's name. " + CheckpointUsage)]
    [InlineData("checkpoint 25:00 Standup", "checkpoint: 25:00 is not a time. Use H:mm, now, -<n>m or +<n>m.")]
    [InlineData("checkpoint 9:5 Standup", "checkpoint: 9:5 is not a time. Use H:mm, now, -<n>m or +<n>m.")]
    [InlineData("checkpoint 25:00", "checkpoint: 25:00 is not a time. Use H:mm, now, -<n>m or +<n>m.")]
    [InlineData("checkpoint a\u2028b", "checkpoint: the name must not contain control characters. Retype it without the hidden characters.")]
    public void Checkpoint_InvalidInput_ShowsHint_AndWritesNoFile(string typed, string expected)
    {
        WriteTwoProjects();
        byte[] projects = File.ReadAllBytes(ProjectsFile);

        (int exitCode, string output) = Run(c =>
        {
            c.Profile.Width = 200;
            foreach (char ch in typed)
            {
                // TestConsoleInput.PushText only takes characters with a ConsoleKey (below 256).
                c.Input.PushKey(new ConsoleKeyInfo(ch, ch < 256 ? (ConsoleKey)char.ToUpperInvariant(ch) : ConsoleKey.NoName, false, false, false));
            }

            c.Input.PushKey(ConsoleKey.Enter);
            c.Input.PushTextWithEnter("quit");
        });

        Assert.Equal(0, exitCode);
        AssertOnHintRow(LastLayout(output), expected);
        Assert.False(File.Exists(DayFile("2026-10-27")));
        Assert.Equal(projects, File.ReadAllBytes(ProjectsFile));
    }

    // AC-4 (036)
    [Fact]
    public void Checkpoint_NameTooLong_ShowsHint_AndWritesNoFile_MaxLengthBooks()
    {
        string max = new string('n', ProjectList.MaxTitleLength);

        (_, string output) = RunWide(200, "checkpoint " + max + "n", "quit");

        AssertOnHintRow(LastLayout(output), "checkpoint: the name is longer than 200 characters.");
        Assert.False(File.Exists(DayFile("2026-10-27")));

        RunCommands("checkpoint 9:00 " + max, "quit");
        Assert.Equal([Checkpoint(9, 0, max)], LoadDay().Entries);
    }

    // AC-4 (036)
    [Fact]
    public void Checkpoint_InvalidInput_LeavesExistingDayUnchanged()
    {
        WriteTwoProjects();
        File.WriteAllText(DayFile("2026-10-27"), ThreeEntries);
        byte[] before = File.ReadAllBytes(DayFile("2026-10-27"));

        RunCommands("checkpoint #9 X", "checkpoint #x X", "checkpoint 9:30", "checkpoint 24:00 X", "break 7", "quit");

        Assert.Equal(before, File.ReadAllBytes(DayFile("2026-10-27")));
    }

    // AC-4 (036)
    [Theory]
    [InlineData("{not json")]
    [InlineData("{\"projects\":{}}")]
    public void Checkpoint_WithProjectNumber_UnreadableQuicklist_ShowsHint_AndWritesNothing(string contents)
    {
        File.WriteAllText(ProjectsFile, contents);

        (int exitCode, string output) = RunWide(200, "checkpoint 9:30 #1 Standup", "quit");

        Assert.Equal(0, exitCode);
        AssertOnHintRow(LastLayout(output), "checkpoint: the project list cannot be read: projects.json is not valid. Nothing was booked. Fix or delete projects.json in the data folder (help shows where).");
        Assert.False(File.Exists(DayFile("2026-10-27")));
        Assert.Equal(contents, File.ReadAllText(ProjectsFile));
    }

    // AC-5 (036)
    [Theory]
    [InlineData("checkpoint Standup", 18, 30, "Standup")]
    [InlineData("checkpoint 2024 review", 18, 30, "2024 review")]
    [InlineData("checkpoint 9:30 2024 review", 9, 30, "2024 review")]
    public void Checkpoint_WithoutProjectNumber_IgnoresABrokenQuicklist(string typed, int hour, int minute, string name)
    {
        File.WriteAllText(ProjectsFile, "{not json");

        (int exitCode, string output) = RunWide(200, typed, "quit");

        Assert.Equal(0, exitCode);
        Assert.Equal([Checkpoint(hour, minute, name)], LoadDay().Entries);
        Assert.Equal(string.Empty, LastLayout(output)[^4].Trim());
    }

    // AC-8 (036)
    [Fact]
    public void Checkpoint_MarkupName_IsShownLiterally()
    {
        (_, string output) = RunCommands("checkpoint 9:00 [red]Fix[/]", "quit");

        Assert.Equal([Checkpoint(9, 0, "[red]Fix[/]")], LoadDay().Entries);
        Assert.Matches(@"1 +CP +09:00 +\[red\]Fix\[/\]", LastFrame(output));
    }

    // AC-8, AC-9 (036); AC-6, AC-7 (045)
    [Fact]
    public void Day_NumbersAllEntriesTogether_AndShowsTimeLeftWithoutBreaksOnEveryRow()
    {
        WriteTwoProjects();

        (int exitCode, string output) = RunWide(120, "arrive 8:00", "leave 17:00", "break 12:00", "breakend 12:30", "checkpoint 9:30 #1 Kickoff", "quit");

        Assert.Equal(0, exitCode);
        string frame = LastFrame(output);
        Assert.Matches(@"1 +AR +08:00 +left 8:00", frame);
        Assert.Matches(@"2 +CP +09:30 +Kickoff #1234 +left 6:30", frame);
        Assert.Matches(@"3 +BS +12:00 +left 4:00", frame);
        Assert.Matches(@"4 +BE +12:30 +left 4:00", frame);
        Assert.Matches(@"5 +LE +17:00 +left -0:30", frame);
    }

    // AC-8 (049)
    [Fact]
    public void Checkpoint_BookedBeforeBreakEndAtSameTime_ShowsAfterIt_AndIsSavedAfterIt()
    {
        (int exitCode, string output) = RunCommands("arrive 8:00", "break 12:00", "cp 12:30 Review", "be 12:30", "quit");

        Assert.Equal(0, exitCode);
        string frame = LastFrame(output);
        Assert.Matches(@"3 +BE +12:30", frame);
        Assert.Matches(@"4 +CP +12:30 +Review", frame);
        Assert.Equal(
            "{\"entries\":[{\"kind\":\"arrival\",\"time\":\"08:00\"},{\"kind\":\"breakStart\",\"time\":\"12:00\"},"
            + "{\"kind\":\"breakEnd\",\"time\":\"12:30\"},{\"kind\":\"checkpoint\",\"time\":\"12:30\",\"name\":\"Review\"}]}",
            ReadDay("2026-10-27"));
    }

    // AC-10 (036)
    [Fact]
    public void Remove_RemovesBreakAndCheckpointEntries()
    {
        WriteTwoProjects();
        RunCommands("arrive 8:00", "break 12:00", "checkpoint 13:00 #2 Review", "breakend 12:30", "quit");

        RunCommands("remove 2", "remove 3", "quit");

        Assert.Equal(Entries("arrival 08:00", "breakEnd 12:30"), ReadDay("2026-10-27"));
    }

    // AC-10 (036)
    [Fact]
    public void Change_MovesBreakAndCheckpoint_KeepingKindNameAndWorkItem()
    {
        WriteTwoProjects();
        RunCommands("arrive 8:00", "checkpoint 9:00 #2 Daily  sync", "break 12:00", "quit");

        (int exitCode, string output) = RunWide(120, "change 2 14:00", "change 2 7:00", "quit");

        Assert.Equal(0, exitCode);
        Assert.Equal(
            [
                new DayEntry(EntryKind.BreakStart, new TimeOnly(7, 0)),
                new DayEntry(EntryKind.Arrival, new TimeOnly(8, 0)),
                Checkpoint(14, 0, "Daily  sync", 5678),
            ],
            LoadDay().Entries);
        Assert.Matches(@"3 +CP +14:00 +Daily  sync #5678", LastFrame(output));
    }

    // AC-11 (036)
    [Fact]
    public void Help_ListsBreakBreakEndAndCheckpoint_WithDescriptions()
    {
        // Wide enough that no description wraps, now that renameproject widens the name column (job 102).
        (_, string output) = Run(c =>
        {
            c.Profile.Width = 120;
            c.Input.PushTextWithEnter("help");
            PageDown(c, HelpPages);
            c.Input.PushTextWithEnter("quit");
        });

        Assert.Matches(@"break +│ b +│ Book the start of a break: now, or at an optional H:mm", output);
        Assert.Matches(@"breakend +│ be +│ Book the end of a break: now, or at an optional H:mm", output);
        Assert.Matches(@"checkpoint +│ cp +│ Book a checkpoint: \[H:mm\|now\|-10m\|\+5m\] \[#project\] <name> +│", output);
    }

    // AC-11 (036)
    [Theory]
    [InlineData("brea", "break ", "Book the start of a break")]
    [InlineData("breake", "breakend ", "Book the end of a break")]
    [InlineData("checkp", "checkpoint ", "Book a checkpoint: [H:mm|now|-10m|+5m] [#project] <name>")]
    public void Popup_ShowsBreakAndCheckpoint(string typed, string name, string description)
    {
        string output = RunAnsi(c =>
        {
            c.Input.PushText(typed);
            c.Input.PushKey(ConsoleKey.Escape);
            c.Input.PushKey(ConsoleKey.Escape);
            c.Input.PushTextWithEnter("quit");
        });

        Assert.Contains(TypingWrites(output, 0), w => w.Text.StartsWith(name, StringComparison.Ordinal) && w.Text.Contains(description, StringComparison.Ordinal));
    }

    // AC-11 (036)
    [Fact]
    public void Tab_CompletesCheckpoint()
    {
        (int exitCode, string output) = Run(c =>
        {
            c.Input.PushText("checkp");
            c.Input.PushKey(ConsoleKey.Tab);
            c.Input.PushTextWithEnter(" 9:00 Done");
            c.Input.PushTextWithEnter("quit");
        });

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain("Unknown command", output);
        Assert.Equal([Checkpoint(9, 0, "Done")], LoadDay().Entries);
    }

    // Scrollable output (job 037) ----------------------------------------------------------

    private const string BelowCue = "more lines below · PgDn / Ctrl+↓";

    /// <summary>Indexes (into <see cref="AnsiWrites.Parse"/>) of the full frames: written at 1;1 and reaching the input box.</summary>
    private static List<int> FrameStarts(List<AnsiWrite> writes) =>
        writes.Select((w, i) => (w, i))
            .Where(x => x.w.Row == 1 && x.w.Column == 1 && x.w.Text.Contains('╰'))
            .Select(x => x.i)
            .ToList();

    /// <summary>
    /// The states of the top area's rows (1..<paramref name="topHeight"/>, trimmed) from full frame
    /// <paramref name="index"/> up to the next full frame: the frame's own rows, then a new state for
    /// every in-place redraw that starts at row 1 (a scroll), updated by any later writes to those rows.
    /// </summary>
    private static List<string[]> TopScreens(string output, int index, int topHeight = TopHeight)
    {
        List<AnsiWrite> writes = AnsiWrites.Parse(output);
        List<int> starts = FrameStarts(writes);
        int end = index + 1 < starts.Count ? starts[index + 1] : writes.Count;
        List<string[]> screens = [writes[starts[index]].Text.Split('\n')[..topHeight].Select(l => l.TrimEnd()).ToArray()];
        foreach (AnsiWrite write in writes.Skip(starts[index] + 1).Take(end - starts[index] - 1))
        {
            if (write.Column != 1 || write.Row < 1 || write.Row > topHeight)
            {
                continue;
            }

            if (write.Row == 1)
            {
                screens.Add((string[])screens[^1].Clone());
            }

            screens[^1][write.Row - 1] = write.Text.TrimEnd();
        }

        return screens;
    }

    /// <summary>The raw output between full frame <paramref name="index"/> and the next one.</summary>
    private static string BetweenFrames(string output, int index)
    {
        List<int> starts = Regex.Matches(output, @"\x1b\[2J").Select(m => m.Index).ToList();
        int end = index + 1 < starts.Count ? starts[index + 1] : output.Length;
        return output[starts[index]..end];
    }

    // AC-6 (037)
    [Fact]
    public void Scroll_MovesOnePage_OfHeightMinusTwoLines()
    {
        string[] full = UnclippedHelpTop().Select(l => l.TrimEnd()).ToArray();

        string output = RunAnsi(c =>
        {
            c.Profile.Height = 12;
            c.Input.PushTextWithEnter("help");
            c.Input.PushKey(ConsoleKey.PageDown);
            c.Input.PushKey(ConsoleKey.PageDown);
            c.Input.PushKey(ConsoleKey.PageUp);
            c.Input.PushKey(ConsoleKey.Enter);
            c.Input.PushTextWithEnter("quit");
        }, ManyCommands());

        // 12 rows: an 8-row top area, so one page is 6 lines.
        List<string[]> screens = TopScreens(output, 1, 8);
        Assert.Equal(4, screens.Count);
        Assert.Equal([.. full[..7], $"↓ {full.Length - 7} {BelowCue}"], screens[0]);
        Assert.Equal(["↑ 6 more lines above · PgUp / Ctrl+↑", .. full[6..12], $"↓ {full.Length - 12} {BelowCue}"], screens[1]);
        Assert.Equal(["↑ 12 more lines above · PgUp / Ctrl+↑", .. full[12..18], $"↓ {full.Length - 18} {BelowCue}"], screens[2]);
        Assert.Equal(screens[1], screens[3]);
    }

    // AC-6 (037)
    [Fact]
    public void Scroll_ToTheBottom_IsClamped_AndShowsTheLastLines()
    {
        string[] full = UnclippedHelpTop().Select(l => l.TrimEnd()).ToArray();

        string output = RunAnsi(c =>
        {
            c.Profile.Height = 12;
            c.Input.PushTextWithEnter("help");
            PageDown(c, 20);
            c.Input.PushKey(ConsoleKey.Enter);
            c.Input.PushTextWithEnter("quit");
        }, ManyCommands());

        List<string[]> screens = TopScreens(output, 1, 8);
        int max = full.Length - 7;
        Assert.Equal(1 + ((max + 5) / 6), screens.Count);
        Assert.Equal([$"↑ {max} more lines above · PgUp / Ctrl+↑", .. full[^7..]], screens[^1]);
    }

    // AC-6 (037)
    [Theory]
    [InlineData(ConsoleKey.PageUp, "help")] // already at the top
    [InlineData(ConsoleKey.PageDown, "")]    // the empty day fits
    [InlineData(ConsoleKey.PageUp, "")]
    public void Scroll_ThatChangesNothing_WritesNothing(ConsoleKey key, string command)
    {
        string output = RunAnsi(c =>
        {
            c.Input.PushTextWithEnter(command);
            c.Input.PushKey(key);
            c.Input.PushKey(key);
            c.Input.PushKey(ConsoleKey.Enter);
            c.Input.PushTextWithEnter("quit");
        });

        Assert.DoesNotContain(AnsiWrites.Parse(BetweenFrames(output, 1)).Skip(1), w => w.Text.Length > 0);
    }

    // AC-6 (037)
    [Fact]
    public void Scroll_PastTheBottom_WritesNothing()
    {
        string output = RunAnsi(c =>
        {
            c.Input.PushTextWithEnter("help");
            PageDown(c, HelpPages);
            c.Input.PushKey(ConsoleKey.Enter);
            c.Input.PushTextWithEnter("help");
            PageDown(c, HelpPages + 3);
            c.Input.PushKey(ConsoleKey.Enter);
            c.Input.PushTextWithEnter("quit");
        });

        Assert.Equal(BetweenFrames(output, 1), BetweenFrames(output, 3));
    }

    // AC-7 (037)
    [Fact]
    public void Scroll_RewritesOnlyTheTopRows_ThenTheInputLine_KeepingTheText()
    {
        string output = RunAnsi(c =>
        {
            c.Input.PushTextWithEnter("help");
            c.Input.PushText("to");
            c.Input.PushKey(ConsoleKey.PageDown);
            c.Input.PushTextWithEnter("day");
            c.Input.PushTextWithEnter("quit");
        });

        string between = BetweenFrames(output, 1);
        Assert.Equal(1, Count(between, ClearScreen));
        List<AnsiWrite> writes = AnsiWrites.Parse(between);
        int scrollStart = writes.FindIndex(w => w.Row == 1 && w.Column == 1 && w.Text.StartsWith('↑'));
        Assert.True(scrollStart > 0);
        Assert.Equal(Enumerable.Range(1, TopHeight), writes.Skip(scrollStart).Take(TopHeight).Select(w => w.Row));
        Assert.All(writes.Skip(scrollStart).Take(TopHeight), w => Assert.Equal(1, w.Column));

        // The input panel's borders are never written while typing.
        Assert.DoesNotContain(writes.Skip(1), w => w.Row == 22 || w.Row == 24);

        // The popup ("to" is open) is drawn again on top, then the input line with the typed text, cursor after it.
        List<AnsiWrite> after = writes.Skip(scrollStart + TopHeight).ToList();
        int line = after.FindIndex(w => w.Row == 23);
        Assert.True(line > 0);
        Assert.Contains(after.Take(line), w => w.Text.StartsWith("today", StringComparison.Ordinal));
        // Only the popup writes the hint row (21) after the scroll: the scroll itself stops at row 20.
        Assert.All(after.Take(line), w => Assert.True(w.Row < 21 || w.StartsWithCommand || w.Text.StartsWith('…')));
        Assert.Equal(new AnsiWrite(23, 5, "to"), after[line]);
        Assert.Equal(23, after[line + 1].Row);
        Assert.Equal(7, after[line + 1].Column);
        Assert.Equal(Heading, LastHeading(output));
        Assert.DoesNotContain("Unknown command", output);
    }

    // AC-8 (037)
    [Fact]
    public void Scroll_WithPopupOpen_DrawsItOnTop_AndEscRestoresTheScrolledRows()
    {
        string scrolledOutput = RunAnsi(c =>
        {
            c.Profile.Height = 12;
            c.Input.PushTextWithEnter("help");
            c.Input.PushKey(ConsoleKey.PageDown);
            c.Input.PushKey(ConsoleKey.Enter);
            c.Input.PushTextWithEnter("quit");
        }, ManyCommands());
        string[] scrolled = TopScreens(scrolledOutput, 1, 8)[1];

        string output = RunAnsi(c =>
        {
            c.Profile.Height = 12;
            c.Input.PushTextWithEnter("help");
            c.Input.PushText("/");
            c.Input.PushKey(ConsoleKey.PageDown);
            c.Input.PushKey(ConsoleKey.Escape);
            c.Input.PushKey(ConsoleKey.Escape);
            c.Input.PushKey(ConsoleKey.Enter);
            c.Input.PushTextWithEnter("quit");
        }, ManyCommands());

        List<AnsiWrite> writes = AnsiWrites.Parse(BetweenFrames(output, 1));
        int scrollStart = writes.FindIndex(w => w.Row == 1 && w.Column == 1 && w.Text.StartsWith('↑'));
        Assert.True(scrollStart > 0);
        Assert.Contains(writes.Skip(scrollStart + 8), w => w.Column == 1 && w.Row <= 9 && w.StartsWithCommand);

        List<string[]> screens = TopScreens(output, 1, 8);
        Assert.Equal(scrolled, screens[^1]);
        Assert.StartsWith("↑ 6 more lines above", screens[^1][0]);
    }

    // AC-9 (037)
    [Theory]
    [InlineData("")]
    [InlineData("nope")]
    [InlineData("help")]
    [InlineData("next")]
    public void AfterEnter_TheNextFrame_StartsAtTheTop(string command)
    {
        string output = RunAnsi(c =>
        {
            c.Input.PushTextWithEnter("help");
            PageDown(c, HelpPages);
            c.Input.PushTextWithEnter(command);
            c.Input.PushTextWithEnter("help");
            c.Input.PushTextWithEnter("quit");
        });

        Assert.StartsWith("↑", TopScreens(output, 1)[^1][0]);
        string[] next = AnsiFrames(output)[2];
        Assert.DoesNotContain("more lines above", next[0]);
        string[] help = AnsiFrames(output)[3];
        Assert.Matches(@"day, 2026-10-2\d", help[0]);
        Assert.Contains(BelowCue, help[TopHeight - 1]);
    }

    // AC-10 (037)
    [Fact]
    public void Help_OnTheDefaultTerminal_ShowsBelowCue_AndScrollsToTodayAndDataFolder()
    {
        string folder = Path.Combine(Path.GetPathRoot(data.Path)!, "Times"); // a short folder, so the footer is one line (nothing is written)
        TestConsole console = new TestConsole().Interactive().EmitAnsiSequences();
        console.Input.PushTextWithEnter("help");
        PageDown(console, HelpPages);
        console.Input.PushKey(ConsoleKey.Enter);
        console.Input.PushTextWithEnter("quit");

        Assert.Equal(0, new App(console, FixedTimeProvider.At20261027(), new DayStore(folder), instanceLock: SideLock()).Run([]));

        string output = console.Output;
        string[] frame = AnsiFrames(output)[1];
        Assert.Equal(24, frame.Length);
        Assert.Contains(Heading, frame[0]);
        Assert.Matches(@"^↓ \d+ more lines below · PgDn / Ctrl\+↓", frame[TopHeight - 1]);
        Assert.Matches(@"\x1b\[(90|38;5;8)m↓ \d+ more lines below · PgDn / Ctrl\+↓", output);

        List<string[]> screens = TopScreens(output, 1);
        Assert.True(screens.Count > 1);
        Assert.Contains(screens.SelectMany(screen => screen), l => l.Contains("today", StringComparison.Ordinal));
        Assert.Contains(screens[^1], l => l == $"Data folder: {folder}");
        Assert.DoesNotContain(screens[^1], l => l.Contains(BelowCue, StringComparison.Ordinal));
        Assert.Matches(@"^↑ \d+ more lines above · PgUp / Ctrl\+↑", screens[^1][0]);
    }

    // AC-5, AC-7 (037)
    [Theory]
    [InlineData(ConsoleKey.PageDown, true, false)]
    [InlineData(ConsoleKey.DownArrow, false, true)]
    public void ScrollAlternatives_ScrollTheApp_Too(ConsoleKey key, bool shift, bool control)
    {
        string output = RunAnsi(c =>
        {
            c.Input.PushTextWithEnter("help");
            c.Input.PushKey(new ConsoleKeyInfo('\0', key, shift, false, control));
            c.Input.PushKey(ConsoleKey.Enter);
            c.Input.PushTextWithEnter("quit");
        });

        List<string[]> screens = TopScreens(output, 1);
        Assert.Equal(2, screens.Count);
        Assert.Matches(@"^↑ \d+ more lines above · PgUp / Ctrl\+↑$", screens[1][0]);
    }

    // AC-5 (062)
    [Fact]
    public void ScrollCues_UseTheNewWording_InEveryFrame()
    {
        string output = RunAnsi(c =>
        {
            c.Input.PushTextWithEnter("help");
            c.Input.PushKey(new ConsoleKeyInfo('\0', ConsoleKey.DownArrow, false, false, true));
            c.Input.PushKey(new ConsoleKeyInfo('\0', ConsoleKey.UpArrow, false, false, true));
            c.Input.PushKey(ConsoleKey.PageDown);
            c.Input.PushKey(ConsoleKey.Enter);
            c.Input.PushTextWithEnter("quit");
        });

        List<string[]> screens = TopScreens(output, 1);
        Assert.Equal(4, screens.Count);
        Assert.Matches(@"^↓ \d+ more lines below · PgDn / Ctrl\+↓$", screens[0][TopHeight - 1]);
        Assert.Matches(@"^↑ \d+ more lines above · PgUp / Ctrl\+↑$", screens[1][0]);
        Assert.Equal(screens[0], screens[2]);
        Assert.Matches(@"^↑ \d+ more lines above · PgUp / Ctrl\+↑$", screens[3][0]);
        Assert.DoesNotMatch(@"more lines? above · PgUp(?! / Ctrl\+↑)", output);
        Assert.DoesNotMatch(@"more lines? below · PgDn(?! / Ctrl\+↓)", output);
    }

    // Mouse wheel (job 038) -----------------------------------------------------------------

    private const string MouseOn = "\u001b[?1000h\u001b[?1006h";
    private const string MouseOff = "\u001b[?1006l\u001b[?1000l";

    /// <summary>Pushes one SGR wheel notch: Esc, then <c>[&lt;64;…M</c> (up) or <c>[&lt;65;…M</c> (down).</summary>
    private static void Wheel(TestConsole console, int direction, int count = 1)
    {
        for (int i = 0; i < count; i++)
        {
            console.Input.PushKey(new ConsoleKeyInfo('\u001b', ConsoleKey.Escape, false, false, false));
            console.Input.PushText(direction < 0 ? "[<64;10;5M" : "[<65;10;5M");
        }
    }

    private (int ExitCode, string Output) RunWithMouse(Action<TestConsole> input, string[]? args = null, string? folder = null, CommandRegistry? registry = null)
    {
        TestConsole console = new TestConsole().Interactive().EmitAnsiSequences();
        input(console);
        App app = new App(console, FixedTimeProvider.At20261027(), new DayStore(folder ?? data.Path), registry, mouse: new MouseReporting(console));
        int exitCode = app.Run(args ?? []);
        return (exitCode, console.Output);
    }

    private const string PasteOn = "\u001b[?2004h";
    private const string PasteOff = "\u001b[?2004l";

    // Also checks bracketed paste (066 AC-10): on once before the first clear, off once after the last frame.
    private static void AssertMouseOnThenOff(string output, int lastFrameEnd)
    {
        Assert.Equal(1, Count(output, PasteOn));
        Assert.Equal(1, Count(output, PasteOff));
        Assert.True(output.IndexOf(PasteOn, StringComparison.Ordinal) < output.IndexOf(ClearScreen, StringComparison.Ordinal), "paste on before the first clear");
        Assert.True(output.IndexOf(PasteOff, StringComparison.Ordinal) >= lastFrameEnd, "paste off after the last frame");
        Assert.DoesNotContain("\u001b[?2004", output[(output.IndexOf(PasteOff, StringComparison.Ordinal) + PasteOff.Length)..]);

        Assert.Equal(1, Count(output, MouseOn));
        Assert.Equal(1, Count(output, MouseOff));
        int on = output.IndexOf(MouseOn, StringComparison.Ordinal);
        int off = output.IndexOf(MouseOff, StringComparison.Ordinal);
        Assert.True(on < output.IndexOf(ClearScreen, StringComparison.Ordinal), "mouse on before the first clear");
        Assert.True(off >= lastFrameEnd, "mouse off after the last frame");
        Assert.DoesNotContain("\u001b[?100", output[(off + MouseOff.Length)..]);
    }

    // AC-1
    [Fact]
    public void Run_TurnsMouseReportingOn_BeforeTheFirstClear_AndOffAfterQuit()
    {
        (int exitCode, string output) = RunWithMouse(c =>
        {
            c.Input.PushTextWithEnter("help");
            c.Input.PushTextWithEnter("quit");
        });

        Assert.Equal(0, exitCode);
        AssertMouseOnThenOff(output, output.LastIndexOf(ClearScreen, StringComparison.Ordinal) + ClearScreen.Length);
        Assert.EndsWith(MouseOff, output);
    }

    /// <summary>Console input that hands out its keys and then reports the end of input (null), unlike <c>TestConsoleInput</c>.</summary>
    private sealed class EndingInput : IAnsiConsoleInput
    {
        private readonly Queue<ConsoleKeyInfo> keys = new();

        public EndingInput(string text)
        {
            foreach (char c in text)
            {
                keys.Enqueue(c == '\r' ? new ConsoleKeyInfo(c, ConsoleKey.Enter, false, false, false) : new ConsoleKeyInfo(c, default, false, false, false));
            }
        }

        public bool IsKeyAvailable() => keys.Count > 0;

        public ConsoleKeyInfo? ReadKey(bool intercept) => keys.Count > 0 ? keys.Dequeue() : null;

        public Task<ConsoleKeyInfo?> ReadKeyAsync(bool intercept, CancellationToken cancellationToken) =>
            Task.FromResult(ReadKey(intercept));
    }

    // AC-1
    [Fact]
    public void Run_TurnsMouseReportingOff_WhenTheInputRunsOut()
    {
        TestConsole test = new TestConsole().Interactive().EmitAnsiSequences();
        IAnsiConsole console = new InputOverrideConsole(test, new EndingInput("help\r"));
        int exitCode = new App(console, FixedTimeProvider.At20261027(), new DayStore(data.Path), mouse: new MouseReporting(console)).Run([]);
        string output = test.Output;

        Assert.Equal(0, exitCode);
        AssertMouseOnThenOff(output, output.LastIndexOf('╰') + 1);
        Assert.EndsWith(MouseOff, output);
    }

    // AC-2
    [Fact]
    public void Version_WritesNoMouseSequences()
    {
        (int exitCode, string output) = RunWithMouse(_ => { }, ["--version"]);

        Assert.Equal(0, exitCode);
        Assert.Contains($"Times {AppVersion.Current}", output);
        Assert.DoesNotContain("\u001b[?100", output);
        Assert.DoesNotContain("\u001b[?2004", output);
    }

    // AC-2
    [Fact]
    public void StartError_WritesNoMouseSequences()
    {
        (int exitCode, string output) = RunWithMouse(c => c.Input.PushTextWithEnter("quit"), folder: "relative/folder");

        Assert.Equal(2, exitCode);
        Assert.Contains("Times cannot start", output);
        Assert.DoesNotContain("\u001b[?100", output);
    }

    /// <summary>A message that throws when it is drawn.</summary>
    private sealed class ThrowingRenderable : Spectre.Console.Rendering.IRenderable
    {
        public Spectre.Console.Rendering.Measurement Measure(Spectre.Console.Rendering.RenderOptions options, int maxWidth) =>
            throw new InvalidOperationException("boom");

        public IEnumerable<Spectre.Console.Rendering.Segment> Render(Spectre.Console.Rendering.RenderOptions options, int maxWidth) =>
            throw new InvalidOperationException("boom");
    }

    // AC-3
    [Fact]
    public void Run_ThatThrows_HasStillTurnedMouseReportingOff()
    {
        CommandRegistry registry = CommandRegistry.CreateDefault();
        registry.Register("boom", "bm", "Throws while drawing", _ => new CommandResult(new ThrowingRenderable()));
        TestConsole console = new TestConsole().Interactive().EmitAnsiSequences();
        console.Input.PushTextWithEnter("boom");
        console.Input.PushTextWithEnter("quit");
        App app = new App(console, FixedTimeProvider.At20261027(), new DayStore(data.Path), registry, mouse: new MouseReporting(console));

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => app.Run([]));

        Assert.Equal("boom", ex.Message);
        string output = console.Output;
        AssertMouseOnThenOff(output, output.LastIndexOf('╰') + 1);
        Assert.EndsWith(MouseOff, output);
    }

    // AC-9
    [Fact]
    public void Wheel_MovesTheTopAreaByThreeLines()
    {
        string[] full = UnclippedHelpTop().Select(l => l.TrimEnd()).ToArray();

        string output = RunAnsi(c =>
        {
            c.Profile.Height = 12;
            c.Input.PushTextWithEnter("help");
            Wheel(c, 1, 2);
            Wheel(c, -1);
            c.Input.PushKey(ConsoleKey.PageDown);
            Wheel(c, 1);
            c.Input.PushKey(ConsoleKey.Enter);
            c.Input.PushTextWithEnter("quit");
        }, ManyCommands());

        // 12 rows: an 8-row top area, so one page is 6 lines and a notch 3.
        string[] At(int offset) =>
            [$"↑ {offset} more lines above · PgUp / Ctrl+↑", .. full[offset..(offset + 6)], $"↓ {full.Length - offset - 6} {BelowCue}"];

        List<string[]> screens = TopScreens(output, 1, 8);
        Assert.Equal(6, screens.Count);
        Assert.Equal(At(3), screens[1]);
        Assert.Equal(At(6), screens[2]);
        Assert.Equal(At(3), screens[3]);
        Assert.Equal(At(9), screens[4]);
        Assert.Equal(At(12), screens[5]);
        Assert.DoesNotContain("Unknown command", output);
        Assert.DoesNotContain("64;10", output);
    }

    // AC-9
    [Fact]
    public void Wheel_IsClamped_AtTheBottom_AndAtTheTop()
    {
        string[] full = UnclippedHelpTop().Select(l => l.TrimEnd()).ToArray();

        string output = RunAnsi(c =>
        {
            c.Profile.Height = 12;
            c.Input.PushTextWithEnter("help");
            Wheel(c, 1, 40);
            Wheel(c, -1, 40);
            c.Input.PushKey(ConsoleKey.Enter);
            c.Input.PushTextWithEnter("quit");
        }, ManyCommands());

        int max = full.Length - 7;
        int steps = (max + 2) / 3;
        List<string[]> screens = TopScreens(output, 1, 8);
        Assert.Equal(1 + (2 * steps), screens.Count);
        Assert.Equal([$"↑ {max} more lines above · PgUp / Ctrl+↑", .. full[^7..]], screens[steps]);
        Assert.Equal(screens[0], screens[^1]);
    }

    // AC-9
    [Theory]
    [InlineData(-1, "help")] // already at the top
    [InlineData(1, "")]       // the empty day fits
    [InlineData(-1, "")]
    public void Wheel_ThatChangesNothing_WritesNothing(int direction, string command)
    {
        string output = RunAnsi(c =>
        {
            c.Input.PushTextWithEnter(command);
            Wheel(c, direction, 3);
            c.Input.PushKey(ConsoleKey.Enter);
            c.Input.PushTextWithEnter("quit");
        });

        Assert.DoesNotContain(AnsiWrites.Parse(BetweenFrames(output, 1)).Skip(1), w => w.Text.Length > 0);
    }

    // AC-9
    [Fact]
    public void Wheel_RewritesOnlyTheTopRows_ThenThePopup_ThenTheInputLine()
    {
        string output = RunAnsi(c =>
        {
            c.Input.PushTextWithEnter("help");
            c.Input.PushText("to");
            Wheel(c, 1);
            c.Input.PushTextWithEnter("day");
            c.Input.PushTextWithEnter("quit");
        });

        string between = BetweenFrames(output, 1);
        Assert.Equal(1, Count(between, ClearScreen));
        List<AnsiWrite> writes = AnsiWrites.Parse(between);
        int scrollStart = writes.FindIndex(w => w.Row == 1 && w.Column == 1 && w.Text.StartsWith("↑ 3 more lines above", StringComparison.Ordinal));
        Assert.True(scrollStart > 0);
        Assert.Equal(Enumerable.Range(1, TopHeight), writes.Skip(scrollStart).Take(TopHeight).Select(w => w.Row));
        Assert.All(writes.Skip(scrollStart).Take(TopHeight), w => Assert.Equal(1, w.Column));
        Assert.DoesNotContain(writes.Skip(1), w => w.Row == 22 || w.Row == 24);

        List<AnsiWrite> after = writes.Skip(scrollStart + TopHeight).ToList();
        int line = after.FindIndex(w => w.Row == 23);
        Assert.True(line > 0);
        Assert.Contains(after.Take(line), w => w.Text.StartsWith("today", StringComparison.Ordinal));
        Assert.Equal(new AnsiWrite(23, 5, "to"), after[line]);
        Assert.Equal(Heading, LastHeading(output));
    }

    // Commands without slash (job 040) ------------------------------------------------------

    // AC-1 (040)
    [Theory]
    [InlineData("arrive 8:15")]
    [InlineData("/arrive 8:15")]
    [InlineData("ARRIVE 8:15")]
    [InlineData("/Arrive 8:15")]
    public void Arrive_WithOrWithoutSlash_BooksTheSameArrival(string typed)
    {
        (int exitCode, string output) = RunCommands(typed, "quit");

        Assert.Equal(0, exitCode);
        Assert.Equal(Entries("arrival 08:15"), ReadDay("2026-10-27"));
        Assert.Matches(@"1 +AR +08:15", LastFrame(output));
        Assert.DoesNotContain("Unknown command", output);
    }

    // AC-1 (040)
    [Fact]
    public void EveryRegisteredCommand_RunsWithAndWithoutSlash_IgnoringCase()
    {
        TestConsole console = new TestConsole().Interactive();
        CommandRegistry registry = CommandRegistry.CreateDefault();
        App app = new App(console, FixedTimeProvider.At20261027(), new DayStore(data.Path), registry);
        List<string> names = registry.Commands.Select(c => c.Name).Where(n => n != "quit").ToList();
        Assert.Contains("arrive", names);
        Assert.DoesNotContain(names, n => n.StartsWith('/'));
        foreach (string name in names)
        {
            foreach (string typed in (string[])[name, "/" + name, name.ToUpperInvariant(), "/" + char.ToUpperInvariant(name[0]) + name[1..]])
            {
                console.Input.PushTextWithEnter(typed);
                if (name == "settings")
                {
                    // The settings panel is modal: Esc closes it.
                    console.Input.PushKey(ConsoleKey.Escape);
                }
            }
        }

        console.Input.PushTextWithEnter("quit");

        Assert.Equal(0, app.Run([]));
        Assert.DoesNotContain("Unknown command", console.Output);
    }

    // AC-2 (040)
    [Theory]
    [InlineData("quit")]
    [InlineData("/quit")]
    [InlineData("QUIT")]
    [InlineData("/Quit")]
    public void Quit_WithOrWithoutSlash_EndsTheLoopWithZero(string typed)
    {
        // No input follows: if the loop went on, reading the next key would throw.
        (int exitCode, string output) = Run(c => c.Input.PushTextWithEnter(typed));

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain("Unknown command", output);
    }

    // AC-3 (040)
    [Fact]
    public void Help_ListsEveryCommandWithoutSlash_AndShowsTheDataFolder()
    {
        string output = StripAnsi(RunHelp(data.Path, folderFromEnvironment: false));

        string[] names = ["addproject", "arrive", "booked", "break", "breakend", "change", "checkpoint", "help", "leave", "next", "prev", "projects", "quit", "remove", "today", "unbooked"];
        foreach (string name in names)
        {
            Assert.Matches(@"│ " + name + @" +│", output);
        }

        Assert.DoesNotMatch(@"│ /", output);
        Assert.Contains("Data folder:", output);
        Assert.Contains(data.Path, output);
    }

    // AC-3 (040)
    [Fact]
    public void Help_WithSlash_StillShowsTheTable()
    {
        (int exitCode, string output) = RunCommands("/help", "quit");

        Assert.Equal(0, exitCode);
        Assert.Contains(HelpFirstRow, string.Join('\n', Frames(output)[1]));
        Assert.DoesNotContain("Unknown command", output);
    }

    // AC-4 (040)
    [Theory]
    [InlineData("foo", "foo")]
    [InlineData("//help", "//help")]
    [InlineData("/", "/")]
    [InlineData("/ arrive", "/")]
    [InlineData("[red]x y", "[red]x")]
    public void UnknownToken_ShowsHintAboveInputBox_AndLoopGoesOn(string typed, string token)
    {
        (int exitCode, string output) = RunCommands(typed, "quit");

        Assert.Equal(0, exitCode);
        string[] frame = Frames(output)[1];
        AssertOnHintRow(frame, $"Unknown command {token}. Type help to see all commands.");
        Assert.DoesNotContain(frame, l => l.Contains("List all commands", StringComparison.Ordinal));
        Assert.Empty(Directory.EnumerateFiles(data.Path));
    }

    // AC-4 (040)
    [Fact]
    public void UnknownToken_HintIsRed_AndEscaped()
    {
        string output = RunAnsi("[bold]foo");

        Assert.Matches(@"\x1b\[(31|91|38;5;9)mUnknown command ", output);
        Assert.Contains("[bold]foo", output);
    }

    // AC-4 (040)
    [Fact]
    public void Hints_AndUsageLines_NeverShowASlashCommand()
    {
        (int exitCode, string output) = RunCommands(
            "checkpoint", "remove", "change", "addproject", "next x", "prev x", "today x", "projects x",
            "arrive 25:00", "remove 9", "change 1", "projects", "quit");

        Assert.Equal(0, exitCode);
        List<string[]> frames = Frames(output);
        Assert.Equal(13, frames.Count);
        foreach (string[] frame in frames)
        {
            Assert.DoesNotMatch(@"(^|[\s\[(])/[a-z]", string.Join('\n', frame));
        }

        Assert.Contains("Use checkpoint [H:mm|now|-10m|+5m]", output);
        Assert.Contains("Add one with addproject <work item> <title>.", output);
    }

    // AC-1 (040): an old "/next" line recalled from history (Up) still runs.
    [Fact]
    public void HistoryRecall_OfASlashLine_StillRuns()
    {
        (int exitCode, string output) = Run(c =>
        {
            c.Input.PushTextWithEnter("/next");
            c.Input.PushKey(ConsoleKey.UpArrow);
            c.Input.PushKey(ConsoleKey.Enter);
            c.Input.PushTextWithEnter("quit");
        });

        Assert.Equal(0, exitCode);
        Assert.Equal("Thursday, 2026-10-29", LastHeading(output));
    }

    // Job 041 ------------------------------------------------------------------------------

    /// <summary>Runs <paramref name="commands"/> in a fresh data folder (with one quicklist project) and returns the day file and exit code.</summary>
    private static (int ExitCode, string Day, string Output) RunInFreshFolder(TimeProvider time, params string[] commands)
    {
        using TempFolder folder = new TempFolder();
        File.WriteAllText(Path.Combine(folder.Path, "projects.json"), "{\"projects\":[{\"workItem\":500,\"title\":\"Times\"}]}");
        TestConsole console = new TestConsole().Interactive();
        foreach (string command in commands)
        {
            console.Input.PushTextWithEnter(command);
        }

        int exitCode = new App(console, time, new DayStore(folder.Path)).Run([]);
        string dayFile = Path.Combine(folder.Path, "2026-10-27.json");
        return (exitCode, File.Exists(dayFile) ? File.ReadAllText(dayFile) : string.Empty, console.Output);
    }

    // AC-4
    [Theory]
    [InlineData("a 8:15", "arrive 8:15")]
    [InlineData("A 8:15", "arrive 8:15")]
    [InlineData("/a 8:15", "arrive 8:15")]
    [InlineData("cp 9:00 #1 Standup", "checkpoint 9:00 #1 Standup")]
    [InlineData("b 12:00", "break 12:00")]
    [InlineData("be 12:30", "breakend 12:30")]
    [InlineData("l", "leave")]
    public void Alias_BooksTheSameAsTheFullName(string alias, string full)
    {
        FixedTimeProvider clock =
            new FixedTimeProvider(new DateTimeOffset(2026, 10, 27, 17, 45, 10, TimeSpan.Zero), TimeZoneInfo.Utc);

        (int aliasExit, string aliasDay, _) = RunInFreshFolder(clock, alias, "quit");
        (int fullExit, string fullDay, _) = RunInFreshFolder(clock, full, "quit");

        Assert.Equal(0, aliasExit);
        Assert.Equal(0, fullExit);
        Assert.NotEqual(string.Empty, aliasDay);
        Assert.Equal(fullDay, aliasDay);
    }

    // AC-4
    [Fact]
    public void Alias_L_WithoutArgument_BooksLeavingAtClockTime()
    {
        FixedTimeProvider clock =
            new FixedTimeProvider(new DateTimeOffset(2026, 10, 27, 17, 45, 10, TimeSpan.Zero), TimeZoneInfo.Utc);

        (int exitCode, string output) = RunCommands(clock, "l", "quit");

        Assert.Equal(0, exitCode);
        Assert.Equal(Entries("leaving 17:45"), ReadDay("2026-10-27"));
        Assert.Matches(@"1 +LE +17:45", LastFrame(output));
    }

    // AC-4
    [Theory]
    [InlineData("q")]
    [InlineData("Q")]
    [InlineData("/q")]
    public void Alias_Q_EndsTheLoopWithZero(string typed)
    {
        (int exitCode, string output) = Run(c => c.Input.PushTextWithEnter(typed));

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain("Unknown command", output);
    }

    // AC-4
    [Fact]
    public void Alias_WithDoubleSlash_IsUnknown()
    {
        (int exitCode, string output) = RunCommands("//a 8:15", "quit");

        Assert.Equal(0, exitCode);
        AssertOnHintRow(LastLayout(output), "Unknown command //a");
        Assert.Empty(Directory.EnumerateFileSystemEntries(data.Path));
    }

    // AC-5
    [Theory]
    [InlineData("a 25:00", "arrive 25:00", "arrive: 25:00 is not a time.")]
    [InlineData("l abc", "leave abc", "leave: abc is not a time.")]
    [InlineData("r", "remove", "remove")]
    [InlineData("c 1", "change 1", "change")]
    [InlineData("cp", "checkpoint", "checkpoint")]
    [InlineData("p x", "prev x", "prev")]
    public void Alias_ShowsTheSameHintAsTheFullName(string alias, string full, string expected)
    {
        FixedTimeProvider clock = FixedTimeProvider.At20261027();

        (_, string aliasDay, string aliasOutput) = RunInFreshFolder(clock, alias, "quit");
        (_, string fullDay, string fullOutput) = RunInFreshFolder(clock, full, "quit");

        // The checkpoint usage hint wraps onto more than one row.
        string aliasHint = string.Join(' ', LastLayout(aliasOutput)[^6..^3].Select(l => l.Trim()));
        string fullHint = string.Join(' ', LastLayout(fullOutput)[^6..^3].Select(l => l.Trim()));
        Assert.Contains(expected, aliasHint);
        Assert.Equal(fullHint, aliasHint);
        Assert.Equal(fullDay, aliasDay);
    }

    // AC-6
    [Fact]
    public void EveryCommand_HasItsAlias_AndAliasesAreDistinct()
    {
        CommandRegistry registry = CommandRegistry.CreateDefault();
        _ = new App(new TestConsole(), FixedTimeProvider.At20261027(), new DayStore(data.Path), registry);

        Dictionary<string, string> expected = new Dictionary<string, string>
        {
            ["absence"] = "ab",
            ["addproject"] = "ap",
            ["arrive"] = "a",
            ["backup"] = "bu",
            ["booked"] = "bk",
            ["break"] = "b",
            ["breakend"] = "be",
            ["change"] = "c",
            ["checkpoint"] = "cp",
            ["devops"] = "dv",
            ["help"] = "h",
            ["leave"] = "l",
            ["next"] = "n",
            ["prev"] = "p",
            ["projects"] = "pr",
            ["renameproject"] = "rn",
            ["removeproject"] = "xp",
            ["moveproject"] = "mp",
            ["quit"] = "q",
            ["remove"] = "r",
            ["export"] = "ex",
            ["find"] = "f",
            ["report"] = "rp",
            ["settings"] = "se",
            ["statistics"] = "st",
            ["summary"] = "s",
            ["sync"] = "sy",
            ["today"] = "t",
            ["unbooked"] = "ub",
            ["update"] = "up",
            ["vacation"] = "va",
            ["correction"] = "co",
            ["import"] = "im",
        };

        Assert.Equal(expected, registry.Commands.ToDictionary(c => c.Name, c => c.Alias));
        List<string> aliases = registry.Commands.Select(c => c.Alias.ToLowerInvariant()).ToList();
        Assert.Equal(aliases.Count, aliases.Distinct().Count());
        Assert.All(registry.Commands, c => Assert.True(registry.TryGet(c.Alias, out Command? found) && found.Name == c.Name));
    }

    // AC-7
    [Fact]
    public void Help_ShowsAliasColumn()
    {
        TestConsole console = new TestConsole().Interactive().EmitAnsiSequences();
        console.Input.PushTextWithEnter("h");
        PageDown(console, HelpPages);
        console.Input.PushTextWithEnter("quit");

        new App(console, FixedTimeProvider.At20261027(), new DayStore(data.Path)).Run([]);
        string plain = Regex.Replace(console.Output, @"\x1b\[[0-9;?]*[A-Za-z]", string.Empty);

        Assert.Matches(@"│ Command +│ Alias +│ Description +│", plain);
        Assert.Matches(@"│ checkpoint +│ cp +│ Book a checkpoint", plain);
        Assert.Matches(@"│ arrive +│ a +│ Book your arrival", plain);
        Assert.Matches(@"│ today +│ t +│ Go back to today", plain);
        // Header in grey, alias cells in steelblue1 (colour 75).
        Assert.Matches(@"\x1b\[38;5;8mAlias", console.Output);
        Assert.Matches(@"\x1b\[38;5;75mcp\x1b\[0m", console.Output);
    }

    // Job 043 ------------------------------------------------------------------------------

    private const string SummaryDay =
        "{\"entries\":[{\"kind\":\"checkpoint\",\"time\":\"09:00\",\"name\":\"Review\",\"workItem\":1234}," +
        "{\"kind\":\"checkpoint\",\"time\":\"10:00\",\"name\":\"[x]\"}," +
        "{\"kind\":\"checkpoint\",\"time\":\"10:30\",\"name\":\"Fix\",\"workItem\":1234}]}";

    // AC-6, AC-4
    [Fact]
    public void Summary_OnToday_ShowsTableWithWorkItemsNamesAndTotals()
    {
        File.WriteAllText(ProjectsFile, "{\"projects\":[{\"workItem\":1234,\"title\":\"Times\"}]}");

        // The lone break end shows the "outside arrival and leaving" hint, so give the frame room for it.
        (int exitCode, string output) = Run(c =>
        {
            c.Profile.Height = 30;
            foreach (string command in new[] { "cp 9:00 #1 Review", "cp 10:00 [x]", "cp 10:30 #1 Fix", "breakend 11:00", "cp 17:00 [x]", "leave 17:30", "summary", "quit" })
            {
                c.Input.PushTextWithEnter(command);
            }
        });

        Assert.Equal(0, exitCode);
        string[] frame = LastLayout(output);
        AssertLayout(frame, 30);
        Assert.Contains(frame, l => Regex.IsMatch(l, @"│ Work item +│ Name +│ Time +│"));
        // #1234: 9:00-10:00 and 10:30-17:00 = 7:30; [x]: 10:00-10:30 and 17:00-17:30 = 1:00.
        int workItem = Array.FindIndex(frame, l => Regex.IsMatch(l, @"│ #1234 +│ Review, Fix +│ 7:30 +│"));
        int name = Array.FindIndex(frame, l => Regex.IsMatch(l, @"│ +│ \[x\] +│ 1:00 +│"));
        Assert.True(workItem > 0 && name > workItem);
        Assert.DoesNotContain("open", string.Join("\n", frame));
        // The quicklist stays visible.
        Assert.Contains(frame, l => l.Contains("Projects", StringComparison.Ordinal));
        Assert.Equal(string.Empty, frame[^4].Trim());
    }

    // AC-4
    [Fact]
    public void Summary_OnToday_OpenCheckpoint_CountsToTheCurrentMinute()
    {
        File.WriteAllText(DayFile("2026-10-27"), "{\"entries\":[{\"kind\":\"checkpoint\",\"time\":\"17:00\",\"name\":\"Fix\"}]}");

        (_, string output) = RunCommands(At183059(), "summary", "quit");

        string[] frame = LastLayout(output);
        Assert.Contains(frame, l => Regex.IsMatch(l, @"│ +│ Fix +│ 1:30 +│"));
        Assert.DoesNotContain("open", string.Join("\n", frame));
    }

    // AC-6
    [Fact]
    public void Summary_HeadersAndBorderAreGrey_TimeIsSteelBlue_NamesAreEscaped()
    {
        File.WriteAllText(DayFile("2026-10-27"), SummaryDay);

        string output = RunAnsi("summary");

        Assert.Matches(@"\x1b\[(90|38;5;8)mWork item\x1b\[0m", output);
        Assert.Matches(@"\x1b\[(90|38;5;8)mName\x1b\[0m", output);
        Assert.Matches(@"\x1b\[(90|38;5;8)mTime\x1b\[0m", output);
        Assert.Matches(@"\x1b\[(90|38;5;8)m╭─", output);
        // [x] runs from 10:00 to 10:30; #1234 is 9:00-10:00 plus 10:30 to now (18:30).
        Assert.Matches(@"\x1b\[38;5;75m0:30\x1b\[0m", output);
        Assert.Matches(@"\x1b\[38;5;75m9:00\x1b\[0m", output);
        Assert.Contains("[x]", output);
    }

    // AC-5
    [Fact]
    public void Summary_OnAPastDay_ShowsOpenAfterTheTime()
    {
        File.WriteAllText(DayFile("2026-10-26"), SummaryDay);

        (int exitCode, string output) = RunCommands("prev", "summary", "quit");

        Assert.Equal(0, exitCode);
        string[] frame = LastLayout(output);
        Assert.Contains("Monday, 2026-10-26", string.Join("\n", frame));
        Assert.Contains(frame, l => Regex.IsMatch(l, @"│ #1234 +│ Review, Fix +│ 1:00 open +│"));
        Assert.Contains(frame, l => Regex.IsMatch(l, @"│ +│ \[x\] +│ 0:30 +│"));
        Assert.Matches(@"\x1b\[38;5;75m1:00\x1b\[0m \x1b\[(90|38;5;8)mopen\x1b\[0m", RunAnsiCommands("prev", "summary"));
    }

    // AC-5
    [Fact]
    public void Summary_OnAFutureDay_OnlyCheckpointOpen_ShowsZeroOpen()
    {
        File.WriteAllText(DayFile("2026-10-28"), "{\"entries\":[{\"kind\":\"checkpoint\",\"time\":\"09:00\",\"name\":\"Plan\"}]}");

        (_, string output) = RunCommands("next", "summary", "quit");

        Assert.Contains(LastLayout(output), l => Regex.IsMatch(l, @"│ +│ Plan +│ 0:00 open +│"));
    }

    // AC-7, AC-8
    [Fact]
    public void Summary_WithNoFile_ShowsGreyMessage_AndCreatesNothing()
    {
        (int exitCode, string output) = RunCommands("summary", "quit");

        Assert.Equal(0, exitCode);
        Assert.Contains("No checkpoints on this day.", LastFrame(output));
        Assert.Empty(Directory.EnumerateFileSystemEntries(data.Path));
        Assert.Matches(@"\x1b\[(90|38;5;8)mNo checkpoints on this day\.", RunAnsi("summary"));
        Assert.Empty(Directory.EnumerateFileSystemEntries(data.Path));

        using TempFolder missing = new TempFolder(create: false);
        (_, string missingOutput) = Run(c =>
        {
            c.Input.PushTextWithEnter("summary");
            c.Input.PushTextWithEnter("quit");
        }, dataFolder: missing.Path);
        Assert.Contains("No checkpoints on this day.", missingOutput);
        AssertLeftNothing(missing.Path);
    }

    // AC-7, AC-8
    [Fact]
    public void Summary_DayWithoutCheckpoints_ShowsGreyMessage_AndLeavesBytesUnchanged()
    {
        File.WriteAllText(DayFile("2026-10-27"), "{\"entries\":[{\"kind\":\"arrival\",\"time\":\"08:00\"}]}");
        byte[] before = File.ReadAllBytes(DayFile("2026-10-27"));

        (_, string output) = RunCommands("summary", "quit");

        Assert.Contains("No checkpoints on this day.", LastFrame(output));
        Assert.Equal(before, File.ReadAllBytes(DayFile("2026-10-27")));
    }

    // AC-8
    [Fact]
    public void Summary_NeverWrites_DayFileStaysByteForByte_AndNoFileIsCreated()
    {
        // A legacy file would be rewritten in the new format by any save.
        File.WriteAllText(DayFile("2026-10-27"), "{\"arrival\":\"08:15\"}");
        File.WriteAllText(DayFile("2026-10-26"), SummaryDay);
        byte[] today = File.ReadAllBytes(DayFile("2026-10-27"));
        byte[] past = File.ReadAllBytes(DayFile("2026-10-26"));

        (int exitCode, _) = RunCommands("summary", "prev", "summary", "prev", "summary", "quit");

        Assert.Equal(0, exitCode);
        Assert.Equal(today, File.ReadAllBytes(DayFile("2026-10-27")));
        Assert.Equal(past, File.ReadAllBytes(DayFile("2026-10-26")));
        Assert.Equal(["2026-10-26.json", "2026-10-27.json"],
            Directory.EnumerateFileSystemEntries(data.Path).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    // AC-9
    [Fact]
    public void Summary_InvalidDayFile_ShowsRedHintAboveInputBox_AndLoopKeepsRunning()
    {
        File.WriteAllText(DayFile("2026-10-27"), "{not json");
        byte[] before = File.ReadAllBytes(DayFile("2026-10-27"));

        (int exitCode, string output) = RunCommands("summary", "quit");

        Assert.Equal(0, exitCode);
        AssertOnHintRow(LastLayout(output), "summary: The file for 2026-10-27");
        Assert.DoesNotContain("   at ", output);
        Assert.DoesNotContain("failed:", output);
        Assert.Equal(before, File.ReadAllBytes(DayFile("2026-10-27")));
        Assert.Matches(@"\x1b\[(31|91|38;5;9)msummary: The file for 2026-10-27", RunAnsi("summary"));
    }

    // AC-9
    [Fact]
    public void Summary_UnreadableDayFile_ShowsRedHint()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.WriteAllText(DayFile("2026-10-27"), SummaryDay);
        UnixFileMode mode = File.GetUnixFileMode(DayFile("2026-10-27"));
        File.SetUnixFileMode(DayFile("2026-10-27"), UnixFileMode.None);
        try
        {
            if (CanRead(DayFile("2026-10-27")))
            {
                // Running as root: access can't be denied.
                return;
            }

            (int exitCode, string output) = RunCommands("summary", "quit");

            Assert.Equal(0, exitCode);
            Assert.Contains("summary: ", LastLayout(output)[^4]);
            Assert.DoesNotContain("   at ", output);
        }
        finally
        {
            File.SetUnixFileMode(DayFile("2026-10-27"), mode);
        }
    }

    private static bool CanRead(string path)
    {
        try
        {
            File.ReadAllBytes(path);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    // AC-10
    [Fact]
    public void Summary_WithArgument_ShowsTakesNoArgumentsHint()
    {
        File.WriteAllText(DayFile("2026-10-27"), SummaryDay);

        (_, string output) = RunCommands("summary x", "quit");

        string[] frame = LastLayout(output);
        AssertOnHintRow(frame, "summary takes no arguments.");
        Assert.DoesNotContain(frame, l => l.Contains("Work item", StringComparison.Ordinal));
    }

    // AC-10
    [Theory]
    [InlineData("s")]
    [InlineData("S")]
    [InlineData("/summary")]
    [InlineData("/s")]
    public void Summary_AliasAndSlash_DoTheSameAsSummary(string typed)
    {
        File.WriteAllText(DayFile("2026-10-27"), SummaryDay);

        (_, string full) = RunCommands("summary", "quit");
        (_, string other) = RunCommands(typed, "quit");

        Assert.Equal(LastLayout(full), LastLayout(other));
        Assert.Contains(LastLayout(other), l => Regex.IsMatch(l, @"│ #1234 +│ Review, Fix +│ 9:00 +│"));
    }

    // AC-10
    [Fact]
    public void Help_ListsSummary_WithAliasS_AndDescription()
    {
        TestConsole console = new TestConsole().Interactive().EmitAnsiSequences();
        console.Profile.Width = 120;
        console.Input.PushTextWithEnter("help");
        PageDown(console, HelpPages);
        console.Input.PushTextWithEnter("quit");

        new App(console, FixedTimeProvider.At20261027(), new DayStore(data.Path)).Run([]);
        string plain = Regex.Replace(console.Output, @"\x1b\[[0-9;?]*[A-Za-z]", string.Empty);

        Assert.Matches(@"│ summary +│ s +│ Show the time taken per work item from this day's checkpoints +│", plain);
    }

    /// <summary>Runs <paramref name="commands"/> on a wide ANSI console and returns its output, to check colours.</summary>
    private string RunAnsiCommands(params string[] commands)
    {
        TestConsole console = new TestConsole().Interactive().EmitAnsiSequences();
        console.Profile.Width = 200;
        foreach (string command in commands)
        {
            console.Input.PushTextWithEnter(command);
        }

        console.Input.PushTextWithEnter("quit");
        Assert.Equal(0, new App(console, FixedTimeProvider.At20261027(), new DayStore(data.Path)).Run([]));
        return console.Output;
    }

    // AC-7 (047)
    [Fact]
    public void Day_ShortBreak_ShowsBreakLineAndWarning()
    {
        (int exitCode, string output) = RunWide(120, "arrive 8:00", "break 12:00", "breakend 12:20", "leave 17:00", "quit");

        Assert.Equal(0, exitCode);
        string frame = LastFrame(output);
        Assert.Contains("Breaks 0:20 taken · 0:30 required", frame);
        Assert.Contains("Breaks too short: 0:10 missing", frame);
    }

    // AC-7 (047)
    [Fact]
    public void Day_BreakEndChanged_ClearsWarning()
    {
        (int exitCode, string output) =
            RunWide(120, "arrive 8:00", "break 12:00", "breakend 12:20", "leave 17:00", "change 3 12:30", "quit");

        Assert.Equal(0, exitCode);
        string frame = LastFrame(output);
        Assert.Matches(@"3 +BE +12:30", frame);
        Assert.Contains("Breaks 0:30 taken · 0:30 required", frame);
        Assert.DoesNotContain("too short", frame);
    }

    // Safer titles and names (job 056) ---------------------------------------------------------

    private const string AddProjectFormatHint = "addproject: the title must not contain invisible formatting or broken characters. Retype it without the hidden characters.";
    private const string CheckpointFormatHint = "checkpoint: the name must not contain invisible formatting or broken characters. Retype it without the hidden characters.";

    /// <summary>
    /// Types <paramref name="escaped"/> (unescaped with <see cref="Regex.Unescape"/>, so lone surrogates
    /// never go through xUnit's test case serialization) key by key, then Enter, then <c>quit</c>.
    /// </summary>
    private (int ExitCode, string Output) TypeEscaped(string escaped, bool ansi = false)
    {
        Action<TestConsole> input = c =>
        {
            c.Profile.Width = 200;
            foreach (char ch in Regex.Unescape(escaped))
            {
                // TestConsoleInput.PushText only takes characters with a ConsoleKey (below 256).
                c.Input.PushKey(new ConsoleKeyInfo(ch, ch < 256 ? (ConsoleKey)char.ToUpperInvariant(ch) : ConsoleKey.NoName, false, false, false));
            }

            c.Input.PushKey(ConsoleKey.Enter);
            c.Input.PushTextWithEnter("quit");
        };

        if (ansi)
        {
            return (0, RunAnsi(input));
        }

        return Run(input);
    }

    // AC-3, AC-5 (056)
    [Theory]
    [InlineData("addproject 1234 abc\\u202Edef")]
    [InlineData("addproject 1234 abc\\u200Bdef")]
    [InlineData("addproject 1234 abc\\uD800def")]
    [InlineData("addproject 1234 \\u200B")]
    [InlineData("addproject 1234 abc\\uDB40\\uDC41def")]
    public void AddProject_FormatOrBrokenTitle_ShowsHint_AndWritesNothing(string escaped)
    {
        (int exitCode, string output) = TypeEscaped(escaped);

        Assert.Equal(0, exitCode);
        string[] frame = LastLayout(output);
        AssertOnHintRow(frame, AddProjectFormatHint);
        Assert.DoesNotContain("abc", frame[^4]);
        Assert.DoesNotContain("def", frame[^4]);
        Assert.False(File.Exists(ProjectsFile));
        Assert.Empty(Directory.EnumerateFileSystemEntries(data.Path));
    }

    // AC-3 (056)
    [Fact]
    public void AddProject_FormatTitle_LeavesExistingFileUnchanged_AndHintIsRed()
    {
        WriteTwoProjects();
        byte[] before = File.ReadAllBytes(ProjectsFile);

        (_, string output) = TypeEscaped("addproject 1234 abc\\u202Edef", ansi: true);

        Assert.Matches(@"\x1b\[(31|91|38;5;9)maddproject: the title must not contain invisible formatting or broken characters\. Retype it without the hidden characters\.", output);
        Assert.Equal(before, File.ReadAllBytes(ProjectsFile));
    }

    // AC-3 (056)
    [Fact]
    public void AddProject_EmojiTitle_IsAdded()
    {
        (int exitCode, _) = TypeEscaped("addproject 1234 Launch \\uD83D\\uDE80");

        Assert.Equal(0, exitCode);
        Assert.Equal("Launch 🚀", ReadProjects().Projects[0].Title);
    }

    // AC-4, AC-5 (056)
    [Theory]
    [InlineData("checkpoint abc\\u202Edef")]
    [InlineData("checkpoint 9:30 #1 abc\\u202Edef")]
    [InlineData("checkpoint abc\\u200Ddef")]
    [InlineData("checkpoint abc\\uDC00def")]
    [InlineData("checkpoint 9:30 abc\\uFEFFdef")]
    public void Checkpoint_FormatOrBrokenName_ShowsHint_AndWritesNoFile(string escaped)
    {
        WriteTwoProjects();
        byte[] projects = File.ReadAllBytes(ProjectsFile);

        (int exitCode, string output) = TypeEscaped(escaped);

        Assert.Equal(0, exitCode);
        string[] frame = LastLayout(output);
        AssertOnHintRow(frame, CheckpointFormatHint);
        Assert.DoesNotContain("abc", frame[^4]);
        Assert.DoesNotContain("def", frame[^4]);
        Assert.False(File.Exists(DayFile("2026-10-27")));
        Assert.Equal(projects, File.ReadAllBytes(ProjectsFile));
    }

    // AC-4 (056)
    [Fact]
    public void Checkpoint_FormatName_LeavesExistingDayUnchanged_AndHintIsRed()
    {
        File.WriteAllText(DayFile("2026-10-27"), ThreeEntries);
        byte[] before = File.ReadAllBytes(DayFile("2026-10-27"));

        (_, string output) = TypeEscaped("checkpoint abc\\u202Edef", ansi: true);

        Assert.Matches(@"\x1b\[(31|91|38;5;9)mcheckpoint: the name must not contain invisible formatting or broken characters\. Retype it without the hidden characters\.", output);
        Assert.Equal(before, File.ReadAllBytes(DayFile("2026-10-27")));
    }

    // AC-4 (056)
    [Fact]
    public void Checkpoint_EmojiName_IsBooked()
    {
        (int exitCode, _) = TypeEscaped("checkpoint 9:00 Launch \\uD83D\\uDE80");

        Assert.Equal(0, exitCode);
        Assert.Equal([Checkpoint(9, 0, "Launch 🚀")], LoadDay().Entries);
    }

    // AC-9 (056): the control-character check still comes first.
    [Theory]
    [InlineData("checkpoint a\\u2028\\u202Eb", "checkpoint: the name must not contain control characters. Retype it without the hidden characters.")]
    [InlineData("addproject 12 a\\u2028\\u202Eb", "addproject: the title must not contain control characters. Retype it without the hidden characters.")]
    public void FormatAndControlCharacters_ControlHintWins(string escaped, string expected)
    {
        (_, string output) = TypeEscaped(escaped);

        AssertOnHintRow(LastLayout(output), expected);
        Assert.False(File.Exists(ProjectsFile));
        Assert.False(File.Exists(DayFile("2026-10-27")));
    }

    // Wider invisible-character check (job 095) -----------------------------------------------

    // AC-5 (095)
    [Theory]
    [InlineData("addproject 7 abc\\u3164def")]
    [InlineData("addproject 7 abc\\u2800def")]
    [InlineData("addproject 7 a\\uFE0F\\uFE0F")]
    public void AddProject_InvisibleTitle_ShowsHint_AndWritesNothing(string escaped)
    {
        (int exitCode, string output) = TypeEscaped(escaped);

        Assert.Equal(0, exitCode);
        string[] frame = LastLayout(output);
        AssertOnHintRow(frame, AddProjectFormatHint);
        Assert.False(File.Exists(ProjectsFile));
        Assert.Empty(Directory.EnumerateFileSystemEntries(data.Path));
    }

    // AC-5 (095)
    [Fact]
    public void AddProject_InvisibleTitle_LeavesExistingFileUnchanged_AndHintIsRed()
    {
        WriteTwoProjects();
        byte[] before = File.ReadAllBytes(ProjectsFile);

        (_, string output) = TypeEscaped("addproject 7 abc\\u3164def", ansi: true);

        Assert.Matches(@"\x1b\[(31|91|38;5;9)maddproject: the title must not contain invisible formatting or broken characters\. Retype it without the hidden characters\.", output);
        Assert.Equal(before, File.ReadAllBytes(ProjectsFile));
    }

    // AC-5 (095)
    [Theory]
    [InlineData("checkpoint abc\\u2800def")]
    [InlineData("checkpoint abc\\u115Fdef")]
    public void Checkpoint_InvisibleName_ShowsHint_AndWritesNoFile(string escaped)
    {
        (int exitCode, string output) = TypeEscaped(escaped);

        Assert.Equal(0, exitCode);
        string[] frame = LastLayout(output);
        AssertOnHintRow(frame, CheckpointFormatHint);
        Assert.False(File.Exists(DayFile("2026-10-27")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(data.Path));
    }

    // AC-5 (095): the loop goes on after the hint, and an emoji with its selector is still booked.
    [Fact]
    public void Checkpoint_InvisibleName_ThenEmojiName_IsBooked()
    {
        (int exitCode, string output) = Run(c =>
        {
            c.Profile.Width = 200;
            foreach (char ch in "checkpoint 9:00 abc\u2800def")
            {
                c.Input.PushKey(new ConsoleKeyInfo(ch, ch < 256 ? (ConsoleKey)char.ToUpperInvariant(ch) : ConsoleKey.NoName, false, false, false));
            }

            c.Input.PushKey(ConsoleKey.Enter);
            foreach (char ch in "checkpoint 9:00 Love \u2764\uFE0F")
            {
                c.Input.PushKey(new ConsoleKeyInfo(ch, ch < 256 ? (ConsoleKey)char.ToUpperInvariant(ch) : ConsoleKey.NoName, false, false, false));
            }

            c.Input.PushKey(ConsoleKey.Enter);
            c.Input.PushTextWithEnter("quit");
        });

        Assert.Equal(0, exitCode);
        Assert.Contains(CheckpointFormatHint, output);
        Assert.Equal([Checkpoint(9, 0, "Love \u2764\uFE0F")], LoadDay().Entries);
    }

    // Fix-it hint for rejected titles (job 087) ------------------------------------------------

    private const string RetypeHint = "Retype it without the hidden characters.";

    // AC-1..AC-5 (087)
    [Theory]
    [InlineData("addproject 12 a\\u202Eb", "addproject: the title must not contain invisible formatting or broken characters. Retype it without the hidden characters.")]
    [InlineData("addproject 12 a\\u2028b", "addproject: the title must not contain control characters. Retype it without the hidden characters.")]
    [InlineData("checkpoint a\\u200Bb", "checkpoint: the name must not contain invisible formatting or broken characters. Retype it without the hidden characters.")]
    [InlineData("checkpoint a\\u2028b", "checkpoint: the name must not contain control characters. Retype it without the hidden characters.")]
    [InlineData("addproject 12 a\\uD800b", "addproject: the title must not contain invisible formatting or broken characters. Retype it without the hidden characters.")]
    [InlineData("checkpoint a\\uD800b", "checkpoint: the name must not contain invisible formatting or broken characters. Retype it without the hidden characters.")]
    public void RejectedHiddenCharacters_HintSaysRetype_AndWritesNothing(string escaped, string expected)
    {
        (int exitCode, string output) = TypeEscaped(escaped);

        Assert.Equal(0, exitCode);
        AssertOnHintRow(LastLayout(output), expected);
        Assert.False(File.Exists(ProjectsFile));
        Assert.False(File.Exists(DayFile("2026-10-27")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(data.Path));
    }

    // AC-1, AC-3 (087)
    [Theory]
    [InlineData("addproject 12 a\\u202Eb", @"\x1b\[(31|91|38;5;9)maddproject: the title must not contain invisible formatting or broken characters\. Retype it without the hidden characters\.")]
    [InlineData("checkpoint a\\u200Bb", @"\x1b\[(31|91|38;5;9)mcheckpoint: the name must not contain invisible formatting or broken characters\. Retype it without the hidden characters\.")]
    public void RejectedHiddenCharacters_RetypeHintIsRed(string escaped, string pattern)
    {
        (_, string output) = TypeEscaped(escaped, ansi: true);

        Assert.Matches(pattern, output);
        Assert.False(File.Exists(ProjectsFile));
        Assert.False(File.Exists(DayFile("2026-10-27")));
    }

    // AC-6 (087)
    [Theory]
    [InlineData("addproject 12 ", "the title is longer than 200 characters.", 201)]
    [InlineData("checkpoint ", "the name is longer than 200 characters.", 201)]
    [InlineData("addproject 12", "the title is missing.", 0)]
    [InlineData("checkpoint", "say the checkpoint's name.", 0)]
    [InlineData("addproject abc Title", "abc is not a work item number.", 0)]
    [InlineData("checkpoint #abc Standup", "#abc is not a project number.", 0)]
    public void OtherTitleHints_DoNotSayRetype(string command, string expected, int padding)
    {
        (_, string output) = RunWide(200, command + new string('x', padding), "quit");

        string[] frame = LastLayout(output);
        AssertOnHintRow(frame, expected);
        Assert.DoesNotContain("Retype it", frame[^4]);
        Assert.DoesNotContain(RetypeHint, output);
    }

    // AC-7 (087)
    [Theory]
    [InlineData("addproject 12 Launch \\uD83D\\uDE80")]
    [InlineData("checkpoint 9:00 Launch \\uD83D\\uDE80")]
    public void ValidTitleWithEmoji_IsAccepted_WithoutRetypeHint(string escaped)
    {
        (int exitCode, string output) = TypeEscaped(escaped);

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain("Retype it", output);
        Assert.DoesNotContain("must not contain", output);
    }

    // Bracketed paste (job 066) ---------------------------------------------------------------

    // AC-7
    [Fact]
    public void Paste_WithLineBreaks_RunsOneCommand_OnlyOnEnter()
    {
        List<string> calls = [];
        CommandRegistry registry = CommandRegistry.CreateDefault();
        registry.Register("rec", "rc", "Records its argument", args => { calls.Add(args); return CommandResult.None; });

        (int exitCode, _) = RunWithMouse(c =>
        {
            c.Input.PushKey(new ConsoleKeyInfo('\u001b', ConsoleKey.Escape, false, false, false));
            c.Input.PushText("[200~rec a");
            c.Input.PushKey(ConsoleKey.Enter);
            c.Input.PushText("quit");
            c.Input.PushKey(new ConsoleKeyInfo('\n', ConsoleKey.Enter, false, false, false));
            c.Input.PushText("rec b");
            c.Input.PushKey(new ConsoleKeyInfo('\u001b', ConsoleKey.Escape, false, false, false));
            c.Input.PushText("[201~");
            c.Input.PushKey(ConsoleKey.Enter);
            c.Input.PushTextWithEnter("quit");
        }, registry: registry);

        Assert.Equal(0, exitCode);
        Assert.Equal(["a quit rec b"], calls);
    }

    // Job 069 ------------------------------------------------------------------------------

    /// <summary>The first non-empty line below the summary table's bottom border (the one with column joins), trimmed.</summary>
    private static string SummaryCaption(string[] frame)
    {
        int bottom = Array.FindIndex(frame, l => l.TrimStart().StartsWith('╰') && l.Contains('┴', StringComparison.Ordinal));
        Assert.True(bottom > 0, "no summary table bottom border in the frame");
        string? caption = frame.Skip(bottom + 1).FirstOrDefault(l => l.Trim().Length > 0);
        Assert.NotNull(caption);
        return caption.Trim();
    }

    // AC-1, AC-3, AC-6
    [Fact]
    public void SummaryCaption_OnToday_IsTheDayHeadingBelowTheTable()
    {
        File.WriteAllText(DayFile("2026-10-27"), SummaryDay);

        (int exitCode, string output) = RunCommands("summary", "quit");

        Assert.Equal(0, exitCode);
        string[] frame = LastLayout(output);
        AssertLayout(frame);
        Assert.Equal(string.Empty, frame[^4].Trim());
        Assert.Equal("Tuesday, 2026-10-27", SummaryCaption(frame));
        Assert.Equal(DayView.Heading(new DateOnly(2026, 10, 27)), SummaryCaption(frame));
        Assert.Contains(frame, l => Regex.IsMatch(l, @"│ #1234 +│ Review, Fix +│ 9:00 +│"));
        Assert.Contains(frame, l => Regex.IsMatch(l, @"│ +│ \[x\] +│ 0:30 +│"));
    }

    // AC-2, AC-3
    [Theory]
    [InlineData("prev", "2026-10-26", "Monday, 2026-10-26")]
    [InlineData("next", "2026-10-28", "Wednesday, 2026-10-28")]
    public void SummaryCaption_FollowsTheViewedDay(string move, string file, string expected)
    {
        File.WriteAllText(DayFile(file), SummaryDay);

        (int exitCode, string output) = RunCommands(move, "summary", "quit");

        Assert.Equal(0, exitCode);
        string[] frame = LastLayout(output);
        AssertLayout(frame);
        Assert.Equal(expected, SummaryCaption(frame));
        Assert.Equal(DayView.Heading(DateOnly.Parse(file, System.Globalization.CultureInfo.InvariantCulture)), SummaryCaption(frame));
    }

    // AC-4
    [Fact]
    public void SummaryCaption_IsGrey()
    {
        File.WriteAllText(DayFile("2026-10-27"), SummaryDay);
        File.WriteAllText(DayFile("2026-10-26"), SummaryDay);

        Assert.Matches(@"\x1b\[(90|38;5;8)mTuesday, 2026-10-27\x1b\[0m", RunAnsi("summary"));
        Assert.Matches(@"\x1b\[(90|38;5;8)mMonday, 2026-10-26\x1b\[0m", RunAnsiCommands("prev", "summary"));
    }

    // AC-5
    [Fact]
    public void SummaryCaption_DayWithoutCheckpoints_ShowsNoCaptionAndNoTable()
    {
        File.WriteAllText(DayFile("2026-10-27"), "{\"entries\":[{\"kind\":\"arrival\",\"time\":\"08:00\"}]}");

        (_, string output) = RunCommands("summary", "quit");

        string[] frame = LastLayout(output);
        Assert.Contains(frame, l => l.Contains("No checkpoints on this day.", StringComparison.Ordinal));
        Assert.DoesNotContain(frame, l => l.Contains('┴', StringComparison.Ordinal));
        Assert.DoesNotContain(frame, l => l.Contains("Work item", StringComparison.Ordinal));
        // The date shows once: in the day view heading only.
        Assert.Single(frame, l => l.Contains("2026-10-27", StringComparison.Ordinal));
    }

    // Job 097 ------------------------------------------------------------------------------

    /// <summary>
    /// Runs the commands, then <c>quit</c>, on a console <paramref name="width"/> columns wide and 80 rows
    /// high (so the whole summary fits without a scroll cue), with ANSI escapes when <paramref name="ansi"/> is set.
    /// </summary>
    private string RunNarrow(int width, bool ansi, params string[] commands)
    {
        TestConsole console = new TestConsole().Interactive();
        if (ansi)
        {
            console.EmitAnsiSequences();
        }

        console.Profile.Width = width;
        console.Profile.Height = 80;
        foreach (string command in commands)
        {
            console.Input.PushTextWithEnter(command);
        }

        console.Input.PushTextWithEnter("quit");
        Assert.Equal(0, new App(console, FixedTimeProvider.At20261027(), new DayStore(data.Path)).Run([]));
        return console.Output;
    }

    // AC-1, AC-2, AC-4 (097)
    [Theory]
    [InlineData(40)]
    [InlineData(24)]
    public void SummaryCaption_NarrowTerminal_StaysOnOneLineDirectlyBelowTheTable(int width)
    {
        File.WriteAllText(DayFile("2026-10-28"), SummaryDay);

        string[] frame = LastLayout(RunNarrow(width, false, "next", "summary"));

        Assert.Equal(80, frame.Length);
        Assert.StartsWith("╭", frame[^3]);
        Assert.StartsWith("│ > ", frame[^2]);
        Assert.StartsWith("╰", frame[^1]);
        Assert.All(frame, l => Assert.True(l.Length <= width, $"line wider than {width}: '{l}'"));
        int bottom = Array.FindIndex(frame, l => l.TrimStart().StartsWith('╰') && l.Contains('┴', StringComparison.Ordinal));
        Assert.True(bottom > 0, "no summary table bottom border in the frame");
        string caption = frame[bottom + 1];
        Assert.Equal("Wednesday, 2026-10-28", caption.Trim());
        Assert.DoesNotContain('…', caption);
        int borderWidth = frame[bottom].Trim().Length;
        Assert.True(borderWidth >= 21, $"table ({borderWidth}) narrower than the heading");
        int lead = caption.Length - caption.TrimStart().Length;
        Assert.InRange(lead, ((borderWidth - 21) / 2) - 1, ((borderWidth - 21) / 2) + 1);
    }

    // AC-3, AC-4 (097)
    [Fact]
    public void SummaryCaption_TableNarrowerThanHeading_WrapsAtTheWordBreakWithoutLosingText()
    {
        File.WriteAllText(DayFile("2026-10-28"), SummaryDay);

        string[] frame = LastLayout(RunNarrow(20, false, "next", "summary"));

        Assert.Equal(80, frame.Length);
        Assert.StartsWith("╭", frame[^3]);
        Assert.StartsWith("│ > ", frame[^2]);
        Assert.StartsWith("╰", frame[^1]);
        Assert.All(frame, l => Assert.True(l.Length <= 20, $"line wider than 20: '{l}'"));
        int bottom = Array.FindIndex(frame, l => l.TrimStart().StartsWith('╰') && l.Contains('┴', StringComparison.Ordinal));
        Assert.True(bottom > 0, "no summary table bottom border in the frame");
        Assert.True(frame[bottom].Trim().Length < 21, "the table is not narrower than the heading");
        Assert.Equal("Wednesday,", frame[bottom + 1].Trim());
        Assert.Equal("2026-10-28", frame[bottom + 2].Trim());
        Assert.DoesNotContain('…', frame[bottom + 1]);
        Assert.DoesNotContain('…', frame[bottom + 2]);
        Assert.DoesNotContain(frame[(bottom + 1)..], l => l.Contains("Wednesday, 2026-10-28", StringComparison.Ordinal));
    }

    // AC-5 (097)
    [Fact]
    public void SummaryCaption_NarrowTerminal_IsGrey()
    {
        File.WriteAllText(DayFile("2026-10-28"), SummaryDay);

        Assert.Matches(@"\x1b\[(90|38;5;8)mWednesday, 2026-10-28\x1b\[0m", RunNarrow(40, true, "next", "summary"));
        string narrow = RunNarrow(20, true, "next", "summary");
        Assert.Matches(@"\x1b\[(90|38;5;8)mWednesday,\s*\x1b\[0m", narrow);
        Assert.Matches(@"\x1b\[(90|38;5;8)m\s*2026-10-28\s*\x1b\[0m", narrow);
    }

    /// <summary>The commands <see cref="App"/> registers, read from a registry it was built with; the loop is never run.</summary>
    private List<Command> RegisteredCommands()
    {
        CommandRegistry registry = CommandRegistry.CreateDefault();
        _ = new App(new TestConsole(), FixedTimeProvider.At20261027(), new DayStore(data.Path), registry);
        return registry.Commands.ToList();
    }

    private static string ReadRepoReadme()
    {
        DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Times.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.True(directory is not null, "Times.slnx not found above " + AppContext.BaseDirectory);
        return File.ReadAllText(Path.Combine(directory.FullName, "README.md"));
    }

    /// <summary>
    /// The rows of the first table under the <c>## Commands</c> line, up to the next <c># </c> or <c>## </c>
    /// heading outside fenced code: each row's cells, trimmed, with the header and separator rows left out. <c>null</c> if the
    /// text has no <c>## Commands</c> line.
    /// </summary>
    private static List<string[]>? CommandsTableRows(string readme)
    {
        string[] lines = readme.ReplaceLineEndings("\n").Split('\n');
        int start = Array.IndexOf(lines, "## Commands");
        if (start < 0)
        {
            return null;
        }

        List<string[]> rows = [];
        bool header = true;
        bool inFence = false;
        for (int i = start + 1; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }

            if (inFence)
            {
                // Fenced code is never a heading or a table row.
                continue;
            }

            if (line.StartsWith("# ", StringComparison.Ordinal) || line.StartsWith("## ", StringComparison.Ordinal))
            {
                break;
            }

            if (!line.StartsWith('|'))
            {
                if (!header)
                {
                    // The first table has ended.
                    break;
                }

                continue;
            }

            string[] cells = line.Trim().Trim('|').Split('|').Select(cell => cell.Trim()).ToArray();
            if (header)
            {
                header = false;
                continue;
            }

            if (cells.All(cell => cell.Length > 0 && cell.All(c => c == '-' || c == ':')))
            {
                continue;
            }

            rows.Add(cells);
        }

        return rows;
    }

    /// <summary>
    /// What is wrong with the <c>## Commands</c> table of <paramref name="readme"/> for <paramref name="commands"/>,
    /// or <c>null</c> if every command has a row whose Command cell is its name and whose Alias cell is its
    /// alias, both in backticks and compared ordinal.
    /// </summary>
    private static string? CommandsTableProblem(string readme, IEnumerable<Command> commands)
    {
        List<string[]>? rows = CommandsTableRows(readme);
        if (rows is null)
        {
            return "README has no '## Commands' section.";
        }

        List<string> missing = commands
            .Where(command => !rows.Any(row => row.Length >= 2
                && string.Equals(row[0], "`" + command.Name + "`", StringComparison.Ordinal)
                && string.Equals(row[1], "`" + command.Alias + "`", StringComparison.Ordinal)))
            .Select(command => command.Name + " (" + command.Alias + ")")
            .ToList();
        return missing.Count == 0 ? null : "README '## Commands' is missing: " + string.Join(", ", missing);
    }

    private const string CommandsTableHeader =
        "| Command | Alias | Arguments | What it does |\n|---------|-------|-----------|--------------|\n";

    private static string CommandsTableRow(Command command) =>
        "| `" + command.Name + "` | `" + command.Alias + "` | none | " + command.Description + " |\n";

    // AC-3, AC-6, AC-7 (077)
    [Fact]
    public void Readme_CommandsTableListsEveryRegisteredCommand()
    {
        List<Command> commands = RegisteredCommands();
        string readme = ReadRepoReadme();

        Assert.Contains(commands, command => command.Name == "help");
        Assert.Contains(commands, command => command.Name == "quit");
        Assert.Contains(commands, command => command.Name == "summary" && command.Alias == "s");
        string? problem = CommandsTableProblem(readme, commands);
        Assert.True(problem is null, problem);

        List<string[]>? rows = CommandsTableRows(readme);
        Assert.NotNull(rows);
        Assert.NotEmpty(rows);
        Assert.All(rows, row =>
        {
            Assert.Equal(4, row.Length);
            Assert.False(string.IsNullOrWhiteSpace(row[2]), "empty Arguments cell in row " + row[0]);
            Assert.False(string.IsNullOrWhiteSpace(row[3]), "empty 'What it does' cell in row " + row[0]);
        });
    }

    // AC-5 (a) (077)
    [Fact]
    public void CommandsTable_FullTable_GivesEveryRow()
    {
        List<Command> commands = RegisteredCommands();
        string readme = "# Times\n\n## Commands\n\nAliases.\n\n" + CommandsTableHeader
            + string.Concat(commands.Select(CommandsTableRow)) + "\n## Keys\n";

        List<string[]>? rows = CommandsTableRows(readme);

        Assert.NotNull(rows);
        Assert.Equal(commands.Select(c => "`" + c.Name + "`"), rows.Select(row => row[0]));
        Assert.Null(CommandsTableProblem(readme, commands));
    }

    // AC-4, AC-5 (b) (077)
    [Fact]
    public void CommandsTable_WithoutSummaryRow_ReportsSummary()
    {
        List<Command> commands = RegisteredCommands();
        string readme = "## Commands\n\n" + CommandsTableHeader
            + string.Concat(commands.Where(c => c.Name != "summary").Select(CommandsTableRow));

        Assert.Equal("README '## Commands' is missing: summary (s)", CommandsTableProblem(readme, commands));
    }

    // AC-4 (077)
    [Fact]
    public void CommandsTable_ListsEveryMissingCommandAsNameAndAlias()
    {
        List<Command> commands = RegisteredCommands();
        string readme = "## Commands\n\n" + CommandsTableHeader
            + string.Concat(commands.Where(c => c.Name is not ("summary" or "arrive" or "quit")).Select(CommandsTableRow));

        string? problem = CommandsTableProblem(readme, commands);

        Assert.NotNull(problem);
        Assert.Contains("arrive (a)", problem, StringComparison.Ordinal);
        Assert.Contains("summary (s)", problem, StringComparison.Ordinal);
        Assert.Contains("quit (q)", problem, StringComparison.Ordinal);
    }

    // AC-3 (077): an alias on the wrong row, or in the wrong case, does not count.
    [Fact]
    public void CommandsTable_WrongOrSwappedAlias_IsMissing()
    {
        List<Command> commands = RegisteredCommands();
        string rows = string.Concat(commands.Where(c => c.Name is not ("summary" or "today" or "help")).Select(CommandsTableRow));
        string readme = "## Commands\n\n" + CommandsTableHeader + rows
            + "| `summary` | `t` | none | Summary |\n| `today` | `s` | none | Today |\n| `Help` | `H` | none | Help |\n";

        Assert.Equal("README '## Commands' is missing: help (h), summary (s), today (t)", CommandsTableProblem(readme, commands));
    }

    // AC-5 (c) (077)
    [Fact]
    public void CommandsTable_PairInAnotherSection_IsNotCounted()
    {
        List<Command> commands = RegisteredCommands();
        string readme = "## Commands\n\n" + CommandsTableHeader
            + string.Concat(commands.Where(c => c.Name != "summary").Select(CommandsTableRow))
            + "\n## Keys\n\n| Keys | What they do |\n|------|--------------|\n| `summary` | `s` |\n"
            + "\n# Building Times\n\n| `summary` | `s` | none | Summary |\n";

        Assert.Equal("README '## Commands' is missing: summary (s)", CommandsTableProblem(readme, commands));
        Assert.DoesNotContain(CommandsTableRows(readme)!, row => row[0] == "`summary`" || row[0] == "Keys");
    }

    // AC-5 (077, nit): a "# " line inside fenced code does not end the section.
    [Fact]
    public void CommandsTable_FencedHeadingLine_DoesNotEndSection()
    {
        List<Command> commands = RegisteredCommands();
        string readme = "## Commands\n\n```sh\n# a shell comment\n```\n\n" + CommandsTableHeader
            + string.Concat(commands.Select(CommandsTableRow));

        Assert.Null(CommandsTableProblem(readme, commands));
    }

    // AC-5 (d) (077)
    [Fact]
    public void CommandsTable_WithoutCommandsSection_Fails()
    {
        List<Command> commands = RegisteredCommands();
        string readme = "# Times\n\n## Shortcuts\n\n" + CommandsTableHeader + string.Concat(commands.Select(CommandsTableRow));

        Assert.Null(CommandsTableRows(readme));
        Assert.Equal("README has no '## Commands' section.", CommandsTableProblem(readme, commands));
    }

    // Booked checkpoints (job 078) --------------------------------------------------------

    private const string BookDayUnbooked =
        "{\"entries\":[{\"kind\":\"arrival\",\"time\":\"08:00\"},"
        + "{\"kind\":\"checkpoint\",\"time\":\"09:00\",\"name\":\"Review\",\"workItem\":1234},"
        + "{\"kind\":\"checkpoint\",\"time\":\"10:00\",\"name\":\"Fix\"},"
        + "{\"kind\":\"leaving\",\"time\":\"12:00\"}]}";

    private const string BookDayBooked =
        "{\"entries\":[{\"kind\":\"arrival\",\"time\":\"08:00\"},"
        + "{\"kind\":\"checkpoint\",\"time\":\"09:00\",\"name\":\"Review\",\"workItem\":1234,\"booked\":true},"
        + "{\"kind\":\"checkpoint\",\"time\":\"10:00\",\"name\":\"Fix\"},"
        + "{\"kind\":\"leaving\",\"time\":\"12:00\"}]}";

    // AC-1 (078)
    [Theory]
    [InlineData("booked 2")]
    [InlineData("bk 2")]
    [InlineData("/booked 2")]
    [InlineData("BK 2")]
    public void Booked_Checkpoint_SavesBookedTrue_OtherEntriesUnchanged(string typed)
    {
        File.WriteAllText(DayFile("2026-10-27"), BookDayUnbooked);

        (int exitCode, string output) = RunCommands(typed, "quit");

        Assert.Equal(0, exitCode);
        Assert.Equal(BookDayBooked, ReadDay("2026-10-27"));
        Assert.DoesNotContain("Unknown command", output);
        Assert.Matches(@"2 +CP +09:00 +Review #1234 ✓", LastFrame(output));
        Assert.DoesNotMatch(@"Fix ✓", LastFrame(output));
    }

    // AC-1 (078)
    [Fact]
    public void Booked_AfterBookingInTheSameSession_UsesTheShownNumbers()
    {
        (int exitCode, _) = RunCommands("arrive 8:00", "cp 9:00 Review", "booked 2", "quit");

        Assert.Equal(0, exitCode);
        Assert.Equal(
            [new DayEntry(EntryKind.Arrival, new TimeOnly(8, 0)), new DayEntry(EntryKind.Checkpoint, new TimeOnly(9, 0), "Review", null, true)],
            LoadDay().Entries);
    }

    // AC-2 (078)
    [Theory]
    [InlineData("unbooked 2")]
    [InlineData("ub 2")]
    [InlineData("/unbooked 2")]
    public void Unbooked_BookedCheckpoint_RemovesTheProperty(string typed)
    {
        File.WriteAllText(DayFile("2026-10-27"), BookDayBooked);

        (int exitCode, string output) = RunCommands(typed, "quit");

        Assert.Equal(0, exitCode);
        Assert.Equal(BookDayUnbooked, ReadDay("2026-10-27"));
        Assert.DoesNotContain("\"booked\"", ReadDay("2026-10-27"));
        Assert.DoesNotContain("✓", LastFrame(output));
    }

    // AC-2 (078)
    [Fact]
    public void BookedThenUnbooked_FileEqualsWhatItWasBefore()
    {
        RunCommands("arrive 8:00", "cp 9:00 Review", "leave 12:00", "quit");
        byte[] before = File.ReadAllBytes(DayFile("2026-10-27"));

        RunCommands("bk 2", "quit");
        Assert.NotEqual(before, File.ReadAllBytes(DayFile("2026-10-27")));

        RunCommands("ub 2", "quit");
        Assert.Equal(before, File.ReadAllBytes(DayFile("2026-10-27")));
    }

    // AC-3 (078)
    [Theory]
    [InlineData(BookDayBooked, "booked 2")]
    [InlineData(BookDayBooked, "bk 2")]
    [InlineData(BookDayUnbooked, "unbooked 2")]
    [InlineData(BookDayUnbooked, "ub 3")]
    public void Repeat_LeavesFileBytesAndWriteTimeUnchanged_AndShowsNoHint(string contents, string typed)
    {
        string path = DayFile("2026-10-27");
        File.WriteAllText(path, contents);
        DateTime stamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, stamp);
        byte[] before = File.ReadAllBytes(path);

        (int exitCode, string output) = RunCommands(typed, "quit");

        Assert.Equal(0, exitCode);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));
        Assert.Equal(string.Empty, LastLayout(output)[^4].Trim());
    }

    // AC-4 (078)
    [Theory]
    [InlineData("booked", "booked: say which checkpoint to mark.", "Use booked <number>.")]
    [InlineData("bk x", "booked: x is not an entry number.", "Use booked <number>.")]
    [InlineData("booked -1", "booked: -1 is not an entry number.", "Use booked <number>.")]
    [InlineData("booked +2", "booked: +2 is not an entry number.", "Use booked <number>.")]
    [InlineData("booked 2 3", "booked: 2 3 is not an entry number.", "Use booked <number>.")]
    [InlineData("booked [red]x[/]", "booked: [red]x[/] is not an entry number.", "Use booked <number>.")]
    [InlineData("booked 0", "booked: there is no entry 0.", "")]
    [InlineData("booked 5", "booked: there is no entry 5.", "")]
    [InlineData("booked 1", "booked: entry 1 is not a checkpoint.", "")]
    [InlineData("booked 4", "booked: entry 4 is not a checkpoint.", "")]
    [InlineData("unbooked", "unbooked: say which checkpoint to mark.", "Use unbooked <number>.")]
    [InlineData("ub x", "unbooked: x is not an entry number.", "Use unbooked <number>.")]
    [InlineData("unbooked +2", "unbooked: +2 is not an entry number.", "Use unbooked <number>.")]
    [InlineData("unbooked 5", "unbooked: there is no entry 5.", "")]
    [InlineData("ub 1", "unbooked: entry 1 is not a checkpoint.", "")]
    public void BadArgument_ShowsHintAboveInputBox_AndWritesNothing(string typed, string expected, string usage)
    {
        string path = DayFile("2026-10-27");
        File.WriteAllText(path, BookDayBooked);
        DateTime stamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, stamp);
        byte[] before = File.ReadAllBytes(path);

        (int exitCode, string output) = RunCommands("help", typed, "quit");

        Assert.Equal(0, exitCode);
        string[] frame = LastLayout(output);
        AssertOnHintRow(frame, expected);
        if (usage.Length > 0)
        {
            AssertOnHintRow(frame, usage);
        }

        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));
        Assert.DoesNotContain("failed:", output);
    }

    // AC-4 (078)
    [Theory]
    [InlineData("booked 2")]
    [InlineData("bk x")]
    [InlineData("unbooked")]
    [InlineData("ub 1")]
    public void BookedHints_AreRed(string typed)
    {
        File.WriteAllText(DayFile("2026-10-27"), BookDayUnbooked);
        TestConsole console = new TestConsole().Interactive().EmitAnsiSequences();
        console.Input.PushTextWithEnter("booked 9");
        console.Input.PushTextWithEnter(typed);
        console.Input.PushTextWithEnter("quit");

        Assert.Equal(0, new App(console, FixedTimeProvider.At20261027(), new DayStore(data.Path)).Run([]));
        Assert.Matches(@"\x1b\[(31|91|38;5;9)m(un)?booked: ", console.Output);
    }

    // AC-4 (078)
    [Theory]
    [InlineData("booked 1", "booked: there is no entry 1.")]
    [InlineData("unbooked 1", "unbooked: there is no entry 1.")]
    public void Booked_OnDayWithoutFile_ShowsHint_AndCreatesNothing(string typed, string expected)
    {
        using TempFolder missing = new TempFolder(create: false);

        (int exitCode, string output) = Run(c =>
        {
            c.Input.PushTextWithEnter(typed);
            c.Input.PushTextWithEnter("quit");
        }, dataFolder: missing.Path);

        Assert.Equal(0, exitCode);
        AssertOnHintRow(LastLayout(output), expected);
        AssertLeftNothing(missing.Path);

        (exitCode, output) = RunCommands(typed, "quit");

        Assert.Equal(0, exitCode);
        AssertOnHintRow(LastLayout(output), expected);
        Assert.False(File.Exists(DayFile("2026-10-27")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(data.Path));
    }

    // AC-5 (078)
    [Theory]
    [InlineData("booked 1", "booked")]
    [InlineData("ub 1", "unbooked")]
    public void Booked_UnreadableFile_ShowsHint_AndLeavesFileUnchanged(string typed, string name)
    {
        File.WriteAllText(DayFile("2026-10-27"), "{not json");
        byte[] before = File.ReadAllBytes(DayFile("2026-10-27"));

        (int exitCode, string output) = RunCommands(typed, "quit");

        Assert.Equal(0, exitCode);
        Assert.Equal(before, File.ReadAllBytes(DayFile("2026-10-27")));
        AssertOnHintRow(LastLayout(output), $"{name}: The file for 2026-10-27 is not a valid day file. Nothing was changed.");
        Assert.DoesNotContain("   at ", output);
        Assert.DoesNotContain("failed:", output);
    }

    // AC-5 (078)
    [Theory]
    [InlineData("booked 2", "booked")]
    [InlineData("unbooked 2", "unbooked")]
    public void Booked_SaveFailure_ShowsRedHint_NotStackTrace(string typed, string name)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.WriteAllText(DayFile("2026-10-27"), typed.StartsWith("un", StringComparison.Ordinal) ? BookDayBooked : BookDayUnbooked);
        byte[] before = File.ReadAllBytes(DayFile("2026-10-27"));
        UnixFileMode mode = File.GetUnixFileMode(data.Path);
        File.SetUnixFileMode(data.Path, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            TestConsole console = new TestConsole().Interactive().EmitAnsiSequences();
            console.Profile.Width = 200;
            console.Input.PushTextWithEnter(typed);
            console.Input.PushTextWithEnter("quit");

            Assert.Equal(0, new App(console, FixedTimeProvider.At20261027(), new DayStore(data.Path), instanceLock: SideLock()).Run([]));
            Assert.Matches(@"\x1b\[(31|91|38;5;9)m" + name + @": could not save the day\.", console.Output);
            Assert.DoesNotContain("   at ", console.Output);
            Assert.DoesNotContain("failed:", console.Output);
        }
        finally
        {
            File.SetUnixFileMode(data.Path, mode);
        }

        Assert.Equal(before, File.ReadAllBytes(DayFile("2026-10-27")));
    }

    // AC-10 (078)
    [Fact]
    public void Help_ListsBookedAndUnbooked_WithAliasesAndDescriptions()
    {
        string plain = RunHelp(data.Path, folderFromEnvironment: false);

        Assert.Matches(@"booked +│ bk +│ Mark a checkpoint on this day as booked", plain);
        Assert.Matches(@"unbooked +│ ub +│ Mark a checkpoint on this day as not yet booked", plain);
        Assert.Contains("CP checkpoint · ✓ booked", plain);

        // The legend's check mark is grey, as in the day view.
        Assert.Contains("\u001b[38;5;8m✓\u001b[0m booked", RunHelp(data.Path, folderFromEnvironment: false, ansi: true));
    }

    // AC-10 (078)
    [Theory]
    [InlineData("bk", "booked ", "Mark a checkpoint on this day as booked")]
    [InlineData("ub", "unbooked ", "Mark a checkpoint on this day as not yet booked")]
    public void Popup_MatchesBookedAliases(string typed, string name, string description)
    {
        string output = RunAnsi(c =>
        {
            c.Input.PushText(typed);
            c.Input.PushKey(ConsoleKey.Escape);
            c.Input.PushKey(ConsoleKey.Escape);
            c.Input.PushTextWithEnter("quit");
        });

        Assert.Contains(TypingWrites(output, 0), w => w.Text.StartsWith(name, StringComparison.Ordinal) && w.Text.Contains(description, StringComparison.Ordinal));
    }

    // AC-10 (078)
    [Fact]
    public void Booked_LeavesTimeLeftBreaksAndSummaryUnchanged()
    {
        const string Day =
            "{\"entries\":[{\"kind\":\"arrival\",\"time\":\"08:00\"},"
            + "{\"kind\":\"checkpoint\",\"time\":\"09:00\",\"name\":\"Review\",\"workItem\":1234},"
            + "{\"kind\":\"breakStart\",\"time\":\"12:00\"},{\"kind\":\"breakEnd\",\"time\":\"12:10\"},"
            + "{\"kind\":\"leaving\",\"time\":\"17:00\"}]}";
        File.WriteAllText(DayFile("2026-10-27"), Day);

        (_, string before) = RunWide(120, "summary", "quit");
        (_, string after) = RunWide(120, "bk 2", "summary", "quit");

        // The check mark widens the details column, so compare with runs of spaces collapsed.
        static string Strip(string frame) => Regex.Replace(frame.Replace(" ✓", string.Empty, StringComparison.Ordinal), " +", " ");
        Assert.Contains("✓", string.Join('\n', LastLayout(after)));
        Assert.Equal(Strip(string.Join('\n', LastLayout(before))), Strip(string.Join('\n', LastLayout(after))));
        Assert.Contains(LastLayout(after), l => l.Contains("#1234", StringComparison.Ordinal) && l.Contains("Review", StringComparison.Ordinal) && l.Contains('│'));
    }

    // Settings panel (job 079) -------------------------------------------------------------

    private string SettingsPath => Path.Combine(data.Path, "settings.json");

    /// <summary>One setting of each kind, plus <paramref name="extra"/> on/off settings to overflow a small screen.</summary>
    private static SettingsRegistry TestSettings(int extra = 0)
    {
        SettingsRegistry registry = SettingsStoreTests.Registry();
        for (int i = 1; i <= extra; i++)
        {
            registry.RegisterOnOff($"extra-{i}", $"Extra setting {i}", false);
        }

        return registry;
    }

    private (int ExitCode, string Output) RunSettings(Action<TestConsole> input, SettingsRegistry? settings = null, int height = 24, string? folder = null)
    {
        TestConsole console = new TestConsole().Interactive().EmitAnsiSequences();
        console.Profile.Height = height;
        input(console);
        App app = new App(console, FixedTimeProvider.At20261027(), new DayStore(folder ?? data.Path), settings: settings ?? TestSettings(),
            instanceLock: sideLock);
        int exitCode = app.Run([]);
        return (exitCode, console.Output);
    }

    /// <summary>
    /// Every whole screen written, in order: a full frame (one write at 1;1 holding every row) or an
    /// in-place redraw (a run of writes at column 1 on rows 1..height, one per row).
    /// </summary>
    private static List<string[]> Screens(string output, int height = 24)
    {
        List<AnsiWrite> writes = AnsiWrites.Parse(output);
        List<string[]> screens = [];
        for (int i = 0; i < writes.Count; i++)
        {
            AnsiWrite write = writes[i];
            if (write.Row != 1 || write.Column != 1)
            {
                continue;
            }

            if (write.Text.Contains('\n'))
            {
                string[] rows = write.Text.Split('\n');
                if (rows.Length >= height)
                {
                    screens.Add(rows[..height]);
                }

                continue;
            }

            if (i + height - 1 < writes.Count
                && Enumerable.Range(1, height - 1).All(k => writes[i + k].Row == k + 1 && writes[i + k].Column == 1))
            {
                screens.Add(Enumerable.Range(0, height).Select(k => writes[i + k].Text).ToArray());
                i += height - 1;
            }
        }

        return screens;
    }

    /// <summary>The screens that show the settings panel (its header, or a scroll cue above it).</summary>
    private static List<string[]> PanelScreens(string output, int height = 24) =>
        Screens(output, height).Where(s => s.Any(r => r.Contains("Settings─", StringComparison.Ordinal) || r.Contains("↑/↓ move", StringComparison.Ordinal) || r.Contains("─Settings", StringComparison.Ordinal) || r.Contains("Extra setting", StringComparison.Ordinal))).ToList();

    /// <summary>The name on the row marked with ›, or null.</summary>
    private static string? Selected(string[] screen)
    {
        string? row = screen.FirstOrDefault(r => r.StartsWith("│ › ", StringComparison.Ordinal));
        return row?[4..].Split(' ')[0];
    }

    /// <summary>The value shown for setting <paramref name="name"/> on <paramref name="screen"/>.</summary>
    private static string ValueOf(string[] screen, string name)
    {
        string row = screen.First(r => Regex.IsMatch(r, $@"^│ [› ] {Regex.Escape(name)} "));
        return row[4..].Split(' ', StringSplitOptions.RemoveEmptyEntries)[1];
    }

    // AC-2
    [Theory]
    [InlineData("settings")]
    [InlineData("se")]
    [InlineData("/settings")]
    [InlineData("SE")]
    public void Settings_AliasAndSlash_OpenThePanel(string typed)
    {
        (int exitCode, string output) = RunSettings(c =>
        {
            c.Input.PushTextWithEnter(typed);
            c.Input.PushKey(ConsoleKey.Escape);
            c.Input.PushTextWithEnter("quit");
        });

        Assert.Equal(0, exitCode);
        Assert.Single(PanelScreens(output));
        Assert.DoesNotContain("Unknown command", output);
    }

    // AC-2
    [Fact]
    public void Settings_WithArgument_ShowsTheTakesNoArgumentsHint_AndNoPanel()
    {
        (_, string output) = RunSettings(c =>
        {
            c.Input.PushTextWithEnter("settings x");
            c.Input.PushTextWithEnter("quit");
        });

        Assert.Empty(PanelScreens(output));
        string[] frame = AnsiFrames(output)[1];
        Assert.Contains("settings takes no arguments.", frame[^4]);
        Assert.Contains("\u001b[38;5;9m", output);
    }

    // AC-2
    [Fact]
    public void HelpAndPopup_ListSettings_WithItsAlias()
    {
        (_, string help) = RunSettings(c =>
        {
            c.Input.PushTextWithEnter("help");
            c.Input.PushTextWithEnter("quit");
        }, height: 60);
        string popup = RunAnsi(c =>
        {
            c.Input.PushText("sett");
            c.Input.PushKey(ConsoleKey.Escape);
            c.Input.PushKey(ConsoleKey.Escape);
            c.Input.PushTextWithEnter("quit");
        });

        Assert.Matches(@"│ settings +│ se +│ Change your settings", Regex.Replace(help, @"\x1b\[[0-9;?]*[A-Za-z]", string.Empty));
        Assert.Contains(TypingWrites(popup, 0), w => Regex.IsMatch(w.Text, @"^settings +se +Change your settings"));
    }

    // AC-3
    [Fact]
    public void Panel_ReplacesQuicklistAndDayView_WithRowsAndKeyLine()
    {
        (_, string output) = RunSettings(c =>
        {
            c.Input.PushTextWithEnter("settings");
            c.Input.PushKey(ConsoleKey.Escape);
            c.Input.PushTextWithEnter("quit");
        });

        string[] screen = Assert.Single(PanelScreens(output));
        Assert.StartsWith("╭─Settings─", screen[0]);
        Assert.Matches(@"^│ › round +off +Round times", screen[1]);
        Assert.Matches(@"^│   target +480 +Daily target", screen[2]);
        Assert.Matches(@"^│   theme +dark +Theme", screen[3]);
        Assert.Contains("↑/↓ move · Enter/Space change · Esc close", screen[4]);
        Assert.StartsWith("╰", screen[5]);
        Assert.DoesNotContain(screen, r => r.Contains("Projects", StringComparison.Ordinal) || r.Contains(Heading, StringComparison.Ordinal));
        Assert.StartsWith("│ > ", screen[^2]);
    }

    // AC-4, AC-9
    [Fact]
    public void EmptyRegistry_HasNoSettings_OnlyEscDoesAnything_AndNothingIsWritten()
    {
        TestConsole console = new TestConsole().Interactive().EmitAnsiSequences();
        console.Input.PushTextWithEnter("settings");
        console.Input.PushKey(ConsoleKey.DownArrow);
        console.Input.PushKey(ConsoleKey.UpArrow);
        console.Input.PushKey(ConsoleKey.Enter);
        console.Input.PushKey(ConsoleKey.Spacebar);
        console.Input.PushText("x");
        console.Input.PushKey(ConsoleKey.Escape);
        console.Input.PushTextWithEnter("quit");

        Assert.Equal(0, new App(console, FixedTimeProvider.At20261027(), new DayStore(data.Path), settings: new SettingsRegistry()).Run([]));

        string[] screen = Assert.Single(PanelScreens(console.Output));
        Assert.Contains(screen, r => r.Contains("No settings exist yet.", StringComparison.Ordinal));
        Assert.Contains(screen, r => r.Contains("Esc close", StringComparison.Ordinal));
        Assert.Empty(Directory.EnumerateFileSystemEntries(data.Path));
        Assert.DoesNotContain("Unknown command", console.Output);
    }

    // AC-5
    [Fact]
    public void Arrows_MoveTheSelectionByOne_AndStopAtTheEnds()
    {
        (_, string output) = RunSettings(c =>
        {
            c.Input.PushTextWithEnter("settings");
            c.Input.PushKey(ConsoleKey.UpArrow);
            for (int i = 0; i < 3; i++)
            {
                c.Input.PushKey(ConsoleKey.DownArrow);
            }

            for (int i = 0; i < 3; i++)
            {
                c.Input.PushKey(ConsoleKey.UpArrow);
            }

            c.Input.PushKey(ConsoleKey.Escape);
            c.Input.PushTextWithEnter("quit");
        });

        // Keys at an end change nothing and redraw nothing.
        Assert.Equal(["round", "target", "theme", "target", "round"], PanelScreens(output).Select(Selected));
        Assert.False(File.Exists(SettingsPath));
    }

    // AC-5
    [Fact]
    public void OtherKeys_ScrollKeysAndMouseSequences_ChangeNothing_AndDoNotClose()
    {
        (int exitCode, string output) = RunSettings(c =>
        {
            c.Input.PushTextWithEnter("settings");
            c.Input.PushKey(ConsoleKey.PageDown);
            c.Input.PushKey(ConsoleKey.PageUp);
            c.Input.PushKey(new ConsoleKeyInfo('\0', ConsoleKey.DownArrow, false, false, true));
            c.Input.PushKey(ConsoleKey.Tab);
            c.Input.PushKey(ConsoleKey.Backspace);
            c.Input.PushText("xq");
            Wheel(c, 1, 2);
            Wheel(c, -1);
            // A click, and a paste whose space and line break must not change the setting.
            c.Input.PushKey(new ConsoleKeyInfo('\u001b', ConsoleKey.Escape, false, false, false));
            c.Input.PushText("[<0;10;5M");
            c.Input.PushKey(new ConsoleKeyInfo('\u001b', ConsoleKey.Escape, false, false, false));
            c.Input.PushText("[200~ a ");
            c.Input.PushKey(ConsoleKey.Enter);
            c.Input.PushKey(new ConsoleKeyInfo('\u001b', ConsoleKey.Escape, false, false, false));
            c.Input.PushText("[201~");
            c.Input.PushKey(ConsoleKey.Escape);
            c.Input.PushTextWithEnter("quit");
        });

        Assert.Equal(0, exitCode);
        Assert.Single(PanelScreens(output));
        Assert.False(File.Exists(SettingsPath));
        Assert.DoesNotContain("Unknown command", output);
    }

    // AC-5
    [Fact]
    public void Selection_StaysInsideTheVisibleTopArea_OnASmallScreen()
    {
        const int Height = 12;
        (_, string output) = RunSettings(c =>
        {
            c.Input.PushTextWithEnter("settings");
            for (int i = 0; i < 25; i++)
            {
                c.Input.PushKey(ConsoleKey.DownArrow);
            }

            for (int i = 0; i < 25; i++)
            {
                c.Input.PushKey(ConsoleKey.UpArrow);
            }

            c.Input.PushKey(ConsoleKey.Escape);
            c.Input.PushTextWithEnter("quit");
        }, TestSettings(extra: 20), height: Height);

        List<string[]> screens = PanelScreens(output, Height);
        // 23 settings: 1 open + 22 down + 22 up.
        Assert.Equal(45, screens.Count);
        List<string?> selected = screens.Select(s => Selected(s[..(Height - 4)])).ToList();
        Assert.All(selected, Assert.NotNull);
        Assert.Equal("extra-20", selected[22]);
        Assert.Equal("round", selected[^1]);
        Assert.Contains("more lines above", screens[22][0]);
        // The first setting is the panel's second line: only the header hides behind the cue.
        Assert.StartsWith("↑ 1 more line above", screens[^1][0]);
    }

    // AC-6
    [Fact]
    public void EnterOrSpace_FlipsOnOff_SavesAtOnce_AndShowsIt()
    {
        (_, string output) = RunSettings(c =>
        {
            c.Input.PushTextWithEnter("settings");
            c.Input.PushKey(ConsoleKey.Enter);
            c.Input.PushKey(ConsoleKey.Escape);
            c.Input.PushTextWithEnter("settings");
            c.Input.PushKey(ConsoleKey.Spacebar);
            c.Input.PushKey(ConsoleKey.Escape);
            c.Input.PushTextWithEnter("quit");
        });

        List<string[]> screens = PanelScreens(output);
        Assert.Equal(["off", "on", "on", "off"], screens.Select(s => ValueOf(s, "round")));
        Assert.Equal("{\"settings\":{\"round\":false}}", File.ReadAllText(SettingsPath));
    }

    // AC-6
    [Fact]
    public void EnterOnChoice_MovesToTheNextOption_Wrapping_AndSavesEachStep()
    {
        (_, string output) = RunSettings(c =>
        {
            c.Input.PushTextWithEnter("settings");
            c.Input.PushKey(ConsoleKey.DownArrow);
            c.Input.PushKey(ConsoleKey.DownArrow);
            c.Input.PushKey(ConsoleKey.Enter);
            c.Input.PushKey(ConsoleKey.Spacebar);
            c.Input.PushKey(ConsoleKey.Escape);
            c.Input.PushTextWithEnter("settings");
            c.Input.PushKey(ConsoleKey.DownArrow);
            c.Input.PushKey(ConsoleKey.DownArrow);
            c.Input.PushKey(ConsoleKey.Enter);
            c.Input.PushKey(ConsoleKey.Escape);
            c.Input.PushTextWithEnter("quit");
        });

        Assert.Equal(
            ["dark", "dark", "dark", "light", "[red]x[/]", "[red]x[/]", "[red]x[/]", "[red]x[/]", "dark"],
            PanelScreens(output).Select(s => ValueOf(s, "theme")));
        Assert.Equal("{\"settings\":{\"theme\":\"dark\"}}", File.ReadAllText(SettingsPath));
    }

    // AC-7
    [Theory]
    [InlineData("600", 600)]
    [InlineData("  42 ", 42)]
    [InlineData("-10", -10)]
    [InlineData("0", 0)]
    [InlineData("720", 720)]
    public void EnterOnNumber_ReadsAValueInTheInputBox_AndSavesIt(string typed, int expected)
    {
        (_, string output) = RunSettings(c =>
        {
            c.Input.PushTextWithEnter("settings");
            c.Input.PushKey(ConsoleKey.DownArrow);
            c.Input.PushKey(ConsoleKey.Enter);
            c.Input.PushTextWithEnter(typed);
            c.Input.PushKey(ConsoleKey.Escape);
            c.Input.PushTextWithEnter("quit");
        });

        List<string[]> screens = PanelScreens(output);
        Assert.Equal(expected.ToString(System.Globalization.CultureInfo.InvariantCulture), ValueOf(screens[^1], "target"));
        Assert.Contains("target: a whole number from -10 to 720 · Esc cancels", screens[2][^2]);
        Assert.Equal($"{{\"settings\":{{\"target\":{expected.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}}}", File.ReadAllText(SettingsPath));
        Assert.DoesNotContain(screens[^1], r => r.Contains("whole number", StringComparison.Ordinal));
    }

    // AC-7
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("1.5")]
    [InlineData("+3")]
    [InlineData("1,000")]
    [InlineData("- 3")]
    [InlineData("721")]
    [InlineData("-11")]
    [InlineData("99999999999")]
    public void EnterOnNumber_InvalidValue_ShowsRedHint_SavesNothing_AndKeepsThePanelOpen(string typed)
    {
        (_, string output) = RunSettings(c =>
        {
            c.Input.PushTextWithEnter("settings");
            c.Input.PushKey(ConsoleKey.DownArrow);
            c.Input.PushKey(ConsoleKey.Enter);
            c.Input.PushTextWithEnter(typed);
            c.Input.PushKey(ConsoleKey.DownArrow);
            c.Input.PushKey(ConsoleKey.Escape);
            c.Input.PushTextWithEnter("quit");
        });

        List<string[]> screens = PanelScreens(output);
        string[] afterEdit = screens[^2];
        Assert.Contains("settings: target takes a whole number from -10 to 720.", afterEdit[^4]);
        Assert.Equal("480", ValueOf(afterEdit, "target"));
        Assert.Equal("theme", Selected(screens[^1]));
        Assert.False(File.Exists(SettingsPath));
        Assert.Contains("\u001b[38;5;9msettings: ", output);
        // No command popup while the value is typed (abc would list addproject and arrive).
        Assert.DoesNotContain(AnsiWrites.Parse(output), w => w.Text.StartsWith("addproject", StringComparison.Ordinal));
    }

    // AC-7
    [Fact]
    public void NumberEdit_EscCancels_WithoutAHint_AndIsNotAddedToTheHistory()
    {
        (_, string output) = RunSettings(c =>
        {
            c.Input.PushTextWithEnter("settings");
            c.Input.PushKey(ConsoleKey.DownArrow);
            c.Input.PushKey(ConsoleKey.Enter);
            c.Input.PushText("12");
            c.Input.PushKey(ConsoleKey.Escape);
            c.Input.PushKey(ConsoleKey.Enter);
            c.Input.PushTextWithEnter("600");
            c.Input.PushKey(ConsoleKey.Escape);
            // History: Up gives "settings" (not "600"), which opens the panel again.
            c.Input.PushKey(ConsoleKey.UpArrow);
            c.Input.PushKey(ConsoleKey.Enter);
            c.Input.PushKey(ConsoleKey.Escape);
            c.Input.PushTextWithEnter("quit");
        });

        List<string[]> screens = PanelScreens(output);
        string[] cancelled = screens[3];
        Assert.Equal("480", ValueOf(cancelled, "target"));
        Assert.DoesNotContain("whole number", cancelled[^4]);
        Assert.Equal("{\"settings\":{\"target\":600}}", File.ReadAllText(SettingsPath));
        Assert.Equal("round", Selected(screens[^1]));
        Assert.Equal("600", ValueOf(screens[^1], "target"));
        Assert.DoesNotContain("Unknown command", output);
    }

    // AC-8
    [Fact]
    public void Esc_ClosesThePanel_WithAFullRedraw_Unscrolled_AndTheHintCleared()
    {
        const int Height = 12;
        (_, string output) = RunSettings(c =>
        {
            c.Input.PushTextWithEnter("settings");
            for (int i = 0; i < 22; i++)
            {
                c.Input.PushKey(ConsoleKey.DownArrow);
            }

            c.Input.PushKey(ConsoleKey.Escape);
            c.Input.PushTextWithEnter("quit");
        }, TestSettings(extra: 20), height: Height);

        List<string[]> screens = Screens(output, Height);
        string[] last = screens[^1];
        Assert.StartsWith("╭─Projects─", last[0]);
        Assert.Contains(Heading, last[EmptyPanelRows]);
        Assert.Equal(string.Empty, last[^4].Trim());
        Assert.Contains("Type help for commands", last[^2]);
        // A full frame (one write of every row at 1;1), not an in-place redraw.
        AnsiWrite lastFull = AnsiWrites.Parse(output).Last(w => w.Row == 1 && w.Column == 1 && w.Text.Contains('\n'));
        Assert.StartsWith("╭─Projects─", lastFull.Text);
    }

    // AC-8
    [Fact]
    public void Esc_ClearsAHintShownInThePanel()
    {
        (_, string output) = RunSettings(c =>
        {
            c.Input.PushTextWithEnter("settings");
            c.Input.PushKey(ConsoleKey.DownArrow);
            c.Input.PushKey(ConsoleKey.Enter);
            c.Input.PushTextWithEnter("abc");
            c.Input.PushKey(ConsoleKey.Escape);
            c.Input.PushTextWithEnter("quit");
        });

        string[] last = Screens(output)[^1];
        Assert.Contains(Heading, last[EmptyPanelRows]);
        Assert.DoesNotContain("whole number", last[^4]);
    }

    // AC-8
    [Theory]
    [InlineData("settings\r")]
    [InlineData("settings\r\r12")]
    [InlineData("settings\r\u001b[<65;1")]
    [InlineData("settings\r\u001b[200~ a")]
    public void InputEnding_InThePanel_OrInANumberEdit_ReturnsZero(string keys)
    {
        SettingsRegistry registry = new SettingsRegistry();
        registry.RegisterNumber("target", "Daily target", 480, 0, 720);
        TestConsole test = new TestConsole().Interactive().EmitAnsiSequences();
        IAnsiConsole console = new InputOverrideConsole(test, new EndingInput(keys));

        int exitCode = new App(console, FixedTimeProvider.At20261027(), new DayStore(data.Path), settings: registry).Run([]);

        Assert.Equal(0, exitCode);
        Assert.Contains("Settings", test.Output);
        Assert.False(File.Exists(SettingsPath));
    }

    // AC-9
    [Fact]
    public void OpeningAndClosing_WithoutAChange_WritesNothing_NotEvenTheFolder()
    {
        // Outside the data folder: start-up creates a missing folder for times.lock (117), and the data
        // folder's listing is checked below.
        using TempFolder missingFolder = new TempFolder(create: false);
        string missing = missingFolder.Path;
        RunSettings(c =>
        {
            c.Input.PushTextWithEnter("settings");
            c.Input.PushKey(ConsoleKey.DownArrow);
            c.Input.PushKey(ConsoleKey.Escape);
            c.Input.PushTextWithEnter("quit");
        }, folder: missing);

        AssertLeftNothing(missing);

        File.WriteAllText(SettingsPath, "{ \"settings\" : { \"round\" : true } }");
        DateTime written = File.GetLastWriteTimeUtc(SettingsPath);
        (_, string output) = RunSettings(c =>
        {
            c.Input.PushTextWithEnter("settings");
            c.Input.PushKey(ConsoleKey.Escape);
            c.Input.PushTextWithEnter("quit");
        });

        Assert.Equal("on", ValueOf(Assert.Single(PanelScreens(output)), "round"));
        Assert.Equal("{ \"settings\" : { \"round\" : true } }", File.ReadAllText(SettingsPath));
        Assert.Equal(written, File.GetLastWriteTimeUtc(SettingsPath));
        Assert.Equal([SettingsPath], Directory.EnumerateFileSystemEntries(data.Path));
    }

    // AC-10
    [Theory]
    [InlineData("{not json", "settings.json is not valid JSON.")]
    [InlineData("{\"settings\":{\"round\":1}}", "settings.json has a wrong value for \"round\".")]
    [InlineData("{\"settings\":{\"target\":721}}", "settings.json has a wrong value for \"target\".")]
    [InlineData("{\"settings\":{\"theme\":\"blue\"}}", "settings.json has a wrong value for \"theme\".")]
    [InlineData("{\"settings\":{\"a\":1,\"a\":1}}", "settings.json has a property more than once.")]
    public void UnreadableFile_ShowsWhy_ListsDefaults_AndEveryChangeSavesNothing(string contents, string reason)
    {
        File.WriteAllText(SettingsPath, contents);
        byte[] before = File.ReadAllBytes(SettingsPath);

        (_, string output) = RunSettings(c =>
        {
            c.Input.PushTextWithEnter("settings");
            c.Input.PushKey(ConsoleKey.Enter);
            c.Input.PushKey(ConsoleKey.DownArrow);
            c.Input.PushKey(ConsoleKey.Spacebar);
            c.Input.PushKey(ConsoleKey.DownArrow);
            c.Input.PushKey(ConsoleKey.Enter);
            c.Input.PushKey(ConsoleKey.Escape);
            c.Input.PushTextWithEnter("quit");
        });

        List<string[]> screens = PanelScreens(output);
        string[] first = screens[0];
        Assert.Contains($"The settings cannot be read: {reason}", first[1]);
        Assert.Contains("Fix or delete settings.json in the data folder (help shows where).", first[2]);
        Assert.Equal("off", ValueOf(first, "round"));
        Assert.Equal("480", ValueOf(first, "target"));
        Assert.Equal("dark", ValueOf(first, "theme"));
        Assert.Contains("\u001b[38;5;9mThe settings cannot be read:", output);
        // Enter on round, Space on target (no edit opens), Enter on theme: each shows the hint.
        foreach (string[] changed in new[] { screens[1], screens[3], screens[5] })
        {
            Assert.Contains("settings: the settings cannot be read (see above). Nothing was saved.", changed[^4]);
            Assert.Equal("off", ValueOf(changed, "round"));
            Assert.Equal("dark", ValueOf(changed, "theme"));
        }

        Assert.DoesNotContain("whole number", output);
        Assert.Equal(before, File.ReadAllBytes(SettingsPath));
    }

    // AC-10
    [Fact]
    public void FifoSettingsFile_IsRefused_WithoutBlocking()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        SpecialFiles.MakeFifo(SettingsPath);
        string output = string.Empty;
        Exception? thrown = SpecialFiles.RunWithin(TimeSpan.FromSeconds(10), () => (_, output) = RunSettings(c =>
        {
            c.Input.PushTextWithEnter("settings");
            c.Input.PushKey(ConsoleKey.Enter);
            c.Input.PushKey(ConsoleKey.Escape);
            c.Input.PushTextWithEnter("quit");
        }));

        Assert.Null(thrown);
        Assert.Contains("The settings cannot be read: settings.json is not a regular file.", output);
        Assert.Contains("Nothing was saved.", output);
        Assert.True(SpecialFiles.IsFifo(SettingsPath));
    }

    // AC-11
    [Fact]
    public void UnknownNamesInTheFile_AreKeptOnSave()
    {
        File.WriteAllText(SettingsPath, "{\"settings\":{\"later\":{\"x\":[1,2]},\"round\":false}}");

        RunSettings(c =>
        {
            c.Input.PushTextWithEnter("settings");
            c.Input.PushKey(ConsoleKey.Enter);
            c.Input.PushKey(ConsoleKey.Escape);
            c.Input.PushTextWithEnter("quit");
        });

        Assert.Equal("{\"settings\":{\"later\":{\"x\":[1,2]},\"round\":true}}", File.ReadAllText(SettingsPath));
    }

    // AC-11
    [Fact]
    public void SaveFailure_ShowsTheHint_AndThePreviousValue()
    {
        UseSideLock();
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.WriteAllText(SettingsPath, "{\"settings\":{\"round\":false}}");
        byte[] before = File.ReadAllBytes(SettingsPath);
        File.SetUnixFileMode(data.Path, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        string output;
        try
        {
            (_, output) = RunSettings(c =>
            {
                c.Input.PushTextWithEnter("settings");
                c.Input.PushKey(ConsoleKey.Enter);
                c.Input.PushKey(ConsoleKey.Escape);
                c.Input.PushTextWithEnter("quit");
            });
        }
        finally
        {
            File.SetUnixFileMode(data.Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        string[] changed = PanelScreens(output)[1];
        Assert.Contains("settings: could not save the settings.", HintText(changed, "settings: could not"));
        Assert.Equal("off", ValueOf(changed, "round"));
        Assert.Equal(before, File.ReadAllBytes(SettingsPath));
    }

    // AC-7
    [Theory]
    [InlineData("0", 0)]
    [InlineData(" -12\t", -12)]
    [InlineData("2147483647", int.MaxValue)]
    [InlineData("-2147483648", int.MinValue)]
    public void TryParseWhole_AcceptsInvariantWholeNumbers(string text, int expected)
    {
        Assert.True(App.TryParseWhole(text, out int number));
        Assert.Equal(expected, number);
    }

    // AC-7
    [Theory]
    [InlineData("")]
    [InlineData("-")]
    [InlineData("+1")]
    [InlineData("1e3")]
    [InlineData("1 000")]
    [InlineData("\u0663")]
    [InlineData("\uff11")]
    [InlineData("2147483648")]
    [InlineData("--1")]
    public void TryParseWhole_RejectsEverythingElse(string text)
    {
        Assert.False(App.TryParseWhole(text, out _));
    }

    // Job 082 ------------------------------------------------------------------------------

    private const string OpenFootnoteText = "open: this checkpoint has no end on this day and counts no time.";

    /// <summary>The first non-empty line below the summary caption, trimmed, or <c>null</c> if there is none.</summary>
    private static string? LineBelowSummaryCaption(string[] frame)
    {
        int bottom = Array.FindIndex(frame, l => l.TrimStart().StartsWith('╰') && l.Contains('┴', StringComparison.Ordinal));
        Assert.True(bottom > 0, "no summary table bottom border in the frame");
        int caption = Array.FindIndex(frame, bottom + 1, l => l.Trim().Length > 0);
        Assert.True(caption > bottom, "no summary caption in the frame");
        return frame.Skip(caption + 1).FirstOrDefault(l => l.Trim().Length > 0)?.Trim();
    }

    // AC-1, AC-7
    [Fact]
    public void OpenFootnote_OnAPastDay_IsTheLineBelowTheCaption()
    {
        File.WriteAllText(DayFile("2026-10-26"), SummaryDay);

        (int exitCode, string output) = RunCommands("prev", "summary", "quit");

        Assert.Equal(0, exitCode);
        string[] frame = LastLayout(output);
        AssertLayout(frame);
        Assert.Equal("Monday, 2026-10-26", SummaryCaption(frame));
        Assert.Equal(OpenFootnoteText, LineBelowSummaryCaption(frame));
        Assert.Equal(OpenFootnoteText, App.OpenFootnote);
    }

    // AC-1, AC-7
    [Fact]
    public void OpenFootnote_OnAFutureDay_OnlyCheckpointOpen_IsTheLineBelowTheCaption()
    {
        File.WriteAllText(DayFile("2026-10-28"), "{\"entries\":[{\"kind\":\"checkpoint\",\"time\":\"09:00\",\"name\":\"Plan\"}]}");

        (_, string output) = RunCommands("next", "summary", "quit");

        string[] frame = LastLayout(output);
        AssertLayout(frame);
        Assert.Contains(frame, l => Regex.IsMatch(l, @"│ +│ Plan +│ 0:00 open +│"));
        Assert.Equal("Wednesday, 2026-10-28", SummaryCaption(frame));
        Assert.Equal(OpenFootnoteText, LineBelowSummaryCaption(frame));
    }

    // AC-2
    [Fact]
    public void OpenFootnote_IsGrey_WithNoOtherStyle()
    {
        File.WriteAllText(DayFile("2026-10-26"), SummaryDay);

        string output = RunAnsiCommands("prev", "summary");

        Assert.Matches(@"\x1b\[(90|38;5;8)m" + Regex.Escape(OpenFootnoteText) + @"\x1b\[0m", output);
        // Nothing but the grey code opens the footnote.
        Assert.DoesNotMatch(@"\x1b\[[0-9;]*;(90|38;5;8)m" + Regex.Escape(OpenFootnoteText), output);
        Assert.DoesNotMatch(@"\x1b\[(90|38;5;8);[0-9;]*m" + Regex.Escape(OpenFootnoteText), output);
    }

    // AC-3
    [Fact]
    public void OpenFootnote_ShowsOnce_ForTwoOpenGroups()
    {
        // Calculate leaves at most the day's last checkpoint without an end, so two open groups are
        // built directly and rendered the way Summary does.
        List<CheckpointGroup> groups =
        [
            new CheckpointGroup(1234, ["Review"], TimeSpan.Zero, open: true),
            new CheckpointGroup(null, ["Plan"], TimeSpan.Zero, open: true),
        ];
        TestConsole console = new TestConsole();
        console.Profile.Width = 80;

        console.Write(App.SummaryView(new DateOnly(2026, 10, 26), groups));

        string[] lines = console.Output.Split('\n');
        Assert.Single(lines, l => l.Contains("counts no time", StringComparison.Ordinal));
        Assert.Equal(2, lines.Count(l => l.Contains(" open ", StringComparison.Ordinal)));
        Assert.Equal(OpenFootnoteText, LineBelowSummaryCaption(lines));
    }

    // AC-3
    [Fact]
    public void OpenFootnote_ShowsOnce_InTheLoop_WithAWorkItemAndANameGroup()
    {
        File.WriteAllText(DayFile("2026-10-26"),
            "{\"entries\":[{\"kind\":\"checkpoint\",\"time\":\"09:00\",\"name\":\"Plan\"}," +
            "{\"kind\":\"checkpoint\",\"time\":\"10:00\",\"name\":\"Review\",\"workItem\":1234}]}");

        (_, string output) = RunCommands("prev", "summary", "quit");

        string[] frame = LastLayout(output);
        Assert.Single(frame, l => l.Contains("counts no time", StringComparison.Ordinal));
    }

    // AC-4
    [Fact]
    public void OpenFootnote_NotShown_OnToday()
    {
        File.WriteAllText(DayFile("2026-10-27"), SummaryDay);

        (_, string output) = RunCommands("summary", "quit");

        string[] frame = LastLayout(output);
        Assert.Contains(frame, l => l.Contains("Work item", StringComparison.Ordinal));
        Assert.DoesNotContain(frame, l => l.Contains("counts no time", StringComparison.Ordinal));
    }

    // AC-4
    [Theory]
    [InlineData("leaving")]
    [InlineData("breakStart")]
    [InlineData("checkpoint")]
    public void OpenFootnote_NotShown_OnAPastDay_FullyClosed(string ending)
    {
        string end = ending == "checkpoint"
            ? "{\"kind\":\"checkpoint\",\"time\":\"12:00\",\"name\":\"Fix\"},{\"kind\":\"leaving\",\"time\":\"13:00\"}"
            : "{\"kind\":\"" + ending + "\",\"time\":\"12:00\"}";
        File.WriteAllText(DayFile("2026-10-26"),
            "{\"entries\":[{\"kind\":\"checkpoint\",\"time\":\"09:00\",\"name\":\"Plan\"}," +
            "{\"kind\":\"checkpoint\",\"time\":\"10:00\",\"name\":\"Review\",\"workItem\":1234}," + end + "]}");

        (_, string output) = RunCommands("prev", "summary", "quit");

        string[] frame = LastLayout(output);
        AssertLayout(frame);
        Assert.Contains(frame, l => l.Contains("Work item", StringComparison.Ordinal));
        Assert.DoesNotContain(frame, l => l.Contains("open", StringComparison.Ordinal));
        Assert.DoesNotContain(frame, l => l.Contains("counts no time", StringComparison.Ordinal));
    }

    // AC-5
    [Fact]
    public void OpenFootnote_NotShown_ForNoteAndHints()
    {
        // No file on the previous day.
        (_, string none) = RunCommands("prev", "summary", "quit");
        Assert.Contains("No checkpoints on this day.", LastFrame(none));
        Assert.DoesNotContain("counts no time", none);

        // A past day without checkpoints.
        File.WriteAllText(DayFile("2026-10-25"), "{\"entries\":[{\"kind\":\"arrival\",\"time\":\"08:00\"}]}");
        (_, string empty) = RunCommands("prev", "prev", "summary", "quit");
        Assert.Contains("No checkpoints on this day.", LastFrame(empty));
        Assert.DoesNotContain("counts no time", empty);

        // An invalid past day file.
        File.WriteAllText(DayFile("2026-10-26"), "{not json");
        (_, string invalid) = RunCommands("prev", "summary", "quit");
        AssertOnHintRow(LastLayout(invalid), "summary: The file for 2026-10-26");
        Assert.DoesNotContain("counts no time", invalid);

        // An argument on a past day with an open checkpoint.
        File.WriteAllText(DayFile("2026-10-26"), SummaryDay);
        (_, string argument) = RunCommands("prev", "summary x", "quit");
        AssertOnHintRow(LastLayout(argument), "summary takes no arguments.");
        Assert.DoesNotContain("counts no time", argument);
    }

    // AC-5
    [Fact]
    public void OpenFootnote_NotShown_ForUnreadableDayFile()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.WriteAllText(DayFile("2026-10-26"), SummaryDay);
        UnixFileMode mode = File.GetUnixFileMode(DayFile("2026-10-26"));
        File.SetUnixFileMode(DayFile("2026-10-26"), UnixFileMode.None);
        try
        {
            if (CanRead(DayFile("2026-10-26")))
            {
                // Running as root: access can't be denied.
                return;
            }

            (_, string output) = RunCommands("prev", "summary", "quit");

            Assert.Contains("summary: ", LastLayout(output)[^4]);
            Assert.DoesNotContain("counts no time", output);
        }
        finally
        {
            File.SetUnixFileMode(DayFile("2026-10-26"), mode);
        }
    }

    // AC-6
    [Fact]
    public void OpenFootnote_SummaryStaysReadOnly()
    {
        File.WriteAllText(DayFile("2026-10-26"), SummaryDay);
        byte[] before = File.ReadAllBytes(DayFile("2026-10-26"));

        (int exitCode, string output) = RunCommands("prev", "summary", "summary", "quit");

        Assert.Equal(0, exitCode);
        Assert.Contains(LastLayout(output), l => l.Contains("counts no time", StringComparison.Ordinal));
        Assert.Equal(before, File.ReadAllBytes(DayFile("2026-10-26")));
        Assert.Equal(["2026-10-26.json"], Directory.EnumerateFileSystemEntries(data.Path).Select(Path.GetFileName));
    }

    // AC-7
    [Fact]
    public void OpenFootnote_OnAShortTerminal_ScrollsLikeTheRest()
    {
        File.WriteAllText(DayFile("2026-10-26"), SummaryDay);

        TestConsole console = new TestConsole().Interactive();
        console.Profile.Height = 12;
        console.Input.PushTextWithEnter("prev");
        console.Input.PushTextWithEnter("summary");
        console.Input.PushKey(ConsoleKey.PageDown);
        console.Input.PushKey(ConsoleKey.PageDown);
        console.Input.PushKey(ConsoleKey.PageDown);
        console.Input.PushTextWithEnter("quit");
        Assert.Equal(0, new App(console, FixedTimeProvider.At20261027(), new DayStore(data.Path)).Run([]));
        string output = console.Output;

        List<string[]> frames = Frames(output);
        string[] frame = frames[^1];
        AssertLayout(frame, 12);
        Assert.Matches(@"^↓ \d+ more lines? below · PgDn / Ctrl\+↓$", frame[^5].TrimEnd());
        Assert.DoesNotContain(frame, l => l.Contains("counts no time", StringComparison.Ordinal));
        // Paging down draws the footnote in place, like any other summary line.
        int scrolled = output.IndexOf(OpenFootnoteText, StringComparison.Ordinal);
        Assert.True(scrolled > output.LastIndexOf(frame[^1], StringComparison.Ordinal), "the footnote is not drawn after paging down");
    }

    // AC-11 (083)
    [Fact]
    public void Break_NotEnded_ShowsHintInFrame()
    {
        (int exitCode, string output) = RunCommands("arrive 8:00", "break 12:00", "leave 17:00", "quit");

        Assert.Equal(0, exitCode);
        string[] frame = LastLayout(output);
        AssertLayout(frame);
        Assert.Contains(frame, l => l.Contains("! Break not ended: entry 2", StringComparison.Ordinal));
        Assert.DoesNotContain(frame, l => l.Contains("Break outside", StringComparison.Ordinal));
    }

    // Safer file loading (job 086) ------------------------------------------------------------

    // AC-3 (086)
    [Theory]
    [MemberData(nameof(DayRecordTests.LoneSurrogateDayFiles), MemberType = typeof(DayRecordTests))]
    public void LoneSurrogateDayFile_ShowsUnreadableDay_AndArriveBooksNothing(string contents)
    {
        File.WriteAllText(DayFile("2026-10-27"), contents);
        byte[] before = File.ReadAllBytes(DayFile("2026-10-27"));

        (int exitCode, string output) = RunCommands("arrive 9:00", "quit");

        Assert.Equal(0, exitCode);
        Assert.Equal(before, File.ReadAllBytes(DayFile("2026-10-27")));
        string[] first = Frames(output)[0];
        Assert.Contains(first[..^4], l => l.Contains("The file for 2026-10-27 is not a valid day file.", StringComparison.Ordinal));
        string[] frame = LastLayout(output);
        AssertOnHintRow(frame, "is not a valid day file. Nothing was booked.");
        Assert.Contains("is not a valid day file.", string.Join('\n', frame[..^4]));
        Assert.DoesNotContain("   at ", output);
    }

    // AC-3 (086): the day view's unreadable line is red.
    [Fact]
    public void LoneSurrogateDayFile_UnreadableLineIsRed()
    {
        File.WriteAllText(DayFile("2026-10-27"), "{\"entries\":[{\"kind\":\"\\ud800\",\"time\":\"08:00\"}]}");

        Assert.Matches(@"\x1b\[(31|91|38;5;9)mThe file for 2026-10-27 is not a valid day file\.", RunAnsi("quit"));
    }

    // AC-4 (086)
    [Theory]
    [MemberData(nameof(ProjectListTests.LoneSurrogateProjectFiles), MemberType = typeof(ProjectListTests))]
    public void LoneSurrogateProjectsFile_ShowsReasonInPanel_AndCommandsChangeNothing(string contents)
    {
        File.WriteAllText(ProjectsFile, contents);
        byte[] before = File.ReadAllBytes(ProjectsFile);

        (int panelExit, string panelOutput) = RunWide(200, "quit");
        (int listExit, string listed) = RunWide(200, "projects", "quit");
        (int addExit, string added) = RunWide(200, "addproject 1 x", "quit");
        (int bookExit, string booked) = RunWide(200, "checkpoint #1 x", "quit");

        Assert.Equal([0, 0, 0, 0], new[] { panelExit, listExit, addExit, bookExit });
        string[] panel = PanelRows(LastLayout(panelOutput));
        Assert.Contains("The project list cannot be read: projects.json is not valid.", panel[1]);
        AssertOnHintRow(LastLayout(listed), "projects: the project list cannot be read: projects.json is not valid. Fix or delete projects.json in the data folder (help shows where).");
        AssertOnHintRow(LastLayout(added), "addproject: the project list cannot be read: projects.json is not valid. Nothing was added.");
        AssertOnHintRow(LastLayout(booked), "checkpoint: the project list cannot be read: projects.json is not valid. Nothing was booked.");
        Assert.DoesNotContain("   at ", panelOutput + listed + added + booked);
        Assert.Equal(before, File.ReadAllBytes(ProjectsFile));
        Assert.False(File.Exists(DayFile("2026-10-27")));
    }

    // AC-4 (086): the hints are red.
    [Fact]
    public void LoneSurrogateProjectsFile_HintsAreRed()
    {
        File.WriteAllText(ProjectsFile, "{\"projects\":[],\"\\udc00\":1}");

        Assert.Matches(@"\x1b\[(31|91|38;5;9)mprojects: the project list cannot be read: projects\.json is not valid\.", RunAnsi("projects"));
        Assert.Matches(@"\x1b\[(31|91|38;5;9)maddproject: the project list cannot be read: projects\.json is not valid\. Nothing was added\.", RunAnsi("addproject 1 x"));
        Assert.Matches(@"\x1b\[(31|91|38;5;9)mcheckpoint: the project list cannot be read: projects\.json is not valid\. Nothing was booked\.", RunAnsi("checkpoint #1 x"));
    }

    // AC-6 (086)
    [Fact]
    public void FifoDayFile_QuitFinishes_ShowsNotRegular_AndArriveBooksNothing()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        SpecialFiles.MakeFifo(DayFile("2026-10-27"));
        string quitOutput = string.Empty;
        string arriveOutput = string.Empty;
        string ansi = string.Empty;
        int quitExit = -1;
        int arriveExit = -1;

        Assert.Null(SpecialFiles.RunWithin(TimeSpan.FromSeconds(5), () => (quitExit, quitOutput) = RunCommands("quit")));
        Assert.Null(SpecialFiles.RunWithin(TimeSpan.FromSeconds(5), () => (arriveExit, arriveOutput) = RunCommands("arrive 9:00", "quit")));
        Assert.Null(SpecialFiles.RunWithin(TimeSpan.FromSeconds(5), () => ansi = RunAnsi("quit")));

        Assert.Equal(0, quitExit);
        Assert.Contains(LastLayout(quitOutput)[..^4], l => l.Contains("The file for 2026-10-27 is not a regular file.", StringComparison.Ordinal));
        Assert.Matches(@"\x1b\[(31|91|38;5;9)mThe file for 2026-10-27 is not a regular file\.", ansi);
        Assert.Equal(0, arriveExit);
        AssertOnHintRow(LastLayout(arriveOutput), "is not a regular file. Nothing was booked.");
        Assert.True(SpecialFiles.IsFifo(DayFile("2026-10-27")));
        Assert.Equal([DayFile("2026-10-27")], Directory.EnumerateFileSystemEntries(data.Path));
    }

    // Job 088: paste size cap ---------------------------------------------------------------

    private const string PasteCutText = "Paste cut to 4096 characters; the rest was dropped.";

    private static void PushLongPaste(TestConsole console, char c)
    {
        console.Input.PushKey(new ConsoleKeyInfo('\u001b', ConsoleKey.Escape, false, false, false));
        console.Input.PushText("[200~" + new string(c, LineEditor.MaxPasteLength + 1000));
        console.Input.PushKey(new ConsoleKeyInfo('\u001b', ConsoleKey.Escape, false, false, false));
        console.Input.PushText("[201~");
    }

    // The hint is written on the hint row (height - 3) in red, after the paste and before the next full redraw.
    private static void AssertPasteCutHintInPlace(string output, int height = 24)
    {
        int hint = output.IndexOf(PasteCutText, StringComparison.Ordinal);
        Assert.True(hint > 0, "no paste-cut hint");
        Assert.Matches(@"\x1b\[(31|91|38;5;9)m" + Regex.Escape(PasteCutText), output);
        Assert.True(hint > output.IndexOf(ClearScreen, StringComparison.Ordinal), "hint after the first full frame");
        Assert.Contains(AnsiWrites.Parse(output), w => w.Row == height - 3 && w.Column == 1 && w.Text.Contains(PasteCutText, StringComparison.Ordinal));
    }

    // AC-10
    [Fact]
    public void Paste_OverTheCap_AtThePrompt_ShowsTheRedHintInPlace_AndRunsNothing()
    {
        List<string> calls = [];
        CommandRegistry registry = CommandRegistry.CreateDefault();
        registry.Register("rec", "rc", "Records its argument", args => { calls.Add(args); return CommandResult.None; });

        (int exitCode, string output) = RunWithMouse(c =>
        {
            PushLongPaste(c, 'a');
            c.Input.PushKey(ConsoleKey.Escape);
            c.Input.PushKey(ConsoleKey.Escape);
            c.Input.PushTextWithEnter("quit");
        }, registry: registry);

        Assert.Equal(0, exitCode);
        AssertPasteCutHintInPlace(output);
        // Only the first frame and the clear on quit: the hint came without a full redraw.
        Assert.Equal(2, Count(output, ClearScreen));
        Assert.True(output.IndexOf(PasteCutText, StringComparison.Ordinal) < output.LastIndexOf(ClearScreen, StringComparison.Ordinal));
        Assert.Empty(calls);
        Assert.False(File.Exists(DayFile("2026-10-27")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(data.Path));
    }

    // AC-10
    [Fact]
    public void Paste_OverTheCap_InTheNumberEditor_ShowsTheRedHint_AndSavesNothing()
    {
        (int exitCode, string output) = RunSettings(c =>
        {
            c.Input.PushTextWithEnter("settings");
            c.Input.PushKey(ConsoleKey.DownArrow);
            c.Input.PushKey(ConsoleKey.Enter);
            PushLongPaste(c, '1');
            c.Input.PushKey(ConsoleKey.Escape);
            c.Input.PushKey(ConsoleKey.Escape);
            c.Input.PushTextWithEnter("quit");
        });

        Assert.Equal(0, exitCode);
        AssertPasteCutHintInPlace(output);
        Assert.False(File.Exists(SettingsPath));
    }

    // Runs the shipped app (the default settings registry) on a plain interactive console.
    private (int ExitCode, string Output) RunShipped(params string[] commands)
    {
        TestConsole console = new TestConsole().Interactive();
        console.Profile.Width = 120;
        foreach (string command in commands)
        {
            console.Input.PushTextWithEnter(command);
        }

        int exitCode = new App(console, FixedTimeProvider.At20261027(), new DayStore(data.Path)).Run([]);
        return (exitCode, console.Output);
    }

    private void WriteDayFile(string date, string arrival, string leaving) =>
        File.WriteAllText(
            DayFile(date),
            $"{{\"entries\":[{{\"kind\":\"arrival\",\"time\":\"{arrival}\"}},{{\"kind\":\"leaving\",\"time\":\"{leaving}\"}}]}}");

    // AC-1 (091)
    [Fact]
    public void ShippedApp_ListsDailyTarget_AndChangingItSavesSettingsJson()
    {
        TestConsole console = new TestConsole().Interactive().EmitAnsiSequences();
        console.Input.PushTextWithEnter("settings");
        console.Input.PushKey(ConsoleKey.Enter);
        console.Input.PushTextWithEnter("450");
        console.Input.PushKey(ConsoleKey.Escape);
        console.Input.PushTextWithEnter("quit");

        Assert.Equal(0, new App(console, FixedTimeProvider.At20261027(), new DayStore(data.Path)).Run([]));

        List<string[]> screens = PanelScreens(console.Output);
        Assert.Equal("480", ValueOf(screens[0], "daily-target"));
        Assert.Equal("450", ValueOf(screens[^1], "daily-target"));
        Assert.Equal("{\"settings\":{\"daily-target\":450}}", File.ReadAllText(SettingsPath));
    }

    // AC-2 (091)
    [Fact]
    public void NoSettingsFile_TimeLeftUsesEightHours()
    {
        (int exitCode, string output) = RunShipped("arrive 8:00", "leave 12:00", "quit");

        Assert.Equal(0, exitCode);
        string frame = LastFrame(output);
        Assert.Matches(@"1 +AR +08:00 +left 8:00", frame);
        Assert.Matches(@"2 +LE +12:00 +left 4:00", frame);
        Assert.False(File.Exists(SettingsPath));
    }

    // AC-2 (091)
    [Fact]
    public void DailyTarget450_LeavingRowShowsLeft330()
    {
        File.WriteAllText(SettingsPath, "{\"settings\":{\"daily-target\":450}}");

        (_, string output) = RunShipped("arrive 8:00", "leave 12:00", "quit");

        string frame = LastFrame(output);
        Assert.Matches(@"1 +AR +08:00 +left 7:30", frame);
        Assert.Matches(@"2 +LE +12:00 +left 3:30", frame);
        Assert.Contains("Target 7:30 · day -3:30 · balance -3:30", frame);
    }

    // AC-3 (091)
    [Fact]
    public void BookedDay_ShowsTargetLineBelowBreaks()
    {
        WriteDayFile("2026-10-20", "08:00", "17:15");

        (_, string output) = RunShipped("arrive 8:00", "leave 15:30", "quit");

        string[] lines = LastFrame(output).Split('\n').Select(l => l.Trim('│', ' ')).ToArray();
        int breaks = Array.FindIndex(lines, l => l.StartsWith("Breaks ", StringComparison.Ordinal));
        Assert.True(breaks >= 0);
        Assert.Equal("Target 8:00 · day -0:30 · balance +0:45", lines[breaks + 1]);
    }

    // AC-5, AC-6 (091)
    [Fact]
    public void Balance_SumsEarlierDays_IgnoresLaterOnes_AndCountsTheShownDayOnceBooked()
    {
        WriteDayFile("2026-10-20", "08:00", "17:00");
        WriteDayFile("2026-10-21", "08:00", "15:30");
        WriteDayFile("2026-10-30", "06:00", "20:00");

        (_, string output) = RunShipped("arrive 8:00", "leave 16:30", "quit");

        List<string[]> frames = Frames(output);
        string first = string.Join("\n", frames[0]);
        Assert.Contains("No entries yet.", first);
        Assert.Contains("Target 8:00 · balance +0:30", first);
        Assert.DoesNotContain("· day", first);
        Assert.Contains("Target 8:00 · day +0:30 · balance +1:00", LastFrame(output));
    }

    // AC-6 (091)
    [Fact]
    public void EmptyDay_WithNoCountedDay_ShowsNoTargetLine()
    {
        (_, string output) = RunShipped("quit");

        string frame = LastFrame(output);
        Assert.Contains("No entries yet.", frame);
        Assert.DoesNotContain("Target", frame);
    }

    // AC-7 (091)
    [Fact]
    public void UnreadableDayFile_IsSkipped_Counted_AndLeftAsItIs()
    {
        WriteDayFile("2026-10-20", "08:00", "17:00");
        File.WriteAllText(DayFile("2026-10-21"), "not json");
        File.WriteAllText(Path.Combine(data.Path, "2026-13-01.json"), "not json");

        (int exitCode, string output) = RunShipped("next", "prev", "quit");

        Assert.Equal(0, exitCode);
        Assert.Contains("Target 8:00 · balance +1:00  ! 1 day file skipped", LastFrame(output));
        Assert.Equal("not json", File.ReadAllText(DayFile("2026-10-21")));
        Assert.Equal("not json", File.ReadAllText(Path.Combine(data.Path, "2026-13-01.json")));
    }

    // AC-7 (091)
    [Fact]
    public void TwoUnreadableDayFiles_ArePlural()
    {
        WriteDayFile("2026-10-20", "08:00", "17:00");
        File.WriteAllText(DayFile("2026-10-21"), "not json");
        Directory.CreateDirectory(DayFile("2026-10-22"));

        (_, string output) = RunShipped("quit");

        Assert.Contains("! 2 day files skipped", LastFrame(output));
    }

    // AC-8 (091)
    [Fact]
    public void MissingDataFolder_ShowsNoBalance_AndCreatesNothing()
    {
        string missing = Path.Combine(data.Path, "missing");
        TestConsole console = new TestConsole().Interactive();
        console.Input.PushTextWithEnter("quit");

        Assert.Equal(0, new App(console, FixedTimeProvider.At20261027(), new DayStore(missing)).Run([]));

        Assert.DoesNotContain("Target", LastFrame(console.Output));
        AssertLeftNothing(missing);
    }

    // AC-9 (091)
    [Theory]
    [InlineData("not json")]
    [InlineData("{\"settings\":{\"daily-target\":1441}}")]
    [InlineData("{\"settings\":{\"daily-target\":-1}}")]
    [InlineData("{\"settings\":{\"daily-target\":\"450\"}}")]
    [InlineData("{\"settings\":{\"daily-target\":450,\"daily-target\":450}}")]
    public void BrokenSettings_DayViewUsesEightHours_AndLeavesTheFile(string contents)
    {
        File.WriteAllText(SettingsPath, contents);

        (int exitCode, string output) = RunShipped("arrive 8:00", "leave 12:00", "quit");

        Assert.Equal(0, exitCode);
        string frame = LastFrame(output);
        Assert.Matches(@"2 +LE +12:00 +left 4:00", frame);
        Assert.Contains("Target 8:00 · day -4:00 · balance -4:00", frame);
        Assert.Equal(contents, File.ReadAllText(SettingsPath));
    }

    // AC-10 (091)
    [Fact]
    public void ChangingDailyTarget_RedrawsTheDayViewWithTheNewTarget()
    {
        WriteDayFile("2026-10-20", "08:00", "16:00");
        TestConsole console = new TestConsole().Interactive();
        console.Profile.Width = 120;
        console.Input.PushTextWithEnter("arrive 8:00");
        console.Input.PushTextWithEnter("leave 12:00");
        console.Input.PushTextWithEnter("settings");
        console.Input.PushKey(ConsoleKey.Enter);
        console.Input.PushTextWithEnter("450");
        console.Input.PushKey(ConsoleKey.Escape);
        console.Input.PushTextWithEnter("quit");

        Assert.Equal(0, new App(console, FixedTimeProvider.At20261027(), new DayStore(data.Path)).Run([]));

        string output = console.Output;
        int closed = output.LastIndexOf("daily-target", StringComparison.Ordinal);
        string after = output[closed..];
        Assert.Matches(@"2 +LE +12:00 +left 3:30", after);
        Assert.Contains("Target 7:30 · day -3:30 · balance -3:00", after);
        string before = output[..output.IndexOf("daily-target", StringComparison.Ordinal)];
        Assert.Contains("Target 8:00 · day -4:00 · balance -4:00", before);
    }

    // ---- 093 auto update ----

    private sealed class UpdateRig : IDisposable
    {
        public UpdateRig(AppTests owner, string? release = "1.2.0", bool installed = true, UpdateSignature? signature = null,
            Func<Func<Task<UpdateOutcome>>, Task<UpdateOutcome>>? background = null)
        {
            Owner = owner;
            Source = new FakeReleaseSource([]);
            if (release is not null)
            {
                Source.Releases.Add(Signer.SignedRelease(Source, release));
            }

            Install = installed ? new ToolInstall(global: true, Path.Combine(Stage.Path, "tools")) : null;
            Applier = new UpdateApplier(Launcher, windows: false);
            Service = new UpdateService(Source, signature ?? Signer.Signature, Install, Applier, "1.0.0",
                () => Directory.CreateDirectory(StageFolder), background ?? (work => work()));
        }

        public AppTests Owner { get; }

        public TempFolder Stage { get; } = new();

        public TestSigner Signer { get; } = new();

        public FakeReleaseSource Source { get; }

        public FakeLauncher Launcher { get; } = new();

        public ToolInstall? Install { get; }

        public UpdateApplier Applier { get; }

        public UpdateService Service { get; }

        public string StageFolder => Path.Combine(Stage.Path, "times-update-x");

        public (int ExitCode, string Output) Run(params string[] commands)
        {
            TestConsole console = new TestConsole().Interactive();
            console.Profile.Width = 120;
            foreach (string command in commands)
            {
                console.Input.PushTextWithEnter(command);
            }

            int exitCode = new App(console, FixedTimeProvider.At20261027(), new DayStore(Owner.data.Path), updates: Service).Run([]);
            return (exitCode, console.Output);
        }

        public void Dispose()
        {
            Signer.Dispose();
            Stage.Dispose();
        }
    }

    private void WriteUpdateMode(string mode, string channel = "stable") =>
        File.WriteAllText(SettingsPath, "{\"settings\":{\"update-mode\":\"" + mode + "\",\"update-channel\":\"" + channel + "\"}}");

    private const string Available = "Times 1.2.0 is available. Type update to install it when you quit.";
    private const string Ready = "Times 1.2.0 is ready and installs when you quit.";
    private const string Installing = "Times 1.2.0 is installing. Start tm again in a few seconds.";

    // AC-6: notify only (the default) shows the steelblue hint at the first redraw; nothing is downloaded or installed.
    [Fact]
    public void Update_NotifyOnly_ShowsAvailableAtStart()
    {
        using UpdateRig rig = new UpdateRig(this);

        (int exitCode, string output) = rig.Run("quit");

        Assert.Equal(0, exitCode);
        Assert.Contains(Available, output);
        Assert.Equal(1, rig.Source.ListCalls);
        Assert.Empty(rig.Source.Downloads);
        Assert.Empty(rig.Launcher.Calls);
        Assert.False(File.Exists(Path.Combine(data.Path, "update-pending.json")));
    }

    // AC-6: the hint shows once, at the first full redraw only.
    [Fact]
    public void Update_StartupHint_ShowsOnce()
    {
        using UpdateRig rig = new UpdateRig(this);

        (_, string output) = rig.Run("", "", "quit");

        Assert.Single(Regex.Matches(output, Regex.Escape(Available)));
    }

    // AC-6: a command hint at that redraw wins; the check's hint waits for the next redraw with none.
    [Fact]
    public void Update_StartupHint_WaitsBehindACommandHint()
    {
        using UpdateRig rig = new UpdateRig(this);
        File.WriteAllText(Path.Combine(data.Path, "update-pending.json"), "{\"version\":\"0.9.0\",\"time\":\"2026-10-27T08:00:00+00:00\"}");

        (_, string output) = rig.Run("bogus", "", "quit");

        int failed = output.IndexOf("Update to 0.9.0 failed; see update.log in the data folder.", StringComparison.Ordinal);
        int unknown = output.IndexOf("Unknown command bogus", StringComparison.Ordinal);
        int available = output.IndexOf(Available, StringComparison.Ordinal);
        Assert.True(failed >= 0);
        Assert.True(unknown > failed);
        // Not in the first frame (the pending outcome) nor the second (the unknown command), but in the third.
        Assert.True(available > unknown);
        Assert.Equal(3, Regex.Matches(output, Regex.Escape(Heading)).Count);
    }

    // AC-6: a check that never finishes never blocks input, redraws or quit.
    [Fact]
    public void Update_HangingCheck_NeverBlocks()
    {
        using UpdateRig rig = new UpdateRig(this, background: null);
        rig.Source.ListTask = new TaskCompletionSource<IReadOnlyList<ReleaseInfo>>().Task;
        UpdateService service = new UpdateService(rig.Source, rig.Signer.Signature, rig.Install, rig.Applier, "1.0.0");
        TestConsole console = new TestConsole().Interactive();
        console.Input.PushTextWithEnter("arrive 8:00");
        console.Input.PushTextWithEnter("quit");

        int exitCode = new App(console, FixedTimeProvider.At20261027(), new DayStore(data.Path), updates: service).Run([]);

        Assert.Equal(0, exitCode);
        Assert.Contains("08:00", console.Output);
        Assert.DoesNotContain("Update", console.Output);
        Assert.Empty(rig.Launcher.Calls);
    }

    // AC-6: offline, rate limit and the like: a grey skip on the hint row, the loop goes on.
    [Fact]
    public void Update_CheckFails_ShowsSkipAndGoesOn()
    {
        using UpdateRig rig = new UpdateRig(this);
        rig.Source.ListFailure = new UpdateException("GitHub's rate limit was reached");

        (int exitCode, string output) = rig.Run("quit");

        Assert.Equal(0, exitCode);
        Assert.Contains("Update check skipped: GitHub's rate limit was reached.", output);
    }

    // AC-6/AC-9: install on quit stages at start; quit writes the pending file, calls the launcher once and prints nothing.
    [Fact]
    public void Update_InstallOnQuit_StagesAndLaunchesOnceAtQuit()
    {
        using UpdateRig rig = new UpdateRig(this);
        WriteUpdateMode("install on quit");

        (int exitCode, string output) = rig.Run("quit");

        Assert.Equal(0, exitCode);
        Assert.Contains(Ready, output);
        Assert.DoesNotContain(Installing, output);
        ProcessCall call = Assert.Single(rig.Launcher.Calls);
        Assert.Equal("nohup", call.FileName);
        Assert.Equal(
            ["Times", "-g", "--configfile", Path.Combine(rig.StageFolder, "nuget.config"), "--add-source", rig.StageFolder, "--version", "1.2.0"],
            call.Arguments.Skip(7));
        Assert.Equal(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), call.Arguments[4]);
        Assert.Equal(Path.Combine(data.Path, "update.log"), call.Arguments[5]);
        Assert.Contains("\"version\":\"1.2.0\"", File.ReadAllText(Path.Combine(data.Path, "update-pending.json")));
    }

    // AC-10: install and restart makes the same single launcher call, and only it prints the quit line.
    [Fact]
    public void Update_InstallAndRestart_SameCallPlusQuitLine()
    {
        List<string> calls = [];
        foreach (string mode in new[] { "install on quit", "install and restart" })
        {
            using UpdateRig rig = new UpdateRig(this);
            WriteUpdateMode(mode);
            File.Delete(Path.Combine(data.Path, "update-pending.json"));

            (int exitCode, string output) = rig.Run("quit");

            Assert.Equal(0, exitCode);
            ProcessCall call = Assert.Single(rig.Launcher.Calls);
            calls.Add(call.FileName + "|" + string.Join("|", call.Arguments).Replace(rig.Stage.Path, "<stage>", StringComparison.Ordinal));
            string afterQuit = output[output.LastIndexOf("Type help", StringComparison.Ordinal)..];
            if (mode == "install and restart")
            {
                Assert.Contains(Installing, afterQuit);
                Assert.Single(Regex.Matches(output, Regex.Escape(Installing)));
            }
            else
            {
                Assert.DoesNotContain(Installing, output);
            }
        }

        Assert.Equal(calls[0], calls[1]);
    }

    // AC-7: update (and up) checks right away, stages in notify only mode and installs at quit.
    [Theory]
    [InlineData("update")]
    [InlineData("up")]
    [InlineData("/up")]
    public void Update_Command_StagesForQuit(string command)
    {
        using UpdateRig rig = new UpdateRig(this);

        (int exitCode, string output) = rig.Run(command, "quit");

        Assert.Equal(0, exitCode);
        Assert.Contains(Ready, output);
        Assert.Equal(2, rig.Source.ListCalls);
        Assert.Single(rig.Launcher.Calls);
    }

    // AC-7: update takes no arguments.
    [Fact]
    public void Update_Command_RefusesArguments()
    {
        using UpdateRig rig = new UpdateRig(this);

        (_, string output) = rig.Run("update now", "quit");

        Assert.Equal(1, rig.Source.ListCalls);
        Assert.Empty(rig.Launcher.Calls);
        Assert.Contains("update", LastFrame(output));
    }

    // AC-7: nothing newer: up to date with the running version, nothing staged.
    [Fact]
    public void Update_Command_UpToDate()
    {
        using UpdateRig rig = new UpdateRig(this, release: null);

        (_, string output) = rig.Run("update", "quit");

        Assert.Contains("Times is up to date (1.0.0).", output);
        Assert.Empty(rig.Launcher.Calls);
    }

    // AC-7: the command uses the settings as they are now (a channel changed after the start).
    [Fact]
    public void Update_Command_UsesTheChannelAsItIsNow()
    {
        using UpdateRig rig = new UpdateRig(this, release: null);
        rig.Source.Releases.Add(rig.Signer.SignedRelease(rig.Source, "1.2.0-beta.1", prerelease: true));

        (_, string output) = rig.Run("update", "quit");
        Assert.Contains("Times is up to date (1.0.0).", output);

        WriteUpdateMode("notify only", "beta");
        (_, output) = rig.Run("update", "quit");
        Assert.Contains("Times 1.2.0-beta.1 is ready and installs when you quit.", output);
        Assert.Equal("--prerelease", Assert.Single(rig.Launcher.Calls).Arguments[^1]);
    }

    // AC-8: a copy that is not an installed tool stages nothing.
    [Fact]
    public void Update_NotInstalled_StagesNothing()
    {
        using UpdateRig rig = new UpdateRig(this, installed: false);

        (_, string output) = rig.Run("update", "quit");

        Assert.Contains("Times is not installed as a dotnet tool; update it by hand.", output);
        Assert.Empty(rig.Source.Downloads);
        Assert.Empty(rig.Launcher.Calls);
    }

    // AC-13: no embedded key: still notifies, but update and the install modes stage nothing.
    [Fact]
    public void Update_NoKey_NotifiesButNeverStages()
    {
        using UpdateRig rig = new UpdateRig(this, signature: UpdateSignature.None);

        (_, string output) = rig.Run("update", "quit");
        Assert.Contains(Available, output);
        Assert.Contains("Update skipped: this build has no signing key.", output);

        WriteUpdateMode("install and restart");
        (_, output) = rig.Run("quit");
        Assert.Contains("Update skipped: this build has no signing key.", output);
        Assert.DoesNotContain(Installing, output);
        Assert.Empty(rig.Source.Downloads);
        Assert.Empty(rig.Launcher.Calls);
    }

    // AC-11: the pending file from the last run is reported at start and deleted.
    [Fact]
    public void Update_PendingOutcome_ShownAtStartAndDeleted()
    {
        string pending = Path.Combine(data.Path, "update-pending.json");
        File.WriteAllText(pending, "{\"version\":\"" + AppVersion.Current + "\",\"time\":\"2026-10-27T08:00:00+00:00\"}");

        (_, string output) = Run(c => c.Input.PushTextWithEnter("quit"));

        Assert.Contains("Updated to " + AppVersion.Current + ".", output);
        Assert.False(File.Exists(pending));
    }

    // AC-11: a broken pending file is deleted and ignored.
    [Fact]
    public void Update_BrokenPendingFile_DeletedAndIgnored()
    {
        string pending = Path.Combine(data.Path, "update-pending.json");
        File.WriteAllText(pending, "{oops");

        (int exitCode, string output) = Run(c => c.Input.PushTextWithEnter("quit"));

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain("Update", output);
        Assert.False(File.Exists(pending));
    }

    // Without an update service (the default in tests), update says so and nothing touches the network.
    [Fact]
    public void Update_NoService_SaysSo()
    {
        (_, string output) = RunCommands("update", "quit");

        Assert.Contains("Update check skipped: this build cannot update itself.", output);
    }

    // Job 096: hint rows wrap on narrow terminals -------------------------------------------

    /// <summary>A token whose unknown-command hint needs exactly 2 lines at width 40.</summary>
    private const string TwoLineToken = "zzzzzzzzzzzz";

    /// <summary>A token whose unknown-command hint needs exactly 3 lines at width 40.</summary>
    private static readonly string ThreeLineToken = new string('z', 30);

    /// <summary>A token whose unknown-command hint needs more than 3 lines at width 40.</summary>
    private static readonly string LongToken = new string('z', 150);

    private static string UnknownHint(string token) => $"Unknown command {token}. Type help to see all commands.";

    private (int ExitCode, string Output) RunNarrow(int width, int height, params string[] commands) =>
        Run(c =>
        {
            c.Profile.Width = width;
            c.Profile.Height = height;
            foreach (string command in commands)
            {
                c.Input.PushTextWithEnter(command);
            }
        });

    // AC-1 (096)
    [Fact]
    public void OneLineHint_AtWidth60_StaysOnOneRow_AndTheTopAreaKeepsHeightMinus4Rows()
    {
        (_, string output) = RunNarrow(60, 24, "nope", "quit");

        string[] frame = Frames(output)[1];
        AssertOnHintRow(frame, "Unknown command nope");
        Assert.Equal([UnknownHint("nope")], HintRowsOf(frame, "Unknown command"));
        Assert.Equal(TopHeight, frame[..^4].Length);
        Assert.Contains(Heading, frame[EmptyPanelRows]);
    }

    // AC-2 (096)
    [Fact]
    public void TwoLineHint_AtWidth40_TakesTwoRowsAboveTheInputPanel()
    {
        (int exitCode, string output) = RunNarrow(40, 24, TwoLineToken, "quit");

        Assert.Equal(0, exitCode);
        string[] frame = Frames(output)[1];
        AssertLayout(frame);
        Assert.Contains("Unknown command", frame[^5]);
        Assert.False(string.IsNullOrWhiteSpace(frame[^4]));
        Assert.Equal(2, HintRowsOf(frame, "Unknown command").Length);
        Assert.Equal(UnknownHint(TwoLineToken), HintText(frame, "Unknown command"));
        // The top area is 24 - 3 - 2 = 19 rows, still headed by the day.
        Assert.Equal(19, frame[..^5].Length);
        Assert.Contains(Heading, frame[EmptyPanelRows]);
        Assert.DoesNotContain(frame[..^5], l => l.Contains("Unknown", StringComparison.Ordinal) || l.Contains("help to see", StringComparison.Ordinal));
    }

    // AC-3 (096)
    [Fact]
    public void LongerHint_TakesThreeRows_AndItsLastRowEndsInAnEllipsis()
    {
        (_, string output) = RunNarrow(40, 24, LongToken, "quit");

        string[] frame = Frames(output)[1];
        AssertLayout(frame);
        Assert.StartsWith("Unknown command", frame[^6]);
        Assert.Equal(3, HintRowsOf(frame, "Unknown command").Length);
        Assert.EndsWith("…", frame[^4].TrimEnd());
        Assert.DoesNotContain(frame[..^6], l => l.Contains("Unknown", StringComparison.Ordinal) || l.Contains("zzz", StringComparison.Ordinal));
        Assert.Contains(Heading, frame[EmptyPanelRows]);
    }

    // AC-3 (096)
    [Fact]
    public void ThreeLineHint_ShowsInFull_WithoutAnEllipsis()
    {
        (_, string output) = RunNarrow(40, 24, ThreeLineToken, "quit");

        string[] frame = Frames(output)[1];
        AssertLayout(frame);
        Assert.StartsWith("Unknown command", frame[^6]);
        Assert.Equal(UnknownHint(ThreeLineToken), HintText(frame, "Unknown command"));
        Assert.DoesNotContain(frame[^6..^3], l => l.Contains('…'));
    }

    // AC-3 (096): the grey ellipsis is drawn grey, the hint keeps its red.
    [Fact]
    public void Ellipsis_IsGrey_AndTheHintStaysRed()
    {
        string output = RunAnsi(c =>
        {
            c.Profile.Width = 40;
            c.Input.PushTextWithEnter(LongToken);
            c.Input.PushTextWithEnter("quit");
        });

        Assert.Matches(@"\x1b\[(31|91|38;5;9)mUnknown command", output);
        Assert.Matches(@"\x1b\[(90|38;5;8)m…", output);
    }

    // AC-4 (096)
    [Fact]
    public void ShortTerminal_ThreeLineHint_GetsOneRow_EndingInAnEllipsis()
    {
        (_, string output) = RunNarrow(40, 8, ThreeLineToken, "quit");

        string[] frame = Frames(output)[1];
        Assert.Equal(8, frame.Length);
        Assert.StartsWith("╭", frame[^3]);
        Assert.StartsWith("Unknown command", frame[^4]);
        Assert.EndsWith("…", frame[^4].TrimEnd());
        Assert.DoesNotContain(frame[..^4], l => l.Contains("Unknown", StringComparison.Ordinal) || l.Contains("zzz", StringComparison.Ordinal));
        // The top area keeps its 4 rows (at least 3).
        Assert.Contains(frame[..^4], l => l.Contains("Projects", StringComparison.Ordinal));
    }

    // AC-5 (096)
    [Fact]
    public void NextCommandWithoutHint_BringsBackOneBlankHintRow()
    {
        (_, string output) = RunNarrow(40, 24, LongToken, "today", "quit");

        string[] frame = Frames(output)[2];
        AssertLayout(frame);
        Assert.True(string.IsNullOrWhiteSpace(frame[^4]));
        Assert.DoesNotContain(frame, l => l.Contains("Unknown", StringComparison.Ordinal) || l.Contains("zzz", StringComparison.Ordinal));
        Assert.Contains(Heading, frame[EmptyPanelRows]);
        // The top area is height - 4 = 20 rows: the 20th row is part of it, not hint.
        Assert.Equal(TopHeight, frame[..^4].Length);
    }

    /// <summary>A command that shows <paramref name="lines"/> numbered lines and a 2-row hint at width 40.</summary>
    private static CommandRegistry LongOutputWithHint(int lines)
    {
        CommandRegistry registry = CommandRegistry.CreateDefault();
        registry.Register("both", "bo", "Long output and a long hint", _ => new CommandResult(
            message: new Rows(Enumerable.Range(1, lines).Select(i => new Markup($"line {i}"))),
            hint: new Markup($"[red]{Markup.Escape(UnknownHint(TwoLineToken))}[/]"),
            hideQuicklist: true));
        return registry;
    }

    // AC-6 (096)
    [Fact]
    public void TwoRowHint_ScrollCuesAndPaging_UseTheSmallerTopArea()
    {
        string output = RunAnsi(c =>
        {
            c.Profile.Width = 40;
            c.Input.PushTextWithEnter("both");
            PageDown(c, 10);
            c.Input.PushKey(ConsoleKey.Enter);
            c.Input.PushTextWithEnter("quit");
        }, LongOutputWithHint(60));

        // 24 rows: 3 input, 2 hint (rows 20 and 21), a 19-row top area.
        string[] frame = AnsiFrames(output)[1];
        Assert.Contains("Unknown command", frame[19]);
        Assert.StartsWith("↓ ", frame[18]);
        Assert.Contains(BelowCue, frame[18]);

        List<string[]> screens = TopScreens(output, 1, 19);
        Assert.True(screens.Count > 1);
        Assert.Equal("line 60", screens[^1][18]);
        Assert.StartsWith("↑ ", screens[^1][0]);
        // Paging never wrote into the hint rows.
        List<AnsiWrite> writes = AnsiWrites.Parse(BetweenFrames(output, 1));
        Assert.DoesNotContain(writes.Skip(1), w => w.Column == 1 && (w.Row == 20 || w.Row == 21));
    }

    // AC-7 (096)
    [Fact]
    public void PasteCut_AtWidth40_WrapsTheHintOnTwoRows_InPlace_AndKeepsTheTypedLine()
    {
        (int exitCode, string output) = RunWithMouse(c =>
        {
            c.Profile.Width = 40;
            PushLongPaste(c, 'a');
            c.Input.PushKey(ConsoleKey.Escape);
            c.Input.PushKey(ConsoleKey.Escape);
            c.Input.PushTextWithEnter("quit");
        });

        Assert.Equal(0, exitCode);
        // Only the first frame and the clear on quit: no full clear for the hint.
        Assert.Equal(2, Count(output, ClearScreen));

        List<AnsiWrite> writes = AnsiWrites.Parse(output);
        int first = writes.FindIndex(w => w.Row == 20 && w.Column == 1 && w.Text.StartsWith("Paste cut", StringComparison.Ordinal));
        Assert.True(first > 0, "no paste-cut hint on row 20");
        AnsiWrite second = writes.Skip(first).First(w => w.Row == 21 && w.Column == 1);
        Assert.Equal(PasteCutText, writes[first].Text.Trim() + " " + second.Text.Trim());

        // Every row was rewritten in place, except the input line with the typed text.
        int start = writes.FindLastIndex(first, w => w.Row == 1 && w.Column == 1);
        List<AnsiWrite> rewrite = writes.Skip(start).TakeWhile(w => w.Column == 1).ToList();
        Assert.Equal([.. Enumerable.Range(1, 22), 24], rewrite.Select(w => w.Row));

        // Then the cursor is back on the input line and the editor redraws the typed line's tail.
        AnsiWrite after = writes[start + rewrite.Count];
        Assert.Equal(23, after.Row);
        AnsiWrite typed = writes.Skip(start + rewrite.Count).First(w => w.Row == 23 && w.Text.Length > 0);
        Assert.StartsWith("…aaa", typed.Text);
    }

    // AC-8 (096)
    [Fact]
    public void TwoRowHint_PopupEndsAboveTheInputBox_AndEscRestoresBothHintRows()
    {
        string output = RunAnsi(c =>
        {
            c.Profile.Width = 40;
            c.Input.PushTextWithEnter(TwoLineToken);
            c.Input.PushText("/");
            c.Input.PushKey(ConsoleKey.Escape);
            c.Input.PushKey(ConsoleKey.Escape);
            c.Input.PushTextWithEnter("quit");
        });

        string[] frame = AnsiFrames(output)[1];
        Assert.Contains("Unknown command", frame[19]);
        Assert.Contains("to see all commands.", frame[20]);

        List<AnsiWrite> writes = TypingWrites(output, 1);
        List<AnsiWrite> popup = writes.TakeWhile(w => w.StartsWithCommand || w.Text.StartsWith('…')).ToList();
        Assert.Equal(21, popup[^1].Row);
        Assert.Contains(popup, w => w.Row == 20);

        Dictionary<int, string> screen = [];
        foreach (AnsiWrite write in writes)
        {
            screen[write.Row] = write.Text;
        }

        Assert.Equal(frame[19], screen[20]);
        Assert.Equal(frame[20], screen[21]);
    }

    // AC-9 (096)
    [Fact]
    public void Settings_AtWidth40_OutOfRangeHint_WrapsOnTwoRows_AndKeepsTheSelectedRowVisible()
    {
        SettingsRegistry registry = new SettingsRegistry();
        for (int i = 1; i <= 15; i++)
        {
            registry.RegisterOnOff($"extra-{i}", $"Extra setting {i}", false);
        }

        registry.RegisterNumber(SettingsRegistry.DailyTargetName, "Daily target", 480, 0, SettingsRegistry.DailyTargetMax);
        const int Height = 16;
        (int exitCode, string output) = RunSettings(c =>
        {
            c.Profile.Width = 40;
            c.Input.PushTextWithEnter("settings");
            for (int i = 0; i < 15; i++)
            {
                c.Input.PushKey(ConsoleKey.DownArrow);
            }

            c.Input.PushKey(ConsoleKey.Enter);
            c.Input.PushTextWithEnter("9999");
            c.Input.PushKey(ConsoleKey.Escape);
            c.Input.PushTextWithEnter("quit");
        }, registry, height: Height);

        Assert.Equal(0, exitCode);
        string[] screen = PanelScreens(output, Height)[^1];
        Assert.Equal(Height, screen.Length);
        string[] hint = HintRowsOf(screen, "settings:");
        Assert.Equal(2, hint.Length);
        Assert.Equal("settings: daily-target takes a whole number from 0 to 1440.", string.Join(' ', hint));
        Assert.Equal("daily-target", Selected(screen[..^5]));
        Assert.Equal("480", ValueOf(screen[..^5], "daily-target"));
        Assert.False(File.Exists(SettingsPath));
    }

    // Job 098 ------------------------------------------------------------------------------

    // AC-1
    [Theory]
    [InlineData("absence vacation", "vacation")]
    [InlineData("ab vacation", "vacation")]
    [InlineData("/absence Vacation", "vacation")]
    [InlineData("absence SICK", "sick")]
    [InlineData("ab holiday", "publicHoliday")]
    public void Absence_OnDayWithoutFile_CreatesFileWithAbsence(string command, string saved)
    {
        (int exitCode, string output) = RunCommands(command, "quit");

        Assert.Equal(0, exitCode);
        Assert.Equal("{\"absence\":\"" + saved + "\",\"entries\":[]}", File.ReadAllText(DayFile("2026-10-27")));
        Assert.Contains("Absence:", LastFrame(output));
    }

    // AC-1
    [Fact]
    public void Absence_MarksTheShownDay_NotToday()
    {
        (int exitCode, _) = RunCommands("prev", "absence sick", "quit");

        Assert.Equal(0, exitCode);
        Assert.False(File.Exists(DayFile("2026-10-27")));
        Assert.Equal("{\"absence\":\"sick\",\"entries\":[]}", File.ReadAllText(DayFile("2026-10-26")));
    }

    // AC-2
    [Fact]
    public void Absence_OnAbsenceDay_ReplacesIt()
    {
        File.WriteAllText(DayFile("2026-10-27"), "{\"absence\":\"vacation\",\"entries\":[]}");

        (int exitCode, _) = RunCommands("absence holiday", "quit");

        Assert.Equal(0, exitCode);
        Assert.Equal("{\"absence\":\"publicHoliday\",\"entries\":[]}", File.ReadAllText(DayFile("2026-10-27")));
    }

    // AC-3
    [Fact]
    public void AbsenceClear_OnAbsenceDay_SavesWithoutAbsence()
    {
        File.WriteAllText(DayFile("2026-10-27"), "{\"absence\":\"sick\",\"entries\":[]}");

        (int exitCode, string output) = RunCommands("absence clear", "quit");

        Assert.Equal(0, exitCode);
        Assert.Equal("{\"entries\":[]}", File.ReadAllText(DayFile("2026-10-27")));
        Assert.Contains("No entries yet.", LastFrame(output));
    }

    // AC-3
    [Fact]
    public void AbsenceClear_OnDayWithoutAbsence_ShowsHint_AndWritesNothing()
    {
        (int exitCode, string output) = RunCommands("absence clear", "quit");

        Assert.Equal(0, exitCode);
        Assert.False(File.Exists(DayFile("2026-10-27")));
        Assert.Contains("This day has no absence.", HintText(LastLayout(output), "absence:"));
    }

    // AC-3
    [Fact]
    public void AbsenceClear_OnDayWithEntries_ShowsHint_AndLeavesFileUnchanged()
    {
        File.WriteAllText(DayFile("2026-10-27"), "{\"entries\":[{\"kind\":\"arrival\",\"time\":\"08:00\"}]}");
        byte[] before = File.ReadAllBytes(DayFile("2026-10-27"));

        (_, string output) = RunCommands("absence clear", "quit");

        Assert.Equal(before, File.ReadAllBytes(DayFile("2026-10-27")));
        Assert.Contains("This day has no absence.", HintText(LastLayout(output), "absence:"));
    }

    // AC-4
    [Theory]
    [InlineData("absence")]
    [InlineData("ab")]
    [InlineData("absence party")]
    [InlineData("absence vacation sick")]
    public void Absence_WithMissingOrUnknownArgument_ShowsUsage_AndWritesNothing(string command)
    {
        (int exitCode, string output) = RunCommands(command, "quit");

        Assert.Equal(0, exitCode);
        Assert.False(File.Exists(DayFile("2026-10-27")));
        string[] frame = LastLayout(output);
        AssertLayout(frame);
        Assert.Contains("absence vacation|sick|holiday|clear", HintText(frame, "absence:"));
    }

    // AC-4: only ASCII letters match, so a long s (which folds to S) is no "sick".
    [Fact]
    public void Absence_NonAsciiLookalike_IsUnknown_AndWritesNothing()
    {
        (int exitCode, string output) = Run(c =>
        {
            c.Input.PushText("absence ");
            c.Input.PushKey(new ConsoleKeyInfo('\u017F', ConsoleKey.NoName, false, false, false));
            c.Input.PushTextWithEnter("ick");
            c.Input.PushTextWithEnter("quit");
        });

        Assert.Equal(0, exitCode);
        Assert.False(File.Exists(DayFile("2026-10-27")));
        Assert.Contains("absence vacation|sick|holiday|clear", HintText(LastLayout(output), "absence:"));
    }

    // AC-4
    [Fact]
    public void Absence_UsageHint_IsRed()
    {
        TestConsole console = new TestConsole().Interactive().EmitAnsiSequences();
        console.Input.PushTextWithEnter("absence party");
        console.Input.PushTextWithEnter("quit");

        new App(console, FixedTimeProvider.At20261027(), new DayStore(data.Path)).Run([]);

        Assert.Matches(@"\x1b\[38;5;9mabsence: ", console.Output);
    }

    // AC-5
    [Fact]
    public void Absence_OnDayWithEntries_ShowsHint_AndLeavesFileUnchanged()
    {
        File.WriteAllText(DayFile("2026-10-27"), "{\"entries\":[{\"kind\":\"arrival\",\"time\":\"08:00\"}]}");
        byte[] before = File.ReadAllBytes(DayFile("2026-10-27"));

        (int exitCode, string output) = RunCommands("absence vacation", "quit");

        Assert.Equal(0, exitCode);
        Assert.Equal(before, File.ReadAllBytes(DayFile("2026-10-27")));
        string hint = HintText(LastLayout(output), "absence:");
        Assert.Contains("this day has entries", hint);
        Assert.Contains("Remove them first", hint);
    }

    // AC-6
    [Theory]
    [InlineData("arrive 8:00", "arrive:")]
    [InlineData("leave 17:00", "leave:")]
    [InlineData("break 12:00", "break:")]
    [InlineData("breakend 12:30", "breakend:")]
    [InlineData("checkpoint 9:00 Review", "checkpoint:")]
    [InlineData("a", "arrive:")]
    public void Booking_OnAbsenceDay_ShowsHint_AndLeavesFileUnchanged(string command, string start)
    {
        File.WriteAllText(DayFile("2026-10-27"), "{\"absence\":\"vacation\",\"entries\":[]}");
        byte[] before = File.ReadAllBytes(DayFile("2026-10-27"));

        (int exitCode, string output) = RunCommands(command, "quit");

        Assert.Equal(0, exitCode);
        Assert.Equal(before, File.ReadAllBytes(DayFile("2026-10-27")));
        string hint = HintText(LastLayout(output), start);
        Assert.Contains("Vacation", hint);
        Assert.Contains("absence clear", hint);
    }

    // AC-7
    [Theory]
    [InlineData("absence vacation")]
    [InlineData("absence clear")]
    public void Absence_UnreadableFile_ShowsHint_AndLeavesFileUnchanged(string command)
    {
        File.WriteAllText(DayFile("2026-10-27"), "{\"absence\":\"party\",\"entries\":[]}");
        byte[] before = File.ReadAllBytes(DayFile("2026-10-27"));

        (int exitCode, string output) = RunCommands(command, "quit");

        Assert.Equal(0, exitCode);
        Assert.Equal(before, File.ReadAllBytes(DayFile("2026-10-27")));
        Assert.Contains("is not a valid day file. Nothing was changed.", HintText(LastLayout(output), "absence:"));
    }

    // AC-10
    [Fact]
    public void AbsenceDay_ShowsAbsenceAndTargetLine_InTheApp()
    {
        File.WriteAllText(DayFile("2026-10-27"), "{\"absence\":\"publicHoliday\",\"entries\":[]}");

        (_, string output) = RunCommands("quit");

        string frame = LastFrame(output);
        Assert.Contains("Absence: Public holiday", frame);
        Assert.Contains("Target 8:00 · day +0:00 · balance 0:00", frame);
        Assert.DoesNotContain("No entries yet.", frame);
    }

    // AC-11
    [Fact]
    public void Help_ListsAbsence_WithAlias()
    {
        TestConsole console = new TestConsole().Interactive().EmitAnsiSequences();
        console.Input.PushTextWithEnter("help");
        PageDown(console, HelpPages);
        console.Input.PushTextWithEnter("quit");

        new App(console, FixedTimeProvider.At20261027(), new DayStore(data.Path)).Run([]);
        string plain = Regex.Replace(console.Output, @"\x1b\[[0-9;?]*[A-Za-z]", string.Empty);

        Assert.Matches(@"│ absence +│ ab +│ Mark this day as vacation", plain);
    }

    // Job 099 ------------------------------------------------------------------------------

    private static string ReportCp(string time, string name, int? workItem = null) =>
        workItem is { } w
            ? $"{{\"kind\":\"checkpoint\",\"time\":\"{time}\",\"name\":\"{name}\",\"workItem\":{w}}}"
            : $"{{\"kind\":\"checkpoint\",\"time\":\"{time}\",\"name\":\"{name}\"}}";

    private void WriteReportDay(string date, params string[] entries) =>
        File.WriteAllText(DayFile(date), "{\"entries\":[" + string.Join(',', entries) + "]}");

    private const string ReportLeave = "{\"kind\":\"leaving\",\"time\":\"18:00\"}";

    /// <summary>Three days of checkpoints: #1234 7:30 + 24:00, #99 0:30, a name row "Mail" 1:00, plus days outside the range.</summary>
    private void WriteReportDays()
    {
        WriteReportDay("2026-11-01", ReportCp("09:00", "Outside", 5), ReportLeave);
        WriteReportDay("2026-11-02", ReportCp("09:00", "Review", 1234), ReportCp("16:30", "Mail"), ReportCp("17:30", "Call", 99), ReportLeave);
        WriteReportDay("2026-11-03", ReportCp("00:00", "Fix", 1234), "{\"kind\":\"leaving\",\"time\":\"23:59\"}");
        WriteReportDay("2026-11-04", ReportCp("09:00", "Fix", 1234), ReportLeave);
        WriteReportDay("2026-11-05", ReportCp("09:00", "Outside", 5), ReportLeave);
    }

    /// <summary>Runs <paramref name="commands"/> then quit on a 40-row console and returns the exit code and the last frame.</summary>
    private (int ExitCode, string[] Frame) RunReport(params string[] commands)
    {
        (int exitCode, string output) = Run(c =>
        {
            c.Profile.Height = 40;
            c.Profile.Width = 100;
            foreach (string command in commands)
            {
                c.Input.PushTextWithEnter(command);
            }
        });
        string[] frame = LastLayout(output);
        AssertLayout(frame, 40);
        return (exitCode, frame);
    }

    // AC-1, AC-2, AC-3
    [Fact]
    public void Report_ShowsColumnsRowsInWorkItemOrder_HoursAndTotal()
    {
        WriteReportDays();
        File.WriteAllText(ProjectsFile, "{\"projects\":[{\"workItem\":1234,\"title\":\"Times\"}]}");

        (int exitCode, string[] frame) = RunReport("report 2026-11-02 2026-11-04", "quit");

        Assert.Equal(0, exitCode);
        Assert.Contains(frame, l => Regex.IsMatch(l, @"│ Work item +│ Title +│ Time +│ Hours +│"));
        // #1234: 9:00-16:30 (7:30), 0:00-23:59 (23:59), 9:00-18:00 (9:00) = 40:29.
        int first = Array.FindIndex(frame, l => Regex.IsMatch(l, @"│ #99 +│ Call +│ 0:30 +│ +0\.50 │"));
        int second = Array.FindIndex(frame, l => Regex.IsMatch(l, @"│ #1234 +│ Times +│ 40:29 +│ +40\.48 │"));
        int name = Array.FindIndex(frame, l => Regex.IsMatch(l, @"│ +│ Mail +│ 1:00 +│ +1\.00 │"));
        int total = Array.FindIndex(frame, l => Regex.IsMatch(l, @"│ Total +│ +│ 41:59 +│ +41\.98 │"));
        Assert.True(first > 0 && second > first && name > second && total > name, string.Join("\n", frame));
        Assert.Contains(frame, l => l.Trim() == "2026-11-02 – 2026-11-04");
        Assert.DoesNotContain(frame, l => l.Contains("Outside", StringComparison.Ordinal));
        Assert.DoesNotContain(frame, l => l.Contains("open", StringComparison.Ordinal));
        // The quicklist panel is hidden.
        Assert.DoesNotContain(frame, l => l.Contains("Projects", StringComparison.Ordinal));
        Assert.Equal(string.Empty, frame[^4].Trim());
    }

    // AC-2
    [Fact]
    public void Report_HeadersAndBorderAreGrey_TimesAreSteelBlue()
    {
        WriteReportDays();

        string output = RunAnsiCommands("report 2026-11-02 2026-11-02");

        Assert.Matches(@"\x1b\[(90|38;5;8)mWork item\x1b\[0m", output);
        Assert.Matches(@"\x1b\[(90|38;5;8)mHours\x1b\[0m", output);
        Assert.Matches(@"\x1b\[(90|38;5;8)m╭─", output);
        Assert.Matches(@"\x1b\[38;5;75m7:30\x1b\[0m", output);
        Assert.Matches(@"\x1b\[38;5;75m7\.50\x1b\[0m", output);
    }

    // AC-1
    [Theory]
    [InlineData("rp")]
    [InlineData("RP")]
    [InlineData("/report")]
    [InlineData("/rp")]
    public void Report_AliasAndSlash_DoTheSameAsReport(string typed)
    {
        WriteReportDays();

        (_, string[] full) = RunReport("report 2026-11-02 2026-11-04", "quit");
        (_, string[] other) = RunReport(typed + " 2026-11-02 2026-11-04", "quit");

        Assert.Equal(full, other);
        Assert.Contains(other, l => Regex.IsMatch(l, @"│ Total +│ +│ 41:59 +│"));
    }

    // AC-3
    [Fact]
    public void Report_WithoutQuicklistProject_ShowsTheNamesInFirstBookedOrder()
    {
        WriteReportDays();
        File.WriteAllText(ProjectsFile, "{\"projects\":[{\"workItem\":99,\"title\":\"Calls\"}]}");

        (_, string[] frame) = RunReport("report 2026-11-02 2026-11-04", "quit");

        Assert.Contains(frame, l => Regex.IsMatch(l, @"│ #99 +│ Calls +│ 0:30 +│"));
        Assert.Contains(frame, l => Regex.IsMatch(l, @"│ #1234 +│ Review, Fix +│ 40:29 +│"));
    }

    // AC-3
    [Theory]
    [InlineData("{not json")]
    [InlineData(null)]
    public void Report_UnreadableOrMissingProjects_FallsBackToNames(string? projects)
    {
        WriteReportDays();
        if (projects is not null)
        {
            File.WriteAllText(ProjectsFile, projects);
        }

        (int exitCode, string[] frame) = RunReport("report 2026-11-02 2026-11-04", "quit");

        Assert.Equal(0, exitCode);
        Assert.Contains(frame, l => Regex.IsMatch(l, @"│ #1234 +│ Review, Fix +│ 40:29 +│"));
        Assert.Contains(frame, l => Regex.IsMatch(l, @"│ #99 +│ Call +│ 0:30 +│"));
        Assert.Equal(string.Empty, frame[^4].Trim());
        Assert.Equal(projects is not null, File.Exists(ProjectsFile));
    }

    // AC-5
    [Fact]
    public void Report_OpenCheckpointOnAnotherDay_IsMarkedOpen_WithFootnote()
    {
        WriteReportDay("2026-11-02", ReportCp("09:00", "Plan", 7));

        (_, string[] frame) = RunReport("report 2026-11-02 2026-11-02", "quit");

        Assert.Contains(frame, l => Regex.IsMatch(l, @"│ #7 +│ Plan +│ 0:00 open +│ +0\.00 │"));
        Assert.Contains(frame, l => l.Contains(App.OpenFootnote, StringComparison.Ordinal));
    }

    // AC-5
    [Fact]
    public void Report_OpenCheckpointToday_CountsToNow()
    {
        WriteReportDay("2026-10-27", ReportCp("17:00", "Plan", 7));

        (_, string[] frame) = RunReport("report 2026-10-27 2026-10-27", "quit");

        Assert.Contains(frame, l => Regex.IsMatch(l, @"│ #7 +│ Plan +│ 1:30 +│ +1\.50 │"));
        Assert.DoesNotContain(frame, l => l.Contains("open", StringComparison.Ordinal));
    }

    // AC-6, AC-7
    [Fact]
    public void Report_NoCheckpoints_ShowsGreyMessage_SkippedLine_AndWritesNothing()
    {
        File.WriteAllText(DayFile("2026-11-02"), "{\"absence\":\"vacation\"}");
        File.WriteAllText(DayFile("2026-11-03"), "{not json");
        string[] before = Directory.EnumerateFileSystemEntries(data.Path).Order(StringComparer.Ordinal).ToArray();
        byte[] bytes = File.ReadAllBytes(DayFile("2026-11-02"));

        (int exitCode, string[] frame) = RunReport("report 2026-11-02 2026-11-04", "quit");

        Assert.Equal(0, exitCode);
        Assert.Contains(frame, l => l.Contains("No checkpoints between 2026-11-02 and 2026-11-04.", StringComparison.Ordinal));
        Assert.Contains(frame, l => l.Contains("! 1 day file skipped", StringComparison.Ordinal));
        Assert.DoesNotContain(frame, l => l.Contains("2026-11-03", StringComparison.Ordinal));
        Assert.Equal(before, Directory.EnumerateFileSystemEntries(data.Path).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(bytes, File.ReadAllBytes(DayFile("2026-11-02")));

        string ansi = RunAnsiCommands("report 2026-11-02 2026-11-04");
        Assert.Matches(@"\x1b\[(90|38;5;8)mNo checkpoints between", ansi);
        Assert.Matches(@"\x1b\[(33|93|38;5;11)m! 1 day file skipped", ansi);
    }

    // AC-6
    [Fact]
    public void Report_SkippedFile_ShowsYellowLineBelowTheTable()
    {
        WriteReportDays();
        File.WriteAllText(DayFile("2026-11-03"), "{not json");

        (_, string[] frame) = RunReport("report 2026-11-02 2026-11-04", "quit");

        int total = Array.FindIndex(frame, l => l.Contains("│ Total", StringComparison.Ordinal));
        int skipped = Array.FindIndex(frame, l => l.Contains("! 1 day file skipped", StringComparison.Ordinal));
        Assert.True(total > 0 && skipped > total);
    }

    // AC-6
    [Fact]
    public void Report_MissingFolder_CreatesNothing()
    {
        using TempFolder missing = new TempFolder(create: false);
        (_, string output) = Run(c =>
        {
            c.Input.PushTextWithEnter("report 2026-11-02 2026-11-04");
            c.Input.PushTextWithEnter("quit");
        }, dataFolder: missing.Path);

        Assert.Contains("No checkpoints between 2026-11-02 and 2026-11-04.", output);
        AssertLeftNothing(missing.Path);
    }

    // AC-8
    [Theory]
    [InlineData("report", "report: say the first and the last day. Use report <from> <to> with dates as yyyy-MM-dd.")]
    [InlineData("report 2026-11-02", "report: say the first and the last day.")]
    [InlineData("report 2026-11-02 2026-11-04 x", "report: say the first and the last day.")]
    [InlineData("report 2026-11-2 2026-11-04", "report: 2026-11-2 is not a date.")]
    [InlineData("report 2026-11-02 04.11.2026", "report: 04.11.2026 is not a date.")]
    [InlineData("report 2026-02-30 2026-03-01", "report: 2026-02-30 is not a date.")]
    [InlineData("report [red]x 2026-11-04", "report: [red]x is not a date.")]
    [InlineData("report 2026-11-05 2026-11-04", "report: the first day 2026-11-05 is after the last day 2026-11-04.")]
    public void Report_BadInput_ShowsRedHint_AndLoopKeepsRunning(string command, string hint)
    {
        WriteReportDays();

        (int exitCode, string output) = Run(c =>
        {
            c.Profile.Width = 200;
            c.Input.PushTextWithEnter(command);
            c.Input.PushTextWithEnter("quit");
        });

        Assert.Equal(0, exitCode);
        string[] frame = LastLayout(output);
        AssertOnHintRow(frame, hint);
        Assert.DoesNotContain(frame, l => l.Contains("Work item", StringComparison.Ordinal));
        Assert.DoesNotContain("   at ", output);

        // The loop keeps running: a following command still works.
        (_, string[] next) = RunReport(command, "report 2026-11-02 2026-11-04", "quit");
        Assert.Contains(next, l => Regex.IsMatch(l, @"│ Total +│"));
    }

    // AC-8
    [Fact]
    public void Report_BadInput_HintIsRed()
    {
        string output = RunAnsiCommands("report 2026-11-05 2026-11-04");

        Assert.Matches(@"\x1b\[(31|91|38;5;9)mreport: the first day", output);
    }

    // AC-8
    [Fact]
    public void Report_FolderThatCannotBeListed_ShowsRedHint()
    {
        UseSideLock();
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        UnixFileMode mode = File.GetUnixFileMode(data.Path);
        File.SetUnixFileMode(data.Path, UnixFileMode.None);
        try
        {
            try
            {
                Directory.EnumerateFileSystemEntries(data.Path).ToList();
                // Running as root: access can't be denied.
                return;
            }
            catch (UnauthorizedAccessException)
            {
            }

            (int exitCode, string output) = RunCommands("report 2026-11-02 2026-11-04", "quit");

            Assert.Equal(0, exitCode);
            Assert.StartsWith("report: the data folder cannot be listed", HintText(LastLayout(output), "report: the data folder"));
            Assert.DoesNotContain("   at ", output);
        }
        finally
        {
            File.SetUnixFileMode(data.Path, mode);
        }
    }

    // AC-10
    [Fact]
    public void Report_MarkupInTitlesAndNames_ShowsLiterally()
    {
        WriteReportDay("2026-11-02", ReportCp("09:00", "[red]x", 1), ReportCp("10:00", "[bold]y"), ReportLeave);
        File.WriteAllText(ProjectsFile, "{\"projects\":[{\"workItem\":1,\"title\":\"[red]x[/] title\"}]}");

        (int exitCode, string[] frame) = RunReport("report 2026-11-02 2026-11-02", "quit");

        Assert.Equal(0, exitCode);
        Assert.Contains(frame, l => Regex.IsMatch(l, @"│ #1 +│ \[red\]x\[/\] title +│ 1:00 +│"));
        Assert.Contains(frame, l => Regex.IsMatch(l, @"│ +│ \[bold\]y +│ 8:00 +│"));
    }

    // AC-11
    [Fact]
    public void Help_ListsReport_WithAliasRp()
    {
        TestConsole console = new TestConsole().Interactive().EmitAnsiSequences();
        console.Profile.Width = 120;
        console.Input.PushTextWithEnter("help");
        PageDown(console, HelpPages);
        console.Input.PushTextWithEnter("quit");

        new App(console, FixedTimeProvider.At20261027(), new DayStore(data.Path)).Run([]);
        string plain = Regex.Replace(console.Output, @"\x1b\[[0-9;?]*[A-Za-z]", string.Empty);

        Assert.Matches(@"│ report +│ rp +│ Show the hours per project over a date range: <from> <to> +│", plain);
    }

    // Job 100 ------------------------------------------------------------------------------

    private const string ExportName = "times-report-2026-11-02-to-2026-11-04.csv";

    /// <summary>The CSV for <see cref="WriteReportDays"/> over 2026-11-02 to 2026-11-04 with #1234 titled Times.</summary>
    private const string ExportedCsv = "\uFEFFWork item,Title,Hours\r\n99,Call,0.50\r\n1234,Times,40.48\r\n,Mail,1.00\r\n";

    /// <summary>Runs <paramref name="commands"/> then quit on a wide 40-row console and returns the exit code and the last frame.</summary>
    private (int ExitCode, string[] Frame) RunExport(params string[] commands)
    {
        (int exitCode, string output) = Run(c =>
        {
            c.Profile.Height = 40;
            c.Profile.Width = 250;
            foreach (string command in commands)
            {
                c.Input.PushTextWithEnter(command);
            }

            c.Input.PushTextWithEnter("quit");
        });
        Assert.DoesNotContain("   at ", output);
        string[] frame = LastLayout(output);
        AssertLayout(frame, 40);
        return (exitCode, frame);
    }

    private static string[] Listing(string folder) =>
        Directory.EnumerateFileSystemEntries(folder).Select(p => Path.GetFileName(p)!).Order(StringComparer.Ordinal).ToArray();

    private static string ExportHint(string[] frame) => HintText(frame, "export:");

    // AC-1, AC-2, AC-8, AC-11
    [Fact]
    public void Export_WritesTheReportRowsAsCsv_AndShowsThePath()
    {
        using TempFolder target = new TempFolder();
        WriteReportDays();
        File.WriteAllText(ProjectsFile, "{\"projects\":[{\"workItem\":1234,\"title\":\"Times\"}]}");
        string[] dataBefore = Listing(data.Path);
        byte[] projectsBefore = File.ReadAllBytes(ProjectsFile);

        (int exitCode, string[] frame) = RunExport($"export 2026-11-02 2026-11-04 {target.Path}");

        Assert.Equal(0, exitCode);
        string path = Path.Combine(target.Path, ExportName);
        Assert.Equal(Encoding.UTF8.GetBytes(ExportedCsv), File.ReadAllBytes(path));
        Assert.Contains(frame, l => l.Contains($"Exported 3 rows to {path}", StringComparison.Ordinal));
        Assert.Equal(string.Empty, frame[^4].Trim());
        Assert.Equal([ExportName], Listing(target.Path));
        Assert.Equal(dataBefore, Listing(data.Path));
        Assert.Equal(projectsBefore, File.ReadAllBytes(ProjectsFile));
        Assert.DoesNotContain(frame, l => l.Contains(App.ExportOpenNote, StringComparison.Ordinal));
    }

    // AC-1: the same rows, in the same order, as report shows.
    [Fact]
    public void Export_RowsMatchTheReport()
    {
        using TempFolder target = new TempFolder();
        WriteReportDays();

        (_, string[] report) = RunReport("report 2026-11-02 2026-11-04", "quit");
        RunExport($"export 2026-11-02 2026-11-04 {target.Path}");

        string[] csvRows = File.ReadAllText(Path.Combine(target.Path, ExportName)).Split("\r\n")[1..^1];
        List<string> reportRows = report
            .Select(l => Regex.Match(l, @"^│ (#?\d*) +│ (.+?) +│ [0-9:]+(?: open)? +│ +([0-9.]+) │"))
            .Where(m => m.Success)
            .Select(m => $"{m.Groups[1].Value.TrimStart('#')},{CsvTitle(m.Groups[2].Value)},{m.Groups[3].Value}")
            .ToList();
        Assert.Equal(reportRows, csvRows);
        Assert.Equal(3, csvRows.Length);
    }

    private static string CsvTitle(string title) => title.Contains(',', StringComparison.Ordinal) ? $"\"{title}\"" : title;

    // AC-1
    [Theory]
    [InlineData("ex")]
    [InlineData("EX")]
    [InlineData("/export")]
    [InlineData("/ex")]
    public void Export_AliasAndSlash_WriteTheSameFile(string typed)
    {
        using TempFolder target = new TempFolder();
        WriteReportDays();
        File.WriteAllText(ProjectsFile, "{\"projects\":[{\"workItem\":1234,\"title\":\"Times\"}]}");

        (int exitCode, _) = RunExport($"{typed} 2026-11-02 2026-11-04 {target.Path}");

        Assert.Equal(0, exitCode);
        Assert.Equal(Encoding.UTF8.GetBytes(ExportedCsv), File.ReadAllBytes(Path.Combine(target.Path, ExportName)));
    }

    // AC-5
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Export_FolderWithSpace_QuotedOrNot_IsTheRestOfTheLine(bool quoted)
    {
        using TempFolder target = new TempFolder();
        string folder = Path.Combine(target.Path, "my  folder");
        Directory.CreateDirectory(folder);
        WriteReportDays();
        string typed = quoted ? $"\"{folder}\"" : folder;

        (int exitCode, _) = RunExport($"export   2026-11-02   2026-11-04   {typed}   ");

        Assert.Equal(0, exitCode);
        Assert.Equal([ExportName], Listing(folder));
    }

    // AC-5
    [Fact]
    public void ExpandFolder_RemovesOnePairOfQuotes_AndExpandsTheTilde()
    {
        string home = Path.Combine(Path.GetTempPath(), "home");

        Assert.Equal(home, App.ExpandFolder("~", home));
        Assert.Equal(Path.Combine(home, "Exports"), App.ExpandFolder("~/Exports", home));
        Assert.Equal(Path.Combine(home, "a b"), App.ExpandFolder("\"~/a b\"", home));
        Assert.Equal("~x", App.ExpandFolder("~x", home));
        Assert.Equal("\"/tmp/x\"", App.ExpandFolder("\"\"/tmp/x\"\"", home));
        Assert.Equal("\"/tmp/x", App.ExpandFolder("\"/tmp/x", home));
        Assert.Equal("/tmp/~/x", App.ExpandFolder("/tmp/~/x", home));
    }

    // AC-5
    [Fact]
    public void Export_TildeFolder_IsExpandedToTheHomeFolder()
    {
        string missing = "times-test-" + Guid.NewGuid().ToString("N");
        WriteReportDays();

        (_, string[] frame) = RunExport($"export 2026-11-02 2026-11-04 ~/{missing}");

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Contains($"export: the folder {Path.Combine(home, missing)} does not exist.", ExportHint(frame));
        Assert.False(Directory.Exists(Path.Combine(home, missing)));
    }

    // AC-5
    [Theory]
    [InlineData("relative", "is not an absolute path.")]
    [InlineData("missing", "does not exist. Nothing was created.")]
    [InlineData("file", "is a file, not a folder.")]
    public void Export_BadFolder_ShowsRedHint_AndCreatesNothing(string kind, string hint)
    {
        using TempFolder target = new TempFolder();
        string file = Path.Combine(target.Path, "plain.txt");
        File.WriteAllText(file, "x");
        string folder = kind switch
        {
            "relative" => "exports" + Guid.NewGuid().ToString("N"),
            "missing" => Path.Combine(target.Path, "missing"),
            _ => file,
        };
        WriteReportDays();
        string[] dataBefore = Listing(data.Path);
        string[] cwdBefore = WorkingDirectoryEntries();

        (int exitCode, string[] frame) = RunExport($"export 2026-11-02 2026-11-04 {folder}", "report 2026-11-02 2026-11-04");
        (_, string[] hintFrame) = RunExport($"export 2026-11-02 2026-11-04 {folder}");

        Assert.Equal(0, exitCode);
        // The loop keeps running: report still works afterwards.
        Assert.Contains(frame, l => Regex.IsMatch(l, @"│ Total +│"));
        string expected = kind == "missing" ? $"export: the folder {folder} {hint}" : $"export: {folder} {hint}";
        Assert.Contains(expected, ExportHint(hintFrame));
        Assert.Equal(["plain.txt"], Listing(target.Path));
        Assert.Equal("x", File.ReadAllText(file));
        Assert.Equal(dataBefore, Listing(data.Path));
        Assert.Equal(cwdBefore, WorkingDirectoryEntries());
    }

    // AC-6
    [Fact]
    public void Export_ExistingFile_StaysByteIdentical_AndHintNamesIt()
    {
        using TempFolder target = new TempFolder();
        string path = Path.Combine(target.Path, ExportName);
        File.WriteAllBytes(path, [1, 2, 3]);
        WriteReportDays();

        (int exitCode, string[] frame) = RunExport($"export 2026-11-02 2026-11-04 {target.Path}");

        Assert.Equal(0, exitCode);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(path));
        Assert.Equal([ExportName], Listing(target.Path));
        Assert.Equal($"export: {path} already exists. Move or delete it first.", ExportHint(frame));
        Assert.DoesNotContain(frame, l => l.Contains("Exported", StringComparison.Ordinal));
    }

    // AC-6
    [Fact]
    public void Export_DanglingSymlink_IsNotFollowed()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using TempFolder target = new TempFolder();
        using TempFolder elsewhere = new TempFolder();
        string link = Path.Combine(target.Path, ExportName);
        string pointsTo = Path.Combine(elsewhere.Path, "victim.csv");
        File.CreateSymbolicLink(link, pointsTo);
        WriteReportDays();

        (_, string[] frame) = RunExport($"export 2026-11-02 2026-11-04 {target.Path}");

        Assert.Equal($"export: {link} already exists. Move or delete it first.", ExportHint(frame));
        Assert.False(File.Exists(pointsTo));
        Assert.Empty(Listing(elsewhere.Path));
        Assert.Equal(pointsTo, new FileInfo(link).LinkTarget);
    }

    // AC-7
    [Fact]
    public void Export_ReadOnlyFolder_ShowsRedHint_AndLeavesNoFile()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using TempFolder target = new TempFolder();
        WriteReportDays();
        UnixFileMode mode = File.GetUnixFileMode(target.Path);
        File.SetUnixFileMode(target.Path, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            try
            {
                File.WriteAllText(Path.Combine(target.Path, "probe"), "x");
                // Running as root: access can't be denied.
                return;
            }
            catch (UnauthorizedAccessException)
            {
            }

            (int exitCode, string[] frame) = RunExport($"export 2026-11-02 2026-11-04 {target.Path}", "report 2026-11-02 2026-11-04");
            (_, string[] hintFrame) = RunExport($"export 2026-11-02 2026-11-04 {target.Path}");

            Assert.Equal(0, exitCode);
            Assert.Contains(frame, l => Regex.IsMatch(l, @"│ Total +│"));
            Assert.StartsWith($"export: could not write {Path.Combine(target.Path, ExportName)}.", ExportHint(hintFrame));
            Assert.Empty(Listing(target.Path));
        }
        finally
        {
            File.SetUnixFileMode(target.Path, mode);
        }
    }

    // AC-7, AC-10
    [Fact]
    public void Export_Hints_AreRed()
    {
        using TempFolder target = new TempFolder();
        File.WriteAllBytes(Path.Combine(target.Path, ExportName), [1]);
        WriteReportDays();

        string output = RunAnsiCommands(
            "export",
            "export 2026-11-05 2026-11-04 /x",
            "export 2026-11-02 2026-11-04 relative",
            $"export 2026-11-02 2026-11-04 {target.Path}");

        Assert.Matches(@"\x1b\[(31|91|38;5;9)mexport: say the first day", output);
        Assert.Matches(@"\x1b\[(31|91|38;5;9)mexport: the first day", output);
        Assert.Matches(@"\x1b\[(31|91|38;5;9)mexport: [^\n]{0,20}relative[^\n]{0,20} is not an absolute path", output);
        Assert.Matches(@"\x1b\[(31|91|38;5;9)mexport: .*already exists", output);
    }

    // AC-8
    [Fact]
    public void Export_OpenCheckpoint_AndSkippedFile_AddGreyNoteAndYellowLine()
    {
        using TempFolder target = new TempFolder();
        WriteReportDay("2026-11-02", ReportCp("09:00", "Plan", 7));
        File.WriteAllText(DayFile("2026-11-03"), "{not json");

        (_, string[] frame) = RunExport($"export 2026-11-02 2026-11-04 {target.Path}");

        int message = Array.FindIndex(frame, l => l.Contains("Exported 1 row to ", StringComparison.Ordinal));
        int note = Array.FindIndex(frame, l => l.Contains(App.ExportOpenNote, StringComparison.Ordinal));
        int skipped = Array.FindIndex(frame, l => l.Contains("! 1 day file skipped", StringComparison.Ordinal));
        Assert.True(message >= 0 && note > message && skipped > note, string.Join("\n", frame));
        Assert.Equal(Encoding.UTF8.GetBytes("\uFEFFWork item,Title,Hours\r\n7,Plan,0.00\r\n"), File.ReadAllBytes(Path.Combine(target.Path, ExportName)));

        Directory.CreateDirectory(Path.Combine(target.Path, "again"));
        string ansi = RunAnsiCommands($"export 2026-11-02 2026-11-04 {Path.Combine(target.Path, "again")}");
        Assert.Matches(@"\x1b\[(90|38;5;8)mA row has an open checkpoint", ansi);
        Assert.Matches(@"\x1b\[(33|93|38;5;11)m! 1 day file skipped", ansi);
    }

    // AC-8
    [Fact]
    public void Export_PathWithMarkupAndHiddenCharacters_IsShownEscapedAndCleaned()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using TempFolder target = new TempFolder();
        string folder = Path.Combine(target.Path, "[red]x‮y");
        Directory.CreateDirectory(folder);
        WriteReportDays();

        // The test console types only chars up to U+00FF as text, so the U+202E is pushed as a key.
        (int exitCode, string output) = Run(c =>
        {
            c.Profile.Height = 40;
            c.Profile.Width = 250;
            c.Input.PushText($"export 2026-11-02 2026-11-04 {Path.Combine(target.Path, "[red]x")}");
            c.Input.PushKey(new ConsoleKeyInfo('\u202E', ConsoleKey.NoName, false, false, false));
            c.Input.PushTextWithEnter("y");
            c.Input.PushTextWithEnter("quit");
        });
        string[] frame = LastLayout(output);

        Assert.Equal(0, exitCode);
        Assert.Equal([ExportName], Listing(folder));
        string shown = Path.Combine(target.Path, "[red]xy", ExportName);
        Assert.Contains(frame, l => l.Contains($"Exported 3 rows to {shown}", StringComparison.Ordinal));
        Assert.DoesNotContain(frame, l => l.Contains('‮', StringComparison.Ordinal));
    }

    // AC-9
    [Fact]
    public void Export_NoCheckpoints_WritesNoFile_AndShowsGreyNote()
    {
        using TempFolder target = new TempFolder();
        File.WriteAllText(DayFile("2026-11-02"), "{\"absence\":\"vacation\"}");

        (int exitCode, string[] frame) = RunExport($"export 2026-11-02 2026-11-04 {target.Path}", "export 2026-11-02 2026-11-04 " + target.Path);

        Assert.Equal(0, exitCode);
        Assert.Contains(frame, l => l.Contains("No checkpoints between 2026-11-02 and 2026-11-04. Nothing was exported.", StringComparison.Ordinal));
        Assert.Empty(Listing(target.Path));

        string ansi = RunAnsiCommands($"export 2026-11-02 2026-11-04 {target.Path}");
        Assert.Matches(@"\x1b\[(90|38;5;8)mNo checkpoints between 2026-11-02 and 2026-11-04\. Nothing was exported\.", ansi);
    }

    // AC-10
    [Theory]
    [InlineData("export", "export: say the first day, the last day and the folder. Use export <from> <to> <folder> with dates as yyyy-MM-dd.")]
    [InlineData("ex 2026-11-02", "export: say the first day, the last day and the folder.")]
    [InlineData("export 2026-11-02 2026-11-04", "export: say the first day, the last day and the folder.")]
    [InlineData("export 2026-11-02 2026-11-04   ", "export: say the first day, the last day and the folder.")]
    [InlineData("export 2026-11-2 2026-11-04 /tmp", "export: 2026-11-2 is not a date. Use export <from> <to> <folder>")]
    [InlineData("export 2026-11-02 04.11.2026 /tmp", "export: 04.11.2026 is not a date.")]
    [InlineData("export [red]x 2026-11-04 /tmp", "export: [red]x is not a date.")]
    [InlineData("export 2026-11-05 2026-11-04 /tmp", "export: the first day 2026-11-05 is after the last day 2026-11-04.")]
    public void Export_BadInput_ShowsHint_AndLoopKeepsRunning(string command, string hint)
    {
        WriteReportDays();

        (int exitCode, string[] frame) = RunExport(command);
        (_, string[] next) = RunExport(command, "report 2026-11-02 2026-11-04");

        Assert.Equal(0, exitCode);
        Assert.StartsWith(hint, ExportHint(frame));
        Assert.DoesNotContain(frame, l => l.Contains("Exported", StringComparison.Ordinal));
        Assert.Contains(next, l => Regex.IsMatch(l, @"│ Total +│"));
    }

    // AC-10
    [Fact]
    public void Export_FolderThatCannotBeListed_ShowsRedHint()
    {
        UseSideLock();
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using TempFolder target = new TempFolder();
        UnixFileMode mode = File.GetUnixFileMode(data.Path);
        File.SetUnixFileMode(data.Path, UnixFileMode.None);
        try
        {
            try
            {
                Directory.EnumerateFileSystemEntries(data.Path).ToList();
                // Running as root: access can't be denied.
                return;
            }
            catch (UnauthorizedAccessException)
            {
            }

            (int exitCode, string[] frame) = RunExport($"export 2026-11-02 2026-11-04 {target.Path}");

            Assert.Equal(0, exitCode);
            Assert.StartsWith("export: the data folder cannot be listed", ExportHint(frame));
            Assert.Empty(Listing(target.Path));
        }
        finally
        {
            File.SetUnixFileMode(data.Path, mode);
        }
    }

    // AC-12
    [Fact]
    public void Help_ListsExport_WithAliasEx()
    {
        TestConsole console = new TestConsole().Interactive().EmitAnsiSequences();
        console.Profile.Width = 120;
        console.Input.PushTextWithEnter("help");
        PageDown(console, HelpPages);
        console.Input.PushTextWithEnter("quit");

        new App(console, FixedTimeProvider.At20261027(), new DayStore(data.Path)).Run([]);
        string plain = Regex.Replace(console.Output, @"\x1b\[[0-9;?]*[A-Za-z]", string.Empty);

        Assert.Matches(@"│ export +│ ex +│ Save the project report for a date range as CSV: <from> <to> <folder> +│", plain);
    }

    // Job 101 ------------------------------------------------------------------------------

    private const string DevOpsPat = "PAT-MARKER-5e2d";

    private const string NotAvailable =
        "Azure DevOps needs the macOS Keychain or Windows Credential Manager; not available on this system";

    /// <summary>Runs <paramref name="commands"/> (each with Enter) on a 40×140 console with the fakes and returns the exit code and the output.</summary>
    private (int ExitCode, string Output) RunDevOps(FakeTokenStore store, FakeDevOpsClient? client, params string[] commands) =>
        RunDevOps(store, client, c =>
        {
            foreach (string command in commands)
            {
                c.Input.PushTextWithEnter(command);
            }
        });

    private (int ExitCode, string Output) RunDevOps(FakeTokenStore store, FakeDevOpsClient? client, Action<TestConsole> input)
    {
        TestConsole console = new TestConsole().Interactive();
        console.Profile.Height = 40;
        console.Profile.Width = 140;
        input(console);
        App app = new App(console, FixedTimeProvider.At20261027(), new DayStore(data.Path),
            tokenStore: store, devOpsClient: client is null ? null : client.For);
        int exitCode = app.Run([]);
        Assert.DoesNotContain(DevOpsPat, console.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", console.Output, StringComparison.Ordinal);
        return (exitCode, console.Output);
    }

    private static string LastHint(string output) => string.Join(' ', LastLayout(output)[..^3].Reverse().Take(3).Reverse().Select(l => l.Trim()));

    private static string Booked(string time, string name, int workItem) =>
        $"{{\"kind\":\"checkpoint\",\"time\":\"{time}\",\"name\":\"{name}\",\"workItem\":{workItem},\"booked\":true}}";

    /// <summary>
    /// 2026-11-02: #12 Review 9:00-10:30 (unbooked), #7 Call 10:30-11:00, #12 Booked 11:00-12:00 (booked), Mail 12:00-13:00 (no work item);
    /// 2026-11-03: #12 Fix 9:00-9:45, #7 Open 10:00 (never ended); 2026-11-05 outside the range.
    /// </summary>
    private void WriteSyncDays()
    {
        WriteReportDay("2026-11-02", ReportCp("09:00", "Review", 12), ReportCp("10:30", "Call", 7),
            Booked("11:00", "Booked", 12), ReportCp("12:00", "Mail"), "{\"kind\":\"leaving\",\"time\":\"13:00\"}");
        WriteReportDay("2026-11-03", ReportCp("09:00", "Fix", 12), "{\"kind\":\"breakStart\",\"time\":\"09:45\"}",
            ReportCp("10:00", "Open", 7));
        WriteReportDay("2026-11-05", ReportCp("09:00", "Outside", 12), ReportLeave);
    }

    private Dictionary<string, string> DataFiles() =>
        Directory.GetFiles(data.Path).ToDictionary(f => Path.GetFileName(f), File.ReadAllText);

    // AC-1
    [Fact]
    public void DevOpsConnect_AsksForTheTokenMasked_StoresIt_AndConfirms()
    {
        FakeTokenStore store = new FakeTokenStore();

        (int exitCode, string output) = RunDevOps(store, null, "devops connect contoso", DevOpsPat, "quit");

        Assert.Equal(0, exitCode);
        Assert.Equal([("contoso", DevOpsPat)], store.Writes);
        Assert.Contains("Personal access token · Esc cancels", output);
        Assert.Contains(new string('•', DevOpsPat.Length), output);
        Assert.Contains("Connected to contoso", LastLayout(output)[^4]);
        Assert.Empty(Directory.GetFiles(data.Path));
    }

    // AC-1
    [Fact]
    public void DevOpsConnect_ReplacesAnEarlierToken()
    {
        FakeTokenStore store = new FakeTokenStore(stored: ("old-org", "old-token"));

        RunDevOps(store, null, "dv connect new-org", DevOpsPat, "quit");

        Assert.Equal(("new-org", DevOpsPat), store.Stored);
    }

    // AC-1: the token never reaches the history; Up recalls the command, not the token.
    [Fact]
    public void DevOpsConnect_TokenNeverEntersHistory()
    {
        FakeTokenStore store = new FakeTokenStore();

        (_, string output) = RunDevOps(store, null, c =>
        {
            c.Input.PushTextWithEnter("help");
            c.Input.PushTextWithEnter("devops connect contoso");
            c.Input.PushTextWithEnter(DevOpsPat);
            c.Input.PushKey(ConsoleKey.UpArrow);
            c.Input.PushKey(ConsoleKey.Enter);
            c.Input.PushKey(ConsoleKey.Escape);
            c.Input.PushTextWithEnter("quit");
        });

        // One Up skipped the connect line (kept out of history) and recalled help; never the token or dots.
        Assert.Single(store.Writes);
        Assert.Equal(1, Count(output, "Paste or type the personal access token for contoso."));
        Assert.Matches(@"arrive +│ a +│", string.Join('\n', LastLayout(output)));
        Assert.DoesNotContain("Unknown command", output);
    }

    // Rework: a devops line with an argument never stays in the in-memory history, so a token typed on it
    // (by mistake, or after a mistyped sub-command) cannot be brought back with Up.
    [Theory]
    [InlineData("devops connect contoso SECRET-ON-THE-LINE")]
    [InlineData("dv connect contoso SECRET-ON-THE-LINE")]
    [InlineData("/devops conect SECRET-ON-THE-LINE")]
    [InlineData("  DV   CONNECT contoso")]
    public void DevOpsLines_WithAnArgument_NeverEnterHistory(string line)
    {
        FakeTokenStore store = new FakeTokenStore();

        (_, string output) = RunDevOps(store, null, c =>
        {
            c.Input.PushTextWithEnter("devops");
            c.Input.PushTextWithEnter(line);
            c.Input.PushKey(ConsoleKey.Escape);
            c.Input.PushKey(ConsoleKey.UpArrow);
            c.Input.PushKey(ConsoleKey.Enter);
            c.Input.PushTextWithEnter("quit");
        });

        // Up recalled the plain devops line before it, so the last hint is the status again.
        Assert.Empty(store.Writes);
        Assert.Contains("Azure DevOps: not connected", LastHint(output));
    }

    // Other lines still enter history as before.
    [Fact]
    public void DevOpsStatusLine_StillEntersHistory()
    {
        FakeTokenStore store = new FakeTokenStore();

        (_, string output) = RunDevOps(store, null, c =>
        {
            c.Input.PushTextWithEnter("devops");
            c.Input.PushTextWithEnter("help");
            c.Input.PushKey(ConsoleKey.UpArrow);
            c.Input.PushKey(ConsoleKey.UpArrow);
            c.Input.PushKey(ConsoleKey.Enter);
            c.Input.PushTextWithEnter("quit");
        });

        Assert.Contains("Azure DevOps: not connected", LastHint(output));
    }

    // AC-1
    [Fact]
    public void DevOpsConnect_Esc_StoresNothing()
    {
        FakeTokenStore store = new FakeTokenStore();

        (_, string output) = RunDevOps(store, null, c =>
        {
            c.Input.PushTextWithEnter("devops connect contoso");
            c.Input.PushText(DevOpsPat);
            c.Input.PushKey(ConsoleKey.Escape);
            c.Input.PushTextWithEnter("quit");
        });

        Assert.Empty(store.Writes);
        Assert.Contains("Cancelled. Nothing was stored.", LastLayout(output)[^4]);
    }

    // AC-1
    [Theory]
    [InlineData(DevOpsPat + " more")]
    [InlineData("   ")]
    public void DevOpsConnect_InvalidToken_StoresNothing_AndIsNotEchoed(string typed)
    {
        FakeTokenStore store = new FakeTokenStore();

        (_, string output) = RunDevOps(store, null, "devops connect contoso", typed, "quit");

        Assert.Empty(store.Writes);
        Assert.Contains("devops: that is not a personal access token", LastHint(output));
        Assert.DoesNotContain(typed.Trim().Length == 0 ? DevOpsPat : typed, output);
    }

    // AC-1
    [Theory]
    [InlineData("devops connect bad/org", "devops: bad/org is not an organization name")]
    [InlineData("devops connect -x", "devops: -x is not an organization name")]
    [InlineData("devops connect", "devops: say the organization")]
    [InlineData("devops frobnicate", "devops: frobnicate is not something devops can do")]
    [InlineData("devops disconnect now", "devops disconnect takes no arguments.")]
    public void DevOps_BadInput_ShowsRedHint_WithoutAPrompt(string command, string hint)
    {
        FakeTokenStore store = new FakeTokenStore();

        (int exitCode, string output) = RunDevOps(store, null, command, "quit");

        Assert.Equal(0, exitCode);
        Assert.Contains(hint, LastHint(output));
        Assert.DoesNotContain("Personal access token", output);
        Assert.Equal(0, store.Calls);
    }

    // AC-2
    [Fact]
    public void DevOps_ShowsOrganizationAndTokenStored_NeverTheToken()
    {
        FakeTokenStore store = new FakeTokenStore(stored: ("contoso", DevOpsPat));

        (_, string output) = RunDevOps(store, null, "devops", "quit");

        Assert.Contains("Azure DevOps: contoso · token stored", LastLayout(output)[^4]);
    }

    // AC-2, AC-4: a stored item that fails validation counts as not connected.
    [Theory]
    [InlineData(null, null)]
    [InlineData("bad org", DevOpsPat)]
    [InlineData("contoso", "has space")]
    public void DevOps_NotConnected(string? organization, string? pat)
    {
        FakeTokenStore store = new FakeTokenStore(stored: organization is null ? null : (organization, pat!));

        (_, string output) = RunDevOps(store, null, "dv", "quit");

        Assert.Contains("Azure DevOps: not connected.", LastHint(output));
        Assert.DoesNotContain("has space", output);
    }

    // AC-2
    [Fact]
    public void DevOpsDisconnect_RemovesTheToken_ThenNothingToRemove()
    {
        FakeTokenStore store = new FakeTokenStore(stored: ("contoso", DevOpsPat));

        (_, string first) = RunDevOps(store, null, "devops disconnect", "quit");
        (_, string second) = RunDevOps(store, null, "devops disconnect", "quit");

        Assert.Contains("Disconnected", LastLayout(first)[^4]);
        Assert.Contains("Nothing to remove", LastLayout(second)[^4]);
        Assert.Null(store.Stored);
        Assert.Equal(2, store.Deletes);
    }

    // AC-3
    [Theory]
    [InlineData("devops connect contoso")]
    [InlineData("devops disconnect")]
    [InlineData("sync 2026-11-02 2026-11-04 send")]
    public void Unsupported_ShowsRedHint_AndWritesNothing(string command)
    {
        WriteSyncDays();
        Dictionary<string, string> before = DataFiles();
        FakeTokenStore store = new FakeTokenStore(supported: false);
        FakeDevOpsClient client = new FakeDevOpsClient();

        (_, string output) = RunDevOps(store, client, command, "quit");

        Assert.Contains(NotAvailable, LastHint(output));
        Assert.DoesNotContain("Personal access token", output);
        Assert.Equal(0, store.Calls);
        Assert.Empty(client.Credentials);
        Assert.Equal(before, DataFiles());
    }

    // AC-3
    [Fact]
    public void Unsupported_HintIsRed()
    {
        TestConsole console = new TestConsole().Interactive().EmitAnsiSequences();
        console.Profile.Width = 140;
        console.Input.PushTextWithEnter("devops connect contoso");
        console.Input.PushTextWithEnter("quit");

        new App(console, FixedTimeProvider.At20261027(), new DayStore(data.Path), tokenStore: new FakeTokenStore(supported: false)).Run([]);

        Assert.Matches(@"\x1b\[(31|91|38;5;9)mAzure DevOps needs the macOS Keychain", console.Output);
    }

    // Rework round 3: a store that would refuse the write (Times run through the shared dotnet host) says so
    // before the masked prompt, so the user never types the token; the next line is a command again.
    [Fact]
    public void DevOpsConnect_WriteRefused_ShowsRedHint_BeforeTheTokenPrompt()
    {
        FakeTokenStore store = new FakeTokenStore { WriteRefusal = MacKeychainTokenStore.SharedHostRefused };
        TestConsole console = new TestConsole().Interactive().EmitAnsiSequences();
        console.Profile.Width = 140;
        console.Profile.Height = 40;
        console.Input.PushTextWithEnter("devops connect contoso");
        console.Input.PushTextWithEnter("devops");
        console.Input.PushTextWithEnter("quit");

        new App(console, FixedTimeProvider.At20261027(), new DayStore(data.Path), tokenStore: store).Run([]);

        Assert.Empty(store.Writes);
        Assert.DoesNotContain("Paste or type the personal access token", console.Output);
        Assert.Matches(@"\x1b\[(31|91|38;5;9)mdevops: The token is stored only when Times runs as its own program", console.Output);
        Assert.Contains("Nothing was stored.", console.Output);
        Assert.Contains("Azure DevOps: not connected", console.Output);
    }

    // AC-3: without an injected store (tests, Linux) nothing is supported and no real store is touched.
    [Fact]
    public void NoStoreInjected_IsUnsupported()
    {
        (_, string output) = Run(c =>
        {
            c.Profile.Width = 140;
            c.Input.PushTextWithEnter("devops connect contoso");
            c.Input.PushTextWithEnter("quit");
        });

        Assert.Contains(NotAvailable, output);
    }

    // AC-3: the preview still works.
    [Fact]
    public void Unsupported_PreviewStillWorks()
    {
        WriteSyncDays();
        FakeTokenStore store = new FakeTokenStore(supported: false);

        (_, string output) = RunDevOps(store, null, "sync 2026-11-02 2026-11-04", "quit");

        Assert.Contains(LastLayout(output), l => Regex.IsMatch(l, @"│ #12 +│"));
        Assert.Equal(0, store.Calls);
    }

    // AC-4
    [Theory]
    [InlineData("devops")]
    [InlineData("devops disconnect")]
    [InlineData("sync 2026-11-02 2026-11-04 send")]
    public void StoreError_ShowsRedHint_AndLoopKeepsRunning(string command)
    {
        WriteSyncDays();
        FakeTokenStore store = new FakeTokenStore { Throws = true };
        FakeDevOpsClient client = new FakeDevOpsClient();

        (int exitCode, string output) = RunDevOps(store, client, command, "report 2026-11-02 2026-11-04", command, "quit");

        Assert.Equal(0, exitCode);
        Assert.Contains(FakeTokenStore.Failure, LastHint(output));
        Assert.Contains("│ Total", output);
        Assert.Empty(client.Credentials);
    }

    // AC-4
    [Fact]
    public void StoreError_OnWrite_ShowsRedHint()
    {
        FakeTokenStore store = new FakeTokenStore { Throws = true };

        (_, string output) = RunDevOps(store, null, "devops connect contoso", DevOpsPat, "quit");

        Assert.Contains($"devops: {FakeTokenStore.Failure} Nothing was stored.", LastHint(output));
    }

    // AC-4
    [Theory]
    [InlineData(null, null)]
    [InlineData("bad org", DevOpsPat)]
    [InlineData("contoso", "two words")]
    public void Send_NotConnected_ShowsRedHint_AndMakesNoRequest(string? organization, string? pat)
    {
        WriteSyncDays();
        Dictionary<string, string> before = DataFiles();
        FakeTokenStore store = new FakeTokenStore(stored: organization is null ? null : (organization, pat!));
        FakeDevOpsClient client = new FakeDevOpsClient();

        (_, string output) = RunDevOps(store, client, "sync 2026-11-02 2026-11-04 send", "quit");

        Assert.Contains("sync: not connected to Azure DevOps.", LastHint(output));
        Assert.Empty(client.Credentials);
        Assert.Empty(client.Gets);
        Assert.Equal(before, DataFiles());
    }

    // AC-5
    [Theory]
    [InlineData("sync", "sync: say the first and the last day.")]
    [InlineData("sync 2026-11-02", "sync: say the first and the last day.")]
    [InlineData("sync 2026-11-02 2026-11-04 send now", "sync: say the first and the last day.")]
    [InlineData("sync 2026-02-30 2026-03-01", "sync: 2026-02-30 is not a date.")]
    [InlineData("sync [red]x 2026-11-04", "sync: [red]x is not a date.")]
    [InlineData("sync 2026-11-05 2026-11-04", "sync: the first day 2026-11-05 is after the last day 2026-11-04.")]
    [InlineData("sync 2026-11-02 2026-11-04 Send", "sync: Send is not send.")]
    public void Sync_BadInput_ShowsRedHint(string command, string hint)
    {
        FakeTokenStore store = new FakeTokenStore(stored: ("contoso", DevOpsPat));
        FakeDevOpsClient client = new FakeDevOpsClient();

        (int exitCode, string output) = RunDevOps(store, client, command, "quit");

        Assert.Equal(0, exitCode);
        Assert.Contains(hint, LastHint(output));
        Assert.Equal(0, store.Calls);
        Assert.Empty(client.Gets);
    }

    // AC-5, AC-6
    [Fact]
    public void SyncPreview_ShowsOneRowPerWorkItem_WithoutStoreNetworkOrWrites()
    {
        WriteSyncDays();
        File.WriteAllText(ProjectsFile, "{\"projects\":[{\"workItem\":12,\"title\":\"Times\"}]}");
        Dictionary<string, string> before = DataFiles();
        FakeTokenStore store = new FakeTokenStore(stored: ("contoso", DevOpsPat));
        FakeDevOpsClient client = new FakeDevOpsClient();

        (_, string output) = RunDevOps(store, client, "sy 2026-11-02 2026-11-04", "quit");

        string[] frame = LastLayout(output);
        Assert.Contains(frame, l => Regex.IsMatch(l, @"│ Work item +│ Title +│ Time +│ +Hours │ Checkpoints │"));
        // #7: Call 0:30 (Open on 11-03 never ended); #12: Review 1:30 + Fix 0:45 (booked and outside skipped).
        int seven = Array.FindIndex(frame, l => Regex.IsMatch(l, @"│ #7 +│ Call +│ 0:30 +│ +0\.50 │ +1 │"));
        int twelve = Array.FindIndex(frame, l => Regex.IsMatch(l, @"│ #12 +│ Times +│ 2:15 +│ +2\.25 │ +2 │"));
        Assert.True(seven > 0 && twelve > seven, string.Join("\n", frame));
        Assert.DoesNotContain(frame, l => l.Contains("Mail", StringComparison.Ordinal));
        Assert.Contains(frame, l => l.Contains("Type sync 2026-11-02 2026-11-04 send to write these hours to Azure DevOps", StringComparison.Ordinal));
        Assert.Equal(0, store.Calls);
        Assert.Empty(client.Credentials);
        Assert.Equal(before, DataFiles());
    }

    // AC-5: a running checkpoint on today is not sent; an ended one on today is.
    [Fact]
    public void SyncPreview_RunningCheckpointToday_IsSkipped()
    {
        WriteReportDay("2026-10-27", ReportCp("09:00", "Done", 3), ReportCp("10:00", "Running", 4));

        (_, string output) = RunDevOps(new FakeTokenStore(), null, "sync 2026-10-27 2026-10-27", "quit");

        string[] frame = LastLayout(output);
        Assert.Contains(frame, l => Regex.IsMatch(l, @"│ #3 +│ Done +│ 1:00 +│"));
        Assert.DoesNotContain(frame, l => l.Contains("│ #4 ", StringComparison.Ordinal));
    }

    // AC-6
    [Fact]
    public void SyncPreview_NothingToSend_ShowsGreyNote()
    {
        WriteReportDay("2026-11-02", Booked("09:00", "Done", 3), ReportLeave);

        (_, string output) = RunDevOps(new FakeTokenStore(), null, "sync 2026-11-02 2026-11-04", "quit");

        Assert.Contains("Nothing to send: no unbooked, ended checkpoints with a work item between 2026-11-02 and 2026-11-04.", output);
        Assert.DoesNotContain("send to write these hours", output);
    }

    // AC-7, AC-8
    [Fact]
    public void SyncSend_AddsHoursWithRevTest_MarksBooked_AndASecondSendSendsNothing()
    {
        WriteSyncDays();
        FakeTokenStore store = new FakeTokenStore(stored: ("contoso", DevOpsPat));
        FakeDevOpsClient client = new FakeDevOpsClient();
        client.Items[12] = (1.1, 5);

        (_, string output) = RunDevOps(store, client, "sync 2026-11-02 2026-11-04 send", "quit");

        Assert.Equal(1, store.Reads);
        DevOpsCredential credential = Assert.Single(client.Credentials);
        Assert.Equal("contoso", credential.Organization);
        Assert.Equal([7, 12], client.Gets);
        // #7 has no Completed Work yet (0, rev 1); #12 had 1.1 at rev 5: round(1.1 + 2.25, 2) = 3.35.
        Assert.Equal([(7, 1, 0.5), (12, 5, 3.35)], client.Sets);
        string[] frame = LastLayout(output);
        Assert.Contains(frame, l => Regex.IsMatch(l, @"│ #7 +│ Call +│ +0\.50 │ sent 0\.00 → 0\.50 +│"));
        Assert.Contains(frame, l => Regex.IsMatch(l, @"│ #12 +│ Review, Fix +│ +2\.25 │ sent 1\.10 → 3\.35 +│"));
        Assert.Contains("Sent 2 work items to Azure DevOps.", LastHint(output));

        DayStore days = new DayStore(data.Path);
        DayRecord first = days.Load(new DateOnly(2026, 11, 2));
        Assert.Equal([true, true, true, false], first.Entries.Where(e => e.Kind == EntryKind.Checkpoint).Select(e => e.Booked));
        DayRecord second = days.Load(new DateOnly(2026, 11, 3));
        Assert.Equal([true, false], second.Entries.Where(e => e.Kind == EntryKind.Checkpoint).Select(e => e.Booked));
        Assert.Contains("\"name\":\"Outside\",\"workItem\":12}", ReadDay("2026-11-05"));

        // AC-8: nothing is sent twice.
        (_, string again) = RunDevOps(store, client, "sync 2026-11-02 2026-11-04 send", "quit");

        Assert.Equal(2, client.Sets.Count);
        Assert.Contains("Nothing to send", again);
    }

    // AC-8
    [Fact]
    public void SyncSend_OneFailure_LeavesItsCheckpointsUnbooked_AndTheRestStillRun()
    {
        WriteSyncDays();
        FakeTokenStore store = new FakeTokenStore(stored: ("contoso", DevOpsPat));
        FakeDevOpsClient client = new FakeDevOpsClient();
        client.SetFailures[7] = DevOpsException.Changed;

        (_, string output) = RunDevOps(store, client, "sync 2026-11-02 2026-11-04 send", "quit");

        string[] frame = LastLayout(output);
        Assert.Contains(frame, l => Regex.IsMatch(l, @"│ #7 +│ Call +│ +0\.50 │ failed: the work item changed while syncing; run sync again +│"));
        Assert.Contains(frame, l => Regex.IsMatch(l, @"│ #12 +│ Review, Fix +│ +2\.25 │ sent 0\.00 → 2\.25 +│"));
        Assert.Contains("sync: 1 of 2 work items failed", LastHint(output));
        DayRecord day = new DayStore(data.Path).Load(new DateOnly(2026, 11, 2));
        Assert.False(day.Entries.Single(e => e.Name == "Call").Booked);
        Assert.True(day.Entries.Single(e => e.Name == "Review").Booked);

        // Only #7 is left for the next send.
        client.SetFailures.Clear();
        RunDevOps(store, client, "sync 2026-11-02 2026-11-04 send", "quit");
        Assert.Equal([7, 12, 7], client.Sets.Select(s => s.Id));
    }

    // AC-8, AC-10: a failed GET never PATCHes.
    [Fact]
    public void SyncSend_FailedRead_NeverWrites()
    {
        WriteSyncDays();
        FakeDevOpsClient client = new FakeDevOpsClient();
        client.GetFailures[7] = DevOpsException.Refused;
        client.GetFailures[12] = DevOpsException.TimedOut;

        (_, string output) = RunDevOps(new FakeTokenStore(stored: ("contoso", DevOpsPat)), client, "sync 2026-11-02 2026-11-04 send", "quit");

        Assert.Empty(client.Sets);
        Assert.Contains("failed: Azure DevOps refused the token", output);
        Assert.Contains("failed: the request timed out", output);
        Assert.Contains("sync: 2 of 2 work items failed", LastHint(output));
        Assert.DoesNotContain("\"booked\":true}", ReadDay("2026-11-03"));
    }

    // AC-9
    [Fact]
    public void SyncSend_SaveFailsAfterPatch_NamesWorkItemAndDay_AndSaysToBookByHand()
    {
        WriteSyncDays();
        FakeDevOpsClient client = new FakeDevOpsClient();
        // After #12 is sent, its 2026-11-03 day file becomes unreadable, so it cannot be marked.
        client.AfterSet = id =>
        {
            if (id == 12)
            {
                File.WriteAllText(DayFile("2026-11-03"), "broken");
            }
        };

        (_, string output) = RunDevOps(new FakeTokenStore(stored: ("contoso", DevOpsPat)), client, "sync 2026-11-02 2026-11-04 send", "quit");

        Assert.Contains("#12: the hours were sent, but 2026-11-03 could not be saved. Run booked 1 on 2026-11-03 by hand.", output);
        Assert.Contains("and some days could not be saved", LastHint(output));
        Assert.Equal("broken", ReadDay("2026-11-03"));
        // The other day of #12 was still marked.
        Assert.True(new DayStore(data.Path).Load(new DateOnly(2026, 11, 2)).Entries.Single(e => e.Name == "Review").Booked);
    }

    // AC-9
    [Fact]
    public void Sync_UnreadableDayFiles_AreSkippedCounted_AndNeverWritten()
    {
        WriteSyncDays();
        File.WriteAllText(DayFile("2026-11-04"), "{ broken");
        FakeDevOpsClient client = new FakeDevOpsClient();

        (_, string preview) = RunDevOps(new FakeTokenStore(stored: ("contoso", DevOpsPat)), client, "sync 2026-11-02 2026-11-04", "quit");
        (_, string sent) = RunDevOps(new FakeTokenStore(stored: ("contoso", DevOpsPat)), client, "sync 2026-11-02 2026-11-04 send", "quit");

        Assert.Contains("! 1 day file skipped", preview);
        Assert.Contains("! 1 day file skipped", sent);
        Assert.Equal("{ broken", ReadDay("2026-11-04"));
        Assert.Equal(2, client.Sets.Count);
    }

    // AC-11
    [Fact]
    public void Help_ListsDevOpsAndSync_WithAliases()
    {
        TestConsole console = new TestConsole().Interactive();
        console.Profile.Width = 140;
        console.Profile.Height = 80;
        console.Input.PushTextWithEnter("help");
        console.Input.PushTextWithEnter("quit");

        new App(console, FixedTimeProvider.At20261027(), new DayStore(data.Path)).Run([]);

        Assert.Matches(@"│ devops +│ dv +│ Show or set the Azure DevOps link", console.Output);
        Assert.Contains("import", console.Output, StringComparison.Ordinal);
        Assert.Matches(@"│ sync +│ sy +│ Send unbooked hours to Azure DevOps: <from> <to> \[send\]", console.Output);
    }

    // Nit (review-security 1): a token typed after the organization is never echoed.
    [Fact]
    public void DevOpsConnect_ExtraWord_IsNotEchoed()
    {
        FakeTokenStore store = new FakeTokenStore();

        // The typed line itself shows the word while typing, so this uses another marker than DevOpsPat.
        (_, string output) = RunDevOps(store, null, "devops connect contoso typed-token-77", "quit");

        Assert.Contains("connect takes only the organization; the token is asked for next.", LastHint(output));
        Assert.DoesNotContain(LastLayout(output), l => l.Contains("typed-token-77", StringComparison.Ordinal));
        Assert.Equal(0, store.Calls);
    }

    // Nit (review-security 4): a non-finite sum is never sent.
    [Fact]
    public void SyncSend_NonFiniteSum_FailsThatItem()
    {
        WriteSyncDays();
        FakeDevOpsClient client = new FakeDevOpsClient();
        client.Items[12] = (double.PositiveInfinity, 2);

        (_, string output) = RunDevOps(new FakeTokenStore(stored: ("contoso", DevOpsPat)), client, "sync 2026-11-02 2026-11-04 send", "quit");

        Assert.Equal([7], client.Sets.Select(s => s.Id));
        Assert.Contains("sync: 1 of 2 work items failed", LastHint(output));
    }

    // Nit (review-security 3): the client is disposed after a send.
    [Fact]
    public void SyncSend_DisposesTheClient()
    {
        WriteSyncDays();
        FakeDevOpsClient client = new FakeDevOpsClient();

        RunDevOps(new FakeTokenStore(stored: ("contoso", DevOpsPat)), client, "sync 2026-11-02 2026-11-04 send", "quit");

        Assert.Equal(1, client.Disposed);
    }

    // Manage the quicklist (job 102) ------------------------------------------------------------

    private const string RenameFormatHint = "renameproject: the title must not contain invisible formatting or broken characters. Retype it without the hidden characters.";

    private static readonly DateTime LongAgo = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // AC-1 (102)
    [Theory]
    [InlineData("renameproject 2 New title")]
    [InlineData("rn 2 New title")]
    [InlineData("/renameproject   2    New title   ")]
    public void RenameProject_ReplacesTitle_KeepsWorkItemAndPosition(string command)
    {
        WriteProjects(3);

        (int exitCode, string output) = RunCommands(command, "quit");

        Assert.Equal(0, exitCode);
        Assert.Equal([new Project(101, "Project 1"), new Project(102, "New title"), new Project(103, "Project 3")], ReadProjects().Projects);
        string[] frame = LastLayout(output);
        AssertLayout(frame);
        Assert.Contains(frame[..^4], l => l.Contains("Renamed project 2: #102 New title", StringComparison.Ordinal));
        Assert.Equal(string.Empty, frame[^4].Trim());
    }

    // AC-1 (102)
    [Fact]
    public void RenameProject_MarkupTitle_IsEscaped_AndNumberIsSteelBlue()
    {
        WriteProjects(2);

        string output = RunAnsi("renameproject 2 [red]Fix[/]");

        Assert.Contains("#102 [red]Fix[/]", output);
        Assert.Matches(@"Renamed project \x1b\[38;5;75m2\x1b\[0m: #102 \[red\]Fix\[/\]", output);
        Assert.Equal("[red]Fix[/]", ReadProjects().Projects[1].Title);
    }

    // AC-2 (102)
    [Theory]
    [InlineData("renameproject 2", "renameproject: the title is missing. Use renameproject <number> <title>.")]
    [InlineData("rn 2   ", "renameproject: the title is missing. Use renameproject <number> <title>.")]
    [InlineData("renameproject 2 a\\u200Bb", RenameFormatHint)]
    [InlineData("renameproject 2 a\\u202Eb", RenameFormatHint)]
    [InlineData("renameproject 2 a\\u3164b", RenameFormatHint)]
    [InlineData("renameproject 2 a\\uD800b", RenameFormatHint)]
    public void RenameProject_InvalidTitle_ShowsAddProjectHint_AndWritesNothing(string escaped, string expected)
    {
        WriteProjects(2);
        File.SetLastWriteTimeUtc(ProjectsFile, LongAgo);
        byte[] before = File.ReadAllBytes(ProjectsFile);

        (int exitCode, string output) = TypeEscaped(escaped);

        Assert.Equal(0, exitCode);
        AssertOnHintRow(LastLayout(output), expected);
        Assert.Equal(before, File.ReadAllBytes(ProjectsFile));
        Assert.Equal(LongAgo, File.GetLastWriteTimeUtc(ProjectsFile));
    }

    // AC-2 (102)
    [Fact]
    public void RenameProject_TitleTooLong_ShowsHint_AndWritesNothing()
    {
        WriteProjects(2);
        byte[] before = File.ReadAllBytes(ProjectsFile);

        (_, string output) = RunWide(200, "renameproject 1 " + new string('x', 201), "quit");

        AssertOnHintRow(LastLayout(output), "renameproject: the title is longer than 200 characters.");
        Assert.Equal(before, File.ReadAllBytes(ProjectsFile));

        RunCommands("renameproject 1 " + new string('x', 200), "quit");
        Assert.Equal(200, ReadProjects().Projects[0].Title.Length);
    }

    // AC-2 (102): the line editor drops C0/C1 controls as they are typed, so a line separator is what reaches the handler.
    [Fact]
    public void RenameProject_TitleWithLineSeparator_ShowsHint_AndWritesNothing()
    {
        WriteProjects(2);
        byte[] before = File.ReadAllBytes(ProjectsFile);

        (_, string output) = Run(c =>
        {
            c.Profile.Width = 200;
            c.Input.PushText("renameproject 1 a");
            c.Input.PushKey(new ConsoleKeyInfo('\u2028', ConsoleKey.NoName, false, false, false));
            c.Input.PushTextWithEnter("b");
            c.Input.PushTextWithEnter("quit");
        });

        AssertOnHintRow(LastLayout(output), "renameproject: the title must not contain control characters. Retype it without the hidden characters.");
        Assert.Equal(before, File.ReadAllBytes(ProjectsFile));
        Assert.Matches(@"\x1b\[(31|91|38;5;9)mrenameproject: the title is missing", RunAnsi("renameproject 1"));
    }

    // AC-3 (102)
    [Theory]
    [InlineData("removeproject 2")]
    [InlineData("xp 2")]
    [InlineData("removeproject  2 ")]
    public void RemoveProject_DropsProject_AndLaterOnesMoveUp(string command)
    {
        WriteProjects(3);

        (int exitCode, string output) = RunCommands(command, "quit");

        Assert.Equal(0, exitCode);
        Assert.Equal([new Project(101, "Project 1"), new Project(103, "Project 3")], ReadProjects().Projects);
        string[] frame = LastLayout(output);
        AssertLayout(frame);
        Assert.Contains(frame[..^4], l => l.Contains("Removed project 2: #102 Project 2", StringComparison.Ordinal));
    }

    // AC-3 (102)
    [Fact]
    public void RemoveProject_MarkupTitle_IsEscaped()
    {
        File.WriteAllText(ProjectsFile, "{\"projects\":[{\"workItem\":7,\"title\":\"[bold]x\"}]}");

        string output = RunAnsi("removeproject 1");

        Assert.Contains("#7 [bold]x", output);
        Assert.Equal("{\"projects\":[]}", File.ReadAllText(ProjectsFile));
    }

    // AC-4 (102)
    [Fact]
    public void RemoveProject_ChangesNoDayFile_AndDayViewKeepsWorkItem()
    {
        WriteProjects(3);
        RunCommands("checkpoint #2 Work", "quit");
        string day = DayFile("2026-10-27");
        File.SetLastWriteTimeUtc(day, LongAgo);
        byte[] before = File.ReadAllBytes(day);
        Assert.Contains("\"workItem\":102", ReadDay("2026-10-27"));

        (int exitCode, string output) = RunCommands("removeproject 2", "quit");

        Assert.Equal(0, exitCode);
        Assert.Equal(before, File.ReadAllBytes(day));
        Assert.Equal(LongAgo, File.GetLastWriteTimeUtc(day));
        Assert.Null(ReadProjects().NumberOf(102));
        Assert.Matches(@"1 +CP +\d\d:\d\d +Work #102", LastFrame(output));
        Assert.Equal(["2026-10-27.json", "projects.json"], Directory.EnumerateFiles(data.Path).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    // AC-5 (102)
    [Theory]
    [InlineData("moveproject 3 1")]
    [InlineData("mp 3 1")]
    [InlineData("moveproject   3   1  ")]
    public void MoveProject_MovesToNewNumber_AndShiftsOthers(string command)
    {
        WriteProjects(4);

        (int exitCode, string output) = RunCommands(command, "quit");

        Assert.Equal(0, exitCode);
        Assert.Equal(
            [new Project(103, "Project 3"), new Project(101, "Project 1"), new Project(102, "Project 2"), new Project(104, "Project 4")],
            ReadProjects().Projects);
        string[] frame = LastLayout(output);
        AssertLayout(frame);
        Assert.Contains(frame[..^4], l => l.Contains("Moved project #103 to number 1", StringComparison.Ordinal));
    }

    // AC-5 (102)
    [Fact]
    public void MoveProject_Down_ShiftsOthersUp()
    {
        WriteProjects(3);

        (_, string output) = RunCommands("mp 1 3", "quit");

        Assert.Equal([102, 103, 101], ReadProjects().Projects.Select(p => p.WorkItem));
        Assert.Contains("Moved project #101 to number 3", output);
    }

    // AC-6 (102)
    [Theory]
    [InlineData("moveproject 2 2")]
    [InlineData("mp 1 1")]
    public void MoveProject_SameNumber_ShowsGreyNote_AndDoesNotWrite(string command)
    {
        WriteProjects(2);
        File.SetLastWriteTimeUtc(ProjectsFile, LongAgo);
        byte[] before = File.ReadAllBytes(ProjectsFile);
        string number = command[^1..];
        string workItem = "10" + number;

        (int exitCode, string output) = RunWide(200, command, "quit");

        Assert.Equal(0, exitCode);
        AssertOnHintRow(LastLayout(output), $"Project #{workItem} is already number {number}. Nothing was moved.");
        Assert.Equal(before, File.ReadAllBytes(ProjectsFile));
        Assert.Equal(LongAgo, File.GetLastWriteTimeUtc(ProjectsFile));
        Assert.Matches(@"\x1b\[(90|38;5;8)mProject #10\d is already number", RunAnsi(command));
    }

    // AC-7 (102)
    [Theory]
    [InlineData("renameproject", "renameproject: say the project number and the new title. Use renameproject <number> <title>.")]
    [InlineData("rn", "renameproject: say the project number and the new title. Use renameproject <number> <title>.")]
    [InlineData("renameproject abc New", "renameproject: abc is not a project number. Use renameproject <number> <title>.")]
    [InlineData("renameproject 0 New", "renameproject: 0 is not a project number. Use renameproject <number> <title>.")]
    [InlineData("renameproject -1 New", "renameproject: -1 is not a project number.")]
    [InlineData("renameproject +1 New", "renameproject: +1 is not a project number.")]
    [InlineData("renameproject 1.0 New", "renameproject: 1.0 is not a project number.")]
    [InlineData("renameproject 2147483648 New", "renameproject: 2147483648 is not a project number.")]
    [InlineData("renameproject [red]1[/] New", "renameproject: [red]1[/] is not a project number.")]
    [InlineData("renameproject 3 New", "renameproject: there is no project 3; the quicklist has 2. Use renameproject <number> <title>.")]
    [InlineData("removeproject", "removeproject: say which project to remove. Use removeproject <number>. It changes only the quicklist; booked times stay.")]
    [InlineData("xp", "removeproject: say which project to remove. Use removeproject <number>.")]
    [InlineData("removeproject x", "removeproject: x is not a project number. Use removeproject <number>.")]
    [InlineData("removeproject 0", "removeproject: 0 is not a project number.")]
    [InlineData("removeproject 1 2", "removeproject: 1 2 is not a project number.")]
    [InlineData("removeproject +1", "removeproject: +1 is not a project number.")]
    [InlineData("removeproject 3", "removeproject: there is no project 3; the quicklist has 2. Use removeproject <number>.")]
    [InlineData("removeproject 99999999999", "removeproject: 99999999999 is not a project number.")]
    [InlineData("moveproject", "moveproject: say the project number and its new number. Use moveproject <number> <new number>.")]
    [InlineData("mp", "moveproject: say the project number and its new number. Use moveproject <number> <new number>.")]
    [InlineData("moveproject 1", "moveproject: the new number is missing. Use moveproject <number> <new number>.")]
    [InlineData("moveproject x 1", "moveproject: x is not a project number. Use moveproject <number> <new number>.")]
    [InlineData("moveproject 1 x", "moveproject: x is not a project number. Use moveproject <number> <new number>.")]
    [InlineData("moveproject 1 0", "moveproject: 0 is not a project number.")]
    [InlineData("moveproject 1 -2", "moveproject: -2 is not a project number.")]
    [InlineData("moveproject 1 2 3", "moveproject: say only the project number and its new number. Use moveproject <number> <new number>.")]
    [InlineData("moveproject 3 1", "moveproject: there is no project 3; the quicklist has 2. Use moveproject <number> <new number>.")]
    [InlineData("moveproject 1 3", "moveproject: there is no project 3; the quicklist has 2. Use moveproject <number> <new number>.")]
    [InlineData("moveproject 3 3", "moveproject: there is no project 3; the quicklist has 2.")]
    public void ManageProjects_InvalidArguments_ShowRedHint_AndLeaveFileUnchanged(string command, string expected)
    {
        WriteProjects(2);
        File.SetLastWriteTimeUtc(ProjectsFile, LongAgo);
        byte[] before = File.ReadAllBytes(ProjectsFile);

        (int exitCode, string output) = RunWide(200, command, "quit");

        Assert.Equal(0, exitCode);
        AssertOnHintRow(LastLayout(output), expected);
        Assert.Equal(before, File.ReadAllBytes(ProjectsFile));
        Assert.Equal(LongAgo, File.GetLastWriteTimeUtc(ProjectsFile));
        string name = expected[..expected.IndexOf(':')];
        Assert.Matches(@"\x1b\[(31|91|38;5;9)m" + name + ": ", RunAnsi(command));
    }

    // AC-7 (102)
    [Theory]
    [InlineData("renameproject 1 New", "renameproject: the quicklist is empty, so there is no project 1. Use renameproject <number> <title>.")]
    [InlineData("removeproject 1", "removeproject: the quicklist is empty, so there is no project 1. Use removeproject <number>.")]
    [InlineData("moveproject 1 1", "moveproject: the quicklist is empty, so there is no project 1. Use moveproject <number> <new number>.")]
    public void ManageProjects_EmptyList_ShowsHint_AndCreatesOrChangesNothing(string command, string expected)
    {
        (_, string absent) = RunWide(200, command, "quit");

        AssertOnHintRow(LastLayout(absent), expected);
        Assert.Empty(Directory.EnumerateFileSystemEntries(data.Path));

        File.WriteAllText(ProjectsFile, "{\"projects\":[]}");
        (_, string empty) = RunWide(200, command, "quit");

        AssertOnHintRow(LastLayout(empty), expected);
        Assert.Equal("{\"projects\":[]}", File.ReadAllText(ProjectsFile));
    }

    // AC-8 (102)
    [Theory]
    [InlineData("{not json")]
    [InlineData("{\"projects\":[{\"workItem\":1,\"title\":\"A\"},{\"workItem\":1,\"title\":\"B\"}]}")]
    [InlineData("{\"projects\":[{\"workItem\":1,\"title\":\"abc\\u200bdef\"}]}")]
    public void ManageProjects_UnreadableFile_ShowsNextStep_AndLeavesBytesUnchanged(string contents)
    {
        File.WriteAllText(ProjectsFile, contents);
        byte[] before = File.ReadAllBytes(ProjectsFile);

        (_, string renamed) = RunWide(200, "renameproject 1 New", "quit");
        (_, string removed) = RunWide(200, "removeproject 1", "quit");
        (_, string moved) = RunWide(200, "moveproject 1 1", "quit");

        AssertOnHintRow(LastLayout(renamed), "renameproject: the project list cannot be read: projects.json is not valid. Nothing was renamed. " + App.UnreadableProjectsNextStep);
        AssertOnHintRow(LastLayout(removed), "removeproject: the project list cannot be read: projects.json is not valid. Nothing was removed. " + App.UnreadableProjectsNextStep);
        AssertOnHintRow(LastLayout(moved), "moveproject: the project list cannot be read: projects.json is not valid. Nothing was moved. " + App.UnreadableProjectsNextStep);
        Assert.Equal(before, File.ReadAllBytes(ProjectsFile));
        Assert.Matches(@"\x1b\[(31|91|38;5;9)mremoveproject: the project list cannot be read", RunAnsi("removeproject 1"));
    }

    // AC-8 (102)
    [Fact]
    public void ManageProjects_TooLargeFile_ShowsNextStep_AndLeavesBytesUnchanged()
    {
        File.WriteAllText(ProjectsFile, new string(' ', (int)ProjectStore.MaxFileBytes + 1) + "{\"projects\":[]}");
        byte[] before = File.ReadAllBytes(ProjectsFile);

        (_, string output) = RunWide(200, "renameproject 1 New", "removeproject 1", "moveproject 1 1", "quit");

        Assert.Contains("renameproject: the project list cannot be read: projects.json is too large. Nothing was renamed. " + App.UnreadableProjectsNextStep, output);
        Assert.Contains("removeproject: the project list cannot be read: projects.json is too large. Nothing was removed. " + App.UnreadableProjectsNextStep, output);
        AssertOnHintRow(LastLayout(output), "moveproject: the project list cannot be read: projects.json is too large. Nothing was moved. " + App.UnreadableProjectsNextStep);
        Assert.Equal(before, File.ReadAllBytes(ProjectsFile));
    }

    // AC-8 (102)
    [Fact]
    public void ManageProjects_FifoFile_ShowsNextStep_AndChangesNothing()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        SpecialFiles.MakeFifo(ProjectsFile);
        string output = string.Empty;

        Assert.Null(SpecialFiles.RunWithin(TimeSpan.FromSeconds(10), () =>
            (_, output) = RunWide(200, "renameproject 1 New", "removeproject 1", "moveproject 1 2", "quit")));

        Assert.Contains("renameproject: the project list cannot be read: projects.json is not a regular file. Nothing was renamed. " + App.UnreadableProjectsNextStep, output);
        Assert.Contains("removeproject: the project list cannot be read: projects.json is not a regular file. Nothing was removed. " + App.UnreadableProjectsNextStep, output);
        AssertOnHintRow(LastLayout(output), "moveproject: the project list cannot be read: projects.json is not a regular file. Nothing was moved. " + App.UnreadableProjectsNextStep);
        Assert.DoesNotContain("   at ", output);
        Assert.True(SpecialFiles.IsFifo(ProjectsFile));
        Assert.Equal([ProjectsFile], Directory.EnumerateFileSystemEntries(data.Path));
    }

    // AC-9 (102)
    [Fact]
    public void ManageProjects_QuicklistPanelShowsNewTitlesAndOrder()
    {
        WriteProjects(3);

        (_, string renamed) = RunCommands("rn 1 Alpha", "quit");
        Assert.Equal("│ 1 #101 Alpha", PanelRows(LastLayout(renamed))[1].TrimEnd('│', ' '));

        (_, string moved) = RunCommands("mp 3 1", "quit");
        string[] panel = PanelRows(LastLayout(moved));
        Assert.Equal("│ 1 #103 Project 3", panel[1].TrimEnd('│', ' '));
        Assert.Equal("│ 2 #101 Alpha", panel[2].TrimEnd('│', ' '));
        Assert.Equal("│ 3 #102 Project 2", panel[3].TrimEnd('│', ' '));

        (_, string removed) = RunCommands("xp 2", "quit");
        panel = PanelRows(LastLayout(removed));
        Assert.Equal(4, panel.Length);
        Assert.Equal("│ 1 #103 Project 3", panel[1].TrimEnd('│', ' '));
        Assert.Equal("│ 2 #102 Project 2", panel[2].TrimEnd('│', ' '));
    }

    // AC-10 (102)
    [Fact]
    public void Help_ListsManageProjectCommands_WithAliases()
    {
        (_, string output) = Run(c =>
        {
            c.Profile.Width = 140;
            c.Input.PushTextWithEnter("help");
            PageDown(c, HelpPages);
            c.Input.PushTextWithEnter("quit");
        });

        Assert.Matches(@"renameproject +│ rn +│ Rename a project on the quicklist: <number> <title>", output);
        Assert.Matches(@"removeproject +│ xp +│ Remove a project from the quicklist by its number", output);
        Assert.Matches(@"moveproject +│ mp +│ Move a project to another number on the quicklist: <number> <new number>", output);
        Assert.Matches(@"remove +│ r +│ Remove an entry from this day by its number", output);
    }

    // AC-10 (102)
    [Theory]
    [InlineData("remove 1")]
    [InlineData("r 1")]
    public void Remove_StillRemovesADayEntry_NotAProject(string command)
    {
        WriteProjects(2);
        byte[] before = File.ReadAllBytes(ProjectsFile);
        RunCommands("arrive 8:00", "checkpoint 9:00 #1 Work", "quit");
        Assert.Equal(2, LoadDay().Entries.Count);

        RunCommands(command, "quit");

        Assert.Single(LoadDay().Entries);
        Assert.Equal(before, File.ReadAllBytes(ProjectsFile));
        CommandRegistry registry = CommandRegistry.CreateDefault();
        _ = new App(new TestConsole(), FixedTimeProvider.At20261027(), new DayStore(data.Path), registry);
        Assert.True(registry.TryGet("r", out Command? remove));
        Assert.Equal("remove", remove.Name);
        Assert.True(registry.TryGet("remove", out Command? byName));
        Assert.Equal("remove", byName.Name);
        Assert.True(registry.TryGet("xp", out Command? removeProject));
        Assert.Equal("removeproject", removeProject.Name);
    }

    // Relative times (job 103) ---------------------------------------------------------------

    private static FixedTimeProvider At(int hour, int minute, int second = 0) =>
        new(new DateTimeOffset(2026, 10, 27, hour, minute, second, TimeSpan.Zero), TimeZoneInfo.Utc);

    private const string TimeForms = "Use H:mm, now, -<n>m or +<n>m.";

    // AC-5 (103)
    [Theory]
    [InlineData("arrive -10m", "arrival 18:20")]
    [InlineData("leave now", "leaving 18:30")]
    [InlineData("break +15m", "breakStart 18:45")]
    [InlineData("breakend -0m", "breakEnd 18:30")]
    public void Booking_RelativeTime_BooksFromTheCurrentMinute(string typed, string entry)
    {
        (int exitCode, string output) = RunCommands(At(18, 30), typed, "quit");

        Assert.Equal(0, exitCode);
        Assert.Equal(Entries(entry), ReadDay("2026-10-27"));
        Assert.DoesNotContain("is not a time", output);
    }

    // AC-5 (103)
    [Theory]
    [InlineData("arrive -1m", "arrival 18:29")]
    [InlineData("leave now", "leaving 18:30")]
    [InlineData("break +1m", "breakStart 18:31")]
    public void Booking_RelativeTime_DropsTheClocksSecondsFirst(string typed, string entry)
    {
        (int exitCode, _) = RunCommands(At(18, 30, 59), typed, "quit");

        Assert.Equal(0, exitCode);
        Assert.Equal(Entries(entry), ReadDay("2026-10-27"));
    }

    // AC-5 (103)
    [Fact]
    public void Booking_RelativeTime_GoesToTheShownDay()
    {
        (int exitCode, _) = RunCommands(At(18, 30), "prev", "arrive -10m", "quit");

        Assert.Equal(0, exitCode);
        Assert.Equal(["2026-10-26.json"], Directory.EnumerateFiles(data.Path).Select(Path.GetFileName));
        Assert.Equal(Entries("arrival 18:20"), ReadDay("2026-10-26"));
    }

    // AC-6 (103)
    [Theory]
    [InlineData("change 1 -30m", 18, 0)]
    [InlineData("change 1 now", 18, 30)]
    [InlineData("change 1 +5m", 18, 35)]
    public void Change_RelativeTime_SetsTheEntryFromNow(string typed, int hour, int minute)
    {
        File.WriteAllText(DayFile("2026-10-27"), ThreeEntries);

        (int exitCode, string output) = RunCommands(At(18, 30, 59), typed, "quit");

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain("is not a time", output);
        IReadOnlyList<DayEntry> entries = LoadDay().Entries;
        Assert.Equal(3, entries.Count);
        Assert.Contains(new DayEntry(EntryKind.Arrival, new TimeOnly(hour, minute)), entries);
        Assert.DoesNotContain(new DayEntry(EntryKind.Arrival, new TimeOnly(8, 0)), entries);
    }

    // AC-7 (103)
    [Fact]
    public void Checkpoint_RelativeTime_WithProject_BooksFromNow()
    {
        WriteTwoProjects();

        (int exitCode, _) = RunCommands(At(18, 30, 59), "checkpoint -15m #1 Standup", "quit");

        Assert.Equal(0, exitCode);
        Assert.Equal([Checkpoint(18, 15, "Standup", 1234)], LoadDay().Entries);
    }

    // AC-7 (103)
    [Theory]
    [InlineData("checkpoint now Review", 18, 30, "Review")]
    [InlineData("checkpoint +5m Review", 18, 35, "Review")]
    [InlineData("checkpoint now what", 18, 30, "what")]
    [InlineData("checkpoint nowhere Review", 18, 30, "nowhere Review")]
    [InlineData("checkpoint -10 Review", 18, 30, "-10 Review")]
    [InlineData("checkpoint -m Review", 18, 30, "-m Review")]
    public void Checkpoint_FirstToken_IsATimeOnlyInAnAcceptedForm(string typed, int hour, int minute, string name)
    {
        (int exitCode, _) = RunCommands(At(18, 30, 59), typed, "quit");

        Assert.Equal(0, exitCode);
        Assert.Equal([Checkpoint(hour, minute, name)], LoadDay().Entries);
    }

    // AC-7 (103)
    [Theory]
    [InlineData("checkpoint -99999m Standup", "checkpoint: -99999m is not a time. " + TimeForms)]
    [InlineData("checkpoint +12345m", "checkpoint: +12345m is not a time. " + TimeForms)]
    [InlineData("checkpoint now", "checkpoint: say the checkpoint's name. " + CheckpointUsage)]
    [InlineData("checkpoint -15m #1", "checkpoint: say the checkpoint's name. " + CheckpointUsage)]
    public void Checkpoint_BadRelativeTimeOrNoName_ShowsHint_AndWritesNothing(string typed, string expected)
    {
        WriteTwoProjects();

        (int exitCode, string output) = RunWide(200, typed, "quit");

        Assert.Equal(0, exitCode);
        AssertOnHintRow(LastLayout(output), expected);
        Assert.Equal([ProjectsFile], Directory.EnumerateFileSystemEntries(data.Path));
    }

    // AC-8 (103)
    [Theory]
    [InlineData("arrive -20m", "arrive: -20m crosses midnight.")]
    [InlineData("leave -11m", "leave: -11m crosses midnight.")]
    [InlineData("break -0011m", "break: -0011m crosses midnight.")]
    [InlineData("breakend -9999m", "breakend: -9999m crosses midnight.")]
    [InlineData("checkpoint -20m Standup", "checkpoint: -20m crosses midnight.")]
    public void RelativeTime_CrossingMidnight_ShowsHint_AndWritesNothing(string typed, string expected)
    {
        (int exitCode, string output) = RunCommands(At(0, 10), typed, "quit");

        Assert.Equal(0, exitCode);
        string[] frame = LastLayout(output);
        AssertLayout(frame);
        string hint = HintText(frame, expected);
        Assert.StartsWith(expected, hint);
        Assert.Contains("Use a time on this day, from 0:00 to 23:59.", hint);
        Assert.Empty(Directory.EnumerateFileSystemEntries(data.Path));
    }

    // AC-8 (103)
    [Fact]
    public void RelativeTime_CrossingMidnight_LateInTheDay_LeavesTheFileUnchanged()
    {
        File.WriteAllText(DayFile("2026-10-27"), ThreeEntries);
        byte[] before = File.ReadAllBytes(DayFile("2026-10-27"));

        (int exitCode, string output) = RunCommands(At(23, 58), "leave +2m", "change 1 +5m", "quit");

        Assert.Equal(0, exitCode);
        Assert.Contains("leave: +2m crosses midnight.", output);
        Assert.Contains("change: +5m crosses midnight.", output);
        Assert.Equal(before, File.ReadAllBytes(DayFile("2026-10-27")));
    }

    // AC-8 (103)
    [Theory]
    [InlineData("arrive +400m", "arrive")]
    [InlineData("change 1 +400m", "change")]
    [InlineData("checkpoint +400m Standup", "checkpoint")]
    public void RelativeTime_CrossingMidnight_HintIsRed(string typed, string name)
    {
        File.WriteAllText(DayFile("2026-10-27"), ThreeEntries);

        string output = RunAnsi(typed);

        Assert.Matches($@"\x1b\[(31|91|38;5;9)m{name}: ", output);
        Assert.Contains("crosses midnight", output);
    }

    // AC-9 (103)
    [Theory]
    [InlineData("arrive -10x", "arrive: -10x is not a time. " + TimeForms)]
    [InlineData("leave -10M", "leave: -10M is not a time. " + TimeForms)]
    [InlineData("break NOW", "break: NOW is not a time. " + TimeForms)]
    [InlineData("breakend -12345m", "breakend: -12345m is not a time. " + TimeForms)]
    [InlineData("arrive -[red]m", "arrive: -[red]m is not a time. " + TimeForms)]
    public void RelativeTime_Malformed_ShowsTheAcceptedForms_AndWritesNothing(string typed, string expected)
    {
        (int exitCode, string output) = RunCommands(typed, "quit");

        Assert.Equal(0, exitCode);
        AssertOnHintRow(LastLayout(output), expected);
        Assert.Empty(Directory.EnumerateFileSystemEntries(data.Path));
    }

    // AC-9 (103)
    [Fact]
    public void Change_MalformedRelativeTime_ShowsTheAcceptedForms_AndChangesNothing()
    {
        File.WriteAllText(DayFile("2026-10-27"), ThreeEntries);
        byte[] before = File.ReadAllBytes(DayFile("2026-10-27"));

        (int exitCode, string output) = RunCommands("change 1 -10x", "quit");

        Assert.Equal(0, exitCode);
        AssertOnHintRow(LastLayout(output), "change: -10x is not a time. " + TimeForms);
        Assert.Equal(before, File.ReadAllBytes(DayFile("2026-10-27")));
    }

    // AC-9 (103)
    [Fact]
    public void RelativeTime_Malformed_HintIsRed()
    {
        string output = RunAnsi("arrive -10x");

        Assert.Matches(@"\x1b\[(31|91|38;5;9)marrive: ", output);
        Assert.Contains("is not a time", output);
    }

    // AC-10 (103)
    [Fact]
    public void Help_DescribesRelativeTimes_ForTheSixCommands()
    {
        (_, string output) = Run(c =>
        {
            c.Profile.Width = 160;
            c.Input.PushTextWithEnter("help");
            PageDown(c, HelpPages);
            c.Input.PushTextWithEnter("quit");
        });

        Assert.Matches(@"arrive +│ a +│ Book your arrival: now, or at an optional H:mm, -10m or \+5m", output);
        Assert.Matches(@"leave +│ l +│ Book your leaving: now, or at an optional H:mm, -10m or \+5m", output);
        Assert.Matches(@"break +│ b +│ Book the start of a break: now, or at an optional H:mm, -10m or \+5m", output);
        Assert.Matches(@"breakend +│ be +│ Book the end of a break: now, or at an optional H:mm, -10m or \+5m", output);
        Assert.Matches(@"checkpoint +│ cp +│ Book a checkpoint: \[H:mm\|now\|-10m\|\+5m\] \[#project\] <name>", output);
        Assert.Matches(@"change +│ c +│ Change the time of an entry on this day: <number> <H:mm\|now\|-10m\|\+5m>", output);
    }

    // Job 104 ------------------------------------------------------------------------------

    private const string RoundingDay = "2026-11-02";

    /// <summary>Books #1 "Fix" from 9:00 to 9:23 (0:23) on <see cref="RoundingDay"/>.</summary>
    private void WriteRoundingDay() =>
        WriteReportDay(RoundingDay, ReportCp("09:00", "Fix", 1), "{\"kind\":\"leaving\",\"time\":\"09:23\"}");

    private void WriteRounding(string json) =>
        File.WriteAllText(SettingsPath, "{\"settings\":{\"report-rounding\":" + json + "}}");

    // AC-6 (104)
    [Fact]
    public void Report_WithRounding15_ShowsRoundedRowsTotalAndCaption()
    {
        WriteRoundingDay();
        WriteRounding("\"15 min\"");

        (int exitCode, string[] frame) = RunReport($"report {RoundingDay} {RoundingDay}", "quit");

        Assert.Equal(0, exitCode);
        Assert.Contains(frame, l => Regex.IsMatch(l, @"│ #1 +│ Fix +│ 0:30 +│ +0\.50 │"));
        Assert.Contains(frame, l => Regex.IsMatch(l, @"│ Total +│ +│ 0:30 +│ +0\.50 │"));
        Assert.Contains("2026-11-02 – 2026-11-02 · rounded to 15 min", Joined(frame));
    }

    // The frame's non-blank rows, trimmed and joined with one space, so a wrapped caption reads as one line.
    private static string Joined(string[] frame) =>
        string.Join(' ', frame.Select(l => l.Trim()).Where(l => l.Length > 0));

    // AC-6 (104)
    [Fact]
    public void Report_CaptionSuffix_IsGrey()
    {
        WriteRoundingDay();
        WriteRounding("\"5 min\"");

        string output = RunAnsiCommands($"report {RoundingDay} {RoundingDay}");

        // The narrow table wraps the caption; every part of it is grey.
        Assert.Matches(@"\x1b\[(90|38;5;8)m2026-11-02 – 2026-11-02 · rounded to\x1b\[0m", output);
        Assert.Matches(@"\x1b\[(90|38;5;8)m5 min\x1b\[0m", output);
    }

    // AC-6 (104)
    [Theory]
    [InlineData("{\"settings\":{\"report-rounding\":\"off\"}}")]
    [InlineData("{\"settings\":{}}")]
    [InlineData("{\"settings\":{\"daily-target\":480}}")]
    public void Report_RoundingOffOrMissing_IsExactlyAsWithoutSettings(string settingsJson)
    {
        WriteReportDays();
        WriteRoundingDay();
        (_, string withoutFile) = RunCommands($"report {RoundingDay} 2026-11-04", "quit");
        Assert.False(File.Exists(SettingsPath));
        File.WriteAllText(SettingsPath, settingsJson);

        (_, string with) = RunCommands($"report {RoundingDay} 2026-11-04", "quit");

        Assert.Equal(withoutFile, with);
        Assert.Contains("2026-11-02 – 2026-11-04", with);
        Assert.DoesNotContain("rounded", with);
        Assert.Matches(@"│ #1 +│ Fix +│ 0:23 +│ +0\.38 │", with);
    }

    // AC-7 (104)
    [Fact]
    public void Export_WithRounding10_WritesRoundedHours_AndSaysSo()
    {
        using TempFolder target = new TempFolder();
        WriteRoundingDay();
        WriteRounding("\"10 min\"");

        (int exitCode, string[] frame) = RunExport($"export {RoundingDay} {RoundingDay} {target.Path}");

        Assert.Equal(0, exitCode);
        string path = Path.Combine(target.Path, "times-report-2026-11-02-to-2026-11-02.csv");
        Assert.Equal(Encoding.UTF8.GetBytes("\uFEFFWork item,Title,Hours\r\n1,Fix,0.33\r\n"), File.ReadAllBytes(path));
        Assert.Contains(frame, l => l.TrimEnd().EndsWith($"Exported 1 row to {path} · rounded to 10 min", StringComparison.Ordinal));
    }

    // AC-7 (104)
    [Fact]
    public void Export_RoundingOff_HasNoSuffix()
    {
        using TempFolder target = new TempFolder();
        WriteRoundingDay();
        WriteRounding("\"off\"");

        (_, string[] frame) = RunExport($"export {RoundingDay} {RoundingDay} {target.Path}");

        string path = Path.Combine(target.Path, "times-report-2026-11-02-to-2026-11-02.csv");
        Assert.Equal(Encoding.UTF8.GetBytes("\uFEFFWork item,Title,Hours\r\n1,Fix,0.38\r\n"), File.ReadAllBytes(path));
        Assert.Contains(frame, l => l.TrimEnd().EndsWith($"Exported 1 row to {path}", StringComparison.Ordinal));
        Assert.DoesNotContain(frame, l => l.Contains("rounded", StringComparison.Ordinal));
    }

    // AC-8 (104)
    [Theory]
    [InlineData("{not json")]
    [InlineData("{\"settings\":{\"report-rounding\":\"7 min\"}}")]
    [InlineData("{\"settings\":{\"report-rounding\":15}}")]
    [InlineData("{\"settings\":{\"report-rounding\":true}}")]
    [InlineData("{\"settings\":{\"report-rounding\":\"15 MIN\"}}")]
    public void BrokenOrUnknownRounding_ActsAsOff_AndLeavesTheFile(string settingsJson)
    {
        using TempFolder target = new TempFolder();
        WriteRoundingDay();
        File.WriteAllText(SettingsPath, settingsJson);
        byte[] before = File.ReadAllBytes(SettingsPath);

        (_, string[] report) = RunReport($"report {RoundingDay} {RoundingDay}", "quit");
        (_, string[] export) = RunExport($"export {RoundingDay} {RoundingDay} {target.Path}");

        Assert.Contains(report, l => Regex.IsMatch(l, @"│ #1 +│ Fix +│ 0:23 +│ +0\.38 │"));
        Assert.Contains(report, l => l.Trim() == "2026-11-02 – 2026-11-02");
        Assert.DoesNotContain(report, l => l.Contains("rounded", StringComparison.Ordinal));
        Assert.DoesNotContain(export, l => l.Contains("rounded", StringComparison.Ordinal));
        string csv = File.ReadAllText(Path.Combine(target.Path, "times-report-2026-11-02-to-2026-11-02.csv"));
        Assert.EndsWith("1,Fix,0.38\r\n", csv);
        Assert.Equal(before, File.ReadAllBytes(SettingsPath));
    }

    // AC-8 (104): a registry without the setting gives off.
    [Fact]
    public void Report_RegistryWithoutRounding_ActsAsOff()
    {
        WriteRoundingDay();
        WriteRounding("\"15 min\"");
        TestConsole console = new TestConsole().Interactive();
        console.Profile.Width = 100;
        console.Input.PushTextWithEnter($"report {RoundingDay} {RoundingDay}");
        console.Input.PushTextWithEnter("quit");

        new App(console, FixedTimeProvider.At20261027(), new DayStore(data.Path), settings: new SettingsRegistry()).Run([]);

        Assert.Matches(@"│ #1 +│ Fix +│ 0:23 +│ +0\.38 │", console.Output);
        Assert.DoesNotContain("rounded", console.Output);
    }

    // AC-9 (104)
    [Fact]
    public void Rounding_ChangesNoFile_AndTheDayViewAndBalanceStayExact()
    {
        using TempFolder target = new TempFolder();
        const string Today = "2026-10-27";
        File.WriteAllText(
            DayFile(Today),
            "{\"entries\":[{\"kind\":\"arrival\",\"time\":\"09:00\"}," + ReportCp("09:00", "Fix", 1)
            + ",{\"kind\":\"leaving\",\"time\":\"09:23\"}]}");
        File.WriteAllText(ProjectsFile, "{\"projects\":[{\"workItem\":1,\"title\":\"Times\"}]}");
        WriteRounding("\"15 min\"");
        string[] files = [DayFile(Today), ProjectsFile, SettingsPath];
        DateTime old = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        foreach (string file in files)
        {
            File.SetLastWriteTimeUtc(file, old);
        }

        byte[][] bytes = files.Select(File.ReadAllBytes).ToArray();
        string[] listing = Listing(data.Path);

        (_, string[] report) = RunReport($"report {Today} {Today}", "quit");
        (_, string[] export) = RunExport($"export {Today} {Today} {target.Path}");
        (_, string output) = RunShipped("summary", "quit");

        Assert.Contains(report, l => Regex.IsMatch(l, @"│ #1 +│ Times +│ 0:30 +│ +0\.50 │"));
        Assert.Contains(export, l => l.Contains("rounded to 15 min", StringComparison.Ordinal));
        Assert.Equal(listing, Listing(data.Path));
        for (int i = 0; i < files.Length; i++)
        {
            Assert.Equal(bytes[i], File.ReadAllBytes(files[i]));
            Assert.Equal(old, File.GetLastWriteTimeUtc(files[i]));
        }

        // The day view's target line and the summary keep the exact 0:23.
        Assert.Contains("Target 8:00 · day -7:37 · balance -7:37", output);
        Assert.Matches(@"│ #1 +│ Fix +│ 0:23 │", output);
        Assert.DoesNotContain("0:30", output);
    }

    // AC-10 (104)
    [Fact]
    public void Settings_ListsReportRounding_AndEnterCyclesAndSavesEachValue()
    {
        List<string> saved = [];
        string output = string.Empty;
        for (int i = 0; i < 4; i++)
        {
            TestConsole console = new TestConsole().Interactive();
            console.Profile.Width = 120;
            console.Input.PushTextWithEnter("settings");
            // Past daily-target, alert-target and alert-limit (job 113), the three checks (job 114), the two holiday settings (job 115)
            // and vacation-days (job 116).
            for (int down = 0; down < 9; down++)
            {
                console.Input.PushKey(ConsoleKey.DownArrow);
            }

            console.Input.PushKey(i % 2 == 0 ? ConsoleKey.Enter : ConsoleKey.Spacebar);
            console.Input.PushKey(ConsoleKey.Escape);
            console.Input.PushTextWithEnter("quit");
            new App(console, FixedTimeProvider.At20261027(), new DayStore(data.Path)).Run([]);
            output += console.Output;
            saved.Add(File.ReadAllText(SettingsPath));
        }

        Assert.Matches(@"report-rounding +off +Round report and export times per row and day", output);
        Assert.Equal(
            [
                "{\"settings\":{\"report-rounding\":\"5 min\"}}",
                "{\"settings\":{\"report-rounding\":\"10 min\"}}",
                "{\"settings\":{\"report-rounding\":\"15 min\"}}",
                "{\"settings\":{\"report-rounding\":\"off\"}}",
            ],
            saved);
    }

    // Job 105 ------------------------------------------------------------------------------

    /// <summary>Like <see cref="RunDevOps(FakeTokenStore, FakeDevOpsClient?, string[])"/>, with ANSI colours in the output.</summary>
    private string RunDevOpsAnsi(FakeTokenStore store, FakeDevOpsClient? client, params string[] commands)
    {
        TestConsole console = new TestConsole().Interactive().EmitAnsiSequences();
        console.Profile.Height = 40;
        console.Profile.Width = 140;
        foreach (string command in commands)
        {
            console.Input.PushTextWithEnter(command);
        }

        new App(console, FixedTimeProvider.At20261027(), new DayStore(data.Path),
            tokenStore: store, devOpsClient: client is null ? null : client.For, instanceLock: sideLock).Run([]);
        Assert.DoesNotContain(DevOpsPat, console.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", console.Output, StringComparison.Ordinal);
        return console.Output;
    }

    private byte[]? ProjectsBytes() => File.Exists(ProjectsFile) ? File.ReadAllBytes(ProjectsFile) : null;

    // AC-5
    [Theory]
    [InlineData("devops import now")]
    [InlineData("dv import 1 2")]
    [InlineData("/devops IMPORT x")]
    public void Import_WithArgument_ShowsRedHint_AndDoesNothing(string command)
    {
        File.WriteAllText(ProjectsFile, "{\"projects\":[{\"workItem\":1,\"title\":\"One\"}]}");
        byte[]? before = ProjectsBytes();
        FakeTokenStore store = new FakeTokenStore(stored: ("contoso", DevOpsPat));
        FakeDevOpsClient client = new FakeDevOpsClient();
        client.Assigned.Add((2, "Two"));

        string output = RunDevOpsAnsi(store, client, command, "quit");

        Assert.Matches(@"\x1b\[(1;)?(31|91|38;5;9)(;1)?mdevops import\x1b\[0m\x1b\[(31|91|38;5;9)m takes no arguments\.", output);
        Assert.Equal(0, store.Calls);
        Assert.Empty(client.Credentials);
        Assert.Equal(0, client.AssignedCalls);
        Assert.Equal(before, ProjectsBytes());
    }

    // AC-6
    [Fact]
    public void Import_Unsupported_ShowsDevOpsUnsupported()
    {
        FakeTokenStore store = new FakeTokenStore(supported: false);
        FakeDevOpsClient client = new FakeDevOpsClient();
        client.Assigned.Add((2, "Two"));

        (_, string output) = RunDevOps(store, client, "devops import", "quit");

        Assert.Contains(NotAvailable, LastHint(output));
        Assert.Equal(0, store.Calls);
        Assert.Empty(client.Credentials);
        Assert.False(File.Exists(ProjectsFile));
    }

    // AC-6
    [Theory]
    [InlineData(null, null)]
    [InlineData("bad org", DevOpsPat)]
    public void Import_NotConnected_ShowsRedHint(string? organization, string? pat)
    {
        File.WriteAllText(ProjectsFile, "{\"projects\":[]}");
        byte[]? before = ProjectsBytes();
        FakeTokenStore store = new FakeTokenStore(stored: organization is null ? null : (organization, pat!));
        FakeDevOpsClient client = new FakeDevOpsClient();
        client.Assigned.Add((2, "Two"));

        string output = RunDevOpsAnsi(store, client, "devops import", "quit");

        Assert.Matches(@"\x1b\[(31|91|38;5;9)mdevops: not connected to Azure DevOps\. Use ", output);
        Assert.Contains("first. Nothing was imported.", output);
        Assert.Empty(client.Credentials);
        Assert.Equal(before, ProjectsBytes());
    }

    // AC-6
    [Fact]
    public void Import_StoreFailure_ShowsStoreMessage()
    {
        File.WriteAllText(ProjectsFile, "{\"projects\":[]}");
        byte[]? before = ProjectsBytes();
        FakeTokenStore store = new FakeTokenStore(stored: ("contoso", DevOpsPat)) { Throws = true };
        FakeDevOpsClient client = new FakeDevOpsClient();

        (int exitCode, string output) = RunDevOps(store, client, "devops import", "quit");

        Assert.Equal(0, exitCode);
        Assert.Contains($"devops: {FakeTokenStore.Failure} Nothing was imported.", LastHint(output));
        Assert.Empty(client.Credentials);
        Assert.Equal(before, ProjectsBytes());
    }

    // AC-6
    [Fact]
    public void Import_NoClientFactory_ShowsCannotReach()
    {
        FakeTokenStore store = new FakeTokenStore(stored: ("contoso", DevOpsPat));

        (_, string output) = RunDevOps(store, null, "devops import", "quit");

        Assert.Contains("devops: this build cannot reach Azure DevOps. Nothing was imported.", LastHint(output));
        Assert.False(File.Exists(ProjectsFile));
    }

    // AC-7
    [Fact]
    public void Import_UnreadableProjects_ShowsHintBeforeStoreOrClient()
    {
        File.WriteAllText(ProjectsFile, "{not json");
        byte[]? before = ProjectsBytes();
        FakeTokenStore store = new FakeTokenStore(stored: ("contoso", DevOpsPat));
        FakeDevOpsClient client = new FakeDevOpsClient();
        client.Assigned.Add((2, "Two"));

        string output = RunDevOpsAnsi(store, client, "devops import", "quit");

        Assert.Matches(@"\x1b\[(31|91|38;5;9)mdevops: the project list cannot be read", output);
        Assert.Contains("Nothing was imported.", output);
        Assert.Equal(0, store.Reads);
        Assert.Empty(client.Credentials);
        Assert.Equal(0, client.AssignedCalls);
        Assert.Equal(before, ProjectsBytes());
    }

    // AC-8, AC-9, AC-10
    [Fact]
    public void Import_AddsNewValidItemsInOrder_KeepsExisting_AndCountsTheRest()
    {
        File.WriteAllText(ProjectsFile,
            "{\"projects\":[{\"workItem\":50,\"title\":\"Mine\"},{\"workItem\":3,\"title\":\"Local name\"}]}");
        FakeTokenStore store = new FakeTokenStore(stored: ("contoso", DevOpsPat));
        FakeDevOpsClient client = new FakeDevOpsClient();
        client.Assigned.AddRange(
        [
            (40, "  Padded [red]title[/]  "),
            (3, "Remote name"),
            (7, "Seven"),
            (9, null),
            (11, "EVILTITLE\u202Etxt"),
            (12, new string('x', 201)),
            (13, "BELLTITLE\u0007"),
            (14, "   "),
            (15, "BROKENTITLE\ud800"),
        ]);

        (int exitCode, string output) = RunDevOps(store, client, "devops import", "quit");

        Assert.Equal(0, exitCode);
        Assert.Equal(
            "{\"projects\":[{\"workItem\":50,\"title\":\"Mine\"},{\"workItem\":3,\"title\":\"Local name\"},"
            + "{\"workItem\":7,\"title\":\"Seven\"},{\"workItem\":40,\"title\":\"Padded [red]title[/]\"}]}",
            File.ReadAllText(ProjectsFile));
        Assert.Equal(1, store.Reads);
        Assert.Equal(1, client.AssignedCalls);
        Assert.Equal(1, client.Disposed);
        Assert.Equal("contoso", Assert.Single(client.Credentials).Organization);
        string[] frame = LastLayout(output);
        Assert.Contains(frame, l => Regex.IsMatch(l, @"│ Work item +│ Title +│"));
        int seven = Array.FindIndex(frame, l => Regex.IsMatch(l, @"│ #7 +│ Seven +│"));
        int forty = Array.FindIndex(frame, l => Regex.IsMatch(l, @"│ #40 +│ Padded \[red\]title\[/\] +│"));
        Assert.True(seven > 0 && forty > seven, string.Join("\n", frame));
        Assert.Contains(frame, l => l.Contains("1 already on the quicklist", StringComparison.Ordinal));
        Assert.Contains(frame, l => l.Contains("6 skipped (title not valid): #9, #11, #12, #13, #14, #15", StringComparison.Ordinal));
        Assert.Contains("Added 2 work items to the quicklist.", LastHint(output));
        Assert.DoesNotContain("Remote name", output, StringComparison.Ordinal);
        Assert.DoesNotContain("EVILTITLE", output, StringComparison.Ordinal);
        Assert.DoesNotContain("BELLTITLE", output, StringComparison.Ordinal);
        Assert.DoesNotContain("BROKENTITLE", output, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('x', 20), output, StringComparison.Ordinal);
    }

    // AC-10: the hint and the count lines are grey.
    [Fact]
    public void Import_HintAndCountLinesAreGrey()
    {
        FakeTokenStore store = new FakeTokenStore(stored: ("contoso", DevOpsPat));
        FakeDevOpsClient client = new FakeDevOpsClient();
        client.Assigned.AddRange([(1, "One"), (2, null)]);

        string output = RunDevOpsAnsi(store, client, "devops import", "quit");

        Assert.Matches(@"\x1b\[(90|38;5;8)mAdded 1 work item to the quicklist\.", output);
        Assert.Matches(@"\x1b\[(90|38;5;8)m1 skipped \(title not valid\): #2", output);
    }

    // AC-10: nothing new, nothing written.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Import_NothingNew_WritesNothing(bool withFile)
    {
        if (withFile)
        {
            File.WriteAllText(ProjectsFile, "{ \"projects\" : [ {\"workItem\":5,\"title\":\"Five\"} ] }");
        }

        byte[]? before = ProjectsBytes();
        DateTime? written = withFile ? File.GetLastWriteTimeUtc(ProjectsFile) : null;
        FakeTokenStore store = new FakeTokenStore(stored: ("contoso", DevOpsPat));
        FakeDevOpsClient client = new FakeDevOpsClient();
        if (withFile)
        {
            client.Assigned.AddRange([(5, "Five remote"), (6, "\u200B")]);
        }

        (_, string output) = RunDevOps(store, client, "devops import", "quit");

        Assert.Contains("No new work items to add.", LastHint(output));
        Assert.Equal(before, ProjectsBytes());
        Assert.Equal(written, withFile ? File.GetLastWriteTimeUtc(ProjectsFile) : null);
        Assert.DoesNotContain(LastLayout(output), l => l.Contains("Work item", StringComparison.Ordinal));
        Assert.Equal(1, client.Disposed);
    }

    // AC-11
    [Fact]
    public void Import_ClientFailure_ShowsRedReason_AndWritesNothing()
    {
        File.WriteAllText(ProjectsFile, "{\"projects\":[]}");
        byte[]? before = ProjectsBytes();
        FakeTokenStore store = new FakeTokenStore(stored: ("contoso", DevOpsPat));
        FakeDevOpsClient client = new FakeDevOpsClient { AssignedFailure = "bad [red]answer" };
        client.Assigned.Add((1, "One"));

        string output = RunDevOpsAnsi(store, client, "devops import", "report 2026-11-02 2026-11-04", "devops import", "quit");

        Assert.Matches(@"\x1b\[(31|91|38;5;9)mdevops: bad \[red\]answer Nothing was imported\.", output);
        Assert.Equal(2, client.AssignedCalls);
        Assert.Equal(2, client.Disposed);
        Assert.Equal(before, ProjectsBytes());
    }

    // AC-12
    [Fact]
    public void Import_TooLargeForProjectsJson_WritesNothing()
    {
        File.WriteAllText(ProjectsFile, "{\"projects\":[{\"workItem\":1,\"title\":\"One\"}]}");
        byte[]? before = ProjectsBytes();
        FakeTokenStore store = new FakeTokenStore(stored: ("contoso", DevOpsPat));
        FakeDevOpsClient client = new FakeDevOpsClient();
        for (int i = 0; i < 300; i++)
        {
            client.Assigned.Add((1000 + i, new string((char)('a' + (i % 26)), ProjectList.MaxTitleLength)));
        }

        string output = RunDevOpsAnsi(store, client, "devops import", "quit");

        Assert.Matches(@"\x1b\[(31|91|38;5;9)mdevops: the quicklist would be too large", output);
        Assert.Contains("Nothing was imported.", output);
        Assert.Equal(before, ProjectsBytes());
    }

    // AC-12: just under the cap is still written, and stays readable.
    [Fact]
    public void Import_JustUnderTheCap_IsWrittenAndReadable()
    {
        FakeTokenStore store = new FakeTokenStore(stored: ("contoso", DevOpsPat));
        FakeDevOpsClient client = new FakeDevOpsClient();
        List<Project> projects = [];
        for (int i = 0; ; i++)
        {
            Project next = new Project(1000 + i, new string('t', ProjectList.MaxTitleLength));
            if (Encoding.UTF8.GetByteCount(new ProjectList(projects.Append(next)).ToJson()) > ProjectStore.MaxFileBytes)
            {
                break;
            }

            projects.Add(next);
            client.Assigned.Add((next.WorkItem, next.Title));
        }

        (_, string output) = RunDevOps(store, client, "devops import", "quit");

        Assert.Contains($"Added {projects.Count} work items to the quicklist.", LastHint(output));
        Assert.Equal(projects.Count, new ProjectStore(data.Path).Load().Projects.Count);
    }

    // AC-12
    [Fact]
    public void Import_SaveFailure_ShowsCouldNotSave()
    {
        UseSideLock();
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.WriteAllText(ProjectsFile, "{\"projects\":[]}");
        byte[]? before = ProjectsBytes();
        FakeTokenStore store = new FakeTokenStore(stored: ("contoso", DevOpsPat));
        FakeDevOpsClient client = new FakeDevOpsClient();
        client.Assigned.Add((1, "One"));
        UnixFileMode mode = File.GetUnixFileMode(data.Path);
        File.SetUnixFileMode(data.Path, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        string output;
        try
        {
            output = RunDevOpsAnsi(store, client, "devops import", "quit");
        }
        finally
        {
            File.SetUnixFileMode(data.Path, mode);
        }

        Assert.Matches(@"\x1b\[(31|91|38;5;9)mdevops: could not save the project list\.", output);
        Assert.Equal(before, ProjectsBytes());
    }

    // AC-13: devops import is not kept in the in-memory history, and the loop keeps running.
    [Fact]
    public void Import_NeverEntersHistory()
    {
        FakeTokenStore store = new FakeTokenStore(stored: ("contoso", DevOpsPat));
        FakeDevOpsClient client = new FakeDevOpsClient();

        (int exitCode, string output) = RunDevOps(store, client, c =>
        {
            c.Input.PushTextWithEnter("devops");
            c.Input.PushTextWithEnter("devops import");
            c.Input.PushKey(ConsoleKey.UpArrow);
            c.Input.PushKey(ConsoleKey.Enter);
            c.Input.PushTextWithEnter("quit");
        });

        Assert.Equal(0, exitCode);
        // Up recalled the plain devops line, so the last hint is the status, and import ran once.
        Assert.Contains("Azure DevOps: contoso · token stored", LastHint(output));
        Assert.Equal(1, client.AssignedCalls);
    }

    // AC-13
    [Fact]
    public void Help_ShowsDevOpsImport_InTheDescriptionAndPopup()
    {
        TestConsole console = new TestConsole().Interactive();
        console.Profile.Width = 200;
        console.Profile.Height = 80;
        console.Input.PushTextWithEnter("help");
        console.Input.PushText("dv");
        console.Input.PushKey(ConsoleKey.Escape);
        console.Input.PushKey(ConsoleKey.Escape);
        console.Input.PushTextWithEnter("quit");

        new App(console, FixedTimeProvider.At20261027(), new DayStore(data.Path)).Run([]);

        string description = Regex.Escape("Show or set the Azure DevOps link: [connect <org> | import | disconnect]");
        Assert.Matches(@"│ devops +│ dv +│ " + description, console.Output);
        Assert.Matches(@"devops +dv +" + description, console.Output);
    }

    // AC-13: the usage hint mentions import.
    [Fact]
    public void DevOps_UnknownSubcommand_MentionsImport()
    {
        (_, string output) = RunDevOps(new FakeTokenStore(), null, "devops frobnicate", "quit");

        Assert.Contains("devops import", LastHint(output));
    }

    // Job 106 ------------------------------------------------------------------------------

    /// <summary>Days for find: deploys on three days (one written out of order), plus a non-matching day.</summary>
    private void WriteFindDays()
    {
        WriteReportDay("2026-11-03", ReportCp("09:00", "predeploy", 1234), ReportCp("10:00", "Mail"), ReportLeave);
        WriteReportDay("2026-11-01", ReportCp("09:00", "Deploy prod"), ReportCp("09:45", "Lunch"), ReportLeave);
        WriteReportDay("2026-11-02", ReportCp("09:00", "Mail"), ReportLeave);
    }

    // AC-1
    [Fact]
    public void Help_ListsFind_WithAliasF()
    {
        TestConsole console = new TestConsole().Interactive().EmitAnsiSequences();
        console.Profile.Width = 120;
        console.Input.PushTextWithEnter("help");
        PageDown(console, HelpPages);
        console.Input.PushTextWithEnter("quit");

        new App(console, FixedTimeProvider.At20261027(), new DayStore(data.Path)).Run([]);
        string plain = Regex.Replace(console.Output, @"\x1b\[[0-9;?]*[A-Za-z]", string.Empty);

        Assert.Matches(@"│ find +│ f +│ Find days whose checkpoints or projects match: <text> +│", plain);
    }

    // AC-1
    [Theory]
    [InlineData("f")]
    [InlineData("F")]
    [InlineData("/find")]
    [InlineData("/f")]
    public void Find_AliasAndSlash_DoTheSameAsFind(string typed)
    {
        WriteFindDays();

        (_, string[] full) = RunReport("find deploy", "quit");
        (_, string[] other) = RunReport(typed + " deploy", "quit");

        Assert.Equal(full, other);
        Assert.Contains(other, l => l.Contains("2 matches for \"deploy\"", StringComparison.Ordinal));
    }

    // AC-2
    [Theory]
    [InlineData("find")]
    [InlineData("find    ")]
    [InlineData("f")]
    public void Find_WithoutText_ShowsRedUsageHint_AndNoTable(string command)
    {
        WriteFindDays();

        (int exitCode, string output) = Run(c =>
        {
            c.Profile.Width = 200;
            c.Input.PushTextWithEnter(command);
            c.Input.PushTextWithEnter("quit");
        });

        Assert.Equal(0, exitCode);
        string[] frame = LastLayout(output);
        AssertOnHintRow(frame, "find: say what to look for. Use find <text>.");
        Assert.DoesNotContain(frame, l => l.Contains("Work item", StringComparison.Ordinal));

        string ansi = RunAnsiCommands(command);
        Assert.Matches(@"\x1b\[(31|91|38;5;9)mfind: say what to look for", ansi);
    }

    // AC-2
    [Fact]
    public void Find_TextIsTheWholeTrimmedArgument_InnerSpacesKept()
    {
        WriteReportDay("2026-11-02", ReportCp("09:00", "Code review"), ReportCp("10:00", "Code"), ReportCp("11:00", "review"), ReportLeave);

        (_, string[] frame) = RunReport("find   code review  ", "quit");

        Assert.Contains(frame, l => l.Contains("1 match for \"code review\"", StringComparison.Ordinal));
        Assert.Contains(frame, l => Regex.IsMatch(l, @"│ 2026-11-02 +│ +│ Code review +│ 1:00 +│"));
        Assert.DoesNotContain(frame, l => Regex.IsMatch(l, @"│ (Code|review) +│"));
    }

    // AC-3, AC-5, AC-11
    [Fact]
    public void Find_ShowsColumnsRowsByDate_CaptionAndHidesTheQuicklist()
    {
        WriteFindDays();

        (int exitCode, string[] frame) = RunReport("find DEPLOY", "quit");

        Assert.Equal(0, exitCode);
        Assert.Contains(frame, l => Regex.IsMatch(l, @"│ Date +│ Work item +│ Title +│ Time +│"));
        int first = Array.FindIndex(frame, l => Regex.IsMatch(l, @"│ 2026-11-01 +│ +│ Deploy prod +│ 0:45 +│"));
        int second = Array.FindIndex(frame, l => Regex.IsMatch(l, @"│ 2026-11-03 +│ #1234 +│ predeploy +│ 1:00 +│"));
        Assert.True(first > 0 && second > first, string.Join("\n", frame));
        Assert.Contains(frame, l => l.Trim() == "2 matches for \"DEPLOY\"");
        Assert.DoesNotContain(frame, l => l.Contains("Mail", StringComparison.Ordinal) || l.Contains("Lunch", StringComparison.Ordinal));
        Assert.DoesNotContain(frame, l => l.Contains("2026-11-02", StringComparison.Ordinal));
        Assert.DoesNotContain(frame, l => l.Contains("open", StringComparison.Ordinal));
        Assert.DoesNotContain(frame, l => l.Contains("Projects", StringComparison.Ordinal));
        Assert.Equal(string.Empty, frame[^4].Trim());
    }

    // AC-4, AC-5
    [Fact]
    public void Find_MatchesTheQuicklistTitle_AndShowsIt()
    {
        WriteFindDays();
        File.WriteAllText(ProjectsFile, "{\"projects\":[{\"workItem\":1234,\"title\":\"Website relaunch\"}]}");

        (_, string[] frame) = RunReport("find RELAUNCH", "quit");

        Assert.Contains(frame, l => Regex.IsMatch(l, @"│ 2026-11-03 +│ #1234 +│ Website relaunch +│ 1:00 +│"));
        Assert.Contains(frame, l => l.Trim() == "1 match for \"RELAUNCH\"");
    }

    // AC-4
    [Theory]
    [InlineData("{not json")]
    [InlineData(null)]
    public void Find_UnreadableOrMissingProjects_SearchesNamesOnly(string? projects)
    {
        WriteFindDays();
        if (projects is not null)
        {
            File.WriteAllText(ProjectsFile, projects);
        }

        (int exitCode, string[] frame) = RunReport("find predeploy", "quit");

        Assert.Equal(0, exitCode);
        Assert.Contains(frame, l => Regex.IsMatch(l, @"│ 2026-11-03 +│ #1234 +│ predeploy +│ 1:00 +│"));
    }

    // AC-5
    [Fact]
    public void Find_HeadersAndBorderAreGrey_TimesAreSteelBlue()
    {
        WriteFindDays();

        string output = RunAnsiCommands("find deploy");

        Assert.Matches(@"\x1b\[(90|38;5;8)mDate\x1b\[0m", output);
        Assert.Matches(@"\x1b\[(90|38;5;8)m╭─", output);
        Assert.Matches(@"\x1b\[38;5;75m0:45\x1b\[0m", output);
        Assert.Matches(@"\x1b\[(90|38;5;8)m2 matches for ""deploy""", output);
    }

    // AC-6
    [Fact]
    public void Find_OpenOnAnotherDay_IsMarkedOpenWithFootnote_TodayCountsToNow()
    {
        WriteReportDay("2026-10-26", ReportCp("09:00", "Plan", 7));
        WriteReportDay("2026-10-27", ReportCp("17:00", "Plan", 7));

        (_, string[] frame) = RunReport("find plan", "quit");

        Assert.Contains(frame, l => Regex.IsMatch(l, @"│ 2026-10-26 +│ #7 +│ Plan +│ 0:00 open +│"));
        Assert.Contains(frame, l => Regex.IsMatch(l, @"│ 2026-10-27 +│ #7 +│ Plan +│ 1:30 +│"));
        Assert.Contains(frame, l => l.Contains(App.OpenFootnote, StringComparison.Ordinal));
    }

    // AC-7
    [Fact]
    public void Find_NoMatches_ShowsGreyNote()
    {
        WriteFindDays();

        (int exitCode, string[] frame) = RunReport("find nothing here", "quit");

        Assert.Equal(0, exitCode);
        Assert.Contains(frame, l => l.Contains("No checkpoints or projects match \"nothing here\".", StringComparison.Ordinal));
        Assert.DoesNotContain(frame, l => l.Contains("Work item", StringComparison.Ordinal));

        string ansi = RunAnsiCommands("find nothing here");
        Assert.Matches(@"\x1b\[(90|38;5;8)mNo checkpoints or projects match", ansi);
    }

    // AC-8
    [Theory]
    [InlineData("deploy")]
    [InlineData("nothing")]
    public void Find_SkippedFile_ShowsYellowLine_WithOrWithoutMatches(string text)
    {
        WriteFindDays();
        File.WriteAllText(DayFile("2026-11-04"), "{not json");

        (_, string[] frame) = RunReport("find " + text, "quit");

        int skipped = Array.FindIndex(frame, l => l.Contains("! 1 day file skipped", StringComparison.Ordinal));
        Assert.True(skipped > 0);
        int above = Array.FindIndex(frame, l => l.Contains("2 matches", StringComparison.Ordinal) || l.Contains("No checkpoints or projects match", StringComparison.Ordinal));
        Assert.True(above > 0 && skipped > above);

        string ansi = RunAnsiCommands("find " + text);
        Assert.Matches(@"\x1b\[(33|93|38;5;11)m! 1 day file skipped", ansi);
    }

    // AC-8
    [Fact]
    public void Find_FolderThatCannotBeListed_ShowsRedHint()
    {
        UseSideLock();
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        UnixFileMode mode = File.GetUnixFileMode(data.Path);
        File.SetUnixFileMode(data.Path, UnixFileMode.None);
        try
        {
            try
            {
                Directory.EnumerateFileSystemEntries(data.Path).ToList();
                // Running as root: access can't be denied.
                return;
            }
            catch (UnauthorizedAccessException)
            {
            }

            (int exitCode, string output) = RunCommands("find deploy", "quit");

            Assert.Equal(0, exitCode);
            Assert.StartsWith("find: the data folder cannot be listed", HintText(LastLayout(output), "find: the data folder"));
            Assert.DoesNotContain("   at ", output);
        }
        finally
        {
            File.SetUnixFileMode(data.Path, mode);
        }
    }

    // AC-9
    [Fact]
    public void Find_MarkupInTextNamesAndTitles_ShowsLiterally()
    {
        WriteReportDay("2026-11-02", ReportCp("09:00", "[red]x"), ReportCp("10:00", "[x]"), ReportCp("11:00", "y", 1), ReportLeave);
        File.WriteAllText(ProjectsFile, "{\"projects\":[{\"workItem\":1,\"title\":\"[bold][x][/]\"}]}");

        (int exitCode, string[] frame) = RunReport("find [x]", "quit");

        Assert.Equal(0, exitCode);
        Assert.Contains(frame, l => Regex.IsMatch(l, @"│ 2026-11-02 +│ +│ \[x\] +│ 1:00 +│"));
        Assert.Contains(frame, l => Regex.IsMatch(l, @"│ 2026-11-02 +│ #1 +│ \[bold\]\[x\]\[/\] +│ 7:00 +│"));
        Assert.Contains(frame, l => l.Trim() == "2 matches for \"[x]\"");

        (exitCode, frame) = RunReport("find [red]", "quit");
        Assert.Equal(0, exitCode);
        Assert.Contains(frame, l => Regex.IsMatch(l, @"│ 2026-11-02 +│ +│ \[red\]x +│ 1:00 +│"));
        Assert.Contains(frame, l => l.Trim() == "1 match for \"[red]\"");

        (exitCode, frame) = RunReport("find [/red]", "quit");
        Assert.Equal(0, exitCode);
        Assert.Contains(frame, l => l.Contains("No checkpoints or projects match \"[/red]\".", StringComparison.Ordinal));
    }

    // AC-10
    [Fact]
    public void Find_WritesNothing_AndKeepsTheShownDay()
    {
        WriteFindDays();
        WriteReportDay("2026-10-26", ReportCp("09:00", "Deploy test"), ReportLeave);
        File.WriteAllText(ProjectsFile, "{\"projects\":[{\"workItem\":1234,\"title\":\"Deploys\"}]}");
        File.WriteAllText(Path.Combine(data.Path, "settings.json"), "{\"settings\":{\"daily-target\":480}}");
        File.WriteAllText(DayFile("2026-11-04"), "{not json");
        DateTime old = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        foreach (string file in Directory.EnumerateFiles(data.Path))
        {
            File.SetLastWriteTimeUtc(file, old);
        }

        Dictionary<string, (DateTime, byte[])> before = Directory.EnumerateFiles(data.Path)
            .ToDictionary(p => p, p => (File.GetLastWriteTimeUtc(p), File.ReadAllBytes(p)));

        (int exitCode, string[] frame) = RunReport("prev", "find deploy", "summary", "quit");

        Assert.Equal(0, exitCode);
        Assert.Contains(frame, l => l.Contains("Monday, 2026-10-26", StringComparison.Ordinal));
        Assert.Contains(frame, l => Regex.IsMatch(l, @"│ +│ Deploy test +│ 9:00 +│"));
        Dictionary<string, (DateTime, byte[])> after = Directory.EnumerateFiles(data.Path)
            .ToDictionary(p => p, p => (File.GetLastWriteTimeUtc(p), File.ReadAllBytes(p)));
        Assert.Equal(before.Keys.Order(StringComparer.Ordinal), after.Keys.Order(StringComparer.Ordinal));
        foreach (KeyValuePair<string, (DateTime, byte[])> pair in before)
        {
            Assert.Equal(pair.Value.Item1, after[pair.Key].Item1);
            Assert.Equal(pair.Value.Item2, after[pair.Key].Item2);
        }

        Assert.Empty(Directory.EnumerateDirectories(data.Path));
    }

    // Job 107 ------------------------------------------------------------------------------

    private static string StatEntry(string kind, string time) => $"{{\"kind\":\"{kind}\",\"time\":\"{time}\"}}";

    /// <summary>Mon 8:00-16:00, Tue 8:05-16:35, Wed open from 9:00, an absence Thu, plus days outside the range.</summary>
    private void WriteStatisticsDays()
    {
        WriteReportDay("2026-11-01", StatEntry("arrival", "05:00"), StatEntry("leaving", "06:00"));
        WriteReportDay("2026-11-02", StatEntry("arrival", "08:00"), StatEntry("leaving", "16:00"));
        WriteReportDay("2026-11-03", StatEntry("arrival", "08:05"), StatEntry("leaving", "16:35"));
        WriteReportDay("2026-11-04", StatEntry("arrival", "09:00"));
        File.WriteAllText(DayFile("2026-11-05"), "{\"absence\":\"vacation\"}");
        WriteReportDay("2026-11-09", StatEntry("arrival", "05:00"), StatEntry("leaving", "06:00"));
    }

    // AC-8
    [Fact]
    public void Help_ListsStatistics_WithAliasSt()
    {
        TestConsole console = new TestConsole().Interactive().EmitAnsiSequences();
        console.Profile.Width = 120;
        console.Input.PushTextWithEnter("help");
        PageDown(console, HelpPages);
        console.Input.PushTextWithEnter("quit");

        new App(console, FixedTimeProvider.At20261027(), new DayStore(data.Path)).Run([]);
        string plain = Regex.Replace(console.Output, @"\x1b\[[0-9;?]*[A-Za-z]", string.Empty);

        Assert.Matches(@"│ statistics +│ st +│ Show average arrival, leaving and work time per weekday: <from> <to> +│", plain);
    }

    // AC-8
    [Theory]
    [InlineData("st")]
    [InlineData("ST")]
    [InlineData("/statistics")]
    [InlineData("/st")]
    public void Statistics_AliasAndSlash_DoTheSameAsStatistics(string typed)
    {
        WriteStatisticsDays();

        (_, string[] full) = RunReport("statistics 2026-11-02 2026-11-08", "quit");
        (_, string[] other) = RunReport(typed + " 2026-11-02 2026-11-08", "quit");

        Assert.Equal(full, other);
        Assert.Contains(other, l => l.Contains("All days", StringComparison.Ordinal));
    }

    // AC-1, AC-2, AC-3, AC-4
    [Fact]
    public void Statistics_ShowsWeekdayRows_AllDays_CaptionAndHidesTheQuicklist()
    {
        WriteStatisticsDays();
        File.WriteAllText(ProjectsFile, "{\"projects\":[{\"workItem\":1234,\"title\":\"Times\"}]}");

        (int exitCode, string[] frame) = RunReport("statistics 2026-11-02 2026-11-08", "quit");

        Assert.Equal(0, exitCode);
        Assert.Contains(frame, l => Regex.IsMatch(l, @"│ Weekday +│ Days │ Arrival │ Leaving │ Work time │"));
        int monday = Array.FindIndex(frame, l => Regex.IsMatch(l, @"│ Monday +│ +1 │ +8:00 │ +16:00 │ +8:00 │"));
        int tuesday = Array.FindIndex(frame, l => Regex.IsMatch(l, @"│ Tuesday +│ +1 │ +8:05 │ +16:35 │ +8:30 │"));
        int wednesday = Array.FindIndex(frame, l => Regex.IsMatch(l, @"│ Wednesday +│ +1 │ +9:00 │ +– │ +– │"));
        int all = Array.FindIndex(frame, l => Regex.IsMatch(l, @"│ All days +│ +3 │ +8:22 │ +16:18 │ +8:15 │"));
        Assert.True(monday > 0 && tuesday > monday && wednesday > tuesday && all > wednesday, string.Join("\n", frame));
        Assert.DoesNotContain(frame, l => l.Contains("Thursday", StringComparison.Ordinal) || l.Contains("Sunday", StringComparison.Ordinal));
        Assert.DoesNotContain(frame, l => l.Contains("5:00", StringComparison.Ordinal));
        Assert.Contains(frame, l => l.Trim() == "2026-11-02 – 2026-11-08");
        Assert.DoesNotContain(frame, l => l.Contains("Projects", StringComparison.Ordinal));
        Assert.DoesNotContain(frame, l => l.Contains("skipped", StringComparison.Ordinal));
    }

    // AC-4
    [Fact]
    public void Statistics_ColoursAndBoldAllDays()
    {
        WriteStatisticsDays();

        string output = RunAnsiCommands("statistics 2026-11-02 2026-11-08");

        Assert.Matches(@"\x1b\[(90|38;5;8)mWeekday\x1b\[0m", output);
        Assert.Matches(@"\x1b\[(90|38;5;8)m╭─", output);
        Assert.Matches(@"\x1b\[38;5;75m16:35\x1b\[0m", output);
        Assert.Matches(@"\x1b\[(90|38;5;8)m–\x1b\[0m", output);
        Assert.Matches(@"\x1b\[1mAll days\x1b\[0m", output);
        Assert.Matches(@"\x1b\[1;38;5;75m8:22\x1b\[0m", output);
        Assert.Matches(@"\x1b\[(90|38;5;8)m2026-11-02 – 2026-11-08", output);
    }

    // AC-5
    [Fact]
    public void Statistics_NoCountedDay_ShowsGreyNote_AndNoTable()
    {
        WriteStatisticsDays();

        (int exitCode, string[] frame) = RunReport("statistics 2026-11-05 2026-11-07", "quit");

        Assert.Equal(0, exitCode);
        Assert.Contains(frame, l => l.Contains("No working days between 2026-11-05 and 2026-11-07.", StringComparison.Ordinal));
        Assert.DoesNotContain(frame, l => l.Contains("Weekday", StringComparison.Ordinal));

        string ansi = RunAnsiCommands("statistics 2026-11-05 2026-11-07");
        Assert.Matches(@"\x1b\[(90|38;5;8)mNo working days between", ansi);
    }

    // AC-6
    [Theory]
    [InlineData("2026-11-02", "All days")]
    [InlineData("2026-11-05", "No working days")]
    public void Statistics_SkippedFile_ShowsYellowLineBelow(string from, string above)
    {
        WriteStatisticsDays();
        File.WriteAllText(DayFile("2026-11-06"), "{not json");
        string command = $"statistics {from} 2026-11-08";

        (_, string[] frame) = RunReport(command, "quit");

        int skipped = Array.FindIndex(frame, l => l.Contains("! 1 day file skipped", StringComparison.Ordinal));
        int index = Array.FindIndex(frame, l => l.Contains(above, StringComparison.Ordinal));
        Assert.True(index > 0 && skipped > index, string.Join("\n", frame));

        string ansi = RunAnsiCommands(command);
        Assert.Matches(@"\x1b\[(33|93|38;5;11)m! 1 day file skipped", ansi);
    }

    // AC-7
    [Theory]
    [InlineData("statistics", "statistics: say the first and the last day. Use statistics <from> <to> with dates as yyyy-MM-dd.")]
    [InlineData("st 2026-11-02", "statistics: say the first and the last day.")]
    [InlineData("statistics 2026-11-02 2026-11-08 x", "statistics: say the first and the last day.")]
    [InlineData("statistics 2026-11-2 2026-11-08", "statistics: 2026-11-2 is not a date.")]
    [InlineData("statistics 2026-02-30 2026-03-01", "statistics: 2026-02-30 is not a date.")]
    [InlineData("statistics [red]x 2026-11-08", "statistics: [red]x is not a date.")]
    [InlineData("statistics 2026-11-09 2026-11-08", "statistics: the first day 2026-11-09 is after the last day 2026-11-08.")]
    public void Statistics_BadInput_ShowsRedHint_AndLoopKeepsRunning(string command, string hint)
    {
        WriteStatisticsDays();

        (int exitCode, string output) = Run(c =>
        {
            c.Profile.Width = 200;
            c.Input.PushTextWithEnter(command);
            c.Input.PushTextWithEnter("quit");
        });

        Assert.Equal(0, exitCode);
        string[] frame = LastLayout(output);
        AssertOnHintRow(frame, hint);
        Assert.DoesNotContain(frame, l => l.Contains("Weekday", StringComparison.Ordinal));
        Assert.DoesNotContain("   at ", output);

        (_, string[] next) = RunReport(command, "statistics 2026-11-02 2026-11-08", "quit");
        Assert.Contains(next, l => l.Contains("All days", StringComparison.Ordinal));

        string ansi = RunAnsiCommands(command);
        Assert.Matches(@"\x1b\[(31|91|38;5;9)mstatistics: ", ansi);
    }

    // AC-7
    [Fact]
    public void Statistics_FolderThatCannotBeListed_ShowsRedHint()
    {
        UseSideLock();
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        UnixFileMode mode = File.GetUnixFileMode(data.Path);
        File.SetUnixFileMode(data.Path, UnixFileMode.None);
        try
        {
            try
            {
                Directory.EnumerateFileSystemEntries(data.Path).ToList();
                // Running as root: access can't be denied.
                return;
            }
            catch (UnauthorizedAccessException)
            {
            }

            (int exitCode, string output) = RunCommands("statistics 2026-11-02 2026-11-08", "quit");

            Assert.Equal(0, exitCode);
            Assert.StartsWith("statistics: the data folder cannot be listed", HintText(LastLayout(output), "statistics: the data folder"));
            Assert.DoesNotContain("   at ", output);
        }
        finally
        {
            File.SetUnixFileMode(data.Path, mode);
        }
    }

    // AC-9
    [Fact]
    public void Statistics_WritesNothing()
    {
        WriteStatisticsDays();
        File.WriteAllText(ProjectsFile, "{\"projects\":[{\"workItem\":1234,\"title\":\"Times\"}]}");
        File.WriteAllText(DayFile("2026-11-06"), "{not json");
        DateTime old = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        foreach (string file in Directory.EnumerateFiles(data.Path))
        {
            File.SetLastWriteTimeUtc(file, old);
        }

        Dictionary<string, (DateTime, byte[])> before = Directory.EnumerateFiles(data.Path)
            .ToDictionary(p => p, p => (File.GetLastWriteTimeUtc(p), File.ReadAllBytes(p)));

        (int exitCode, string[] frame) = RunReport("statistics 2026-11-01 2026-11-30", "st 2030-01-01 2030-12-31", "quit");

        Assert.Equal(0, exitCode);
        Assert.Contains(frame, l => l.Contains("No working days between 2030-01-01 and 2030-12-31.", StringComparison.Ordinal));
        Dictionary<string, (DateTime, byte[])> after = Directory.EnumerateFiles(data.Path)
            .ToDictionary(p => p, p => (File.GetLastWriteTimeUtc(p), File.ReadAllBytes(p)));
        Assert.Equal(before.Keys.Order(StringComparer.Ordinal), after.Keys.Order(StringComparer.Ordinal));
        foreach (KeyValuePair<string, (DateTime, byte[])> pair in before)
        {
            Assert.Equal(pair.Value.Item1, after[pair.Key].Item1);
            Assert.Equal(pair.Value.Item2, after[pair.Key].Item2);
        }

        Assert.Empty(Directory.EnumerateDirectories(data.Path));
    }

    // Job 108 ------------------------------------------------------------------------------

    /// <summary>The backup name for <see cref="FixedTimeProvider.At20261027"/> (18:30 UTC).</summary>
    private const string BackupName = "times-backup-2026-10-27-1830.zip";

    private static string BackupHint(string[] frame) => HintText(frame, "backup:");

    private void WriteBackupData(bool projects = true)
    {
        File.WriteAllText(DayFile("2026-11-02"), "{\"entries\":[{\"kind\":\"arrival\",\"time\":\"08:00\"}]}");
        File.WriteAllText(DayFile("2026-11-03"), "{not json");
        File.WriteAllText(Path.Combine(data.Path, "settings.json"), "{\"settings\":{}}");
        if (projects)
        {
            File.WriteAllText(ProjectsFile, "{\"projects\":[{\"workItem\":1234,\"title\":\"Times\"}]}");
        }
    }

    private Dictionary<string, (DateTime Written, byte[] Bytes)> DataSnapshot() =>
        Directory.EnumerateFileSystemEntries(data.Path, "*", SearchOption.AllDirectories)
            .ToDictionary(p => p, p => File.Exists(p) ? (File.GetLastWriteTimeUtc(p), File.ReadAllBytes(p)) : (Directory.GetLastWriteTimeUtc(p), Array.Empty<byte>()));

    private void AssertSameSnapshot(Dictionary<string, (DateTime Written, byte[] Bytes)> before)
    {
        Dictionary<string, (DateTime Written, byte[] Bytes)> after = DataSnapshot();
        Assert.Equal(before.Keys.Order(StringComparer.Ordinal), after.Keys.Order(StringComparer.Ordinal));
        foreach (KeyValuePair<string, (DateTime Written, byte[] Bytes)> pair in before)
        {
            Assert.Equal(pair.Value.Written, after[pair.Key].Written);
            Assert.Equal(pair.Value.Bytes, after[pair.Key].Bytes);
        }
    }

    private static string[] ZipEntryNames(string path)
    {
        using System.IO.Compression.ZipArchive zip = System.IO.Compression.ZipFile.OpenRead(path);
        return zip.Entries.Select(e => e.FullName).ToArray();
    }

    // AC-1, AC-6, AC-8
    [Fact]
    public void Backup_WritesTheZip_AndShowsThePath_AndChangesNoDataFile()
    {
        using TempFolder target = new TempFolder();
        WriteBackupData();
        Dictionary<string, (DateTime Written, byte[] Bytes)> before = DataSnapshot();

        (int exitCode, string[] frame) = RunExport($"backup {target.Path}");

        Assert.Equal(0, exitCode);
        string path = Path.Combine(target.Path, BackupName);
        Assert.Equal([BackupName], Listing(target.Path));
        Assert.Equal(["2026-11-02.json", "2026-11-03.json", "projects.json"], ZipEntryNames(path));
        Assert.Contains(frame, l => l.Contains($"Backed up 2 day files and projects.json to {path}", StringComparison.Ordinal));
        Assert.DoesNotContain(frame, l => l.Contains("skipped", StringComparison.Ordinal));
        AssertSameSnapshot(before);
    }

    // AC-6
    [Fact]
    public void Backup_WithoutProjects_LeavesOutTheProjectsPart()
    {
        using TempFolder target = new TempFolder();
        File.WriteAllText(DayFile("2026-11-02"), "{}");

        (_, string[] frame) = RunExport($"backup {target.Path}");

        string path = Path.Combine(target.Path, BackupName);
        Assert.Contains(frame, l => l.Contains($"Backed up 1 day file to {path}", StringComparison.Ordinal));
        Assert.DoesNotContain(frame, l => l.Contains("projects.json", StringComparison.Ordinal));
        Assert.Equal(["2026-11-02.json"], ZipEntryNames(path));
    }

    // AC-6
    [Fact]
    public void Backup_Colours_SteelBlueCountAndPath_YellowSkippedLine()
    {
        using TempFolder target = new TempFolder();
        WriteBackupData();
        File.WriteAllBytes(DayFile("2026-11-04"), new byte[DayStore.MaxFileBytes + 1]);

        string output = RunAnsiCommands($"backup {target.Path}");

        Assert.Matches(@"Backed up \x1b\[[0-9;]*38;5;75m2\x1b\[0m day files and projects\.json to \x1b\[[0-9;]*38;5;75m", output);
        Assert.Matches(@"\x1b\[(33|93|38;5;11)m! 1 file skipped", output);
        Assert.Equal(["2026-11-02.json", "2026-11-03.json", "projects.json"], ZipEntryNames(Path.Combine(target.Path, BackupName)));
    }

    // AC-2, AC-6
    [Fact]
    public void Backup_SkippedFiles_AreCountedOnTheYellowLine()
    {
        using TempFolder target = new TempFolder();
        WriteBackupData();
        File.WriteAllBytes(DayFile("2026-11-04"), new byte[DayStore.MaxFileBytes + 1]);
        File.WriteAllBytes(DayFile("2026-11-05"), new byte[DayStore.MaxFileBytes + 1]);

        (_, string[] frame) = RunExport($"backup {target.Path}");

        Assert.Contains(frame, l => l.Contains("Backed up 2 day files and projects.json to", StringComparison.Ordinal));
        Assert.Contains(frame, l => l.Contains("! 2 files skipped", StringComparison.Ordinal));
    }

    // AC-3
    [Theory]
    [InlineData("none", "backup: say the folder. Use backup <folder>.")]
    [InlineData("relative", "is not an absolute path.")]
    [InlineData("missing", "does not exist. Nothing was created.")]
    [InlineData("file", "is a file, not a folder.")]
    public void Backup_BadFolder_ShowsRedHint_AndCreatesNothing(string kind, string hint)
    {
        using TempFolder target = new TempFolder();
        string file = Path.Combine(target.Path, "plain.txt");
        File.WriteAllText(file, "x");
        string folder = kind switch
        {
            "none" => "   ",
            "relative" => "backups" + Guid.NewGuid().ToString("N"),
            "missing" => Path.Combine(target.Path, "missing"),
            _ => file,
        };
        WriteBackupData();
        Dictionary<string, (DateTime Written, byte[] Bytes)> before = DataSnapshot();
        string[] cwdBefore = WorkingDirectoryEntries();

        (int exitCode, string[] frame) = RunExport($"backup {folder}", "report 2026-11-02 2026-11-04");
        (_, string[] hintFrame) = RunExport($"backup {folder}");

        Assert.Equal(0, exitCode);
        Assert.Contains(frame, l => Regex.IsMatch(l, @"│ Total +│|No checkpoints"));
        string expected = kind switch
        {
            "none" => hint,
            "missing" => $"backup: the folder {folder} {hint}",
            _ => $"backup: {folder} {hint}",
        };
        Assert.Contains(expected, BackupHint(hintFrame));
        Assert.Equal(["plain.txt"], Listing(target.Path));
        Assert.Equal("x", File.ReadAllText(file));
        AssertSameSnapshot(before);
        Assert.Equal(cwdBefore, WorkingDirectoryEntries());
    }

    // AC-3
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Backup_FolderWithSpace_QuotedOrNot_IsTheWholeTrimmedArgument(bool quoted)
    {
        using TempFolder target = new TempFolder();
        string folder = Path.Combine(target.Path, "my backups");
        Directory.CreateDirectory(folder);
        WriteBackupData();
        string typed = quoted ? $"\"{folder}\"" : folder;

        (int exitCode, _) = RunExport($"backup   {typed}   ");

        Assert.Equal(0, exitCode);
        Assert.Equal([BackupName], Listing(folder));
    }

    // AC-3
    [Fact]
    public void Backup_TildeFolder_IsExpandedToTheHomeFolder()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string missing = "times-missing-" + Guid.NewGuid().ToString("N");

        (_, string[] frame) = RunExport($"backup ~/{missing}");

        Assert.Contains($"backup: the folder {Path.Combine(home, missing)} does not exist.", BackupHint(frame));
        Assert.False(Directory.Exists(Path.Combine(home, missing)));
    }

    // AC-4
    [Fact]
    public void Backup_DataFolderOrBelow_IsRefused_AndNothingIsWritten()
    {
        WriteBackupData();
        Directory.CreateDirectory(Path.Combine(data.Path, "sub", "deeper"));
        Dictionary<string, (DateTime Written, byte[] Bytes)> before = DataSnapshot();
        const string Refused = "backup: choose a folder outside the data folder.";

        string[] typed =
        [
            data.Path,
            data.Path + Path.DirectorySeparatorChar,
            Path.Combine(data.Path, "sub"),
            Path.Combine(data.Path, "sub", "deeper"),
            Path.Combine(data.Path, "sub", ".."),
        ];
        foreach (string folder in typed)
        {
            (int exitCode, string[] frame) = RunExport($"backup {folder}");
            Assert.Equal(0, exitCode);
            Assert.Equal(Refused, BackupHint(frame));
        }

        AssertSameSnapshot(before);
    }

    // Job 109, AC-2 to AC-4: case is ignored, so a case variant is refused on either kind of file system.
    [Fact]
    public void Backup_CaseVariantOfTheDataFolder_IsRefused_OnEitherFileSystem()
    {
        WriteBackupData();
        Dictionary<string, (DateTime Written, byte[] Bytes)> before = DataSnapshot();
        const string Refused = "backup: choose a folder outside the data folder.";
        string parent = Path.GetDirectoryName(data.Path)!;
        string name = Path.GetFileName(data.Path);
        string variant = Path.Combine(parent, name.ToUpperInvariant());
        Assert.NotEqual(name, name.ToUpperInvariant());

        if (Directory.Exists(variant))
        {
            // Case-insensitive file system: the variant is the data folder itself.
            (int exitCode, string[] frame) = RunExport($"backup {variant}");

            Assert.Equal(0, exitCode);
            Assert.Equal(Refused, BackupHint(frame));
            AssertSameSnapshot(before);
            return;
        }

        // Case-sensitive file system: the variant does not exist yet.
        (_, string[] missing) = RunExport($"backup {variant}");

        Assert.Equal($"backup: the folder {variant} does not exist. Nothing was created.", BackupHint(missing));
        Assert.False(Directory.Exists(variant));

        // A real, separate folder that differs only in case is still refused: case is ignored.
        Directory.CreateDirectory(variant);
        try
        {
            (int exitCode, string[] frame) = RunExport($"backup {variant}");

            Assert.Equal(0, exitCode);
            Assert.Equal(Refused, BackupHint(frame));
            Assert.Empty(Directory.GetFileSystemEntries(variant));
            AssertSameSnapshot(before);
        }
        finally
        {
            Directory.Delete(variant, recursive: true);
        }
    }

    // AC-4
    [Fact]
    public void Backup_SymlinkIntoTheDataFolder_IsRefused_SiblingIsAllowed()
    {
        WriteBackupData();
        Directory.CreateDirectory(Path.Combine(data.Path, "sub"));
        string sibling = data.Path + "-old";
        Directory.CreateDirectory(sibling);
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                using TempFolder outside = new TempFolder();
                string link = Path.Combine(outside.Path, "link");
                Directory.CreateSymbolicLink(link, Path.Combine(data.Path, "sub"));
                Dictionary<string, (DateTime Written, byte[] Bytes)> before = DataSnapshot();

                (_, string[] refused) = RunExport($"backup {link}");

                Assert.Equal("backup: choose a folder outside the data folder.", BackupHint(refused));
                AssertSameSnapshot(before);
            }

            (_, string[] frame) = RunExport($"backup {sibling}");

            Assert.Equal([BackupName], Listing(sibling));
            Assert.Contains(frame, l => l.Contains("Backed up 2 day files", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(sibling, recursive: true);
        }
    }

    // AC-5
    [Fact]
    public void Backup_SecondInTheSameMinute_IsRefused_AndTheFirstStaysByteIdentical()
    {
        using TempFolder target = new TempFolder();
        WriteBackupData();
        string path = Path.Combine(target.Path, BackupName);

        RunExport($"backup {target.Path}");
        byte[] first = File.ReadAllBytes(path);
        File.WriteAllText(DayFile("2026-11-06"), "{}");
        (int exitCode, string[] frame) = RunExport($"backup {target.Path}");

        Assert.Equal(0, exitCode);
        Assert.Equal(first, File.ReadAllBytes(path));
        Assert.Equal([BackupName], Listing(target.Path));
        Assert.Equal($"backup: {path} already exists. Move or delete it first.", BackupHint(frame));
        Assert.DoesNotContain(frame, l => l.Contains("Backed up", StringComparison.Ordinal));
    }

    // AC-5
    [Fact]
    public void Backup_DanglingSymlink_IsNotFollowed()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using TempFolder target = new TempFolder();
        using TempFolder elsewhere = new TempFolder();
        string link = Path.Combine(target.Path, BackupName);
        string pointsTo = Path.Combine(elsewhere.Path, "victim.zip");
        File.CreateSymbolicLink(link, pointsTo);
        WriteBackupData();

        (_, string[] frame) = RunExport($"backup {target.Path}");

        Assert.Equal($"backup: {link} already exists. Move or delete it first.", BackupHint(frame));
        Assert.False(File.Exists(pointsTo));
        Assert.Empty(Listing(elsewhere.Path));
    }

    // AC-5
    [Fact]
    public void Backup_ReadOnlyFolder_ShowsRedHint_AndLeavesNoFile()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using TempFolder target = new TempFolder();
        WriteBackupData();
        UnixFileMode mode = File.GetUnixFileMode(target.Path);
        File.SetUnixFileMode(target.Path, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            try
            {
                File.WriteAllText(Path.Combine(target.Path, "probe"), "x");
                // Running as root: access can't be denied.
                return;
            }
            catch (UnauthorizedAccessException)
            {
            }

            (int exitCode, string[] frame) = RunExport($"backup {target.Path}");

            Assert.Equal(0, exitCode);
            Assert.StartsWith($"backup: could not write {Path.Combine(target.Path, BackupName)}.", BackupHint(frame));
            Assert.Empty(Listing(target.Path));
        }
        finally
        {
            File.SetUnixFileMode(target.Path, mode);
        }
    }

    // AC-7
    [Fact]
    public void Backup_NothingToBackUp_ShowsGreyNote_AndWritesNoFile()
    {
        using TempFolder target = new TempFolder();
        File.WriteAllText(Path.Combine(data.Path, "settings.json"), "{}");

        (int exitCode, string[] frame) = RunExport($"backup {target.Path}");
        string output = RunAnsiCommands($"backup {target.Path}");

        Assert.Equal(0, exitCode);
        Assert.Contains(frame, l => l.Contains("Nothing to back up. No file was written.", StringComparison.Ordinal));
        Assert.Matches(@"\x1b\[[0-9;]*(90|38;5;8)mNothing to back up\. No file was written\.", output);
        Assert.Empty(Listing(target.Path));
    }

    // AC-7
    [Fact]
    public void Backup_MissingDataFolder_ShowsGreyNote_AndCreatesNoFolder()
    {
        using TempFolder target = new TempFolder();
        using TempFolder missing = new TempFolder(create: false);

        (int exitCode, string output) = Run(c =>
        {
            c.Profile.Width = 250;
            c.Input.PushTextWithEnter($"backup {target.Path}");
            c.Input.PushTextWithEnter("quit");
        }, dataFolder: missing.Path);

        Assert.Equal(0, exitCode);
        Assert.Contains("Nothing to back up. No file was written.", output);
        Assert.Empty(Listing(target.Path));
        AssertLeftNothing(missing.Path);
    }

    // AC-9
    [Theory]
    [InlineData("bu")]
    [InlineData("/backup")]
    [InlineData("/bu")]
    public void Backup_AliasAndSlash_WriteTheSameZip(string typed)
    {
        using TempFolder target = new TempFolder();
        WriteBackupData();

        (int exitCode, string[] frame) = RunExport($"{typed} {target.Path}");

        Assert.Equal(0, exitCode);
        string path = Path.Combine(target.Path, BackupName);
        Assert.Equal(["2026-11-02.json", "2026-11-03.json", "projects.json"], ZipEntryNames(path));
        Assert.Contains(frame, l => l.Contains($"Backed up 2 day files and projects.json to {path}", StringComparison.Ordinal));
    }

    // AC-9
    [Fact]
    public void Help_ListsBackup_WithAliasBu()
    {
        TestConsole console = new TestConsole().Interactive().EmitAnsiSequences();
        console.Profile.Width = 120;
        console.Input.PushTextWithEnter("help");
        console.Input.PushTextWithEnter("quit");

        new App(console, FixedTimeProvider.At20261027(), new DayStore(data.Path)).Run([]);
        string plain = Regex.Replace(console.Output, @"\x1b\[[0-9;?]*[A-Za-z]", string.Empty);

        Assert.Matches(@"│ backup +│ bu +│ Zip your day files and projects into a folder: <folder> +│", plain);
        Assert.Matches(@"\x1b\[[0-9;]*38;5;75mbu\x1b\[0m", console.Output);
    }

    // AC-10
    [Fact]
    public void Backup_MarkupAndControlCharactersInTheFolder_ShowLiterally()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using TempFolder target = new TempFolder();
        string folder = Path.Combine(target.Path, "[red]x‮y");
        Directory.CreateDirectory(folder);
        WriteBackupData();

        // The test console types only chars up to U+00FF as text, so the U+202E is pushed as a key.
        (int exitCode, string output) = Run(c =>
        {
            c.Profile.Height = 40;
            c.Profile.Width = 250;
            c.Input.PushText($"backup {Path.Combine(target.Path, "[red]x")}");
            c.Input.PushKey(new ConsoleKeyInfo('‮', ConsoleKey.NoName, false, false, false));
            c.Input.PushTextWithEnter("y");
            c.Input.PushText($"backup {Path.Combine(target.Path, "[red]missing")}");
            c.Input.PushKey(new ConsoleKeyInfo('‮', ConsoleKey.NoName, false, false, false));
            c.Input.PushTextWithEnter("z");
            c.Input.PushTextWithEnter("quit");
        });
        List<string[]> frames = Frames(output);
        string[] success = frames[^2];
        string[] hint = frames[^1];

        Assert.Equal(0, exitCode);
        Assert.Equal([BackupName], Listing(folder));
        string shown = Path.Combine(target.Path, "[red]xy", BackupName);
        Assert.Contains(success, l => l.Contains($"Backed up 2 day files and projects.json to {shown}", StringComparison.Ordinal));
        Assert.Contains($"backup: the folder {Path.Combine(target.Path, "[red]missingz")} does not exist.", BackupHint(hint));
        Assert.DoesNotContain(success.Concat(hint), l => l.Contains('‮', StringComparison.Ordinal));
    }

    // AC-8
    [Fact]
    public void Backup_UnreadableFiles_StillChangeNothingInTheDataFolder()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using TempFolder target = new TempFolder();
        WriteBackupData();
        SpecialFiles.MakeFifo(DayFile("2026-11-07"));
        Dictionary<string, (DateTime Written, byte[] Bytes)> before = Directory.EnumerateFileSystemEntries(data.Path)
            .Where(p => !SpecialFiles.IsFifo(p))
            .ToDictionary(p => p, p => (File.GetLastWriteTimeUtc(p), File.ReadAllBytes(p)));

        (_, string[] frame) = RunExport($"backup {target.Path}");

        Assert.Contains(frame, l => l.Contains("! 1 file skipped", StringComparison.Ordinal));
        Assert.True(SpecialFiles.IsFifo(DayFile("2026-11-07")));
        foreach (KeyValuePair<string, (DateTime Written, byte[] Bytes)> pair in before)
        {
            Assert.Equal(pair.Value.Item1, File.GetLastWriteTimeUtc(pair.Key));
            Assert.Equal(pair.Value.Item2, File.ReadAllBytes(pair.Key));
        }

        Assert.Equal(5, Directory.EnumerateFileSystemEntries(data.Path).Count());
    }

    // Background events (job 110) -----------------------------------------------------------

    private static readonly DateOnly Shown = new(2026, 10, 27);

    private static DayRecord Arrived(int hour) => new DayRecord().Add(new DayEntry(EntryKind.Arrival, new TimeOnly(hour, 0)));

    /// <summary>Runs the app with ANSI output and <paramref name="events"/>, on <paramref name="console"/> (or a new one).</summary>
    private string RunWithEvents(Action<TestConsole> input, IBackgroundEvents events, string? folder = null,
        TestConsole? console = null, IAnsiConsole? appConsole = null)
    {
        console ??= new TestConsole().Interactive().EmitAnsiSequences();
        input(console);
        App app = new App(appConsole ?? console, FixedTimeProvider.At20261027(), new DayStore(folder ?? data.Path), ManyCommands(),
            events: events);
        Assert.Equal(0, app.Run([]));
        return console.Output;
    }

    /// <summary>The ANSI output of the same keys without events, in <paramref name="folder"/>.</summary>
    private static string RunPlain(Action<TestConsole> input, string folder)
    {
        TestConsole console = new TestConsole().Interactive().EmitAnsiSequences();
        input(console);
        Assert.Equal(0, new App(console, FixedTimeProvider.At20261027(), new DayStore(folder), ManyCommands()).Run([]));
        return console.Output;
    }

    // AC-5
    [Fact]
    public void Event_BetweenTypedKeys_KeepsTheLine_AndTheCommandRuns()
    {
        ScriptedEvents events = new ScriptedEvents();
        int appliedAt = -1;
        TestConsole console = new TestConsole().Interactive().EmitAnsiSequences();
        events.At(3, new BackgroundEvent(() =>
        {
            appliedAt = console.Output.Length;
            return true;
        }));

        string output = RunWithEvents(c =>
        {
            c.Input.PushText("he");
            c.Input.PushTextWithEnter("lp");
            c.Input.PushTextWithEnter("quit");
        }, events, console: console);

        Assert.True(appliedAt > 0);
        Assert.Contains(HelpFirstRow, output[appliedAt..]);
        Assert.DoesNotContain("Unknown command", output);
    }

    // AC-6
    [Fact]
    public void Event_ThatBooks_RedrawsTheTopArea_InPlace_KeepingHintAndQuicklist()
    {
        ScriptedEvents events = new ScriptedEvents();
        int appliedAt = -1;
        TestConsole console = new TestConsole().Interactive().EmitAnsiSequences();
        // "bogus" and Enter take waits 1-6; wait 7 comes before the next line's first key.
        events.At(7, new BackgroundEvent(() =>
        {
            appliedAt = console.Output.Length;
            new DayStore(data.Path).Save(Shown, Arrived(8));
            return true;
        }));

        string output = RunWithEvents(c =>
        {
            c.Input.PushTextWithEnter("bogus");
            c.Input.PushTextWithEnter(" quit");
        }, events, console: console);

        int clear = output.IndexOf(ClearScreen, appliedAt, StringComparison.Ordinal);
        Assert.True(clear > appliedAt);
        List<AnsiWrite> writes = AnsiWrites.Parse(output[appliedAt..clear]);
        int input = writes.FindIndex(w => w.Row == 23 && w.Column == 5);
        List<AnsiWrite> redraw = writes.Take(input).ToList();

        // Only the top area's rows, each once, at column 1: the hint row (21) and the input box stay.
        Assert.Equal(Enumerable.Range(1, TopHeight), redraw.Select(w => w.Row));
        Assert.All(redraw, w => Assert.Equal(1, w.Column));
        string top = string.Join('\n', redraw.Select(w => w.Text));
        Assert.Matches(@"1 +AR +08:00", top);
        Assert.Contains("Projects", top);

        // The typed key after the event lands on the same screen: no clear in between.
        Assert.Contains(writes, w => w.Row == 23 && w.Text.StartsWith(" q", StringComparison.Ordinal));

        // The rows match a fresh run that had the entry from the start, and the hint row still holds the hint.
        using TempFolder plain = new TempFolder();
        new DayStore(plain.Path).Save(Shown, Arrived(8));
        string expected = RunPlain(c =>
        {
            c.Input.PushTextWithEnter("bogus");
            c.Input.PushTextWithEnter("quit");
        }, plain.Path);
        Assert.Equal(TopScreens(expected, 1)[0], redraw.Select(w => w.Text.TrimEnd()).ToArray());
        Assert.Contains("Unknown command", AnsiFrames(output)[1][20]);
    }

    // AC-6
    [Fact]
    public void Event_KeepsTheScrollOffset_TheMessage_AndTheHiddenQuicklist()
    {
        ScriptedEvents events = new ScriptedEvents();
        // "help" and Enter take waits 1-5, PageDown wait 6; wait 7 is before the Enter.
        events.At(7, new BackgroundEvent(() =>
        {
            new DayStore(data.Path).Save(Shown, Arrived(8));
            return true;
        }));
        Action<TestConsole> keys = c =>
        {
            c.Profile.Height = 12;
            c.Input.PushTextWithEnter("help");
            c.Input.PushKey(ConsoleKey.PageDown);
            c.Input.PushKey(ConsoleKey.Enter);
            c.Input.PushTextWithEnter("quit");
        };

        string output = RunWithEvents(keys, events);

        using TempFolder plain = new TempFolder();
        new DayStore(plain.Path).Save(Shown, Arrived(8));
        string expected = RunPlain(keys, plain.Path);

        List<string[]> screens = TopScreens(output, 1, 8);
        Assert.Equal(3, screens.Count);
        Assert.StartsWith("↑ 6 more lines above", screens[2][0], StringComparison.Ordinal);
        Assert.Equal(TopScreens(expected, 1, 8)[1], screens[2]);
        Assert.DoesNotContain(screens[2], l => l.Contains("Projects", StringComparison.Ordinal));
    }

    // AC-6
    [Fact]
    public void Event_ThatShortensTheContent_ClampsTheOffset()
    {
        new DayStore(data.Path).Save(Shown, new DayRecord(
            [new DayEntry(EntryKind.Arrival, new TimeOnly(8, 0)), new DayEntry(EntryKind.BreakStart, new TimeOnly(12, 0)),
             new DayEntry(EntryKind.BreakEnd, new TimeOnly(12, 30)), new DayEntry(EntryKind.Leaving, new TimeOnly(17, 0))]));
        ScriptedEvents events = new ScriptedEvents();
        // "help" and Enter take waits 1-5, the 20 PageDowns waits 6-25; wait 26 is before the Enter.
        events.At(26, new BackgroundEvent(() =>
        {
            new DayStore(data.Path).Save(Shown, new DayRecord());
            return true;
        }));
        Action<TestConsole> keys = c =>
        {
            c.Profile.Height = 12;
            c.Input.PushTextWithEnter("help");
            PageDown(c, 20);
            c.Input.PushKey(ConsoleKey.Enter);
            c.Input.PushTextWithEnter("quit");
        };

        string output = RunWithEvents(keys, events);

        using TempFolder plain = new TempFolder();
        new DayStore(plain.Path).Save(Shown, new DayRecord());
        string expected = RunPlain(keys, plain.Path);

        List<string[]> screens = TopScreens(output, 1, 8);
        List<string[]> bottom = TopScreens(expected, 1, 8);
        // The last rows hold the data folder, which differs between the two runs (same length).
        Assert.Equal(bottom[^1][..^2], screens[^1][..^2]);
        Assert.Equal(bottom[^1][^1].Length, screens[^1][^1].Length);
        Assert.Equal(screens[^2][^1], screens[^1][^1]);
        Assert.NotEqual(screens[^2][0], screens[^1][0]);
    }

    // AC-7
    [Fact]
    public void Event_ThatChangesNothing_WritesNothing()
    {
        ScriptedEvents events = new ScriptedEvents();
        TestConsole console = new TestConsole().Interactive().EmitAnsiSequences();
        int before = -1;
        int after = -1;
        events.At(2, new BackgroundEvent(() =>
        {
            before = console.Output.Length;
            return false;
        }));
        events.OnWait = call =>
        {
            if (call == 3)
            {
                after = console.Output.Length;
            }
        };

        RunWithEvents(c => c.Input.PushTextWithEnter("quit"), events, console: console);

        Assert.True(before > 0);
        Assert.Equal(before, after);
    }

    // AC-8
    [Fact]
    public void Event_ThatThrows_IsDropped_AndTheNextOneStillRuns()
    {
        ScriptedEvents events = new ScriptedEvents();
        TestConsole console = new TestConsole().Interactive().EmitAnsiSequences();
        int appliedAt = -1;
        events.At(3,
            new BackgroundEvent(() => throw new IOException("broken event")),
            new BackgroundEvent(() =>
            {
                appliedAt = console.Output.Length;
                new DayStore(data.Path).Save(Shown, Arrived(9));
                return true;
            }));

        string output = RunWithEvents(c =>
        {
            c.Input.PushText("he");
            c.Input.PushTextWithEnter("lp");
            c.Input.PushTextWithEnter("quit");
        }, events, console: console);

        Assert.True(appliedAt > 0);
        Assert.Matches(@"1 +AR +09:00", string.Join('\n', AnsiWrites.Parse(output[appliedAt..]).Select(w => w.Text)));
        Assert.Contains(HelpFirstRow, output[appliedAt..]);
        Assert.DoesNotContain("broken event", output);
        Assert.Equal([new DayEntry(EntryKind.Arrival, new TimeOnly(9, 0))], LoadDay().Entries);
    }

    // AC-9
    [Fact]
    public void Events_PostedWhileSettingsAreOpen_AreApplied_InOrder_AtTheNextPrompt()
    {
        TestConsole console = new TestConsole().Interactive().EmitAnsiSequences();
        HookedInput input = new HookedInput(console.Input);
        BackgroundEventQueue events = new BackgroundEventQueue();
        List<string> log = [];
        int clearsAtPost = -1;
        List<int> clearsAtApply = [];
        int appliedAt = -1;
        // Keys 1-9 are "settings" and Enter; key 10 is the Esc the settings panel reads.
        input.BeforeKey = key =>
        {
            if (key == 10)
            {
                clearsAtPost = Count(console.Output, ClearScreen);
                events.Post(new BackgroundEvent(() =>
                {
                    log.Add("e1");
                    clearsAtApply.Add(Count(console.Output, ClearScreen));
                    appliedAt = console.Output.Length;
                    new DayStore(data.Path).Save(Shown, Arrived(8));
                    return true;
                }));
                events.Post(new BackgroundEvent(() =>
                {
                    log.Add("e2");
                    clearsAtApply.Add(Count(console.Output, ClearScreen));
                    return false;
                }));
            }
        };

        string output = RunWithEvents(c =>
        {
            c.Input.PushTextWithEnter("settings");
            c.Input.PushKey(ConsoleKey.Escape);
            c.Input.PushTextWithEnter("quit");
        }, events, console: console, appConsole: new InputOverrideConsole(console, input));

        Assert.Equal(["e1", "e2"], log);
        // Not applied in the panel: only after its Esc and the full redraw that follows.
        Assert.Equal([clearsAtPost + 1, clearsAtPost + 1], clearsAtApply);
        Assert.Matches(@"1 +AR +08:00", string.Join('\n', AnsiWrites.Parse(output[appliedAt..]).Select(w => w.Text)));
    }

    // AC-10
    [Fact]
    public void WithoutEvents_TheLoopNeverAsksWhetherAKeyIsAvailable_BeforeAKey()
    {
        TestConsole console = new TestConsole().Interactive().EmitAnsiSequences();
        HookedInput input = new HookedInput(console.Input);
        console.Input.PushTextWithEnter("help");
        console.Input.PushTextWithEnter("quit");

        int exitCode = new App(new InputOverrideConsole(console, input), FixedTimeProvider.At20261027(), new DayStore(data.Path)).Run([]);

        Assert.Equal(0, exitCode);
        Assert.Equal(0, input.KeyAvailableCalls);
    }

    // Live clock (job 112) -------------------------------------------------------------------

    private static SettableTimeProvider LiveTime(int day, int hour, int minute, int second) =>
        new SettableTimeProvider(new DateTimeOffset(2026, 10, day, hour, minute, second, TimeSpan.Zero));

    /// <summary>Moves the clock to <paramref name="at"/> and fires the live clock's timer, as the timer thread would.</summary>
    private static void Tick(SettableTimeProvider time, DateTimeOffset at)
    {
        time.UtcNow = at;
        Assert.Single(time.Timers).Fire();
    }

    /// <summary>Runs the app with the live clock on, on an ANSI console, with <paramref name="events"/>.</summary>
    private string RunLive(SettableTimeProvider time, IBackgroundEvents events, TestConsole console, IAnsiConsole? appConsole = null,
        CommandRegistry? registry = null)
    {
        App app = new App(appConsole ?? console, time, new DayStore(data.Path), registry, events: events, liveClock: true);
        Assert.Equal(0, app.Run([]));
        return console.Output;
    }

    // The last cursor position (row, column) set in output.
    private static (int Row, int Column) LastCursor(string output)
    {
        AnsiWrite last = AnsiWrites.Parse(output)[^1];
        return (last.Row, last.Column);
    }

    // AC-2, AC-3: the first frame on today shows the now row and moves the day and the balance with it.
    [Fact]
    public void LiveClock_Today_Working_ShowsTheNowRow_AndTheLiveBalance()
    {
        new DayStore(data.Path).Save(Shown, Arrived(8));
        SettableTimeProvider time = LiveTime(27, 18, 30, 20);
        TestConsole console = new TestConsole().Interactive().EmitAnsiSequences();
        console.Input.PushTextWithEnter("quit");

        string output = RunLive(time, new ScriptedEvents(), console);

        string[] frame = AnsiFrames(output)[0];
        Assert.Contains(frame, l => Regex.IsMatch(l, @"^ +now +18:30 +left -2:30"));
        Assert.Contains(frame, l => l.Contains("Target 8:00 · day +2:30 · balance +2:30", StringComparison.Ordinal));
    }

    // AC-6
    [Fact]
    public void LiveClock_Tick_RewritesOnlyTheTopArea_KeepingLineCursorAndPopup_AndEnterRunsTheLine()
    {
        new DayStore(data.Path).Save(Shown, Arrived(8));
        SettableTimeProvider time = LiveTime(27, 18, 30, 20);
        TestConsole console = new TestConsole().Interactive().EmitAnsiSequences();
        ScriptedEvents scripted = new ScriptedEvents();
        ObservedEvents events = new ObservedEvents(scripted);
        int beforeTick = -1;
        int appliedAt = -1;
        // "arr" takes waits 1-3; wait 4 comes before the Enter, with the popup open.
        scripted.OnWait = call =>
        {
            if (call == 4)
            {
                beforeTick = console.Output.Length;
                Tick(time, new DateTimeOffset(2026, 10, 27, 18, 31, 5, TimeSpan.Zero));
            }
        };
        events.OnApply = () => appliedAt = console.Output.Length;
        console.Input.PushTextWithEnter("arr");
        console.Input.PushTextWithEnter("quit");

        string output = RunLive(time, events, console);

        Assert.Equal([true], events.Results);
        Assert.Equal(beforeTick, appliedAt);
        int clear = output.IndexOf(ClearScreen, appliedAt, StringComparison.Ordinal);
        Assert.True(clear > appliedAt);
        string typed = output[output.LastIndexOf(ClearScreen, appliedAt, StringComparison.Ordinal)..appliedAt];
        List<AnsiWrite> before = AnsiWrites.Parse(typed);
        List<AnsiWrite> writes = AnsiWrites.Parse(output[appliedAt..clear]);

        // First the top-area rows, each once, at column 1.
        List<AnsiWrite> top = writes.TakeWhile(w => w.Column == 1 && w.Row <= TopHeight).ToList();
        Assert.Equal(Enumerable.Range(1, TopHeight), top.Select(w => w.Row));
        string topText = string.Join('\n', top.Select(w => w.Text));
        Assert.Matches(@"(?m)^ +now +18:31 +left -2:31", topText);
        Assert.Contains("day +2:31", topText);
        Assert.DoesNotContain("18:30", topText);

        // Then the popup rows as they were, and the input line with the same text and cursor.
        List<AnsiWrite> after = writes.Skip(top.Count).ToList();
        List<string> popupBefore = before.Where(w => w.StartsWithCommand && w.Row <= 21).Select(w => w.Text).ToList();
        List<string> popupAfter = after.Where(w => w.StartsWithCommand && w.Row <= 21).Select(w => w.Text).ToList();
        Assert.NotEmpty(popupAfter);
        Assert.Equal(popupBefore.TakeLast(popupAfter.Count), popupAfter);
        Assert.Contains(after, w => w.Row == 23 && w.Text.Contains("arr", StringComparison.Ordinal));
        Assert.Equal(LastCursor(typed), LastCursor(output[appliedAt..clear]));

        // No input-box border and no hint row outside the popup were written.
        Assert.DoesNotContain(writes, w => w.Row == 22 || w.Row == 24);
        int popupTop = after.Where(w => w.StartsWithCommand).Min(w => w.Row);
        Assert.DoesNotContain(after, w => w.Row > TopHeight && w.Row < popupTop);

        // Enter runs "arr" as typed: the same as a run without the clock.
        using TempFolder plain = new TempFolder();
        new DayStore(plain.Path).Save(Shown, Arrived(8));
        TestConsole plainConsole = new TestConsole().Interactive().EmitAnsiSequences();
        plainConsole.Input.PushTextWithEnter("arr");
        plainConsole.Input.PushTextWithEnter("quit");
        Assert.Equal(0, new App(plainConsole, LiveTime(27, 18, 31, 5), new DayStore(plain.Path)).Run([]));
        Assert.Equal(AnsiFrames(plainConsole.Output)[1][20], AnsiFrames(output)[1][20]);
        Assert.Equal(new DayStore(plain.Path).Load(Shown), LoadDay());
    }

    // AC-7
    [Theory]
    [InlineData("prev")]
    [InlineData("next")]
    public void LiveClock_Tick_OnAnotherDay_ReturnsFalse_AndWritesNothing(string command)
    {
        new DayStore(data.Path).Save(Shown, Arrived(8));
        SettableTimeProvider time = LiveTime(27, 18, 30, 20);
        TestConsole console = new TestConsole().Interactive().EmitAnsiSequences();
        ScriptedEvents scripted = new ScriptedEvents();
        ObservedEvents events = new ObservedEvents(scripted);
        int fired = -1;
        int next = -1;
        // The command and Enter take waits 1 to n; the next wait is before "quit", the one after it ends that round.
        int at = command.Length + 2;
        scripted.OnWait = call =>
        {
            if (call == at)
            {
                Tick(time, new DateTimeOffset(2026, 10, 27, 18, 31, 5, TimeSpan.Zero));
                fired = console.Output.Length;
            }
            else if (call == at + 1)
            {
                next = console.Output.Length;
            }
        };
        console.Input.PushTextWithEnter(command);
        console.Input.PushTextWithEnter("quit");

        string output = RunLive(time, events, console);

        Assert.Equal([false], events.Results);
        Assert.True(fired > 0);
        Assert.Equal(fired, next);
        Assert.DoesNotContain(AnsiFrames(output)[1], l => l.Contains("now", StringComparison.Ordinal));
    }

    // AC-8
    [Fact]
    public void LiveClock_Tick_AfterMidnight_KeepsTheShownDay_ReturnsFalse_AndWritesNothing()
    {
        new DayStore(data.Path).Save(Shown, Arrived(8));
        SettableTimeProvider time = LiveTime(27, 23, 59, 30);
        TestConsole console = new TestConsole().Interactive().EmitAnsiSequences();
        ScriptedEvents scripted = new ScriptedEvents();
        ObservedEvents events = new ObservedEvents(scripted);
        int fired = -1;
        int next = -1;
        scripted.OnWait = call =>
        {
            if (call == 1)
            {
                Tick(time, new DateTimeOffset(2026, 10, 28, 0, 0, 0, TimeSpan.Zero));
                fired = console.Output.Length;
            }
            else if (call == 2)
            {
                next = console.Output.Length;
            }
        };
        console.Input.PushTextWithEnter("quit");

        string output = RunLive(time, events, console);

        Assert.Equal([false], events.Results);
        Assert.Equal(fired, next);
        Assert.Contains(AnsiFrames(output)[0], l => Regex.IsMatch(l, @"^ +now +23:59"));
        // Only the first draw and the quit clear the screen: the tick drew no frame.
        Assert.Equal(2, Count(output, ClearScreen));
        Assert.DoesNotContain("2026-10-28", output);
    }

    // AC-9
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LiveClock_Tick_WhileSettingsOrAValuePromptIsOpen_WritesNothing_AndIsAppliedAtTheNextPrompt(bool prompt)
    {
        new DayStore(data.Path).Save(Shown, Arrived(8));
        SettableTimeProvider time = LiveTime(27, 18, 30, 20);
        TestConsole console = new TestConsole().Interactive().EmitAnsiSequences();
        HookedInput input = new HookedInput(console.Input);
        ObservedEvents events = new ObservedEvents(new BackgroundEventQueue());
        int clearsAtTick = -1;
        int clearsAtApply = -1;
        int appliedAt = -1;
        // Keys 1-9 are "settings" and Enter. In the panel, key 10 is the Esc that closes it; with the prompt,
        // key 10 is the Enter that opens daily-target's value prompt, key 11 a typed "5", 12 and 13 two Esc.
        int tickKey = prompt ? 11 : 10;
        input.BeforeKey = key =>
        {
            if (key == tickKey)
            {
                clearsAtTick = Count(console.Output, ClearScreen);
                Tick(time, new DateTimeOffset(2026, 10, 27, 18, 31, 5, TimeSpan.Zero));
            }
        };
        events.OnApply = () =>
        {
            clearsAtApply = Count(console.Output, ClearScreen);
            appliedAt = console.Output.Length;
        };
        console.Input.PushTextWithEnter("settings");
        if (prompt)
        {
            console.Input.PushKey(ConsoleKey.Enter);
            console.Input.PushText("5");
            console.Input.PushKey(ConsoleKey.Escape);
        }

        console.Input.PushKey(ConsoleKey.Escape);
        console.Input.PushTextWithEnter("quit");

        string output = RunLive(time, events, console, new InputOverrideConsole(console, input));

        Assert.Equal([true], events.Results);
        // Not applied in the panel or the prompt: only after the panel closed and the full redraw that follows.
        Assert.Equal(clearsAtTick + 1, clearsAtApply);
        Assert.Matches(@"(?m)^ +now +18:31 +left -2:31",
            string.Join('\n', AnsiWrites.Parse(output[appliedAt..]).Select(w => w.Text)));
        if (prompt)
        {
            Assert.Contains("daily-target: a whole number", output);
        }
    }

    // AC-10
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void LiveClock_WithoutEventsOrSwitchedOff_NeverCreatesATimer(bool withEvents, bool liveClock)
    {
        SettableTimeProvider time = LiveTime(27, 18, 30, 20);
        TestConsole console = new TestConsole().Interactive();
        console.Input.PushTextWithEnter("quit");

        App app = new App(console, time, new DayStore(data.Path), events: withEvents ? new ScriptedEvents() : null, liveClock: liveClock);

        Assert.Equal(0, app.Run([]));
        Assert.Empty(time.Timers);
    }

    // AC-10
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LiveClock_TimerIsDisposed_OnQuit_AndAtTheEndOfInput(bool quit)
    {
        SettableTimeProvider time = LiveTime(27, 18, 30, 20);
        TestConsole test = new TestConsole().Interactive();
        IAnsiConsole console = new InputOverrideConsole(test, new EndingInput(quit ? "quit\r" : "help\r"));
        ScriptedEvents events = new ScriptedEvents();
        bool disposedWhileRunning = true;
        events.OnWait = _ => disposedWhileRunning = time.Timers.Single().Disposed;

        Assert.Equal(0, new App(console, time, new DayStore(data.Path), events: events, liveClock: true).Run([]));

        ManualTimer timer = Assert.Single(time.Timers);
        Assert.False(disposedWhileRunning);
        Assert.True(timer.Disposed);
        Assert.Equal([TimeSpan.FromSeconds(40)], timer.DueTimes);
    }

    // AC-10: an exception out of the loop disposes the timer too.
    [Fact]
    public void LiveClock_TimerIsDisposed_WhenTheLoopThrows()
    {
        CommandRegistry registry = CommandRegistry.CreateDefault();
        registry.Register("boom", "bm", "Throws while drawing", _ => new CommandResult(new ThrowingRenderable()));
        SettableTimeProvider time = LiveTime(27, 18, 30, 20);
        TestConsole console = new TestConsole().Interactive();
        console.Input.PushTextWithEnter("boom");
        console.Input.PushTextWithEnter("quit");
        App app = new App(console, time, new DayStore(data.Path), registry, events: new ScriptedEvents(), liveClock: true);

        Assert.Throws<InvalidOperationException>(() => app.Run([]));

        Assert.True(Assert.Single(time.Timers).Disposed);
    }

    // End-of-day alerts (job 113) ------------------------------------------------------------

    private const string Bell = "\a";
    private const string TargetReached = "Daily target 8:00 reached.";
    private const string LimitWarning = "! 15 minutes left until 10 hours of work.";

    /// <summary>
    /// Runs the app on the live clock with today's (2026-10-27) arrival at 8:00, started at
    /// <paramref name="start"/>, and fires one tick at each wait in <paramref name="ticks"/> (wait number to time).
    /// Returns the output and the output length right before each applied tick.
    /// </summary>
    private (string Output, List<int> AppliedAt, List<bool> Results) RunAlerts(TimeOnly start, Dictionary<int, TimeOnly> ticks,
        Action<TestConsole> input, DayRecord? today = null, int width = 80)
    {
        if (today is not null)
        {
            new DayStore(data.Path).Save(Shown, today);
        }

        SettableTimeProvider time = LiveTime(27, start.Hour, start.Minute, 20);
        TestConsole console = new TestConsole().Interactive().EmitAnsiSequences();
        console.Profile.Width = width;
        ScriptedEvents scripted = new ScriptedEvents();
        ObservedEvents events = new ObservedEvents(scripted);
        List<int> appliedAt = [];
        scripted.OnWait = call =>
        {
            if (ticks.TryGetValue(call, out TimeOnly at))
            {
                Tick(time, new DateTimeOffset(2026, 10, 27, at.Hour, at.Minute, 5, TimeSpan.Zero));
            }
        };
        events.OnApply = () => appliedAt.Add(console.Output.Length);
        input(console);

        string output = RunLive(time, events, console);
        return (output, appliedAt, events.Results);
    }

    // The ANSI text a [style] markup renders to on a test console, so the colour can be checked.
    private static string Styled(string markup)
    {
        TestConsole console = new TestConsole().EmitAnsiSequences();
        console.Write(new Markup(markup));
        return console.Output;
    }

    // AC-6
    [Fact]
    public void Alert_Target_RingsOnce_ShowsTheHintInPlace_AndKeepsTheTypedLineAndCursor()
    {
        // "arr" takes waits 1-3; the tick comes at wait 4, before the Enter, with the popup open.
        (string output, List<int> appliedAt, List<bool> results) = RunAlerts(new TimeOnly(15, 59),
            new Dictionary<int, TimeOnly> { [4] = new TimeOnly(16, 0) },
            c =>
            {
                c.Input.PushText("arr");
                c.Input.PushKey(ConsoleKey.Escape);
                c.Input.PushKey(ConsoleKey.Escape);
                c.Input.PushTextWithEnter("quit");
            },
            Arrived(8));

        Assert.Equal([true], results);
        Assert.Equal(1, Count(output, Bell));
        int at = Assert.Single(appliedAt);
        Assert.True(output.IndexOf(Bell, StringComparison.Ordinal) >= at);
        Assert.DoesNotContain(TargetReached, output[..at]);

        string typed = output[output.LastIndexOf(ClearScreen, at, StringComparison.Ordinal)..at];
        // The tick's writes end where the next key (the first Esc) starts writing: the cursor is back in the line.
        List<AnsiWrite> writes = AnsiWrites.Parse(output[at..]);
        int hint = writes.FindIndex(w => w.Row == 21 && w.Text.Contains(TargetReached, StringComparison.Ordinal));
        Assert.True(hint >= 0);
        int line = writes.FindIndex(hint, w => w.Row == 23 && w.Text.Contains("arr", StringComparison.Ordinal));
        Assert.True(line > hint);
        Assert.Contains(writes.Skip(hint + 1).Take(line - hint), w => w.StartsWithCommand && w.Row <= 21);
        Assert.Equal(LastCursor(typed), (writes[line + 1].Row, writes[line + 1].Column));
        Assert.Contains(Styled("[steelblue1]" + TargetReached + "[/]").TrimEnd('\n'), output[at..]);
    }

    // AC-6: later ticks write no more bells.
    [Fact]
    public void Alert_Target_FurtherTicks_RingNoMore()
    {
        (string output, _, List<bool> results) = RunAlerts(new TimeOnly(15, 59),
            new Dictionary<int, TimeOnly> { [1] = new TimeOnly(16, 0), [2] = new TimeOnly(16, 1), [3] = new TimeOnly(16, 2) },
            c => c.Input.PushTextWithEnter("quit"),
            Arrived(8));

        Assert.Equal([true, true, true], results);
        Assert.Equal(1, Count(output, Bell));
        Assert.Contains(TargetReached, output);
    }

    // AC-7
    [Fact]
    public void Alert_Limit_RingsOnce_WithTheYellowHint()
    {
        // Started past the target (primed), so only the limit fires.
        (string output, List<int> appliedAt, _) = RunAlerts(new TimeOnly(17, 44),
            new Dictionary<int, TimeOnly> { [1] = new TimeOnly(17, 45), [2] = new TimeOnly(17, 46) },
            c => c.Input.PushTextWithEnter("quit"),
            Arrived(8));

        Assert.Equal(1, Count(output, Bell));
        Assert.Contains(Styled("[yellow]" + LimitWarning + "[/]").TrimEnd('\n'), output);
        Assert.DoesNotContain(TargetReached, output);
        Assert.Contains(AnsiWrites.Parse(output[appliedAt[0]..]), w => w.Row == 21 && w.Text.Contains(LimitWarning, StringComparison.Ordinal));
    }

    // AC-7: both on the same tick ring once, and the hint holds both texts, target first.
    [Fact]
    public void Alert_BothOnOneTick_OneBell_TargetFirst()
    {
        (string output, List<int> appliedAt, _) = RunAlerts(new TimeOnly(15, 0),
            new Dictionary<int, TimeOnly> { [1] = new TimeOnly(17, 50) },
            c => c.Input.PushTextWithEnter("quit"),
            Arrived(8));

        Assert.Equal(1, Count(output, Bell));
        AnsiWrite row = AnsiWrites.Parse(output[appliedAt[0]..]).First(w => w.Row == 21 && w.Text.Contains(TargetReached, StringComparison.Ordinal));
        Assert.Contains(TargetReached + " " + LimitWarning, row.Text);
    }

    // AC-7, AC-10: a hint that needs one more row rewrites every row but the input line, which the editor
    // then redraws with the typed text and the cursor where it was.
    [Fact]
    public void Alert_HintWrappingToTwoRows_RewritesTheOtherRows_AndKeepsTheTypedLine()
    {
        (string output, List<int> appliedAt, _) = RunAlerts(new TimeOnly(15, 0),
            new Dictionary<int, TimeOnly> { [2] = new TimeOnly(17, 50) },
            c =>
            {
                c.Input.PushText("q");
                c.Input.PushKey(ConsoleKey.Escape);
                c.Input.PushKey(ConsoleKey.Escape);
                c.Input.PushTextWithEnter("quit");
            },
            Arrived(8), width: 60);

        int at = Assert.Single(appliedAt);
        string typed = output[output.LastIndexOf(ClearScreen, at, StringComparison.Ordinal)..at];
        List<AnsiWrite> writes = AnsiWrites.Parse(output[at..]);
        Assert.Equal(1, Count(output, Bell));
        int first = writes.FindIndex(w => w.Row == 20 && w.Text.Contains("Daily target", StringComparison.Ordinal));
        Assert.True(first >= 0);
        Assert.Contains("10 hours", writes[first].Text);
        Assert.Contains(writes.Skip(first), w => w.Row == 21 && w.Text.Contains("of work.", StringComparison.Ordinal));
        // Every row but the input line was rewritten in place by the hint.
        List<int> hintRows = writes.Take(writes.FindIndex(first, w => w.Row == 24) + 1).Where(w => w.Column == 1).Select(w => w.Row).ToList();
        Assert.Contains(1, hintRows);
        Assert.Contains(22, hintRows);
        Assert.DoesNotContain(23, hintRows);
        int line = writes.FindIndex(first, w => w.Row == 23 && w.Text.Contains('q'));
        Assert.True(line > first);
        Assert.Equal(LastCursor(typed), (writes[line + 1].Row, writes[line + 1].Column));
    }

    // AC-8: an alert fires from today's file while another day is shown; the shown day stays, and the popup
    // and the typed line are redrawn after the hint.
    [Fact]
    public void Alert_WhileAnotherDayIsShown_FiresFromTodaysFile_AndRedrawsPopupAndLineAfterTheHint()
    {
        // "prev" and Enter take waits 1-5, "ar" waits 6-7; the tick comes at wait 8, with the popup open.
        (string output, List<int> appliedAt, List<bool> results) = RunAlerts(new TimeOnly(15, 59),
            new Dictionary<int, TimeOnly> { [8] = new TimeOnly(16, 0) },
            c =>
            {
                c.Input.PushTextWithEnter("prev");
                c.Input.PushText("ar");
                c.Input.PushKey(ConsoleKey.Escape);
                c.Input.PushKey(ConsoleKey.Escape);
                c.Input.PushTextWithEnter("quit");
            },
            Arrived(8));

        Assert.Equal([true], results);
        Assert.Equal(1, Count(output, Bell));
        int at = Assert.Single(appliedAt);
        string typed = output[output.LastIndexOf(ClearScreen, at, StringComparison.Ordinal)..at];
        List<AnsiWrite> writes = AnsiWrites.Parse(output[at..]);
        int hint = writes.FindIndex(w => w.Row == 21 && w.Text.Contains(TargetReached, StringComparison.Ordinal));
        Assert.True(hint >= 0);
        int line = writes.FindIndex(hint, w => w.Row == 23 && w.Text.Contains("ar", StringComparison.Ordinal));
        Assert.True(line > hint);
        Assert.Contains(writes.Skip(hint + 1).Take(line - hint), w => w.StartsWithCommand && w.Row <= 21);
        Assert.Equal(LastCursor(typed), (writes[line + 1].Row, writes[line + 1].Column));

        // The shown day is still the day before, without a now row.
        string top = string.Join('\n', writes.Take(line).Where(w => w.Column == 1 && w.Row <= TopHeight).Select(w => w.Text));
        Assert.Contains("2026-10-26", string.Join('\n', AnsiFrames(output)[1]));
        Assert.DoesNotMatch(@"(?m)^ +now +\d", top);
        Assert.DoesNotContain("08:00", top);
    }

    // AC-8: an unreadable or missing day file for today fires nothing and shows no error.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Alert_TodaysFileUnreadableOrMissing_FiresNothing(bool unreadable)
    {
        if (unreadable)
        {
            File.WriteAllText(Path.Combine(data.Path, "2026-10-27.json"), "{ not json");
        }

        (string output, List<int> appliedAt, List<bool> results) = RunAlerts(new TimeOnly(15, 59),
            new Dictionary<int, TimeOnly> { [1] = new TimeOnly(16, 0), [2] = new TimeOnly(18, 0) },
            c => c.Input.PushTextWithEnter("quit"));

        Assert.Equal([true, true], results);
        Assert.Equal(0, Count(output, Bell));
        string after = string.Join('\n', AnsiWrites.Parse(output[appliedAt[0]..]).Select(w => w.Text));
        Assert.DoesNotContain(TargetReached, after);
        Assert.DoesNotContain(LimitWarning, after);
        Assert.DoesNotContain(after, AnsiWrites.Parse(output[appliedAt[0]..]).Where(w => w.Row == 21).Select(w => w.Text.Trim()).Where(t => t.Length > 0));
    }

    // AC-9
    [Fact]
    public void Alert_BothSettingsOff_RingNothing_AndShowNoHint()
    {
        File.WriteAllText(SettingsPath, "{\"settings\":{\"alert-target\":false,\"alert-limit\":false}}");

        (string output, _, List<bool> results) = RunAlerts(new TimeOnly(15, 59),
            new Dictionary<int, TimeOnly> { [1] = new TimeOnly(16, 0), [2] = new TimeOnly(17, 45), [3] = new TimeOnly(18, 0) },
            c => c.Input.PushTextWithEnter("quit"),
            Arrived(8));

        Assert.Equal([true, true, true], results);
        Assert.Equal(0, Count(output, Bell));
        Assert.DoesNotContain(TargetReached, output);
        Assert.DoesNotContain(LimitWarning, output);
    }

    // AC-9: a broken settings.json counts as both on.
    [Fact]
    public void Alert_BrokenSettings_CountsAsBothOn()
    {
        File.WriteAllText(SettingsPath, "{ broken");

        (string output, List<int> appliedAt, _) = RunAlerts(new TimeOnly(15, 59),
            new Dictionary<int, TimeOnly> { [1] = new TimeOnly(17, 50) },
            c => c.Input.PushTextWithEnter("quit"),
            Arrived(8));

        Assert.Equal(1, Count(output, Bell));
        Assert.Contains(AnsiWrites.Parse(output[appliedAt[0]..]), w => w.Row == 21 && w.Text.Contains(TargetReached + " " + LimitWarning, StringComparison.Ordinal));
    }

    // AC-3 end to end: a run started past both thresholds rings nothing.
    [Fact]
    public void Alert_StartedPastBothThresholds_RingsNothing()
    {
        (string output, _, List<bool> results) = RunAlerts(new TimeOnly(18, 30),
            new Dictionary<int, TimeOnly> { [1] = new TimeOnly(18, 31), [2] = new TimeOnly(18, 32) },
            c => c.Input.PushTextWithEnter("quit"),
            Arrived(8));

        Assert.Equal([true, true], results);
        Assert.Equal(0, Count(output, Bell));
        Assert.DoesNotContain(TargetReached, output);
        Assert.DoesNotContain(LimitWarning, output);
    }

    // AC-10: the alert hint stays until the next full redraw, which shows that command's hint instead.
    [Fact]
    public void Alert_HintStaysUntilTheNextFullRedraw()
    {
        // The tick comes at wait 1, before "next" is typed.
        (string output, List<int> appliedAt, _) = RunAlerts(new TimeOnly(15, 59),
            new Dictionary<int, TimeOnly> { [1] = new TimeOnly(16, 0) },
            c =>
            {
                c.Input.PushTextWithEnter("next");
                c.Input.PushTextWithEnter("quit");
            },
            Arrived(8));

        int at = Assert.Single(appliedAt);
        int clear = output.IndexOf(ClearScreen, at, StringComparison.Ordinal);
        Assert.Contains(TargetReached, output[at..clear]);
        // While "next" is typed the popup covers it; once the popup closes for Enter, the hint row shows it again.
        Assert.Contains(TargetReached, AnsiWrites.Parse(output[at..clear]).Last(w => w.Row == 21 && w.Column == 1).Text);
        Assert.DoesNotContain(AnsiFrames(output)[1], l => l.Contains(TargetReached, StringComparison.Ordinal));
    }

    // AC-5: the settings panel lists both alerts and toggles alert-target.
    [Fact]
    public void Settings_ListsBothAlerts_AndEnterTogglesAlertTarget()
    {
        TestConsole console = new TestConsole().Interactive();
        console.Profile.Width = 120;
        console.Input.PushTextWithEnter("settings");
        console.Input.PushKey(ConsoleKey.DownArrow);
        console.Input.PushKey(ConsoleKey.Enter);
        console.Input.PushKey(ConsoleKey.DownArrow);
        console.Input.PushKey(ConsoleKey.Spacebar);
        console.Input.PushKey(ConsoleKey.Escape);
        console.Input.PushTextWithEnter("quit");

        Assert.Equal(0, new App(console, FixedTimeProvider.At20261027(), new DayStore(data.Path)).Run([]));

        Assert.Matches(@"alert-target +on +Ring and show a hint when today's work reaches the daily target", console.Output);
        Assert.Matches(@"alert-limit +on +Ring and show a hint 15 minutes before 10 hours of work", console.Output);
        Assert.Matches(@"alert-target +off ", console.Output);
        Assert.Equal("{\"settings\":{\"alert-target\":false,\"alert-limit\":false}}", File.ReadAllText(SettingsPath));
    }

    // Job 114 ------------------------------------------------------------------------------

    /// <summary>
    /// A week around today (Tuesday 2026-10-27): Monday follows a Sunday leaving at 23:00 (8:00 rest), Wednesday has
    /// 14 hours of work with no break, today has a plain short day.
    /// </summary>
    private void WriteCheckWeek()
    {
        WriteDayFile("2026-10-25", "20:00", "23:00");
        WriteDayFile("2026-10-26", "07:00", "12:00");
        WriteDayFile("2026-10-27", "13:00", "15:00");
        WriteDayFile("2026-10-28", "06:00", "20:00");
    }

    private (int ExitCode, string Output) RunWide(params string[] commands) =>
        Run(c =>
        {
            c.Profile.Width = 200;
            c.Profile.Height = 40;
            foreach (string command in commands)
            {
                c.Input.PushTextWithEnter(command);
            }
        });

    private Dictionary<string, string> Snapshot() =>
        Directory.EnumerateFileSystemEntries(data.Path).ToDictionary(p => p, p => File.Exists(p) ? File.ReadAllText(p) : "<dir>");

    // AC-5: the week line names the days with a warning, Monday first; each is checked against its own previous day.
    [Fact]
    public void WorkingTimeChecks_WeekLine_ListsTheWeeksDaysWithWarnings()
    {
        WriteCheckWeek();

        (int exitCode, string output) = RunWide("quit");

        Assert.Equal(0, exitCode);
        string frame = LastFrame(output);
        Assert.Contains("! Working-time warnings this week: Mon, Wed", frame);
        Assert.DoesNotContain("Rest too short", frame);
        Assert.DoesNotContain("Over 10 hours", frame);
    }

    // AC-2, AC-3, AC-5: the later day shows the rest warning, the long day the work warning; the earlier day never the rest.
    [Fact]
    public void WorkingTimeChecks_BreakLine_ShowsEachDaysOwnWarnings()
    {
        WriteCheckWeek();

        string monday = LastFrame(RunWide("prev", "quit").Output);
        string sunday = LastFrame(RunWide("prev", "prev", "quit").Output);
        string wednesday = LastFrame(RunWide("next", "quit").Output);

        Assert.Contains("Breaks 0:00 taken · 0:00 required  ! Rest too short: 8:00 since last leaving", monday);
        Assert.Contains("! Working-time warnings this week: Mon, Wed", monday);
        Assert.DoesNotContain("Rest too short", sunday);
        Assert.DoesNotContain("Working-time warnings", sunday);
        Assert.Contains("! Breaks too short: 0:45 missing  ! Over 10 hours: 14:00 worked", wednesday);
    }

    // AC-5: an unreadable or absence day in the week adds nothing and does not break the view.
    [Fact]
    public void WorkingTimeChecks_UnreadableAndAbsenceDays_AddNoWarning()
    {
        WriteDayFile("2026-10-27", "13:00", "15:00");
        File.WriteAllText(DayFile("2026-10-26"), "{ not json");
        File.WriteAllText(DayFile("2026-10-28"), "{\"absence\":\"vacation\"}");

        string frame = LastFrame(RunWide("quit").Output);

        Assert.Contains("Target 8:00", frame);
        Assert.DoesNotContain("Working-time warnings", frame);
    }

    // AC-6: turning check-rest off and on in settings removes and brings back its warnings on the next redraw.
    [Fact]
    public void WorkingTimeChecks_SettingsToggle_RemovesAndRestoresWarnings()
    {
        WriteCheckWeek();
        TestConsole console = new TestConsole().Interactive();
        console.Profile.Width = 200;
        console.Profile.Height = 40;
        console.Input.PushTextWithEnter("prev");
        for (int pass = 0; pass < 2; pass++)
        {
            console.Input.PushTextWithEnter("settings");
            for (int down = 0; down < 5; down++)
            {
                console.Input.PushKey(ConsoleKey.DownArrow);
            }

            console.Input.PushKey(ConsoleKey.Enter);
            console.Input.PushKey(ConsoleKey.Escape);
            console.Input.PushTextWithEnter(pass == 0 ? "help" : "today");
            console.Input.PushTextWithEnter("prev");
        }

        console.Input.PushTextWithEnter("quit");
        new App(console, FixedTimeProvider.At20261027(), new DayStore(data.Path)).Run([]);

        string output = console.Output;
        Assert.Matches(@"check-breaks +on +Warn when breaks are shorter than the law requires", output);
        Assert.Matches(@"check-max-work +on +Warn when a day has more than 10 hours of work", output);
        Assert.Matches(@"check-rest +on +Warn when the rest between two working days is under 11 hours", output);
        Assert.Matches(@"check-rest +off ", output);
        Assert.Equal("{\"settings\":{\"check-rest\":true}}", File.ReadAllText(SettingsPath));

        // The Monday frames: before, with check-rest off, and on again.
        List<string> mondays = Regex.Split(output, "Monday, 2026-10-26").Skip(1).Select(f => f.Split("Tuesday, 2026-10-27")[0]).ToList();
        Assert.Contains("Rest too short: 8:00", mondays[0]);
        Assert.Contains("this week: Mon, Wed", mondays[0]);
        Assert.Contains(mondays, m => !m.Contains("Rest too short", StringComparison.Ordinal) && m.Contains("this week: Wed", StringComparison.Ordinal));
        Assert.Contains("Rest too short: 8:00", mondays[^1]);
        Assert.Contains("this week: Mon, Wed", mondays[^1]);
    }

    // AC-6: settings.json holds the three checks as true/false.
    [Fact]
    public void WorkingTimeChecks_AllOffInSettingsJson_NoWarnings()
    {
        WriteCheckWeek();
        File.WriteAllText(SettingsPath, "{\"settings\":{\"check-breaks\":false,\"check-max-work\":false,\"check-rest\":false}}");

        string monday = LastFrame(RunWide("prev", "quit").Output);
        string wednesday = LastFrame(RunWide("next", "quit").Output);

        Assert.DoesNotContain("!", monday);
        Assert.DoesNotContain("!", wednesday);
        Assert.Contains("Breaks 0:00 taken · 0:45 required", wednesday);
    }

    // AC-7: a broken settings.json leaves every check on and is never written.
    [Fact]
    public void WorkingTimeChecks_BrokenSettings_AllOn_FileUnchanged()
    {
        WriteCheckWeek();
        byte[] broken = Encoding.UTF8.GetBytes("{\"settings\":{\"check-rest\":false");
        File.WriteAllBytes(SettingsPath, broken);

        string monday = LastFrame(RunWide("prev", "quit").Output);

        Assert.Contains("! Rest too short: 8:00 since last leaving", monday);
        Assert.Contains("! Working-time warnings this week: Mon, Wed", monday);
        Assert.Equal(broken, File.ReadAllBytes(SettingsPath));
    }

    // AC-7: a missing settings.json leaves every check on and is not created.
    [Fact]
    public void WorkingTimeChecks_MissingSettings_AllOn_NotCreated()
    {
        WriteCheckWeek();

        string wednesday = LastFrame(RunWide("next", "quit").Output);

        Assert.Contains("! Over 10 hours: 14:00 worked", wednesday);
        Assert.False(File.Exists(SettingsPath));
    }

    // AC-8: rendering only reads: navigating around the week creates and changes no file.
    [Fact]
    public void WorkingTimeChecks_Navigation_CreatesAndChangesNoFile()
    {
        WriteCheckWeek();
        Dictionary<string, string> before = Snapshot();

        RunWide("prev", "prev", "prev", "today", "next", "next", "next", "next", "next", "today", "quit");

        Assert.Equal(before, Snapshot());
    }

    // Job 115 ------------------------------------------------------------------------------

    private const string GermanyBavaria = "{\"settings\":{\"holiday-country\":\"Germany\",\"holiday-state\":\"BY\"}}";

    // 18:30 UTC on the given day, so the shown day starts there.
    private static FixedTimeProvider HolidayClock(int month, int day) =>
        new(new DateTimeOffset(2026, month, day, 18, 30, 0, TimeSpan.Zero), TimeZoneInfo.Utc);

    private (int ExitCode, string Output) RunOn(FixedTimeProvider time, params string[] commands) =>
        Run(c =>
        {
            c.Profile.Width = 200;
            c.Profile.Height = 40;
            foreach (string command in commands)
            {
                c.Input.PushTextWithEnter(command);
            }
        }, time: time);

    private string[] DataEntryNames() =>
        Directory.EnumerateFileSystemEntries(data.Path).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray()!;

    // AC-5
    [Fact]
    public void Settings_ListsHolidaySettingsAfterCheckRest_AndSavesGermanyBavaria()
    {
        TestConsole console = new TestConsole().Interactive();
        console.Profile.Width = 160;
        console.Profile.Height = 40;
        console.Input.PushTextWithEnter("settings");
        for (int down = 0; down < 6; down++)
        {
            console.Input.PushKey(ConsoleKey.DownArrow);
        }

        console.Input.PushKey(ConsoleKey.Enter);
        console.Input.PushKey(ConsoleKey.DownArrow);
        for (int press = 0; press < 4; press++)
        {
            console.Input.PushKey(ConsoleKey.Enter);
        }

        console.Input.PushKey(ConsoleKey.Escape);
        console.Input.PushTextWithEnter("quit");
        new App(console, FixedTimeProvider.At20261027(), new DayStore(data.Path)).Run([]);

        Assert.Matches(@"check-rest +on [^\n]*\n[^\n]*holiday-country +none [^\n]*\n[^\n]*holiday-state +none ", console.Output);
        Assert.Matches(@"holiday-country +Germany ", console.Output);
        Assert.Matches(@"holiday-state +BY ", console.Output);
        Assert.Equal(GermanyBavaria, File.ReadAllText(SettingsPath));
    }

    // AC-6
    [Fact]
    public void Holiday_WithGermanyBavaria_ShowsCorpusChristiAbsence_AndWritesNoFile()
    {
        File.WriteAllText(SettingsPath, GermanyBavaria);

        (int exitCode, string output) = RunOn(HolidayClock(6, 4), "quit");

        string frame = LastFrame(output);
        Assert.Equal(0, exitCode);
        Assert.Contains("Thursday, 2026-06-04", frame);
        Assert.Contains("Absence: Public holiday · Corpus Christi", frame);
        Assert.DoesNotContain("No entries yet.", frame);
        Assert.Matches(@"Target 8:00 · day \+0:00 · balance 0:00", frame);
        Assert.Equal(["settings.json"], DataEntryNames());
    }

    // AC-6: navigating onto a holiday shows it too, and browsing leaves no file.
    [Fact]
    public void Holiday_NavigatingOntoIt_ShowsIt_AndWritesNoFile()
    {
        File.WriteAllText(SettingsPath, GermanyBavaria);

        (_, string output) = RunOn(HolidayClock(6, 5), "prev", "quit");

        Assert.Contains("Absence: Public holiday · Corpus Christi", LastFrame(output));
        Assert.Equal(["settings.json"], DataEntryNames());
    }

    // AC-7
    [Theory]
    [InlineData(null)]
    [InlineData("{\"settings\":{}}")]
    [InlineData("{\"settings\":{\"holiday-country\":\"Germany\"")]
    [InlineData("{\"settings\":{\"holiday-country\":\"France\",\"holiday-state\":\"BY\"}}")]
    public void Holiday_DefaultsMissingOrBrokenSettings_ShowUsualEmptyDay(string? settingsJson)
    {
        if (settingsJson is not null)
        {
            File.WriteAllText(SettingsPath, settingsJson);
        }

        (_, string output) = RunOn(HolidayClock(6, 4), "quit");

        string frame = LastFrame(output);
        Assert.Contains("No entries yet.", frame);
        Assert.DoesNotContain("Public holiday", frame);
        Assert.Equal(settingsJson is null ? [] : ["settings.json"], DataEntryNames());
    }

    // AC-7: an unknown option makes settings.json broken, so both settings are none (no holidays at all).
    [Fact]
    public void Holiday_UnknownStateInSettings_NoHolidays()
    {
        File.WriteAllText(SettingsPath, "{\"settings\":{\"holiday-country\":\"Germany\",\"holiday-state\":\"XX\"}}");

        Assert.Contains("No entries yet.", LastFrame(RunOn(HolidayClock(6, 4), "quit").Output));
        Assert.Contains("No entries yet.", LastFrame(RunOn(HolidayClock(4, 3), "quit").Output));
    }

    // AC-4: Germany with state none gives the nationwide holidays only.
    [Fact]
    public void Holiday_GermanyWithoutState_NationwideOnly()
    {
        File.WriteAllText(SettingsPath, "{\"settings\":{\"holiday-country\":\"Germany\"}}");

        Assert.Contains("No entries yet.", LastFrame(RunOn(HolidayClock(6, 4), "quit").Output));
        Assert.Contains("Absence: Public holiday · Good Friday", LastFrame(RunOn(HolidayClock(4, 3), "quit").Output));
    }

    // AC-8
    [Fact]
    public void Holiday_DayFileWithEntries_ShowsBooked_WithGreyHolidayLine()
    {
        File.WriteAllText(SettingsPath, GermanyBavaria);
        File.WriteAllText(DayFile("2026-06-04"),
            "{\"entries\":[{\"kind\":\"arrival\",\"time\":\"08:00\"},{\"kind\":\"leaving\",\"time\":\"12:00\"}]}");

        (_, string output) = RunOn(HolidayClock(6, 5), "prev", "quit");

        string frame = LastFrame(output);
        string[] lines = frame.Split('\n').Select(l => l.Trim('│', ' ', '\r')).ToArray();
        int heading = Array.FindIndex(lines, l => l.Contains("Thursday, 2026-06-04", StringComparison.Ordinal));
        Assert.Equal("Public holiday: Corpus Christi", lines[heading + 1]);
        Assert.DoesNotContain("Absence:", frame);
        Assert.Matches(@"AR +08:00", frame);
        Assert.Matches(@"Target 8:00 · day -4:00 · balance -4:00", frame);
    }

    // AC-8
    [Fact]
    public void Holiday_ArriveOnHolidayWithoutFile_IsAccepted_AndCreatesTheFile()
    {
        File.WriteAllText(SettingsPath, GermanyBavaria);

        (int exitCode, string output) = RunOn(HolidayClock(6, 5), "prev", "arrive 9:00", "quit");

        string frame = LastFrame(output);
        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(DayFile("2026-06-04")));
        Assert.Contains("\"arrival\"", ReadDay("2026-06-04"));
        Assert.Contains("Public holiday: Corpus Christi", frame);
        Assert.Matches(@"AR +09:00", frame);
        Assert.DoesNotContain("Absence:", frame);
    }

    // AC-9
    [Theory]
    [InlineData("vacation", "Absence: Vacation")]
    [InlineData("sick", "Absence: Sick")]
    [InlineData("publicHoliday", "Absence: Public holiday · Corpus Christi")]
    public void Holiday_StoredAbsenceOnHoliday_ShowsTheStoredAbsence(string stored, string expected)
    {
        File.WriteAllText(SettingsPath, GermanyBavaria);
        File.WriteAllText(DayFile("2026-06-04"), "{\"absence\":\"" + stored + "\",\"entries\":[]}");

        string frame = LastFrame(RunOn(HolidayClock(6, 4), "quit").Output);

        Assert.Contains(expected, frame);
        if (stored != "publicHoliday")
        {
            Assert.DoesNotContain("Public holiday", frame);
        }
    }

    // AC-9
    [Fact]
    public void Holiday_StoredPublicHolidayOnOtherDate_AsBefore()
    {
        File.WriteAllText(SettingsPath, GermanyBavaria);
        File.WriteAllText(DayFile("2026-06-05"), "{\"absence\":\"publicHoliday\",\"entries\":[]}");

        string frame = LastFrame(RunOn(HolidayClock(6, 5), "quit").Output);

        Assert.Contains("Absence: Public holiday", frame);
        Assert.DoesNotContain("Public holiday ·", frame);
    }

    // AC-10
    [Fact]
    public void Holiday_BalanceIsTheSameWithAndWithoutHolidaySettings()
    {
        File.WriteAllText(DayFile("2026-06-02"),
            "{\"entries\":[{\"kind\":\"arrival\",\"time\":\"08:00\"},{\"kind\":\"leaving\",\"time\":\"17:30\"}]}");
        File.WriteAllText(DayFile("2026-06-03"),
            "{\"entries\":[{\"kind\":\"arrival\",\"time\":\"08:00\"},{\"kind\":\"leaving\",\"time\":\"15:00\"}]}");

        string without = Regex.Match(LastFrame(RunOn(HolidayClock(6, 5), "quit").Output), @"balance [^ \n]+").Value;
        File.WriteAllText(SettingsPath, GermanyBavaria);
        string with = Regex.Match(LastFrame(RunOn(HolidayClock(6, 5), "quit").Output), @"balance [^ \n]+").Value;
        string onHoliday = Regex.Match(LastFrame(RunOn(HolidayClock(6, 5), "prev", "quit").Output), @"balance [^ \n]+").Value;

        Assert.Equal("balance +0:30", without);
        Assert.Equal(without, with);
        Assert.Equal(without, onHoliday);
    }

    // Job 116 ------------------------------------------------------------------------------

    private static FixedTimeProvider VacationClock(int year, int month, int day) =>
        new FixedTimeProvider(new DateTimeOffset(year, month, day, 12, 0, 0, TimeSpan.Zero), TimeZoneInfo.Utc);

    private void WriteAbsenceDay(string date, string kind) => File.WriteAllText(DayFile(date), $"{{\"absence\":\"{kind}\"}}");

    /// <summary>With the clock on 2026-10-27: taken 2026-01-05 and 2026-10-27, planned 2026-12-21, plus days that never count.</summary>
    private void WriteVacationDays()
    {
        WriteAbsenceDay("2025-12-30", "vacation");
        WriteAbsenceDay("2026-01-05", "vacation");
        WriteAbsenceDay("2026-10-27", "vacation");
        WriteAbsenceDay("2026-12-21", "vacation");
        WriteAbsenceDay("2026-03-10", "sick");
        WriteAbsenceDay("2026-10-03", "publicHoliday");
        WriteDayFile("2026-05-04", "08:00", "16:00");
        WriteAbsenceDay("2027-01-04", "vacation");
    }

    private static int RowIndex(string[] frame, string label, string value) =>
        Array.FindIndex(frame, l => Regex.IsMatch(l, $@"│ {label} +│ +{Regex.Escape(value)} │"));

    // AC-1
    [Fact]
    public void Settings_ListsVacationDays_AndChangingItSavesSettingsJson()
    {
        TestConsole console = new TestConsole().Interactive();
        console.Profile.Width = 120;
        console.Profile.Height = 40;
        console.Input.PushTextWithEnter("settings");
        // Past daily-target, the two alerts, the three checks and the two holiday settings.
        for (int down = 0; down < 8; down++)
        {
            console.Input.PushKey(ConsoleKey.DownArrow);
        }

        console.Input.PushKey(ConsoleKey.Enter);
        console.Input.PushTextWithEnter("25");
        console.Input.PushKey(ConsoleKey.Escape);
        console.Input.PushTextWithEnter("vacation");
        console.Input.PushTextWithEnter("quit");

        Assert.Equal(0, new App(console, FixedTimeProvider.At20261027(), new DayStore(data.Path)).Run([]));

        Assert.Matches(@"vacation-days +30 +Vacation days per year", console.Output);
        Assert.Matches(@"vacation-days +25 +Vacation days per year", console.Output);
        Assert.Equal("{\"settings\":{\"vacation-days\":25}}", File.ReadAllText(SettingsPath));
        string[] frame = LastLayout(console.Output);
        Assert.True(RowIndex(frame, "Allowance", "25") > 0, string.Join("\n", frame));
    }

    // AC-1: the panel refuses a value above 366 and keeps the file unchanged.
    [Fact]
    public void Settings_VacationDays_RefusesAValueAbove366()
    {
        TestConsole console = new TestConsole().Interactive();
        console.Profile.Width = 120;
        console.Profile.Height = 40;
        console.Input.PushTextWithEnter("settings");
        for (int down = 0; down < 8; down++)
        {
            console.Input.PushKey(ConsoleKey.DownArrow);
        }

        console.Input.PushKey(ConsoleKey.Enter);
        console.Input.PushTextWithEnter("367");
        console.Input.PushKey(ConsoleKey.Escape);
        console.Input.PushKey(ConsoleKey.Escape);
        console.Input.PushTextWithEnter("quit");

        Assert.Equal(0, new App(console, FixedTimeProvider.At20261027(), new DayStore(data.Path)).Run([]));

        Assert.Contains("vacation-days takes a whole number from 0 to 366.", console.Output);
        Assert.False(File.Exists(SettingsPath));
    }

    // AC-4
    [Fact]
    public void Vacation_ShowsAllowanceTakenPlannedLeft_CaptionAndHidesTheQuicklist()
    {
        WriteVacationDays();
        File.WriteAllText(ProjectsFile, "{\"projects\":[{\"workItem\":1234,\"title\":\"Times\"}]}");
        File.WriteAllText(SettingsPath, "{\"settings\":{\"vacation-days\":25}}");

        (int exitCode, string[] frame) = RunReport("vacation", "quit");

        Assert.Equal(0, exitCode);
        int allowance = RowIndex(frame, "Allowance", "25");
        int taken = RowIndex(frame, "Taken", "2");
        int planned = RowIndex(frame, "Planned", "1");
        int left = RowIndex(frame, "Left", "22");
        Assert.True(allowance > 0 && taken > allowance && planned > taken && left > planned, string.Join("\n", frame));
        Assert.Contains(frame, l => l.Trim() == "Vacation 2026");
        Assert.DoesNotContain(frame, l => l.Contains("Projects", StringComparison.Ordinal));
        Assert.DoesNotContain(frame, l => l.Contains("skipped", StringComparison.Ordinal));
        Assert.DoesNotContain(frame, l => l.Contains("over your allowance", StringComparison.Ordinal));
    }

    // AC-4
    [Theory]
    [InlineData("va")]
    [InlineData("VA")]
    [InlineData("/vacation")]
    [InlineData("/va")]
    public void Vacation_AliasAndSlash_DoTheSameAsVacation(string typed)
    {
        WriteVacationDays();

        (_, string[] full) = RunReport("vacation", "quit");
        (_, string[] other) = RunReport(typed, "quit");

        Assert.Equal(full, other);
        Assert.Contains(other, l => l.Trim() == "Vacation 2026");
    }

    // AC-4
    [Fact]
    public void Vacation_ColoursAndGreyFrame()
    {
        WriteVacationDays();

        string output = RunAnsiCommands("vacation");

        Assert.Matches(@"\x1b\[(90|38;5;8)m╭─", output);
        Assert.Matches(@"\x1b\[38;5;75m30\x1b\[0m", output);
        Assert.Matches(@"\x1b\[1mLeft\x1b\[0m", output);
        Assert.Matches(@"\x1b\[1;38;5;75m27\x1b\[0m", output);
        Assert.Matches(@"\x1b\[(90|38;5;8)mVacation 2026", output);
    }

    // AC-4
    [Theory]
    [InlineData(1, "! 1 file skipped")]
    [InlineData(2, "! 2 files skipped")]
    public void Vacation_SkippedFiles_ShowYellowLineBelowTheTable(int broken, string line)
    {
        WriteVacationDays();
        File.WriteAllText(DayFile("2026-02-02"), "{not json");
        if (broken > 1)
        {
            Directory.CreateDirectory(DayFile("2026-02-03"));
        }

        File.WriteAllText(DayFile("2025-02-02"), "{not json");

        (_, string[] frame) = RunReport("vacation", "quit");

        int left = RowIndex(frame, "Left", "27");
        int skipped = Array.FindIndex(frame, l => l.Contains(line, StringComparison.Ordinal));
        Assert.True(left > 0 && skipped > left, string.Join("\n", frame));

        string ansi = RunAnsiCommands("vacation");
        Assert.Matches($@"\x1b\[(33|93|38;5;11)m{Regex.Escape(line)}", ansi);
    }

    // AC-5
    [Theory]
    [InlineData(1, "-2", "! 2 days over your allowance")]
    [InlineData(2, "-1", "! 1 day over your allowance")]
    public void Vacation_OverTheAllowance_ShowsNegativeLeftAndYellowLine(int days, string left, string line)
    {
        WriteVacationDays();
        File.WriteAllText(SettingsPath, $"{{\"settings\":{{\"vacation-days\":{days}}}}}");

        (int exitCode, string[] frame) = RunReport("vacation", "quit");

        Assert.Equal(0, exitCode);
        int row = RowIndex(frame, "Left", left);
        int over = Array.FindIndex(frame, l => l.Contains(line, StringComparison.Ordinal));
        Assert.True(row > 0 && over > row, string.Join("\n", frame));

        string ansi = RunAnsiCommands("vacation");
        Assert.Matches($@"\x1b\[(33|93|38;5;11)m{Regex.Escape(line)}", ansi);
    }

    // AC-5
    [Theory]
    [InlineData(3, "0")]
    [InlineData(0, "0")]
    public void Vacation_LeftZeroOrMore_ShowsNoOverLine(int days, string left)
    {
        if (days == 0)
        {
            WriteAbsenceDay("2026-03-10", "sick");
        }
        else
        {
            WriteVacationDays();
        }

        File.WriteAllText(SettingsPath, $"{{\"settings\":{{\"vacation-days\":{days}}}}}");

        (_, string[] frame) = RunReport("vacation", "quit");

        Assert.True(RowIndex(frame, "Left", left) > 0, string.Join("\n", frame));
        Assert.DoesNotContain(frame, l => l.Contains("over your allowance", StringComparison.Ordinal));
    }

    // AC-6
    [Theory]
    [InlineData(null)]
    [InlineData("{not json")]
    [InlineData("{\"settings\":{\"vacation-days\":367}}")]
    [InlineData("{\"settings\":{\"vacation-days\":-1}}")]
    [InlineData("{\"settings\":{\"vacation-days\":\"25\"}}")]
    public void Vacation_MissingBrokenOrOutOfRangeSetting_Uses30_AndNeverWritesSettings(string? content)
    {
        WriteVacationDays();
        if (content is not null)
        {
            File.WriteAllText(SettingsPath, content);
        }

        (_, string[] frame) = RunReport("vacation", "va", "quit");

        Assert.True(RowIndex(frame, "Allowance", "30") > 0, string.Join("\n", frame));
        Assert.True(RowIndex(frame, "Left", "27") > 0, string.Join("\n", frame));
        if (content is null)
        {
            Assert.False(File.Exists(SettingsPath));
        }
        else
        {
            Assert.Equal(content, File.ReadAllText(SettingsPath));
        }
    }

    // AC-4: the command never writes a day file.
    [Fact]
    public void Vacation_NeverWritesADayFile()
    {
        WriteVacationDays();
        string[] before = Directory.GetFileSystemEntries(data.Path).Order(StringComparer.Ordinal).ToArray();
        string bytes = string.Join("|", before.Where(File.Exists).Select(File.ReadAllText));

        RunReport("vacation", "quit");

        string[] after = Directory.GetFileSystemEntries(data.Path).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(before, after);
        Assert.Equal(bytes, string.Join("|", after.Where(File.Exists).Select(File.ReadAllText)));
    }

    // AC-7
    [Fact]
    public void Vacation_UsesTodaysYear_EvenAfterPrevMovedTheShownDayIntoLastYear()
    {
        WriteAbsenceDay("2025-12-31", "vacation");
        WriteAbsenceDay("2025-12-30", "vacation");
        WriteAbsenceDay("2026-01-01", "vacation");
        WriteAbsenceDay("2026-01-02", "vacation");

        (int exitCode, string output) = Run(c =>
        {
            c.Profile.Height = 40;
            c.Profile.Width = 100;
            c.Input.PushTextWithEnter("prev");
            c.Input.PushTextWithEnter("vacation");
            c.Input.PushTextWithEnter("quit");
        }, time: VacationClock(2026, 1, 1));

        Assert.Equal(0, exitCode);
        string[] frame = LastLayout(output);
        Assert.Contains(frame, l => l.Trim() == "Vacation 2026");
        Assert.DoesNotContain(frame, l => l.Contains("Vacation 2025", StringComparison.Ordinal));
        Assert.True(RowIndex(frame, "Taken", "1") > 0, string.Join("\n", frame));
        Assert.True(RowIndex(frame, "Planned", "1") > 0, string.Join("\n", frame));
        Assert.True(RowIndex(frame, "Left", "28") > 0, string.Join("\n", frame));
    }

    // AC-8
    [Theory]
    [InlineData("vacation 2025")]
    [InlineData("va x")]
    [InlineData("vacation [red]")]
    public void Vacation_WithAnArgument_ShowsRedHint_AndLoopKeepsRunning(string command)
    {
        WriteVacationDays();

        (int exitCode, string output) = Run(c =>
        {
            c.Profile.Width = 200;
            c.Input.PushTextWithEnter(command);
            c.Input.PushTextWithEnter("quit");
        });

        Assert.Equal(0, exitCode);
        string[] frame = LastLayout(output);
        AssertOnHintRow(frame, "vacation takes no arguments.");
        Assert.DoesNotContain(frame, l => l.Contains("Allowance", StringComparison.Ordinal));
        Assert.DoesNotContain("   at ", output);
        Assert.False(File.Exists(SettingsPath));

        (_, string[] next) = RunReport(command, "vacation", "quit");
        Assert.True(RowIndex(next, "Allowance", "30") > 0, string.Join("\n", next));

        string ansi = RunAnsiCommands(command);
        Assert.Matches(@"\x1b\[(31|91|38;5;9)m.*takes no arguments\.", ansi);
    }

    // AC-9
    [Fact]
    public void Help_ListsVacation_WithAliasVa()
    {
        TestConsole console = new TestConsole().Interactive().EmitAnsiSequences();
        console.Profile.Width = 120;
        console.Input.PushTextWithEnter("help");
        PageDown(console, HelpPages);
        console.Input.PushTextWithEnter("quit");

        new App(console, FixedTimeProvider.At20261027(), new DayStore(data.Path)).Run([]);
        string plain = Regex.Replace(console.Output, @"\x1b\[[0-9;?]*[A-Za-z]", string.Empty);

        Assert.Matches(@"│ vacation +│ va +│ Show vacation days taken and left this year +│", plain);
    }

    // ---- 117: second instance guard and stale-save refusal ----

    private string LockPath => Path.Combine(data.Path, InstanceLock.FileName);

    private static string PidLine => "pid=" + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Runs the app on the data folder with its own lock, checking at the first key what times.lock holds.</summary>
    private (int ExitCode, string Output, string? LockAtFirstKey) RunWatchingTheLock(params string[] commands)
    {
        TestConsole console = new TestConsole().Interactive();
        HookedInput input = new HookedInput(console.Input);
        string? seen = null;
        input.BeforeKey = key =>
        {
            if (key == 1)
            {
                seen = File.Exists(LockPath) ? InstanceLockTests.ReadShared(LockPath) : null;
            }
        };
        foreach (string command in commands)
        {
            console.Input.PushTextWithEnter(command);
        }

        int exitCode = new App(new InputOverrideConsole(console, input), FixedTimeProvider.At20261027(), new DayStore(data.Path)).Run([]);
        return (exitCode, console.Output, seen);
    }

    // AC-1 (117)
    [Fact]
    public void InstanceLock_NoLockFile_StartsNormally_AndHoldsTheLockWhileRunning()
    {
        (int exitCode, string output, string? seen) = RunWatchingTheLock("quit");

        Assert.Equal(0, exitCode);
        Assert.Contains(Heading, output);
        Assert.Equal(PidLine, seen?.TrimEnd('\n'));
    }

    // AC-1 (117): a leftover file no handle holds never blocks a start.
    [Theory]
    [InlineData("")]
    [InlineData("pid=99999")]
    [InlineData("garbage\n\u0001ÿ more\n")]
    public void InstanceLock_LeftoverFileWithoutLiveLock_StartsNormally(string content)
    {
        File.WriteAllText(LockPath, content);

        (int exitCode, string output, string? seen) = RunWatchingTheLock("quit");

        Assert.Equal(0, exitCode);
        Assert.Contains(Heading, output);
        Assert.Equal(PidLine, seen?.TrimEnd('\n'));
        Assert.False(File.Exists(LockPath));
    }

    // AC-2 (117)
    [Fact]
    public void InstanceLock_HeldByAnother_RefusesWithExitCode3_OneRedLine_NoLayout_NoData()
    {
        using InstanceLock other = new InstanceLock(data.Path);
        Assert.Equal(InstanceLockStatus.Acquired, other.TryAcquire().Status);
        string before = InstanceLockTests.ReadShared(LockPath);
        TestConsole console = new TestConsole().Interactive().EmitAnsiSequences();
        console.Profile.Width = 300;
        console.Input.PushTextWithEnter("arrive 8:00");
        console.Input.PushTextWithEnter("quit");

        int exitCode = new App(console, FixedTimeProvider.At20261027(), new DayStore(data.Path)).Run([]);

        Assert.Equal(3, exitCode);
        string plain = Regex.Replace(console.Output, @"\x1b\[[0-9;?]*[A-Za-z]", string.Empty);
        string line = Assert.Single(plain.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.StartsWith("Times is already running with this data folder", line);
        Assert.Contains(data.Path, line);
        Assert.Contains("Close the other copy", line);
        Assert.Matches(@"^\x1b\[(31|91|38;5;9)mTimes is already running", console.Output);
        Assert.DoesNotContain("╭", console.Output);
        Assert.DoesNotContain(Heading, console.Output);
        Assert.Equal([LockPath], Directory.EnumerateFileSystemEntries(data.Path));
        Assert.Equal(before, InstanceLockTests.ReadShared(LockPath));
        Assert.Equal(InstanceLockStatus.Held, new InstanceLock(data.Path).TryAcquire().Status);
    }

    // AC-2 (117): the folder in the line goes through ForDisplay and Markup.Escape.
    [Fact]
    public void InstanceLock_HeldByAnother_ShowsTheFolderCleanedAndEscaped()
    {
        string folder = Path.Combine(data.Path, "[x]‮y");
        using InstanceLock other = new InstanceLock(folder);
        Assert.Equal(InstanceLockStatus.Acquired, other.TryAcquire().Status);
        TestConsole console = new TestConsole().Interactive();
        console.Profile.Width = 300;

        Assert.Equal(3, new App(console, FixedTimeProvider.At20261027(), new DayStore(folder)).Run([]));

        Assert.Contains(Path.Combine(data.Path, "[x]y"), console.Output);
        Assert.DoesNotContain("‮", console.Output, StringComparison.Ordinal);
    }

    // AC-3 (117)
    [Fact]
    public void InstanceLock_AfterQuit_IsGone_AndASecondRunStarts()
    {
        Assert.Equal(0, RunCommands("quit").ExitCode);
        Assert.False(File.Exists(LockPath));

        (int exitCode, string output) = RunCommands("arrive 8:00", "quit");

        Assert.Equal(0, exitCode);
        Assert.Contains(Heading, output);
        Assert.False(File.Exists(LockPath));
        Assert.Equal([DayFile("2026-10-27")], Directory.EnumerateFileSystemEntries(data.Path));
    }

    // AC-3 (117): an exception thrown inside the loop (here: the input runs out) still releases the lock.
    [Fact]
    public void InstanceLock_ExceptionInsideTheLoop_ReleasesTheLock()
    {
        TestConsole console = new TestConsole().Interactive();
        console.Input.PushTextWithEnter("help");

        Assert.ThrowsAny<Exception>(() => new App(console, FixedTimeProvider.At20261027(), new DayStore(data.Path)).Run([]));

        Assert.False(File.Exists(LockPath));
        Assert.Equal(0, RunCommands("quit").ExitCode);
    }

    // AC-3 (117): a command handler that throws is caught by the loop; quitting afterwards releases the lock.
    [Fact]
    public void InstanceLock_ThrowingCommand_ThenQuit_ReleasesTheLock()
    {
        CommandRegistry registry = CommandRegistry.CreateDefault();
        registry.Register("boom", "bo", "Throws", _ => throw new InvalidOperationException("boom"));

        (int exitCode, _) = Run(c =>
        {
            c.Input.PushTextWithEnter("boom");
            c.Input.PushTextWithEnter("quit");
        }, registry);

        Assert.Equal(0, exitCode);
        Assert.False(File.Exists(LockPath));
    }

    // AC-3 (117): a refused start (the lock cannot be taken) leaves no times.lock behind, and a later start works.
    [Fact]
    public void InstanceLock_RefusedStart_LeavesNoLock_AndALaterStartWorks()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        UnixFileMode mode = File.GetUnixFileMode(data.Path);
        File.SetUnixFileMode(data.Path, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        int exitCode;
        try
        {
            if (WritableAnyway(data.Path))
            {
                return;
            }

            exitCode = RunCommands("quit").ExitCode;
        }
        finally
        {
            File.SetUnixFileMode(data.Path, mode);
        }

        Assert.Equal(4, exitCode);
        Assert.False(File.Exists(LockPath));
        Assert.Equal(0, RunCommands("quit").ExitCode);
        Assert.False(File.Exists(LockPath));
    }

    // AC-3 (117): the exit code 3 refusal leaves the other holder's lock in place.
    [Fact]
    public void InstanceLock_HeldRefusal_LeavesTheOthersLock_AndAfterItIsReleasedAStartWorks()
    {
        InstanceLock other = new InstanceLock(data.Path);
        Assert.Equal(InstanceLockStatus.Acquired, other.TryAcquire().Status);

        Assert.Equal(3, RunCommands("quit").ExitCode);
        Assert.True(File.Exists(LockPath));

        other.Dispose();
        Assert.Equal(0, RunCommands("quit").ExitCode);
        Assert.False(File.Exists(LockPath));
    }

    private static bool WritableAnyway(string folder)
    {
        try
        {
            string probe = Path.Combine(folder, "probe");
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // AC-4 (117)
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void InstanceLock_SymbolicLink_RefusesWithExitCode4_AndLeavesLinkAndTarget(bool targetExists)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using TempFolder elsewhere = new TempFolder();
        string target = Path.Combine(elsewhere.Path, "target");
        if (targetExists)
        {
            File.WriteAllText(target, "keep");
        }

        File.CreateSymbolicLink(LockPath, target);
        TestConsole console = new TestConsole().Interactive().EmitAnsiSequences();
        console.Profile.Width = 300;
        console.Input.PushTextWithEnter("quit");

        int exitCode = new App(console, FixedTimeProvider.At20261027(), new DayStore(data.Path)).Run([]);

        Assert.Equal(4, exitCode);
        Assert.Matches(@"^\x1b\[(31|91|38;5;9)mTimes cannot start: times\.lock is a symbolic link", console.Output);
        Assert.Single(console.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.DoesNotContain("╭", console.Output);
        Assert.Equal(target, new FileInfo(LockPath).LinkTarget);
        Assert.Equal(targetExists, File.Exists(target));
        if (targetExists)
        {
            Assert.Equal("keep", File.ReadAllText(target));
        }
    }

    // AC-4 (117)
    [Fact]
    public void InstanceLock_Directory_RefusesWithExitCode4()
    {
        Directory.CreateDirectory(LockPath);

        (int exitCode, string output) = RunCommands("quit");

        Assert.Equal(4, exitCode);
        Assert.StartsWith("Times cannot start: times.lock is a directory", output.Trim());
        Assert.True(Directory.Exists(LockPath));
    }

    // AC-4 (117)
    [Fact]
    public void InstanceLock_FolderCannotBeCreated_RefusesWithExitCode4()
    {
        string blocker = Path.Combine(data.Path, "file");
        File.WriteAllText(blocker, "x");

        (int exitCode, string output) = Run(c => c.Input.PushTextWithEnter("quit"), dataFolder: Path.Combine(blocker, "Times"));

        Assert.Equal(4, exitCode);
        Assert.StartsWith("Times cannot start: the data folder cannot be created", output.Trim());
        Assert.Equal("x", File.ReadAllText(blocker));
    }

    // AC-5 (117)
    [Fact]
    public void InstanceLock_VersionFlag_CreatesNoFolderAndNoLock()
    {
        using TempFolder missing = new TempFolder(create: false);
        TestConsole console = new TestConsole();

        Assert.Equal(0, new App(console, FixedTimeProvider.At20261027(), new DayStore(missing.Path)).Run(["--version"]));
        Assert.Equal(0, new App(new TestConsole(), FixedTimeProvider.At20261027(), new DayStore(data.Path)).Run(["--version"]));

        Assert.False(Directory.Exists(missing.Path));
        Assert.Empty(Directory.EnumerateFileSystemEntries(data.Path));
    }

    // AC-5 (117)
    [Fact]
    public void InstanceLock_InvalidFolder_CreatesNoFolderAndNoLock()
    {
        string relative = "times-117-" + Guid.NewGuid().ToString("N");
        string[] before = WorkingDirectoryEntries();

        Assert.Equal(2, new App(new TestConsole(), FixedTimeProvider.At20261027(), new DayStore(relative)).Run([]));

        Assert.False(Directory.Exists(relative));
        Assert.False(File.Exists(InstanceLock.FileName));
        Assert.Equal(before, WorkingDirectoryEntries());
    }

    private const string StaleFile =
        "{\"entries\":[{\"kind\":\"arrival\",\"time\":\"08:00\"}," +
        "{\"kind\":\"checkpoint\",\"time\":\"09:00\",\"name\":\"Task\",\"workItem\":5}," +
        "{\"kind\":\"checkpoint\",\"time\":\"10:00\",\"name\":\"Done\",\"workItem\":5,\"booked\":true}]}";

    private const string EditedFile =
        "{\"entries\":[{\"kind\":\"arrival\",\"time\":\"08:00\"}," +
        "{\"kind\":\"checkpoint\",\"time\":\"09:00\",\"name\":\"Task\",\"workItem\":5}," +
        "{\"kind\":\"checkpoint\",\"time\":\"10:00\",\"name\":\"Done\",\"workItem\":5,\"booked\":true}," +
        "{\"kind\":\"checkpoint\",\"time\":\"11:00\",\"name\":\"Edited outside\"}]}";

    private static string ChangedHint(string name) =>
        $"{name}: the file for 2026-10-27 changed on disk since it was shown. " +
        "The day is reloaded; check it and run the command again. Nothing was saved.";

    /// <summary>
    /// Runs <paramref name="typed"/> twice on a 250-column console. Before the first key (after the first redraw)
    /// the day file is rewritten to <paramref name="edited"/> (or deleted when null); before the second run of the
    /// command, what the file holds is recorded.
    /// </summary>
    private (string Output, string? BeforeRetry) RunStale(string typed, string? initial, string? edited)
    {
        if (initial is not null)
        {
            File.WriteAllText(DayFile("2026-10-27"), initial);
        }

        TestConsole console = new TestConsole().Interactive();
        console.Profile.Width = 250;
        HookedInput input = new HookedInput(console.Input);
        string? beforeRetry = null;
        int retryKey = typed.Length + 2;
        input.BeforeKey = key =>
        {
            if (key == 1)
            {
                if (edited is null)
                {
                    File.Delete(DayFile("2026-10-27"));
                }
                else
                {
                    File.WriteAllText(DayFile("2026-10-27"), edited);
                }
            }
            else if (key == retryKey)
            {
                beforeRetry = File.Exists(DayFile("2026-10-27")) ? ReadDay("2026-10-27") : null;
            }
        };
        console.Input.PushTextWithEnter(typed);
        console.Input.PushTextWithEnter(typed);
        console.Input.PushTextWithEnter("quit");

        Assert.Equal(0, new App(new InputOverrideConsole(console, input), FixedTimeProvider.At20261027(), new DayStore(data.Path)).Run([]));
        return (console.Output, beforeRetry);
    }

    // AC-8 (117): every writing command refuses a day changed on disk since the last redraw, then works on the retry.
    [Theory]
    [InlineData("arrive 12:00", "arrive")]
    [InlineData("leave 17:00", "leave")]
    [InlineData("break 12:00", "break")]
    [InlineData("breakend 12:30", "breakend")]
    [InlineData("checkpoint 13:00 Lunch call", "checkpoint")]
    [InlineData("remove 1", "remove")]
    [InlineData("change 1 7:30", "change")]
    [InlineData("booked 2", "booked")]
    [InlineData("unbooked 3", "unbooked")]
    [InlineData("a 12:00", "arrive")]
    [InlineData("cp 13:00 Lunch call", "checkpoint")]
    public void StaleDay_WritingCommand_IsRefused_ShowsTheNewContent_AndTheRetryWorks(string typed, string name)
    {
        (string output, string? beforeRetry) = RunStale(typed, StaleFile, EditedFile);

        List<string[]> frames = Frames(output);
        Assert.Equal(3, frames.Count);
        Assert.DoesNotContain(frames[0], l => l.Contains("Edited outside", StringComparison.Ordinal));
        AssertOnHintRow(frames[1], ChangedHint(name));
        Assert.Contains(frames[1], l => l.Contains("Edited outside", StringComparison.Ordinal));
        Assert.Equal(EditedFile, beforeRetry);
        Assert.DoesNotContain(frames[2], l => l.Contains("changed on disk", StringComparison.Ordinal));
        Assert.NotEqual(EditedFile, ReadDay("2026-10-27"));
        Assert.Contains("Edited outside", ReadDay("2026-10-27"));
    }

    // AC-8 (117): the red colour of the hint.
    [Fact]
    public void StaleDay_Hint_IsRed()
    {
        File.WriteAllText(DayFile("2026-10-27"), StaleFile);
        TestConsole console = new TestConsole().Interactive().EmitAnsiSequences();
        console.Profile.Width = 250;
        HookedInput input = new HookedInput(console.Input);
        input.BeforeKey = key =>
        {
            if (key == 1)
            {
                File.WriteAllText(DayFile("2026-10-27"), EditedFile);
            }
        };
        console.Input.PushTextWithEnter("arrive 12:00");
        console.Input.PushTextWithEnter("quit");

        Assert.Equal(0, new App(new InputOverrideConsole(console, input), FixedTimeProvider.At20261027(), new DayStore(data.Path)).Run([]));

        Assert.Matches(@"\x1b\[(31|91|38;5;9)marrive: the file for 2026-10-27 changed on disk since it was shown\.", console.Output);
    }

    // AC-8 (117): absence, with a file created after the redraw that showed none.
    [Fact]
    public void StaleDay_Absence_FileCreatedAfterTheRedraw_IsRefused_AndTheRetryWorks()
    {
        (string output, string? beforeRetry) = RunStale("absence vacation", null, "{\"absence\":\"sick\"}");

        List<string[]> frames = Frames(output);
        AssertOnHintRow(frames[1], ChangedHint("absence"));
        Assert.Contains(frames[1], l => l.Contains("Sick", StringComparison.Ordinal));
        Assert.Equal("{\"absence\":\"sick\"}", beforeRetry);
        Assert.Contains("vacation", ReadDay("2026-10-27"));
    }

    // AC-8 (117): a file deleted after the redraw is not booked over either.
    [Fact]
    public void StaleDay_FileDeletedAfterTheRedraw_IsRefused_AndTheRetryWorks()
    {
        (string output, string? beforeRetry) = RunStale("arrive 12:00", StaleFile, null);

        List<string[]> frames = Frames(output);
        AssertOnHintRow(frames[1], ChangedHint("arrive"));
        Assert.Null(beforeRetry);
        Assert.Single(new DayStore(data.Path).Load(new DateOnly(2026, 10, 27)).Entries);
    }

    // AC-8 (117): a change between the command's own load and its save is refused the same way.
    [Fact]
    public void StaleDay_ChangeBetweenLoadAndSave_IsRefused_AndKeepsTheFile()
    {
        File.WriteAllText(DayFile("2026-10-27"), StaleFile);
        DayStore store = new DayStore(data.Path);
        store.BeforeStampCheck = day => File.WriteAllText(store.PathFor(day), EditedFile);
        TestConsole console = new TestConsole().Interactive();
        console.Profile.Width = 250;
        console.Input.PushTextWithEnter("leave 17:00");
        console.Input.PushTextWithEnter("quit");

        Assert.Equal(0, new App(console, FixedTimeProvider.At20261027(), store).Run([]));

        AssertOnHintRow(LastLayout(console.Output), ChangedHint("leave"));
        Assert.Equal(EditedFile, ReadDay("2026-10-27"));
    }

    // AC-9 (117): a day changed and shown again (a full redraw after another command) is written as before.
    [Fact]
    public void StaleDay_ChangeShownByARedraw_IsNotRefused()
    {
        File.WriteAllText(DayFile("2026-10-27"), StaleFile);
        TestConsole console = new TestConsole().Interactive();
        console.Profile.Width = 250;
        HookedInput input = new HookedInput(console.Input);
        input.BeforeKey = key =>
        {
            if (key == 1)
            {
                File.WriteAllText(DayFile("2026-10-27"), EditedFile);
            }
        };
        console.Input.PushTextWithEnter("help");
        console.Input.PushTextWithEnter("leave 17:00");
        console.Input.PushTextWithEnter("quit");

        Assert.Equal(0, new App(new InputOverrideConsole(console, input), FixedTimeProvider.At20261027(), new DayStore(data.Path)).Run([]));

        Assert.DoesNotContain("changed on disk", console.Output);
        DayRecord saved = new DayStore(data.Path).Load(new DateOnly(2026, 10, 27));
        Assert.Equal(5, saved.Entries.Count);
        Assert.Contains(saved.Entries, e => e.Name == "Edited outside");
    }

    // AC-10 (117): sync send never writes over a day changed between MarkBooked's load and its save.
    [Fact]
    public void SyncSend_DayChangedBeforeTheSave_IsNotWritten_AndReportedLikeADayChange()
    {
        WriteSyncDays();
        DayStore store = new DayStore(data.Path);
        string edited = ReadDay("2026-11-03").Replace("\"Open\"", "\"Open edited\"", StringComparison.Ordinal);
        store.BeforeStampCheck = day =>
        {
            if (day == new DateOnly(2026, 11, 3))
            {
                File.WriteAllText(store.PathFor(day), edited);
            }
        };
        FakeDevOpsClient client = new FakeDevOpsClient();
        TestConsole console = new TestConsole().Interactive();
        console.Profile.Height = 40;
        console.Profile.Width = 140;
        console.Input.PushTextWithEnter("sync 2026-11-02 2026-11-04 send");
        console.Input.PushTextWithEnter("quit");

        new App(console, FixedTimeProvider.At20261027(), store,
            tokenStore: new FakeTokenStore(stored: ("contoso", DevOpsPat)), devOpsClient: client.For).Run([]);
        string output = console.Output;

        Assert.Contains("#12: the hours were sent, but 2026-11-03 could not be saved. Run booked 1 on 2026-11-03 by hand.", output);
        Assert.Contains("and some days could not be saved", LastHint(output));
        Assert.Equal(edited, ReadDay("2026-11-03"));
        Assert.DoesNotContain("\"booked\":true", ReadDay("2026-11-03"));
        Assert.True(new DayStore(data.Path).Load(new DateOnly(2026, 11, 2)).Entries.Single(e => e.Name == "Review").Booked);
    }

    // Job 118 ------------------------------------------------------------------------------

    private const string ImportDay = "2026-10-28";

    /// <summary>Runs <paramref name="input"/> on a wide 60-row console (the caller ends with quit) and returns the exit code and output.</summary>
    private (int ExitCode, string Output) RunImport(Action<TestConsole> input, DayStore? store = null, int height = 60, bool ansi = false)
    {
        TestConsole console = new TestConsole().Interactive();
        if (ansi)
        {
            console.EmitAnsiSequences();
        }

        console.Profile.Height = height;
        console.Profile.Width = 250;
        input(console);
        int exitCode = new App(console, FixedTimeProvider.At20261027(), store ?? new DayStore(data.Path), instanceLock: sideLock).Run([]);
        Assert.DoesNotContain("   at ", console.Output);
        return (exitCode, console.Output);
    }

    /// <summary>Runs <paramref name="commands"/> then quit and returns the last frame's rows, trimmed.</summary>
    private string[] ImportFrame(params string[] commands)
    {
        (int exitCode, string output) = RunImport(c =>
        {
            foreach (string command in commands)
            {
                c.Input.PushTextWithEnter(command);
            }

            c.Input.PushTextWithEnter("quit");
        });
        Assert.Equal(0, exitCode);
        return LastLayout(output).Select(l => l.Trim()).ToArray();
    }

    /// <summary>A source folder holding the sample as <c>2026-10-28.json</c>.</summary>
    private static TempFolder SampleSource()
    {
        TempFolder source = new TempFolder();
        File.Copy(OldDataImportTests.SamplePath, Path.Combine(source.Path, ImportDay + ".json"));
        return source;
    }

    /// <summary>Names, sizes and SHA-256 of everything in <paramref name="folder"/>, recursively.</summary>
    private static List<string> SourceSnapshot(string folder) =>
        Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .Select(p => File.Exists(p)
                ? $"{p} {new FileInfo(p).Length} {Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p)))}"
                : p + "/")
            .ToList();

    private static string ImportHint(string[] frame) => HintText(frame, "import:");

    private static string OldFile(params string[] entries) => "{\"Recordings\":[" + string.Join(',', entries) + "]}";

    private const string OldArrival = "{\"Time\":\"08:00:00\",\"Type\":0,\"Description\":null,\"Booked\":null,\"UsedTime\":null}";

    // AC-1
    [Theory]
    [InlineData("none", "import: say the folder. Use import <folder>.")]
    [InlineData("relative", "is not an absolute path.")]
    [InlineData("missing", "does not exist.")]
    [InlineData("file", "is a file, not a folder.")]
    [InlineData("data", "import: choose a folder outside the data folder.")]
    [InlineData("below", "import: choose a folder outside the data folder.")]
    public void Import_BadFolder_ShowsRedHint_AndReadsOrWritesNothing(string kind, string hint)
    {
        using TempFolder source = SampleSource();
        string file = Path.Combine(source.Path, ImportDay + ".json");
        string below = Path.Combine(data.Path, "old");
        Directory.CreateDirectory(below);
        File.Copy(file, Path.Combine(below, ImportDay + ".json"));
        File.Copy(file, Path.Combine(data.Path, "2026-10-29.json"));
        string folder = kind switch
        {
            "none" => "   ",
            "relative" => "old" + Guid.NewGuid().ToString("N"),
            "missing" => Path.Combine(source.Path, "missing"),
            "file" => file,
            "data" => data.Path,
            _ => below,
        };
        Dictionary<string, (DateTime Written, byte[] Bytes)> before = DataSnapshot();
        List<string> sourceBefore = SourceSnapshot(source.Path);
        string[] cwdBefore = WorkingDirectoryEntries();

        string[] frame = ImportFrame($"import {folder}");

        string expected = kind switch
        {
            "none" or "data" or "below" => hint,
            "missing" => $"import: the folder {folder} {hint}",
            _ => $"import: {folder} {hint}",
        };
        Assert.Contains(expected, ImportHint(frame));
        Assert.DoesNotContain(frame, l => l.Contains("Imported", StringComparison.Ordinal));
        AssertSameSnapshot(before);
        Assert.Equal(sourceBefore, SourceSnapshot(source.Path));
        Assert.Equal(cwdBefore, WorkingDirectoryEntries());
    }

    // AC-1
    [Fact]
    public void Import_HintIsRed()
    {
        string output = RunAnsiCommands("import");

        Assert.Matches(@"\x1b\[[0-9;]*(31|91|38;5;9)mimport: say the folder", output);
    }

    // AC-3, AC-7, AC-10, AC-11
    [Fact]
    public void Import_Sample_WritesTheDay_AndLeavesTheSourceAlone()
    {
        using TempFolder source = SampleSource();
        List<string> sourceBefore = SourceSnapshot(source.Path);

        string[] frame = ImportFrame($"import {source.Path}");

        DayRecord expected = OldDataImport.Parse(new DateOnly(2026, 10, 28), OldDataImportTests.Sample()).Record!;
        Assert.Equal(expected, new DayStore(data.Path).Load(new DateOnly(2026, 10, 28)));
        Assert.Equal(expected.ToJson(), ReadDay(ImportDay));
        Assert.Contains($"Imported 1 day from {source.Path}", frame);
        Assert.Contains("0 already there · 0 kept", frame);
        Assert.DoesNotContain(frame, l => l.StartsWith('!'));
        Assert.DoesNotContain(frame, l => l.Contains("Skipped", StringComparison.Ordinal));
        Assert.Equal(sourceBefore, SourceSnapshot(source.Path));
        Assert.Equal([ImportDay + ".json"], Listing(data.Path));
    }

    // AC-10
    [Fact]
    public void Import_Colours_SteelBlueCount_GreyCounts_YellowLines()
    {
        using TempFolder source = SampleSource();
        File.WriteAllText(Path.Combine(source.Path, "2026-10-29.json"), "{}");
        File.WriteAllText(Path.Combine(source.Path, "notes.txt"), "x");

        string output = RunAnsiCommands($"import {source.Path}");

        Assert.Matches(@"Imported \x1b\[[0-9;]*38;5;75m1\x1b\[0m day from ", output);
        Assert.Matches(@"\x1b\[[0-9;]*38;5;8m0 already there · 0 kept", output);
        Assert.Matches(@"\x1b\[(33|93|38;5;11)m! 2026-10-29\.json: has no Recordings list", output);
        Assert.Matches(@"\x1b\[[0-9;]*38;5;8mSkipped \(not a day file\): notes\.txt", output);
    }

    // AC-8
    [Fact]
    public void Import_Twice_ChangesNothing_AndCountsAlreadyThere()
    {
        using TempFolder source = SampleSource();
        File.WriteAllText(Path.Combine(source.Path, "2026-10-29.json"), OldFile(OldArrival));
        ImportFrame($"import {source.Path}");
        Dictionary<string, (DateTime Written, byte[] Bytes)> before = DataSnapshot();

        string[] frame = ImportFrame($"import {source.Path}");

        Assert.Contains($"Imported 0 days from {source.Path}", frame);
        Assert.Contains("2 already there · 0 kept", frame);
        Assert.DoesNotContain(frame, l => l.Contains("already has a different day", StringComparison.Ordinal));
        AssertSameSnapshot(before);
    }

    // AC-8: same entries, same order, means already there, even when the file was written by hand.
    [Fact]
    public void Import_ExistingDayWithTheSameEntries_IsNotAskedAbout()
    {
        using TempFolder source = new TempFolder();
        File.WriteAllText(Path.Combine(source.Path, "2026-10-29.json"), OldFile(OldArrival));
        File.WriteAllText(DayFile("2026-10-29"), "{ \"entries\": [ { \"kind\": \"arrival\", \"time\": \"08:00\" } ] }");
        Dictionary<string, (DateTime Written, byte[] Bytes)> before = DataSnapshot();

        string[] frame = ImportFrame($"import {source.Path}");

        Assert.Contains("1 already there · 0 kept", frame);
        AssertSameSnapshot(before);
    }

    // AC-2
    [Fact]
    public void Import_OtherNames_AreListedInOrdinalOrder_AtMost20()
    {
        using TempFolder source = SampleSource();
        for (int i = 0; i < 23; i++)
        {
            File.WriteAllText(Path.Combine(source.Path, $"n{i:00}"), "x");
        }

        Directory.CreateDirectory(Path.Combine(source.Path, "Archive"));
        Directory.CreateDirectory(Path.Combine(source.Path, "deeper"));
        File.Copy(OldDataImportTests.SamplePath, Path.Combine(source.Path, "deeper", "2026-10-30.json"));

        string[] frame = ImportFrame($"import {source.Path}");

        string expected = "Skipped (not a day file): Archive, deeper, " + string.Join(", ", Enumerable.Range(0, 18).Select(i => $"n{i:00}"));
        Assert.Contains(expected, frame);
        Assert.Contains("… and 5 more", frame);
        Assert.Equal([ImportDay + ".json"], Listing(data.Path));
    }

    // 120 AC-6: 25 non-day-file entries show the 20 ordinal-smallest names, then a grey "… and 5 more".
    [Fact]
    public void Import_25OtherNames_ShowThe20OrdinalSmallest_AndFiveMore()
    {
        using TempFolder source = new TempFolder();
        for (int i = 24; i >= 0; i--)
        {
            File.WriteAllText(Path.Combine(source.Path, $"n{i:00}"), "x");
        }

        string[] frame = TallImportFrame($"import {source.Path}");
        string output = RunAnsiCommands($"import {source.Path}");

        Assert.Contains("Skipped (not a day file): " + string.Join(", ", Enumerable.Range(0, 20).Select(i => $"n{i:00}")), frame);
        Assert.Contains("… and 5 more", frame);
        Assert.DoesNotContain(frame, l => l.Contains("n20", StringComparison.Ordinal));
        Assert.Matches(@"\x1b\[[0-9;]*38;5;8m… and 5 more", output);
    }

    // 120 AC-7: a source folder that cannot be listed gives the red hint, never a stack trace.
    [Fact]
    public void Import_FolderThatCannotBeListed_ShowsRedHint()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using TempFolder source = new TempFolder();
        File.WriteAllText(Path.Combine(source.Path, "notes.txt"), "x");
        UnixFileMode mode = File.GetUnixFileMode(source.Path);
        File.SetUnixFileMode(source.Path, UnixFileMode.None);
        try
        {
            try
            {
                Directory.EnumerateFileSystemEntries(source.Path).ToList();
                // Running as root: access can't be denied.
                return;
            }
            catch (UnauthorizedAccessException)
            {
            }

            string[] frame = ImportFrame($"import {source.Path}");
            string output = RunAnsiCommands($"import {source.Path}");

            Assert.StartsWith($"import: the folder {source.Path} cannot be listed", ImportHint(frame));
            Assert.Matches(@"\x1b\[[0-9;]*(31|91|38;5;9)mimport: the folder ", output);
            Assert.DoesNotContain("   at ", output);
            Assert.Empty(Listing(data.Path));
        }
        finally
        {
            File.SetUnixFileMode(source.Path, mode);
        }
    }

    // AC-2, AC-10: markup and control characters in a name show literally.
    [Fact]
    public void Import_MarkupInASkippedName_ShowsLiterally()
    {
        using TempFolder source = SampleSource();
        File.WriteAllText(Path.Combine(source.Path, "[red]x[[\u0007.txt"), "x");

        string[] frame = ImportFrame($"import {source.Path}");

        Assert.Contains("Skipped (not a day file): [red]x[[.txt", frame);
    }

    // AC-10
    [Fact]
    public void Import_NoCandidates_ShowsAGreyNote()
    {
        using TempFolder source = new TempFolder();
        File.WriteAllText(Path.Combine(source.Path, "readme.md"), "x");

        string[] frame = ImportFrame($"import {source.Path}");

        Assert.Contains($"No day files to import in {source.Path}. Nothing was written.", frame);
        Assert.Contains("Skipped (not a day file): readme.md", frame);
        Assert.DoesNotContain(frame, l => l.StartsWith("Imported", StringComparison.Ordinal));
        Assert.Empty(Listing(data.Path));
    }

    // AC-5, AC-10: a broken file is listed with its reason, and the others still import.
    [Fact]
    public void Import_BrokenFile_IsListed_AndOthersStillImport()
    {
        using TempFolder source = SampleSource();
        File.WriteAllText(Path.Combine(source.Path, "2026-10-29.json"),
            OldFile(OldArrival, "{\"Time\":\"09:00:00\",\"Type\":9}"));
        File.WriteAllBytes(Path.Combine(source.Path, "2026-10-30.json"), new byte[DayStore.MaxFileBytes + 1]);

        string[] frame = ImportFrame($"import {source.Path}");

        Assert.Contains($"Imported 1 day from {source.Path}", frame);
        Assert.Contains("! 2026-10-29.json: entry 2 has no Type from 0 to 4", frame);
        Assert.Contains("! 2026-10-30.json: is larger than 64 KiB", frame);
        Assert.Equal([ImportDay + ".json"], Listing(data.Path));
    }

    // AC-6
    [Fact]
    public void Import_UsedTimeMismatch_IsAYellowWarning_AndTheDayStillImports()
    {
        using TempFolder source = new TempFolder();
        string json = File.ReadAllText(OldDataImportTests.SamplePath)
            .Replace("\"UsedTime\":\"00:02:00\"", "\"UsedTime\":\"00:05:00\"", StringComparison.Ordinal)
            .Replace("\"UsedTime\":\"00:36:00\"", "\"UsedTime\":\"later\"", StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(source.Path, ImportDay + ".json"), json);

        string[] frame = ImportFrame($"import {source.Path}");

        Assert.Contains("! 2026-10-28 07:22 Post & Rückfragen: UsedTime 0:05 but the entries give 0:02", frame);
        Assert.Contains("! 2026-10-28 07:24 KLM: UsedTime is not a time", frame);
        Assert.Contains($"Imported 1 day from {source.Path}", frame);
    }

    /// <summary>The day file a test puts in the way of the import: one arrival at 06:00.</summary>
    private const string OtherDay = "{\"entries\":[{\"kind\":\"arrival\",\"time\":\"06:00\"}]}";

    private const string ReplacePrompt = "already has a different day. Replace it with the imported one? Type yes to replace.";

    // AC-9
    [Theory]
    [InlineData("yes")]
    [InlineData("  Y ")]
    [InlineData("YES")]
    public void Import_DifferentDay_AnsweredYes_IsReplaced(string answer)
    {
        using TempFolder source = SampleSource();
        File.WriteAllText(DayFile(ImportDay), OtherDay);

        (int exitCode, string output) = RunImport(c =>
        {
            c.Input.PushTextWithEnter($"import {source.Path}");
            c.Input.PushTextWithEnter(answer);
            c.Input.PushTextWithEnter("quit");
        });

        Assert.Equal(0, exitCode);
        Assert.Contains($"{ImportDay} {ReplacePrompt}", output);
        Assert.Contains(App.ImportPlaceholder, output);
        Assert.Equal(24, new DayStore(data.Path).Load(new DateOnly(2026, 10, 28)).Entries.Count);
        string[] frame = LastLayout(output).Select(l => l.Trim()).ToArray();
        Assert.Contains($"Imported 1 day from {source.Path}", frame);
        Assert.Contains("0 already there · 0 kept", frame);
    }

    // AC-9
    [Theory]
    [InlineData("n")]
    [InlineData("")]
    [InlineData("yess")]
    public void Import_DifferentDay_AnsweredOtherwise_IsKept(string answer)
    {
        using TempFolder source = SampleSource();
        File.WriteAllText(DayFile(ImportDay), OtherDay);
        Dictionary<string, (DateTime Written, byte[] Bytes)> before = DataSnapshot();

        (_, string output) = RunImport(c =>
        {
            c.Input.PushTextWithEnter($"import {source.Path}");
            c.Input.PushTextWithEnter(answer);
            c.Input.PushTextWithEnter("quit");
        });

        AssertSameSnapshot(before);
        string[] frame = LastLayout(output).Select(l => l.Trim()).ToArray();
        Assert.Contains($"Imported 0 days from {source.Path}", frame);
        Assert.Contains("0 already there · 1 kept", frame);
    }

    // AC-9: one day at a time in date order; Esc keeps this day and every later one without asking.
    [Fact]
    public void Import_Esc_KeepsThisAndEveryLaterConflict()
    {
        using TempFolder source = SampleSource();
        foreach (string date in new[] { "2026-10-29", "2026-10-30" })
        {
            File.Copy(OldDataImportTests.SamplePath, Path.Combine(source.Path, date + ".json"));
        }

        foreach (string date in new[] { ImportDay, "2026-10-29", "2026-10-30" })
        {
            File.WriteAllText(DayFile(date), OtherDay);
        }

        (_, string output) = RunImport(c =>
        {
            c.Input.PushTextWithEnter($"import {source.Path}");
            c.Input.PushTextWithEnter("yes");
            c.Input.PushKey(ConsoleKey.Escape);
            c.Input.PushTextWithEnter("quit");
        });

        int first = output.IndexOf($"{ImportDay} {ReplacePrompt}", StringComparison.Ordinal);
        int second = output.IndexOf($"2026-10-29 {ReplacePrompt}", StringComparison.Ordinal);
        Assert.True(first >= 0 && second > first);
        Assert.DoesNotContain($"2026-10-30 {ReplacePrompt}", output);
        Assert.Equal(24, new DayStore(data.Path).Load(new DateOnly(2026, 10, 28)).Entries.Count);
        Assert.Equal(OtherDay, ReadDay("2026-10-29"));
        Assert.Equal(OtherDay, ReadDay("2026-10-30"));
        string[] frame = LastLayout(output).Select(l => l.Trim()).ToArray();
        Assert.Contains($"Imported 1 day from {source.Path}", frame);
        Assert.Contains("0 already there · 2 kept", frame);
    }

    // AC-9: end of input stops like the token prompt, with nothing written.
    [Fact]
    public void Import_EndOfInputAtThePrompt_EndsTheLoop_AndKeepsTheDay()
    {
        using TempFolder source = SampleSource();
        File.WriteAllText(DayFile(ImportDay), OtherDay);

        TestConsole test = new TestConsole().Interactive();
        IAnsiConsole console = new InputOverrideConsole(test, new EndingInput($"import {source.Path}\r"));

        int exitCode = new App(console, FixedTimeProvider.At20261027(), new DayStore(data.Path)).Run([]);

        Assert.Equal(0, exitCode);
        Assert.Contains($"{ImportDay} already has a different day.", test.Output);
        Assert.Equal(OtherDay, ReadDay(ImportDay));
    }

    // AC-9: an absence is a different day too.
    [Fact]
    public void Import_AbsenceDay_IsAskedAbout()
    {
        using TempFolder source = SampleSource();
        WriteAbsenceDay(ImportDay, "vacation");

        (_, string output) = RunImport(c =>
        {
            c.Input.PushTextWithEnter($"import {source.Path}");
            c.Input.PushTextWithEnter("no");
            c.Input.PushTextWithEnter("quit");
        });

        Assert.Contains($"{ImportDay} {ReplacePrompt}", output);
        Assert.Equal("{\"absence\":\"vacation\"}", ReadDay(ImportDay));
    }

    // AC-9
    [Fact]
    public void Import_UnreadableExistingDay_IsNeverAskedAbout_OrWritten()
    {
        using TempFolder source = SampleSource();
        File.WriteAllText(DayFile(ImportDay), "{not json");
        Dictionary<string, (DateTime Written, byte[] Bytes)> before = DataSnapshot();

        (_, string output) = RunImport(c =>
        {
            c.Input.PushTextWithEnter($"import {source.Path}");
            c.Input.PushTextWithEnter("quit");
        });

        Assert.DoesNotContain(ReplacePrompt, output);
        AssertSameSnapshot(before);
        string[] frame = LastLayout(output).Select(l => l.Trim()).ToArray();
        Assert.Contains("! 2026-10-28: existing day file cannot be read, kept", frame);
        Assert.Contains("0 already there · 1 kept", frame);
    }

    // AC-7: a file that appears between the check and the save is not overwritten.
    [Fact]
    public void Import_DayFileAppearingMeanwhile_IsNotOverwritten()
    {
        using TempFolder source = SampleSource();
        DayStore store = new DayStore(data.Path);
        store.BeforeStampCheck = day => File.WriteAllText(store.PathFor(day), OtherDay);

        (_, string output) = RunImport(c =>
        {
            c.Input.PushTextWithEnter($"import {source.Path}");
            c.Input.PushTextWithEnter("quit");
        }, store);

        Assert.Equal(OtherDay, ReadDay(ImportDay));
        string[] frame = LastLayout(output).Select(l => l.Trim()).ToArray();
        Assert.Contains("! 2026-10-28: a day file appeared meanwhile, not written", frame);
        Assert.Contains($"Imported 0 days from {source.Path}", frame);
    }

    // AC-9: a replace whose file changed while the user was asked is not written.
    [Fact]
    public void Import_DayChangedWhileAsking_IsNotReplaced()
    {
        using TempFolder source = SampleSource();
        File.WriteAllText(DayFile(ImportDay), OtherDay);
        const string Edited = "{\"entries\":[{\"kind\":\"arrival\",\"time\":\"06:30\"}]}";
        DayStore store = new DayStore(data.Path);
        store.BeforeStampCheck = day => File.WriteAllText(store.PathFor(day), Edited);

        (_, string output) = RunImport(c =>
        {
            c.Input.PushTextWithEnter($"import {source.Path}");
            c.Input.PushTextWithEnter("yes");
            c.Input.PushTextWithEnter("quit");
        }, store);

        Assert.Equal(Edited, ReadDay(ImportDay));
        Assert.Contains("! 2026-10-28: the day file changed on disk, not written", LastLayout(output).Select(l => l.Trim()));
    }

    // AC-10: a failed save is a yellow line, and the remaining days continue.
    [Fact]
    public void Import_FailedSave_IsAYellowLine_AndTheRestContinue()
    {
        using TempFolder source = SampleSource();
        File.Copy(OldDataImportTests.SamplePath, Path.Combine(source.Path, "2026-10-29.json"));
        DayStore store = new DayStore(data.Path);
        store.BeforeStampCheck = day =>
        {
            if (day == new DateOnly(2026, 10, 28))
            {
                throw new IOException("disk full");
            }
        };

        (int exitCode, string output) = RunImport(c =>
        {
            c.Input.PushTextWithEnter($"import {source.Path}");
            c.Input.PushTextWithEnter("quit");
        }, store);

        Assert.Equal(0, exitCode);
        string[] frame = LastLayout(output).Select(l => l.Trim()).ToArray();
        Assert.Contains("! 2026-10-28: could not be written: disk full", frame);
        Assert.Contains($"Imported 1 day from {source.Path}", frame);
        Assert.Equal(["2026-10-29.json"], Listing(data.Path));
    }

    // AC-1: a quoted folder with spaces and a trailing blank is the whole trimmed argument; the alias works.
    [Fact]
    public void Import_QuotedFolderWithSpaces_AndAlias()
    {
        using TempFolder source = new TempFolder();
        string folder = Path.Combine(source.Path, "old data");
        Directory.CreateDirectory(folder);
        File.Copy(OldDataImportTests.SamplePath, Path.Combine(folder, ImportDay + ".json"));

        string[] frame = ImportFrame($"/im   \"{folder}\"  ");

        Assert.Contains($"Imported 1 day from {folder}", frame);
    }

    // AC-11
    [Fact]
    public void Help_ListsImport_WithAliasIm()
    {
        TestConsole console = new TestConsole().Interactive().EmitAnsiSequences();
        console.Profile.Width = 120;
        console.Profile.Height = 80;
        console.Input.PushTextWithEnter("help");
        console.Input.PushTextWithEnter("quit");

        new App(console, FixedTimeProvider.At20261027(), new DayStore(data.Path)).Run([]);
        string plain = Regex.Replace(console.Output, @"\x1b\[[0-9;?]*[A-Za-z]", string.Empty);

        Assert.Matches(@"│ import +│ im +│ Import day files from the old tool: <folder> +│", plain);
        Assert.Matches(@"\x1b\[[0-9;]*38;5;75mim\x1b\[0m", console.Output);
    }

    // Job 119 ------------------------------------------------------------------------------

    /// <summary>Runs <paramref name="command"/> then quit on a 120-row console and returns the last frame's rows, trimmed.</summary>
    private string[] TallImportFrame(string command, DayStore? store = null, params string[] answers)
    {
        (int exitCode, string output) = RunImport(c =>
        {
            c.Input.PushTextWithEnter(command);
            foreach (string answer in answers)
            {
                c.Input.PushTextWithEnter(answer);
            }

            c.Input.PushTextWithEnter("quit");
        }, store, height: 120);
        Assert.Equal(0, exitCode);
        return LastLayout(output).Select(l => l.Trim()).ToArray();
    }

    /// <summary>The date of the <paramref name="index"/>-th (0-based) broken file: 2026-01-01 onwards.</summary>
    private static string BrokenDate(int index) => new DateOnly(2026, 1, 1).AddDays(index).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Writes <paramref name="count"/> broken day files (<c>{}</c>) from 2026-01-01 on.</summary>
    private static void WriteBroken(string folder, int count)
    {
        for (int i = 0; i < count; i++)
        {
            File.WriteAllText(Path.Combine(folder, BrokenDate(i) + ".json"), "{}");
        }
    }

    /// <summary>The date of the <paramref name="index"/>-th (0-based) warning day: 2026-03-01 onwards.</summary>
    private static string WarningDate(int index) => new DateOnly(2026, 3, 1).AddDays(index).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Writes <paramref name="count"/> copies of the sample from 2026-03-01 on, each with one <c>UsedTime</c> mismatch.</summary>
    private static void WriteWarningDays(string folder, int count)
    {
        string json = File.ReadAllText(OldDataImportTests.SamplePath)
            .Replace("\"UsedTime\":\"00:02:00\"", "\"UsedTime\":\"00:05:00\"", StringComparison.Ordinal);
        for (int i = 0; i < count; i++)
        {
            File.WriteAllText(Path.Combine(folder, WarningDate(i) + ".json"), json);
        }
    }

    private static string[] YellowLines(string[] frame) => frame.Where(l => l.StartsWith("! ", StringComparison.Ordinal)).ToArray();

    private static bool IsMoreLine(string line) => Regex.IsMatch(line, "^… and [0-9]+ more$");

    // AC-1: 20 or fewer lines are all shown, with no more line.
    [Theory]
    [InlineData(3)]
    [InlineData(20)]
    public void Import_UpTo20Lines_AreAllShown_WithoutAMoreLine(int count)
    {
        Assert.Equal(20, App.MaxImportLines);
        using TempFolder source = new TempFolder();
        WriteBroken(source.Path, count);

        string[] frame = TallImportFrame($"import {source.Path}");

        string[] yellow = YellowLines(frame);
        Assert.Equal(count, yellow.Length);
        for (int i = 0; i < count; i++)
        {
            Assert.StartsWith($"! {BrokenDate(i)}.json: ", yellow[i], StringComparison.Ordinal);
        }

        Assert.DoesNotContain(frame, IsMoreLine);
    }

    // AC-2: more than 20 lines show the first 20 in order, then one grey more line.
    [Fact]
    public void Import_25BrokenFiles_Show20Lines_AndFiveMore()
    {
        using TempFolder source = new TempFolder();
        WriteBroken(source.Path, 25);

        string[] frame = TallImportFrame($"import {source.Path}");

        string[] yellow = YellowLines(frame);
        Assert.Equal(20, yellow.Length);
        for (int i = 0; i < 20; i++)
        {
            Assert.StartsWith($"! {BrokenDate(i)}.json: ", yellow[i], StringComparison.Ordinal);
        }

        for (int i = 20; i < 25; i++)
        {
            Assert.DoesNotContain(frame, l => l.Contains(BrokenDate(i), StringComparison.Ordinal));
        }

        Assert.Equal(["… and 5 more"], frame.Where(IsMoreLine));
        int more = Array.IndexOf(frame, "… and 5 more");
        Assert.Equal(yellow[^1], frame[more - 1]);
        Assert.Matches(@"\x1b\[[0-9;]*38;5;8m… and 5 more", ColouredRun(source.Path));
    }

    /// <summary>The ANSI output of an import of <paramref name="folder"/> on a tall console.</summary>
    private string ColouredRun(string folder)
    {
        (int exitCode, string output) = RunImport(c =>
        {
            c.Input.PushTextWithEnter($"import {folder}");
            c.Input.PushTextWithEnter("quit");
        }, height: 120, ansi: true);
        Assert.Equal(0, exitCode);
        return output;
    }

    // AC-3: the cap applies to problems then warnings as one list.
    [Fact]
    public void Import_15ProblemsAnd10Warnings_ShowProblemsThenFiveWarnings_AndFiveMore()
    {
        using TempFolder source = new TempFolder();
        WriteBroken(source.Path, 15);
        WriteWarningDays(source.Path, 10);

        string[] frame = TallImportFrame($"import {source.Path}");

        string[] yellow = YellowLines(frame);
        Assert.Equal(20, yellow.Length);
        for (int i = 0; i < 15; i++)
        {
            Assert.StartsWith($"! {BrokenDate(i)}.json: ", yellow[i], StringComparison.Ordinal);
        }

        for (int i = 0; i < 5; i++)
        {
            Assert.Equal($"! {WarningDate(i)} 07:22 Post & Rückfragen: UsedTime 0:05 but the entries give 0:02", yellow[15 + i]);
        }

        Assert.Equal(["… and 5 more"], frame.Where(IsMoreLine));
        Assert.Contains($"Imported 10 days from {source.Path}", frame);
    }

    // AC-3: warnings alone are capped too.
    [Fact]
    public void Import_23Warnings_ShowTheFirst20_AndThreeMore()
    {
        using TempFolder source = new TempFolder();
        WriteWarningDays(source.Path, 23);

        string[] frame = TallImportFrame($"import {source.Path}");

        string[] yellow = YellowLines(frame);
        Assert.Equal(Enumerable.Range(0, 20).Select(i => $"! {WarningDate(i)} 07:22 Post & Rückfragen: UsedTime 0:05 but the entries give 0:02"), yellow);
        Assert.Equal(["… and 3 more"], frame.Where(IsMoreLine));
    }

    // AC-4: the capped block sits before the skipped names, which keep their own cap and more line.
    [Fact]
    public void Import_BothOverflow_ShowTwoSeparateMoreLines_InOrder()
    {
        using TempFolder source = new TempFolder();
        WriteBroken(source.Path, 25);
        for (int i = 0; i < 22; i++)
        {
            File.WriteAllText(Path.Combine(source.Path, $"n{i:00}"), "x");
        }

        string[] frame = TallImportFrame($"import {source.Path}");

        int counts = Array.FindIndex(frame, l => l.EndsWith(" kept", StringComparison.Ordinal));
        int firstYellow = Array.FindIndex(frame, l => l.StartsWith("! ", StringComparison.Ordinal));
        int fiveMore = Array.IndexOf(frame, "… and 5 more");
        int skipped = Array.FindIndex(frame, l => l.StartsWith("Skipped (not a day file): ", StringComparison.Ordinal));
        int twoMore = Array.IndexOf(frame, "… and 2 more");
        Assert.True(counts >= 0 && counts < firstYellow, "counts before the yellow lines");
        Assert.True(firstYellow < fiveMore, "yellow lines before their more line");
        Assert.True(fiveMore < skipped, "the line cap before the skipped names");
        Assert.True(skipped < twoMore, "the skipped names before their own more line");
        Assert.Equal("Skipped (not a day file): " + string.Join(", ", Enumerable.Range(0, 20).Select(i => $"n{i:00}")), frame[skipped]);
        Assert.Equal(2, frame.Count(IsMoreLine));
    }

    // AC-5: the cap changes only the output; counts and written files stay the same.
    [Fact]
    public void Import_25BrokenFilesAndTheSample_ImportsOneDay_WrittenAsWithoutTheCap()
    {
        using TempFolder single = SampleSource();
        ImportFrame($"import {single.Path}");
        string uncapped = ReadDay(ImportDay);
        File.Delete(DayFile(ImportDay));

        using TempFolder source = SampleSource();
        WriteBroken(source.Path, 25);

        string[] frame = TallImportFrame($"import {source.Path}");

        Assert.Contains($"Imported 1 day from {source.Path}", frame);
        Assert.Contains("0 already there · 0 kept", frame);
        Assert.Equal(uncapped, ReadDay(ImportDay));
        Assert.Equal([ImportDay + ".json"], Listing(data.Path));
        Assert.Contains("… and 5 more", frame);
    }

    // AC-6: lines added while asking about conflicts count toward the same cap.
    [Fact]
    public void Import_ConflictLines_CountTowardTheCap()
    {
        using TempFolder source = SampleSource();
        WriteBroken(source.Path, 21);
        File.WriteAllText(DayFile(ImportDay), OtherDay);
        const string Edited = "{\"entries\":[{\"kind\":\"arrival\",\"time\":\"06:30\"}]}";
        DayStore store = new DayStore(data.Path);
        store.BeforeStampCheck = day => File.WriteAllText(store.PathFor(day), Edited);

        string[] frame = TallImportFrame($"import {source.Path}", store, "yes");

        Assert.Equal(Edited, ReadDay(ImportDay));
        string[] yellow = YellowLines(frame);
        Assert.Equal(20, yellow.Length);
        Assert.Equal(Enumerable.Range(0, 20).Select(BrokenDate), yellow.Select(l => l.Substring(2, 10)));
        Assert.DoesNotContain(frame, l => l.Contains("changed on disk", StringComparison.Ordinal));
        Assert.Equal(["… and 2 more"], frame.Where(IsMoreLine));
    }

    // Job 121 ------------------------------------------------------------------------------

    private const string CorrectionUsage = "correction +H:mm|-H:mm <reason>";

    private const string TwoCorrections =
        "{\"entries\":[{\"kind\":\"arrival\",\"time\":\"08:00\"},{\"kind\":\"leaving\",\"time\":\"12:00\"}]," +
        "\"corrections\":[{\"minutes\":150,\"reason\":\"Starting balance\"},{\"minutes\":-65,\"reason\":\"Paid\"}]}";

    // AC-1
    [Theory]
    [InlineData("correction +2:30 Starting balance", 150, "Starting balance")]
    [InlineData("co +2:30 Starting balance", 150, "Starting balance")]
    [InlineData("/correction +2:30 Starting balance", 150, "Starting balance")]
    [InlineData("/co +2:30   Starting balance  ", 150, "Starting balance")]
    [InlineData("correction -1:05 Paid-out overtime", -65, "Paid-out overtime")]
    [InlineData("correction +999:59 Max", 59999, "Max")]
    public void Correction_OnEmptyDay_SavesTheCorrection(string command, int minutes, string reason)
    {
        (int exitCode, string output) = RunCommands(command, "quit");

        Assert.Equal(0, exitCode);
        DayRecord saved = DayRecord.FromJson(ReadDay("2026-10-27"));
        BalanceCorrection correction = Assert.Single(saved.Corrections);
        Assert.Equal(minutes, correction.Minutes);
        Assert.Equal(reason, correction.Reason);
        Assert.Empty(saved.Entries);
        Assert.Null(saved.Absence);
        Assert.Contains("Correction 1", LastFrame(output));
        Assert.Contains(reason, LastFrame(output));
    }

    // AC-1: the exact file.
    [Fact]
    public void Correction_WritesTheCorrectionsProperty()
    {
        (int exitCode, _) = RunCommands("correction +2:30 Starting balance", "correction -1:05 Paid", "quit");

        Assert.Equal(0, exitCode);
        Assert.Equal(
            "{\"entries\":[],\"corrections\":[{\"minutes\":150,\"reason\":\"Starting balance\"},{\"minutes\":-65,\"reason\":\"Paid\"}]}",
            ReadDay("2026-10-27"));
    }

    // AC-1, AC-7, AC-8: the balance on the target line includes the correction, the day part does not.
    [Fact]
    public void Correction_ShowsInTheBalance()
    {
        File.WriteAllText(DayFile("2026-10-27"), Entries("arrival 08:00", "leaving 16:00"));

        (_, string output) = RunCommands("correction +2:30 Starting balance", "quit");

        Assert.Contains("Target 8:00 · day 0:00 · balance +2:30", LastFrame(output));
    }

    // AC-2
    [Theory]
    [InlineData("correction")]
    [InlineData("co")]
    [InlineData("correction Starting balance")]
    [InlineData("correction 2:30 Starting balance")]
    [InlineData("correction +2 Starting balance")]
    [InlineData("correction +2:60 Starting balance")]
    [InlineData("correction +1000:00 Starting balance")]
    [InlineData("correction +0:00 Starting balance")]
    [InlineData("correction -0:00 Starting balance")]
    [InlineData("correction +2:30")]
    [InlineData("correction +2:30    ")]
    public void Correction_BadAmountOrMissingReason_ShowsUsage_AndWritesNothing(string command)
    {
        (int exitCode, string output) = RunCommands(command, "quit");

        Assert.Equal(0, exitCode);
        Assert.False(File.Exists(DayFile("2026-10-27")));
        string[] frame = LastLayout(output);
        AssertLayout(frame);
        Assert.Contains(CorrectionUsage, HintText(frame, "correction:"));
    }

    // AC-2: non-ASCII digits are no amount.
    [Fact]
    public void Correction_NonAsciiDigit_IsNoAmount_AndWritesNothing()
    {
        (int exitCode, string output) = Run(c =>
        {
            c.Input.PushText("correction +");
            c.Input.PushKey(new ConsoleKeyInfo('\u0662', ConsoleKey.NoName, false, false, false));
            c.Input.PushTextWithEnter(":30 Starting balance");
            c.Input.PushTextWithEnter("quit");
        });

        Assert.Equal(0, exitCode);
        Assert.False(File.Exists(DayFile("2026-10-27")));
        Assert.Contains(CorrectionUsage, HintText(LastLayout(output), "correction:"));
    }

    // AC-2: the reason gets the checkpoint name rules and hints.
    [Fact]
    public void Correction_InvisibleReason_ShowsTheNameHint_AndWritesNothing()
    {
        (int exitCode, string output) = Run(c =>
        {
            c.Input.PushText("correction +1:00 Paid");
            c.Input.PushKey(new ConsoleKeyInfo('\u200b', ConsoleKey.NoName, false, false, false));
            c.Input.PushTextWithEnter("x");
            c.Input.PushTextWithEnter("quit");
        });

        Assert.Equal(0, exitCode);
        Assert.False(File.Exists(DayFile("2026-10-27")));
        Assert.Equal(
            "correction: the reason must not contain invisible formatting or broken characters. Retype it without the hidden characters.",
            HintText(LastLayout(output), "correction:"));
    }

    // AC-2: the line editor never types a control character, so the handler is called directly.
    [Fact]
    public void Correction_ControlCharacterInReason_ShowsTheNameHint_AndWritesNothing()
    {
        CommandRegistry registry = CommandRegistry.CreateDefault();
        (_, _) = Run(c => c.Input.PushTextWithEnter("quit"), registry);
        Assert.True(registry.TryGet("correction", out Command command));

        CommandResult result = command.Handler("+1:00 Paid\u0007x");

        TestConsole hint = new TestConsole();
        hint.Profile.Width = 200;
        hint.Write(Assert.IsAssignableFrom<Spectre.Console.Rendering.IRenderable>(result.Hint));
        Assert.Contains("correction: the reason must not contain control characters. Retype it without the hidden characters.", hint.Output);
        Assert.False(File.Exists(DayFile("2026-10-27")));
    }

    // AC-2
    [Fact]
    public void Correction_ReasonOverTwoHundredCharacters_ShowsHint_AndWritesNothing()
    {
        (int exitCode, string output) = RunCommands("correction +1:00 " + new string('a', 201), "quit");

        Assert.Equal(0, exitCode);
        Assert.False(File.Exists(DayFile("2026-10-27")));
        Assert.Equal("correction: the reason is longer than 200 characters.", HintText(LastLayout(output), "correction:"));
    }

    // AC-2: the hint is red.
    [Fact]
    public void Correction_UsageHint_IsRed()
    {
        TestConsole console = new TestConsole().Interactive().EmitAnsiSequences();
        console.Input.PushTextWithEnter("correction +2");
        console.Input.PushTextWithEnter("quit");

        new App(console, FixedTimeProvider.At20261027(), new DayStore(data.Path), instanceLock: sideLock).Run([]);

        Assert.Matches(@"\x1b\[38;5;9mcorrection: ", console.Output);
    }

    // AC-3
    [Fact]
    public void CorrectionRemove_RemovesOnlyThatCorrection()
    {
        File.WriteAllText(DayFile("2026-10-27"), TwoCorrections);

        (int exitCode, _) = RunCommands("correction remove 1", "quit");

        Assert.Equal(0, exitCode);
        DayRecord saved = DayRecord.FromJson(ReadDay("2026-10-27"));
        Assert.Equal(2, saved.Entries.Count);
        BalanceCorrection left = Assert.Single(saved.Corrections);
        Assert.Equal("Paid", left.Reason);
    }

    // AC-3
    [Theory]
    [InlineData("correction remove", "say which correction to remove")]
    [InlineData("co remove x", "is not a correction number")]
    [InlineData("correction remove -1", "is not a correction number")]
    [InlineData("correction remove 0", "there is no correction 0")]
    [InlineData("correction remove 3", "there is no correction 3")]
    public void CorrectionRemove_BadNumber_ShowsHint_AndWritesNothing(string command, string expected)
    {
        File.WriteAllText(DayFile("2026-10-27"), TwoCorrections);

        (int exitCode, string output) = RunCommands(command, "quit");

        Assert.Equal(0, exitCode);
        Assert.Equal(TwoCorrections, ReadDay("2026-10-27"));
        Assert.Contains(expected, HintText(LastLayout(output), "correction:"));
    }

    // AC-3
    [Fact]
    public void CorrectionRemove_OnDayWithoutFile_WritesNothing()
    {
        (_, string output) = RunCommands("correction remove 1", "quit");

        Assert.False(File.Exists(DayFile("2026-10-27")));
        Assert.Contains("there is no correction 1", HintText(LastLayout(output), "correction:"));
    }

    // AC-3
    [Fact]
    public void Remove_StillRemovesOnlyEntries()
    {
        File.WriteAllText(DayFile("2026-10-27"), TwoCorrections);

        (_, _) = RunCommands("remove 1", "quit");

        DayRecord saved = DayRecord.FromJson(ReadDay("2026-10-27"));
        Assert.Single(saved.Entries);
        Assert.Equal(2, saved.Corrections.Count);
    }

    // AC-4
    [Fact]
    public void Correction_OnDayWithEntries_KeepsTheEntries()
    {
        File.WriteAllText(DayFile("2026-10-27"), Entries("arrival 08:00"));

        (_, _) = RunCommands("correction -0:30 Paid", "quit");

        Assert.Equal(
            "{\"entries\":[{\"kind\":\"arrival\",\"time\":\"08:00\"}],\"corrections\":[{\"minutes\":-30,\"reason\":\"Paid\"}]}",
            ReadDay("2026-10-27"));
    }

    // AC-4
    [Fact]
    public void Correction_OnAbsenceDay_IsSaved()
    {
        File.WriteAllText(DayFile("2026-10-27"), "{\"absence\":\"vacation\",\"entries\":[]}");

        (_, string output) = RunCommands("correction +1:00 Carry over", "quit");

        Assert.Equal(
            "{\"absence\":\"vacation\",\"entries\":[],\"corrections\":[{\"minutes\":60,\"reason\":\"Carry over\"}]}",
            ReadDay("2026-10-27"));
        Assert.Contains("Absence:", LastFrame(output));
        Assert.Contains("Correction 1", LastFrame(output));
    }

    // AC-4
    [Fact]
    public void Absence_OnDayWithOnlyCorrections_IsSet_AndBookingStaysRefused()
    {
        (_, string output) = RunCommands("correction +1:00 Carry over", "absence sick", "arrive 8:00", "quit");

        Assert.Equal(
            "{\"absence\":\"sick\",\"entries\":[],\"corrections\":[{\"minutes\":60,\"reason\":\"Carry over\"}]}",
            ReadDay("2026-10-27"));
        Assert.Contains("this day is marked as absence", HintText(LastLayout(output), "arrive:"));
    }

    // AC-4: clearing the absence keeps the corrections.
    [Fact]
    public void AbsenceClear_KeepsCorrections()
    {
        File.WriteAllText(DayFile("2026-10-27"), "{\"absence\":\"sick\",\"entries\":[],\"corrections\":[{\"minutes\":60,\"reason\":\"x\"}]}");

        (_, _) = RunCommands("absence clear", "quit");

        Assert.Equal("{\"entries\":[],\"corrections\":[{\"minutes\":60,\"reason\":\"x\"}]}", ReadDay("2026-10-27"));
    }

    // AC-9
    [Fact]
    public void Correction_IsRegistered_WithAliasCo_AndListedInHelpAndPopup()
    {
        CommandRegistry registry = CommandRegistry.CreateDefault();

        (_, _) = Run(c => c.Input.PushTextWithEnter("quit"), registry);

        Assert.True(registry.TryGet("co", out Command byAlias));
        Assert.Equal("correction", byAlias.Name);
        Assert.Equal("co", byAlias.Alias);
        Assert.False(string.IsNullOrWhiteSpace(byAlias.Description));
        Assert.Contains(registry.Match("co"), c => c.Name == "correction");
        TestConsole help = new TestConsole();
        help.Profile.Width = 200;
        help.Write(registry.RenderHelp());
        Assert.Matches(@"correction +│ co +│ Correct the balance", help.Output);
    }

    // AC-9
    [Theory]
    [InlineData("correction +1:00 Paid")]
    [InlineData("correction remove 1")]
    public void StaleDay_Correction_IsRefused_AndTheRetryWorks(string typed)
    {
        const string Initial = "{\"entries\":[],\"corrections\":[{\"minutes\":30,\"reason\":\"Old\"}]}";
        const string Edited = "{\"entries\":[],\"corrections\":[{\"minutes\":30,\"reason\":\"Old\"},{\"minutes\":45,\"reason\":\"Edited outside\"}]}";

        (string output, string? beforeRetry) = RunStale(typed, Initial, Edited);

        List<string[]> frames = Frames(output);
        Assert.Equal(3, frames.Count);
        AssertOnHintRow(frames[1], ChangedHint("correction"));
        Assert.Equal(Edited, beforeRetry);
        Assert.NotEqual(Edited, ReadDay("2026-10-27"));
        Assert.Contains("Edited outside", ReadDay("2026-10-27"));
    }
}
