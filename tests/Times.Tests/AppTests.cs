using System.Text.RegularExpressions;
using Spectre.Console;
using Spectre.Console.Testing;

namespace Times.Tests;

public sealed class AppTests : IDisposable
{
    private const string Heading = "Tuesday, 2026-10-27";

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
    /// Splits the output into drawn frames. Each frame runs from a day heading to the bottom
    /// border of the input panel (the <c>╰</c> line right after the <c>│ > </c> line), split on <c>\n</c>.
    /// </summary>
    private static List<string[]> Frames(string output)
    {
        List<string[]> frames = new List<string[]>();
        List<int> starts = Regex.Matches(output, @"[A-Z][a-z]+day, \d{4}-\d{2}-\d{2}").Select(m => m.Index).ToList();
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
        (int exitCode, string output) = Run(c => c.Input.PushTextWithEnter("/quit"));

        Assert.Equal(0, exitCode);
        Assert.Contains(Heading, output);
        Assert.Contains("No entries yet.", output);
    }

    // AC-2
    [Fact]
    public void StarterOutput_IsGone()
    {
        (_, string output) = Run(c => c.Input.PushTextWithEnter("/quit"));

        Assert.DoesNotContain("_____", output); // Figlet "Times" banner
        Assert.DoesNotContain("Zone", output);
        Assert.DoesNotContain("18:30:00", output);
    }

    // AC-3
    [Fact]
    public void InputBox_IsRoundedPanel_BelowDayView_AtBottom()
    {
        (_, string output) = Run(c => c.Input.PushTextWithEnter("/quit"));

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
        registry.Register("/demo", "A command from the test", _ => CommandResult.None);

        (int exitCode, string output) = Run(c =>
        {
            c.Input.PushTextWithEnter("/help");
            c.Input.PushTextWithEnter("/quit");
        }, registry);

        Assert.Equal(0, exitCode);
        Assert.Contains("/help", output);
        Assert.Contains("List all commands", output);
        Assert.Contains("/quit", output);
        Assert.Contains("Exit Times", output);
        Assert.Contains("/demo", output);
        Assert.Contains("A command from the test", output);
    }

    // AC-5
    [Fact]
    public void UnknownCommand_ShowsHint_AndLoopContinues()
    {
        (int exitCode, string output) = Run(c =>
        {
            c.Input.PushTextWithEnter("/nope");
            c.Input.PushTextWithEnter("/quit");
        });

        Assert.Equal(0, exitCode);
        Assert.Contains("Unknown command /nope", output);
        Assert.Contains("Type /help to see all commands.", output);
    }

    [Fact]
    public void UnknownCommand_HintIsRed()
    {
        TestConsole console = new TestConsole().Interactive().EmitAnsiSequences();
        console.Input.PushTextWithEnter("/nope");
        console.Input.PushTextWithEnter("/quit");

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
            c.Input.PushTextWithEnter("/quit");
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
            c.Input.PushTextWithEnter("/quit");
        });

        Assert.Equal(0, exitCode);
        Assert.Contains("Commands start with /", output);
    }

    [Fact]
    public void HandlerException_ShowsRedHint_NotStackTrace()
    {
        CommandRegistry registry = CommandRegistry.CreateDefault();
        registry.Register("/boom", "Throws", _ => throw new InvalidOperationException("bad [thing]"));

        (int exitCode, string output) = Run(c =>
        {
            c.Input.PushTextWithEnter("/boom");
            c.Input.PushTextWithEnter("/quit");
        }, registry);

        Assert.Equal(0, exitCode);
        Assert.Contains("/boom failed: bad [thing]", output);
        Assert.DoesNotContain("   at ", output);
    }

    // AC-7
    [Fact]
    public void InputWithoutSlash_ShowsHint_AndLoopContinues()
    {
        (int exitCode, string output) = Run(c =>
        {
            c.Input.PushTextWithEnter("hello");
            c.Input.PushTextWithEnter("/quit");
        });

        Assert.Equal(0, exitCode);
        Assert.Contains("Commands start with /", output);
    }

    [Fact]
    public void EmptyInput_ShowsNoHint_AndLoopContinues()
    {
        (int exitCode, string output) = Run(c =>
        {
            c.Input.PushTextWithEnter("");
            c.Input.PushTextWithEnter("   ");
            c.Input.PushTextWithEnter("/quit");
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
        (int exitCode, _) = Run(c => c.Input.PushTextWithEnter("/QUIT"));

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public void TextAfterName_IsPassedToHandler()
    {
        string? received = null;
        CommandRegistry registry = CommandRegistry.CreateDefault();
        registry.Register("/echo", "Echo", a => { received = a; return CommandResult.None; });

        Run(c =>
        {
            c.Input.PushTextWithEnter("/Echo  hello [world] ");
            c.Input.PushTextWithEnter("/quit");
        }, registry);

        Assert.Equal("hello [world]", received);
    }

    // AC-9
    [Fact]
    public void Screen_IsRedrawnAfterEachCommand()
    {
        (_, string output) = Run(c =>
        {
            c.Input.PushTextWithEnter("/nope");
            c.Input.PushTextWithEnter("/quit");
        });

        Assert.True(Count(output, Heading) >= 2);
    }

    [Fact]
    public void Message_StaysInTopArea_UntilNextCommand()
    {
        (_, string output) = Run(c =>
        {
            c.Input.PushTextWithEnter("/help");
            c.Input.PushTextWithEnter("/nope");
            c.Input.PushTextWithEnter("");
            c.Input.PushTextWithEnter("/quit");
        });

        string[] frames = output.Split(Heading).Skip(1).ToArray();
        Assert.Equal(4, frames.Length);
        Assert.Contains("List all commands", frames[1]);           // after /help
        Assert.DoesNotContain("List all commands", frames[2]);     // replaced by the hint
        Assert.Contains("Unknown command /nope", frames[2]);
        Assert.DoesNotContain("Unknown command", frames[3]);       // cleared by the empty line

        // The message sits above the input panel.
        Assert.True(frames[1].IndexOf("List all commands", StringComparison.Ordinal) < frames[1].IndexOf("│ > ", StringComparison.Ordinal));

        // Job 004: /help stays in the top area and the hint row stays blank (AC-6);
        // the hint is on the hint row and gone after the next command (AC-7).
        List<string[]> lines = Frames(output);
        Assert.Equal(4, lines.Count);
        AssertLayout(lines[1]);
        Assert.Contains(lines[1][..^4], l => l.Contains("List all commands", StringComparison.Ordinal));
        Assert.True(string.IsNullOrWhiteSpace(lines[1][^4]));
        AssertOnHintRow(lines[2], "Unknown command /nope");
        AssertLayout(lines[3]);
        Assert.True(string.IsNullOrWhiteSpace(lines[3][^4]));
    }

    // Hint position (job 004) -------------------------------------------------------------

    // AC-1, AC-3
    [Fact]
    public void UnknownCommand_HintSitsDirectlyAboveInputBox()
    {
        (_, string output) = RunCommands("/nope", "/quit");

        string[] frame = Frames(output)[1];
        AssertOnHintRow(frame, "Unknown command /nope");
        Assert.DoesNotContain(frame[..^4], l => l.Contains("Unknown command", StringComparison.Ordinal));
        Assert.Contains(Heading, frame[0]);
    }

    // AC-2, AC-3
    [Theory]
    [InlineData("hello", "Commands start with /")]
    [InlineData("/next x", "/next takes no arguments.")]
    [InlineData("/prev [red]", "/prev takes no arguments.")]
    public void Hints_SitDirectlyAboveInputBox(string typed, string expected)
    {
        (int exitCode, string output) = RunCommands(typed, "/quit");

        Assert.Equal(0, exitCode);
        string[] frame = Frames(output)[1];
        AssertOnHintRow(frame, expected);
        Assert.Contains(Heading, frame[0]);
    }

    // AC-2, AC-3
    [Fact]
    public void BoundaryHint_SitsDirectlyAboveInputBox()
    {
        (int exitCode, string output) =
            RunCommands(new SettableTimeProvider(DateTimeOffset.MaxValue), "/next", "/quit");

        Assert.Equal(0, exitCode);
        string[] frame = Frames(output)[1];
        AssertOnHintRow(frame, "There is no day after this one.");
        Assert.Contains("Friday, 9999-12-31", frame[0]);
    }

    // AC-2, AC-3
    [Fact]
    public void HandlerExceptionHint_SitsDirectlyAboveInputBox()
    {
        CommandRegistry registry = CommandRegistry.CreateDefault();
        registry.Register("/boom", "Throws", _ => throw new InvalidOperationException("bad [thing]"));

        (int exitCode, string output) = Run(c =>
        {
            c.Input.PushTextWithEnter("/boom");
            c.Input.PushTextWithEnter("/quit");
        }, registry);

        Assert.Equal(0, exitCode);
        string[] frame = Frames(output)[1];
        AssertOnHintRow(frame, "/boom failed: bad [thing]");
        Assert.Contains(Heading, frame[0]);
    }

    // AC-4
    [Fact]
    public void HintRow_IsBlank_WhenThereIsNoHint()
    {
        (_, string output) = RunCommands("/nope", "", "/quit");

        List<string[]> frames = Frames(output);
        Assert.Equal(3, frames.Count);
        foreach (string[] frame in new[] { frames[0], frames[2] })
        {
            AssertLayout(frame);
            Assert.True(string.IsNullOrWhiteSpace(frame[^4]));
            Assert.Contains(Heading, frame[0]);
        }

        Assert.DoesNotContain(frames[2], l => l.Contains("Unknown command", StringComparison.Ordinal));
    }

    // AC-7
    [Fact]
    public void Hint_IsReplacedByHelp_OnNextCommand()
    {
        (_, string output) = RunCommands("/nope", "/help", "/quit");

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
        registry.Register("/long", "Long hint", _ => CommandResult.Error(Markup.Escape(text)));

        (int exitCode, string output) = Run(c =>
        {
            c.Input.PushTextWithEnter("/long");
            c.Input.PushTextWithEnter("/quit");
        }, registry);

        Assert.Equal(0, exitCode);
        string[] frame = Frames(output)[1];
        AssertLayout(frame);
        Assert.Contains("word", frame[^4]);
        Assert.DoesNotContain(frame[..^4], l => l.Contains("word", StringComparison.Ordinal));
        Assert.Contains(Heading, frame[0]);
    }

    // AC-10
    [Fact]
    public void Backspace_EditsTheLine()
    {
        (int exitCode, string output) = Run(c =>
        {
            c.Input.PushText("/quiz");
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
            c.Input.PushKey(ConsoleKey.Escape);
            c.Input.PushTextWithEnter("/quit");
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
            c.Input.PushText("/q");
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
            c.Input.PushTextWithEnter("/help");
            c.Input.PushKey(ConsoleKey.UpArrow);
            c.Input.PushKey(ConsoleKey.Enter);
            c.Input.PushTextWithEnter("/quit");
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
        (int exitCode, string output) = RunCommands("/prev", "/quit");

        Assert.Equal(0, exitCode);
        Assert.Equal("Monday, 2026-10-26", LastHeading(output));
    }

    // AC-2
    [Fact]
    public void Next_MovesToNextDay()
    {
        (int exitCode, string output) = RunCommands("/next", "/quit");

        Assert.Equal(0, exitCode);
        Assert.Equal("Wednesday, 2026-10-28", LastHeading(output));
    }

    // AC-3
    [Fact]
    public void Today_JumpsBackToToday()
    {
        (int exitCode, string output) = RunCommands("/prev", "/prev", "/today", "/quit");

        Assert.Equal(0, exitCode);
        Assert.Contains("Sunday, 2026-10-25", output);
        Assert.Equal(Heading, LastHeading(output));
    }

    // AC-4
    [Theory]
    [InlineData(2026, 1, 1, "/prev", "Wednesday, 2025-12-31")]
    [InlineData(2026, 2, 28, "/next", "Sunday, 2026-03-01")]
    public void Navigation_CrossesMonthAndYearBoundaries(int year, int month, int day, string command, string expected)
    {
        SettableTimeProvider time =
            new SettableTimeProvider(new DateTimeOffset(year, month, day, 12, 0, 0, TimeSpan.Zero));

        (int exitCode, string output) = RunCommands(time, command, "/quit");

        Assert.Equal(0, exitCode);
        Assert.Equal(expected, LastHeading(output));
    }

    // AC-5
    [Fact]
    public void Today_UsesTheClockWhenTheCommandRuns()
    {
        SettableTimeProvider time = new SettableTimeProvider(new DateTimeOffset(2026, 10, 27, 23, 0, 0, TimeSpan.Zero));
        CommandRegistry registry = CommandRegistry.CreateDefault();
        registry.Register("/tick", "Advance the test clock", _ =>
        {
            time.UtcNow = time.UtcNow.AddHours(2);
            return CommandResult.None;
        });

        (int exitCode, string output) = Run(c =>
        {
            c.Input.PushTextWithEnter("/tick");
            c.Input.PushTextWithEnter("/today");
            c.Input.PushTextWithEnter("/quit");
        }, registry, time);

        Assert.Equal(0, exitCode);
        Assert.Equal("Wednesday, 2026-10-28", LastHeading(output));
    }

    // AC-6
    [Fact]
    public void DayWithoutFile_ShowsHeadingAndEmptyState()
    {
        (_, string output) = RunCommands("/next", "/quit");

        string frame = LastFrame(output);
        Assert.StartsWith("Wednesday, 2026-10-28", frame);
        Assert.Contains("No entries yet.", frame);
    }

    // AC-7
    [Fact]
    public void Browsing_LeavesExistingFolderEmpty()
    {
        (int exitCode, _) = RunCommands("/prev", "/next", "/next", "/today", "/quit");

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
            foreach (string command in new[] { "/prev", "/next", "/next", "/today", "/quit" })
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
        (_, string output) = RunCommands("/help", "/quit");

        Assert.Contains("/prev", output);
        Assert.Contains("Go to the previous day", output);
        Assert.Contains("/next", output);
        Assert.Contains("Go to the next day", output);
        Assert.Contains("/today", output);
        Assert.Contains("Go back to today", output);
    }

    // AC-8
    [Fact]
    public void NavigationCommands_MatchCaseInsensitively()
    {
        (int exitCode, string output) = RunCommands("/NEXT", "/quit");

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain("Unknown command", output);
        Assert.Equal("Wednesday, 2026-10-28", LastHeading(output));
    }

    // AC-8
    [Theory]
    [InlineData("/p", "Monday, 2026-10-26")]
    [InlineData("/n", "Wednesday, 2026-10-28")]
    [InlineData("/t", Heading)]
    public void Tab_CompletesNavigationCommands(string prefix, string expected)
    {
        (int exitCode, string output) = Run(c =>
        {
            if (prefix == "/t")
            {
                c.Input.PushTextWithEnter("/next"); // so /today has somewhere to come back from
            }

            c.Input.PushText(prefix);
            c.Input.PushKey(ConsoleKey.Tab);
            c.Input.PushKey(ConsoleKey.Enter);
            c.Input.PushTextWithEnter("/quit");
        });

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain("Unknown command", output);
        Assert.Equal(expected, LastHeading(output));
    }

    // AC-9
    [Theory]
    [InlineData("/next 3", "/next")]
    [InlineData("/prev x", "/prev")]
    [InlineData("/today [red]", "/today")]
    public void NavigationWithArgument_ShowsHint_AndKeepsDay(string typed, string name)
    {
        (int exitCode, string output) = RunCommands(typed, "/quit");

        Assert.Equal(0, exitCode);
        Assert.Equal(Heading, LastHeading(output));
        Assert.Contains($"{name} takes no arguments.", LastFrame(output));
    }

    // AC-9
    [Fact]
    public void NavigationWithArgument_HintIsRed_AndLoopContinues()
    {
        TestConsole console = new TestConsole().Interactive().EmitAnsiSequences();
        console.Input.PushTextWithEnter("/next 3");
        console.Input.PushTextWithEnter("/prev");
        console.Input.PushTextWithEnter("/quit");

        int exitCode = new App(console, FixedTimeProvider.At20261027(), new DayStore(data.Path)).Run([]);

        Assert.Equal(0, exitCode);
        Assert.Matches(@"\x1b\[(1;)?(31|91|38;5;9)(;1)?m(\x1b\[[0-9;]*m)*/next", console.Output);
        Assert.Matches(@"\x1b\[(31|91|38;5;9)m takes no arguments\.", console.Output);
        Assert.Contains("takes no arguments.", console.Output);
        Assert.Contains("Monday, 2026-10-26", console.Output); // the loop went on to /prev
    }

    // AC-10
    [Fact]
    public void Prev_AtMinValue_ShowsHint_AndStaysPut()
    {
        (int exitCode, string output) =
            RunCommands(new SettableTimeProvider(DateTimeOffset.MinValue), "/prev", "/quit");

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
            RunCommands(new SettableTimeProvider(DateTimeOffset.MaxValue), "/next", "/quit");

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
        console.Input.PushTextWithEnter("/next");
        console.Input.PushTextWithEnter("/quit");

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
            registry.Register($"/c{i:00}", $"Dummy command {i}", _ => CommandResult.None);
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
            c.Input.PushTextWithEnter("/quit");
        });

        Assert.Contains("\u001b[21;1H", output);
        List<AnsiWrite> popup = TypingWrites(output, 0).Take(5).ToList();
        Assert.Equal([17, 18, 19, 20, 21], popup.Select(w => w.Row));
        Assert.Equal(["/help", "/next", "/prev", "/quit", "/today"], popup.Select(w => w.Text.Split(' ')[0]));
        Assert.Contains("Go to the previous day", popup[2].Text);
        Assert.Equal(80, popup[0].Text.Length);
    }

    // AC-4
    [Fact]
    public void Typo_ShowsTodayFirst()
    {
        string output = RunAnsi(c =>
        {
            c.Input.PushTextWithEnter("/tdoay");
            c.Input.PushTextWithEnter("/quit");
        });

        List<AnsiWrite> writes = TypingWrites(output, 0);
        AnsiWrite lastPopupRow = writes.Last(w => w.Row == 21 && w.Text.StartsWith('/'));
        Assert.StartsWith("/today", lastPopupRow.Text);
    }

    // AC-4, AC-5
    [Fact]
    public void ClosedOrShrunkPopup_RestoresTheFramesRows()
    {
        // /help fills the top area with a long table, so the popup covers real content.
        string output = RunAnsi(c =>
        {
            c.Input.PushTextWithEnter("/help");
            c.Input.PushText("/c");
            c.Input.PushKey(ConsoleKey.Backspace);
            c.Input.PushKey(ConsoleKey.Backspace);
            c.Input.PushKey(ConsoleKey.Enter);
            c.Input.PushTextWithEnter("/quit");
        }, ManyCommands());

        string[] frame = AnsiFrames(output)[1];
        List<AnsiWrite> writes = TypingWrites(output, 1);

        // "/" and "/c" both fill 8 rows (14-21); Backspace to empty gives every row back.
        Assert.Contains(writes, w => w.Row == 14 && w.Text.StartsWith("/c01", StringComparison.Ordinal));
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
            c.Input.PushTextWithEnter("/nope");
            c.Input.PushText("/");
            c.Input.PushKey(ConsoleKey.Backspace);
            c.Input.PushText("/");
            c.Input.PushKey(ConsoleKey.Escape);
            c.Input.PushKey(ConsoleKey.Escape);
            c.Input.PushTextWithEnter("/quit");
        });

        List<string> hintRow = TypingWrites(output, 1).Where(w => w.Row == 21).Select(w => w.Text).ToList();

        // popup, restored (Backspace), popup, restored (Esc), popup (/quit typing), ...
        Assert.StartsWith("/today", hintRow[0]);
        Assert.Contains("Unknown command /nope", hintRow[1]);
        Assert.StartsWith("/today", hintRow[2]);
        Assert.Contains("Unknown command /nope", hintRow[3]);
        Assert.Contains("Unknown command /nope", hintRow[^1]);
        Assert.Contains("\u001b[38;5;9mUnknown command", output[output.LastIndexOf("\u001b[21;1H", StringComparison.Ordinal)..]);
    }

    // AC-5
    [Fact]
    public void Enter_StillDispatchesTheTypedLine()
    {
        string output = RunAnsi(c =>
        {
            c.Input.PushTextWithEnter("/prev");
            c.Input.PushTextWithEnter("/quit");
        });

        Assert.Contains("Monday, 2026-10-26", AnsiFrames(output)[1][0]);
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
            c.Input.PushTextWithEnter("/quit");
        }, ManyCommands());

        List<AnsiWrite> popup = TypingWrites(output, 0).Take(8).ToList();
        Assert.Equal(Enumerable.Range(14, 8), popup.Select(w => w.Row));
        // 17 commands: 7 shown, the eighth row counts the other 10.
        Assert.StartsWith("… 10 more", popup[^1].Text);
        // Nothing is drawn on row 1 while typing (the final clear on /quit homes the cursor there, writing nothing).
        Assert.DoesNotContain(TypingWrites(output, 0), w => w.Row == 1 && w.Text.Length > 0);
    }

    // AC-9
    [Fact]
    public void Typing_NeverClearsOrRedrawsTheLayout()
    {
        string output = RunAnsi(c =>
        {
            c.Input.PushText("/tdoay");
            c.Input.PushKey(ConsoleKey.Backspace);
            c.Input.PushKey(ConsoleKey.Tab);
            c.Input.PushKey(ConsoleKey.Escape);
            c.Input.PushKey(ConsoleKey.Escape);
            c.Input.PushText("/he");
            c.Input.PushKey(ConsoleKey.Tab);
            c.Input.PushKey(ConsoleKey.Enter);
            c.Input.PushTextWithEnter("/quit");
        });

        // One clear per frame (start, after /help) plus one on /quit; one heading per frame.
        Assert.Equal(3, Count(output, ClearScreen));
        Assert.Equal(2, AnsiFrames(output).Count);
        Assert.Equal(2, Count(output, Heading));
    }
}
