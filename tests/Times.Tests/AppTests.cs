using System.Text.RegularExpressions;
using Spectre.Console;
using Spectre.Console.Testing;

namespace Times.Tests;

public sealed class AppTests : IDisposable
{
    private const string Heading = "Tuesday, 2026-10-27";

    /// <summary>Rows the quicklist panel takes with no projects (border, one line, border): the day heading is on the next row.</summary>
    private const int EmptyPanelRows = 3;

    /// <summary>Rows of the top area on the default 24-row terminal: 24 minus the hint row and the 3-line input box.</summary>
    private const int TopHeight = 20;

    /// <summary>More PageDown presses than help needs to reach the bottom at 24 rows; the extra ones change nothing.</summary>
    private const int HelpPages = 4;

    /// <summary>Pushes <paramref name="count"/> PageDown keys.</summary>
    private static void PageDown(TestConsole console, int count)
    {
        for (int i = 0; i < count; i++)
        {
            console.Input.PushKey(ConsoleKey.PageDown);
        }
    }

    private readonly TempFolder data = new();

    public void Dispose() => data.Dispose();

    private (int ExitCode, string Output) Run(Action<TestConsole> input, CommandRegistry? registry = null, TimeProvider? time = null, string? dataFolder = null)
    {
        TestConsole console = new TestConsole().Interactive();
        input(console);
        int exitCode = new App(console, time ?? FixedTimeProvider.At20261027(), new DayStore(dataFolder ?? data.Path), registry).Run([]);
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
        Assert.Contains("List all commands", frames[1]);           // after help
        Assert.DoesNotContain("List all commands", frames[2]);     // replaced by the hint
        Assert.Contains("Unknown command nope", frames[2]);
        Assert.DoesNotContain("Unknown command", frames[3]);       // cleared by the empty line

        // The message sits above the input panel.
        Assert.True(frames[1].IndexOf("List all commands", StringComparison.Ordinal) < frames[1].IndexOf("│ > ", StringComparison.Ordinal));

        // Job 004: help stays in the top area and the hint row stays blank (AC-6);
        // the hint is on the hint row and gone after the next command (AC-7).
        List<string[]> lines = Frames(output);
        Assert.Equal(4, lines.Count);
        AssertLayout(lines[1]);
        Assert.Contains(lines[1][..^4], l => l.Contains("List all commands", StringComparison.Ordinal));
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
        Assert.Contains(frame[..^4], l => l.Contains("List all commands", StringComparison.Ordinal));
        Assert.True(string.IsNullOrWhiteSpace(frame[^4]));
        Assert.DoesNotContain(frame, l => l.Contains("Unknown command", StringComparison.Ordinal));
    }

    // AC-8
    [Fact]
    public void LongHint_IsCroppedToOneLine_AndDoesNotMoveInputBox()
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
        Assert.Contains("word", frame[^4]);
        Assert.DoesNotContain(frame[..^4], l => l.Contains("word", StringComparison.Ordinal));
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
        Assert.Contains("List all commands", frames[1]);
        Assert.Contains("List all commands", frames[2]);
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
        Assert.False(Directory.Exists(missing.Path));
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
        Assert.Equal(0, new App(console, FixedTimeProvider.At20261027(), new DayStore(data.Path), registry).Run([]));
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
        // Fifteen commands: the first seven by name, then a row counting the other eight.
        Assert.Equal(
            ["addproject", "arrive", "break", "breakend", "change", "checkpoint", "help", "…"],
            popup.Select(w => w.Text.Split(' ')[0]));
        Assert.StartsWith("… 8 more", popup[^1].Text);
        Assert.Contains("List all commands", popup[6].Text);
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
        Assert.StartsWith("… 8 more", hintRow[0]);
        Assert.Contains("Unknown command nope", hintRow[1]);
        Assert.StartsWith("… 8 more", hintRow[2]);
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
        // 27 commands: 7 shown, the eighth row counts the other 20.
        Assert.StartsWith("… 20 more", popup[^1].Text);
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
        AssertOnHintRow(frame, "Use H:mm, e.g. 8:15.");
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
        Assert.False(Directory.Exists(missing.Path));

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
        (_, string output) = RunCommands("help", "quit");

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
            c.Profile.Height = 80;
            c.Input.PushTextWithEnter("help");
            c.Input.PushTextWithEnter("quit");
        }, ManyCommands());

        string[] frame = Frames(output)[1];
        Assert.Equal(80, frame.Length);
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
        Assert.False(Directory.Exists(missing.Path));

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

            Assert.Equal(0, new App(console, FixedTimeProvider.At20261027(), new DayStore(data.Path)).Run([]));
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
        AssertOnHintRow(frame, "Use H:mm, e.g. 8:15.");
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
        AssertOnHintRow(frame, expected);
        AssertOnHintRow(frame, "Use change <number> <H:mm>.");
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
        Assert.False(Directory.Exists(missing.Path));

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

            Assert.Equal(0, new App(console, FixedTimeProvider.At20261027(), new DayStore(data.Path)).Run([]));
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

        Assert.Matches(@"change +│ c +│ Change the time of an entry on this day: <number> <H:mm>", output);
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
        Assert.Contains("Change the time of an entry on this day: <number> <H:mm>", row.Text);
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
    private static string RunHelp(string folder, bool folderFromEnvironment, bool ansi = false)
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
        int exitCode = new App(console, FixedTimeProvider.At20261027(), new DayStore(folder), folderFromEnvironment: folderFromEnvironment).Run([]);
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
        "Day view codes: AR arrival · LE leaving · BS break start · BE break end · CP checkpoint";

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
        Assert.False(Directory.Exists(folder));
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
        Assert.Equal(0, new App(console, FixedTimeProvider.At20261027(), new DayStore(data.Path)).Run([]));
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
        Assert.False(Directory.Exists(missing.Path));

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

        AssertOnHintRow(LastLayout(output), "addproject: the title must not contain control characters.");
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
        (_, string output) = RunWide(120, "help", "quit");

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
        Assert.False(Directory.Exists(missing.Path));
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

    private const string CheckpointUsage = "Use checkpoint [H:mm] [#project] <name>, where #project is the project's number on the quicklist.";

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
        AssertOnHintRow(LastLayout(output), "Use H:mm, e.g. 8:15.");
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
    [InlineData("checkpoint 25:00 Standup", "checkpoint: 25:00 is not a time. Use H:mm, e.g. 8:15.")]
    [InlineData("checkpoint 9:5 Standup", "checkpoint: 9:5 is not a time. Use H:mm, e.g. 8:15.")]
    [InlineData("checkpoint 25:00", "checkpoint: 25:00 is not a time. Use H:mm, e.g. 8:15.")]
    [InlineData("checkpoint a\u2028b", "checkpoint: the name must not contain control characters.")]
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
        (_, string output) = Run(c =>
        {
            c.Input.PushTextWithEnter("help");
            PageDown(c, HelpPages);
            c.Input.PushTextWithEnter("quit");
        });

        Assert.Matches(@"break +│ b +│ Book the start of a break: now, or at an optional H:mm", output);
        Assert.Matches(@"breakend +│ be +│ Book the end of a break: now, or at an optional H:mm", output);
        Assert.Matches(@"checkpoint +│ cp +│ Book a checkpoint: \[H:mm\] \[#project\] <name> +│", output);
    }

    // AC-11 (036)
    [Theory]
    [InlineData("brea", "break ", "Book the start of a break")]
    [InlineData("breake", "breakend ", "Book the end of a break")]
    [InlineData("checkp", "checkpoint ", "Book a checkpoint: [H:mm] [#project] <name>")]
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

        Assert.Equal(0, new App(console, FixedTimeProvider.At20261027(), new DayStore(folder)).Run([]));

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
            console.Input.PushTextWithEnter(name);
            console.Input.PushTextWithEnter("/" + name);
            console.Input.PushTextWithEnter(name.ToUpperInvariant());
            console.Input.PushTextWithEnter("/" + char.ToUpperInvariant(name[0]) + name[1..]);
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

        string[] names = ["addproject", "arrive", "break", "breakend", "change", "checkpoint", "help", "leave", "next", "prev", "projects", "quit", "remove", "today"];
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
        Assert.Contains("List all commands", string.Join('\n', Frames(output)[1]));
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

        Assert.Contains("Use checkpoint [H:mm] [#project] <name>", output);
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

        string aliasHint = LastLayout(aliasOutput)[^4];
        string fullHint = LastLayout(fullOutput)[^4];
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
            ["addproject"] = "ap",
            ["arrive"] = "a",
            ["break"] = "b",
            ["breakend"] = "be",
            ["change"] = "c",
            ["checkpoint"] = "cp",
            ["help"] = "h",
            ["leave"] = "l",
            ["next"] = "n",
            ["prev"] = "p",
            ["projects"] = "pr",
            ["quit"] = "q",
            ["remove"] = "r",
            ["summary"] = "s",
            ["today"] = "t",
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

        (int exitCode, string output) = RunCommands(
            "cp 9:00 #1 Review", "cp 10:00 [x]", "cp 10:30 #1 Fix", "breakend 11:00", "cp 17:00 [x]", "leave 17:30", "summary", "quit");

        Assert.Equal(0, exitCode);
        string[] frame = LastLayout(output);
        AssertLayout(frame);
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
        Assert.False(Directory.Exists(missing.Path));
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

    private const string AddProjectFormatHint = "addproject: the title must not contain invisible formatting or broken characters.";
    private const string CheckpointFormatHint = "checkpoint: the name must not contain invisible formatting or broken characters.";

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

        Assert.Matches(@"\x1b\[(31|91|38;5;9)maddproject: the title must not contain invisible formatting or broken characters\.", output);
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

        Assert.Matches(@"\x1b\[(31|91|38;5;9)mcheckpoint: the name must not contain invisible formatting or broken characters\.", output);
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
    [InlineData("checkpoint a\\u2028\\u202Eb", "checkpoint: the name must not contain control characters.")]
    [InlineData("addproject 12 a\\u2028\\u202Eb", "addproject: the title must not contain control characters.")]
    public void FormatAndControlCharacters_ControlHintWins(string escaped, string expected)
    {
        (_, string output) = TypeEscaped(escaped);

        AssertOnHintRow(LastLayout(output), expected);
        Assert.False(File.Exists(ProjectsFile));
        Assert.False(File.Exists(DayFile("2026-10-27")));
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
}
