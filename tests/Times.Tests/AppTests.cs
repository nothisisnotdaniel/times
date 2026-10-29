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
        List<AnsiWrite> popup = TypingWrites(output, 0).Take(8).ToList();
        Assert.Equal([14, 15, 16, 17, 18, 19, 20, 21], popup.Select(w => w.Row));
        // Nine commands: the first seven by name, then a row counting the other two.
        Assert.Equal(
            ["/arrive", "/change", "/help", "/leave", "/next", "/prev", "/quit", "…"],
            popup.Select(w => w.Text.Split(' ')[0]));
        Assert.StartsWith("… 2 more", popup[^1].Text);
        Assert.Contains("Go to the previous day", popup[5].Text);
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
        Assert.StartsWith("… 2 more", hintRow[0]);
        Assert.Contains("Unknown command /nope", hintRow[1]);
        Assert.StartsWith("… 2 more", hintRow[2]);
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
        // 21 commands: 7 shown, the eighth row counts the other 14.
        Assert.StartsWith("… 14 more", popup[^1].Text);
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
        (int exitCode, string output) = RunCommands("/arrive 8:15", "/quit");

        Assert.Equal(0, exitCode);
        Assert.Equal(["2026-10-27.json"], Directory.EnumerateFiles(data.Path).Select(Path.GetFileName));
        Assert.Equal(Entries("arrival 08:15"), ReadDay("2026-10-27"));
        string frame = LastFrame(output);
        Assert.Matches(@"1 +Arrival +08:15", frame);
        Assert.DoesNotContain("Leaving", frame);
        Assert.DoesNotContain("No entries yet.", frame);
    }

    // AC-2
    [Fact]
    public void Leave_WithTime_StoresLeaving_AndShowsIt()
    {
        (int exitCode, string output) = RunCommands("/leave 17:30", "/quit");

        Assert.Equal(0, exitCode);
        Assert.Equal(Entries("leaving 17:30"), ReadDay("2026-10-27"));
        Assert.Matches(@"1 +Leaving +17:30", LastFrame(output));
    }

    // AC-2
    [Theory]
    [InlineData("/arrive", "{\"entries\":[{\"kind\":\"arrival\",\"time\":\"18:30\"}]}")]
    [InlineData("/leave", "{\"entries\":[{\"kind\":\"leaving\",\"time\":\"18:30\"}]}")]
    public void WithoutTime_BooksClockTime_SecondsDropped(string command, string expected)
    {
        FixedTimeProvider clock =
            new FixedTimeProvider(new DateTimeOffset(2026, 10, 27, 18, 30, 59, TimeSpan.Zero), TimeZoneInfo.Utc);

        (int exitCode, string output) = RunCommands(clock, command, "/quit");

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

        RunCommands(clock, "/arrive", "/quit");

        Assert.Equal(Entries("arrival 18:30"), ReadDay("2026-10-27"));
    }

    // AC-3
    [Fact]
    public void Booking_GoesToTheViewedDay()
    {
        (int exitCode, string output) = RunCommands("/prev", "/arrive 9:00", "/quit");

        Assert.Equal(0, exitCode);
        Assert.Equal(["2026-10-26.json"], Directory.EnumerateFiles(data.Path).Select(Path.GetFileName));
        Assert.Equal(Entries("arrival 09:00"), ReadDay("2026-10-26"));
        Assert.StartsWith("Monday, 2026-10-26", LastFrame(output));
        Assert.Matches(@"1 +Arrival +09:00", LastFrame(output));
    }

    // AC-2 (017)
    [Theory]
    [InlineData("/arrive", "arrival")]
    [InlineData("/leave", "leaving")]
    public void BookingAgain_AddsAnEntry_AndKeepsTheEarlierOne(string command, string kind)
    {
        (int exitCode, string output) = RunCommands(command + " 8:15", command + " 13:00", "/quit");

        Assert.Equal(0, exitCode);
        Assert.Equal(Entries(kind + " 08:15", kind + " 13:00"), ReadDay("2026-10-27"));
        string frame = LastFrame(output);
        string label = kind == "arrival" ? "Arrival" : "Leaving";
        Assert.Matches($@"1 +{label} +08:15", frame);
        Assert.Matches($@"2 +{label} +13:00", frame);
    }

    // AC-2 (017)
    [Fact]
    public void Booking_KeepsAllEntries_AndReopenedAppLoadsThem()
    {
        RunCommands("/arrive 8:15", "/leave 12:00", "/arrive 13:00", "/leave 17:30", "/quit");

        Assert.Equal(
            Entries("arrival 08:15", "leaving 12:00", "arrival 13:00", "leaving 17:30"),
            ReadDay("2026-10-27"));

        (int exitCode, string output) = RunCommands("/quit");
        Assert.Equal(0, exitCode);
        Assert.Matches(@"1 +Arrival +08:15", output);
        Assert.Matches(@"2 +Leaving +12:00", output);
        Assert.Matches(@"3 +Arrival +13:00", output);
        Assert.Matches(@"4 +Leaving +17:30", output);
    }

    // AC-6 (019)
    [Fact]
    public void ArriveAndLeave_ShowTimeLeftPerEntry()
    {
        (int exitCode, string output) = RunCommands("/arrive 8:00", "/leave 17:00", "/quit");

        Assert.Equal(0, exitCode);
        string frame = LastFrame(output);
        Assert.Contains("left 8:00", frame);
        Assert.Contains("left -1:00", frame);
        Assert.Matches(@"1 +Arrival +08:00 +left 8:00", frame);
        Assert.Matches(@"2 +Leaving +17:00 +left -1:00", frame);
    }

    // AC-3 (017)
    [Fact]
    public void Booking_KeepsEntriesInTimeOrder_WhateverTheBookingOrder()
    {
        (_, string output) = RunCommands("/leave 17:00", "/arrive 8:00", "/quit");

        Assert.Equal(Entries("arrival 08:00", "leaving 17:00"), ReadDay("2026-10-27"));
        string frame = LastFrame(output);
        Assert.Matches(@"1 +Arrival +08:00", frame);
        Assert.Matches(@"2 +Leaving +17:00", frame);
    }

    // AC-3 (017)
    [Fact]
    public void Booking_SameTime_KeepsBookingOrder_AndAllowsDuplicates()
    {
        RunCommands("/leave 12:00", "/arrive 12:00", "/arrive 12:00", "/quit");

        Assert.Equal(Entries("leaving 12:00", "arrival 12:00", "arrival 12:00"), ReadDay("2026-10-27"));
    }

    // AC-6
    [Theory]
    [InlineData("/arrive 25:00", "/arrive: 25:00 is not a time.")]
    [InlineData("/leave abc", "/leave: abc is not a time.")]
    [InlineData("/arrive [bold]", "/arrive: [bold] is not a time.")]
    public void InvalidTime_ShowsHintOnHintRow_AndWritesNothing(string typed, string expected)
    {
        (int exitCode, string output) = RunCommands(typed, "/help", typed, "/quit");

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

        (int exitCode, _) = RunCommands("/arrive 25:00", "/leave abc", "/arrive [bold]", "/quit");

        Assert.Equal(0, exitCode);
        Assert.Equal(before, File.ReadAllBytes(DayFile("2026-10-27")));
    }

    // AC-6
    [Fact]
    public void InvalidTime_HintIsRed()
    {
        TestConsole console = new TestConsole().Interactive().EmitAnsiSequences();
        console.Input.PushTextWithEnter("/arrive 25:00");
        console.Input.PushTextWithEnter("/quit");

        new App(console, FixedTimeProvider.At20261027(), new DayStore(data.Path)).Run([]);

        Assert.Matches(@"\x1b\[(31|91|38;5;9)m/arrive: ", console.Output);
    }

    // AC-4 (017)
    [Theory]
    [InlineData("/arrive 9:00", "/leave 8:00", "leaving 08:00", "arrival 09:00")]
    [InlineData("/arrive 9:00", "/leave 9:00", "arrival 09:00", "leaving 09:00")]
    [InlineData("/leave 17:00", "/arrive 18:00", "leaving 17:00", "arrival 18:00")]
    [InlineData("/leave 17:00", "/arrive 17:00", "leaving 17:00", "arrival 17:00")]
    [InlineData("/arrive 8:00", "/arrive 9:00", "arrival 08:00", "arrival 09:00")]
    [InlineData("/leave 12:00", "/leave 17:00", "leaving 12:00", "leaving 17:00")]
    public void AnyOrder_IsAccepted_WithoutHint(string first, string second, string entry1, string entry2)
    {
        (int exitCode, string output) = RunCommands(first, second, "/quit");

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
        (_, string output) = RunCommands("/leave 0:00", "/quit");

        Assert.Equal(Entries("leaving 00:00"), ReadDay("2026-10-27"));
        Assert.DoesNotContain("must be after", output);
    }

    // AC-6, AC-7 (017)
    [Fact]
    public void LegacyFile_IsShownAsEntries_AndBookingConvertsIt()
    {
        File.WriteAllText(DayFile("2026-10-27"), "{\"arrival\":\"08:15\",\"leaving\":\"17:30\"}");

        (int exitCode, string output) = RunCommands("/arrive 13:00", "/quit");

        Assert.Equal(0, exitCode);
        string first = string.Join('\n', Frames(output)[0]);
        Assert.Matches(@"1 +Arrival +08:15", first);
        Assert.Matches(@"2 +Leaving +17:30", first);
        Assert.Equal(Entries("arrival 08:15", "arrival 13:00", "leaving 17:30"), ReadDay("2026-10-27"));
        string frame = LastFrame(output);
        Assert.Matches(@"1 +Arrival +08:15", frame);
        Assert.Matches(@"2 +Arrival +13:00", frame);
        Assert.Matches(@"3 +Leaving +17:30", frame);
    }

    // AC-6 (017)
    [Fact]
    public void LegacyFile_LeavingBeforeArrival_IsShownInTimeOrder()
    {
        File.WriteAllText(DayFile("2026-10-27"), "{\"arrival\":\"09:00\",\"leaving\":\"08:00\"}");

        (_, string output) = RunCommands("/quit");

        Assert.Matches(@"1 +Leaving +08:00", output);
        Assert.Matches(@"2 +Arrival +09:00", output);
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

        (int exitCode, string output) = RunCommands("/prev", "/next", "/prev", "/quit");

        Assert.Equal(0, exitCode);
        Assert.Matches(@"1 +Arrival +08:15", LastFrame(output));
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));
        Assert.Equal(["2026-10-26.json"], Directory.EnumerateFiles(data.Path).Select(Path.GetFileName));
    }

    // AC-9 (017)
    [Fact]
    public void EmptyEntriesFile_ShowsNoEntriesYet()
    {
        File.WriteAllText(DayFile("2026-10-27"), "{\"entries\":[]}");

        (int exitCode, string output) = RunCommands("/quit");

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
    public void InvalidEntriesFile_ShowsHint_AndNothingIsBooked(string contents)
    {
        File.WriteAllText(DayFile("2026-10-27"), contents);
        byte[] before = File.ReadAllBytes(DayFile("2026-10-27"));

        (int exitCode, string output) = RunCommands("/arrive 9:00", "/quit");

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
        console.Input.PushTextWithEnter("/arrive 8:15");

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
            foreach (string command in new[] { "/prev", "/next", "/arrive 25:00", "/leave x", "/quit" })
            {
                c.Input.PushTextWithEnter(command);
            }
        }, dataFolder: missing.Path);

        Assert.Equal(0, exitCode);
        Assert.False(Directory.Exists(missing.Path));

        Run(c =>
        {
            c.Input.PushTextWithEnter("/arrive 8:15");
            c.Input.PushTextWithEnter("/quit");
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

        RunCommands("/prev", "/leave 8:00", "/next", "/quit");

        Assert.Equal(["2026-10-26.json"], Directory.EnumerateFiles(data.Path).Select(Path.GetFileName));
    }

    // AC-10
    [Fact]
    public void CorruptDayFile_ShowsRedHint_AndIsNotOverwritten()
    {
        File.WriteAllText(DayFile("2026-10-27"), "{not json");
        byte[] before = File.ReadAllBytes(DayFile("2026-10-27"));

        (int exitCode, string output) = RunCommands("/arrive 8:00", "/leave 17:00", "/quit");

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
        console.Input.PushTextWithEnter("/arrive 8:00");
        console.Input.PushTextWithEnter("/quit");

        Assert.Equal(0, new App(console, FixedTimeProvider.At20261027(), new DayStore(data.Path)).Run([]));
        Assert.Matches(@"\x1b\[(31|91|38;5;9)m/arrive: The file for 2026-10-27", console.Output);
    }

    // AC-11
    [Fact]
    public void Help_ListsArriveAndLeave_WithOptionalTime()
    {
        (_, string output) = RunCommands("/help", "/quit");

        Assert.Matches(@"/arrive +│ Book your arrival: now, or at an optional H:mm", output);
        Assert.Matches(@"/leave +│ Book your leaving: now, or at an optional H:mm", output);
    }

    // AC-11
    [Theory]
    [InlineData("/a", "{\"entries\":[{\"kind\":\"arrival\",\"time\":\"18:30\"}]}")]
    [InlineData("/l", "{\"entries\":[{\"kind\":\"leaving\",\"time\":\"18:30\"}]}")]
    public void Tab_CompletesArriveAndLeave(string prefix, string expected)
    {
        (int exitCode, string output) = Run(c =>
        {
            c.Input.PushText(prefix);
            c.Input.PushKey(ConsoleKey.Tab);
            c.Input.PushKey(ConsoleKey.Enter);
            c.Input.PushTextWithEnter("/quit");
        });

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain("Unknown command", output);
        Assert.Equal(expected, ReadDay("2026-10-27"));
    }

    // AC-11
    [Theory]
    [InlineData("/ar", "/arrive")]
    [InlineData("/lea", "/leave")]
    [InlineData("/arive", "/arrive")]
    public void Popup_ShowsArriveAndLeave(string typed, string expected)
    {
        string output = RunAnsi(c =>
        {
            c.Input.PushText(typed);
            c.Input.PushKey(ConsoleKey.Escape);
            c.Input.PushKey(ConsoleKey.Escape);
            c.Input.PushTextWithEnter("/quit");
        });

        // One popup draw per typed key, each ending on row 21. The draw after the last key lists
        // the best match on its top row.
        List<AnsiWrite> writes = TypingWrites(output, 0);
        List<int> bottoms = writes
            .Select((w, i) => (w, i))
            .Where(x => x.w.Row == 21 && (x.w.Text.StartsWith('/') || x.w.Text.StartsWith('…')))
            .Select(x => x.i)
            .ToList();
        int top = bottoms[typed.Length - 1];
        while (top > 0 && writes[top - 1].Row == writes[top].Row - 1 && writes[top - 1].Text.StartsWith('/'))
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

    /// <summary>The /help frame drawn on a terminal tall enough that nothing is cut: the unclipped top content lines.</summary>
    private string[] UnclippedHelpTop()
    {
        (_, string output) = Run(c =>
        {
            c.Profile.Height = 80;
            c.Input.PushTextWithEnter("/help");
            c.Input.PushTextWithEnter("/quit");
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
            c.Input.PushTextWithEnter("/help");
            c.Input.PushTextWithEnter("/quit");
        }, ManyCommands());

        Assert.Equal(0, exitCode);
        List<string[]> frames = Frames(output);
        Assert.Equal(2, frames.Count);
        foreach (string[] each in frames)
        {
            AssertLayout(each, 12);
        }

        string[] frame = frames[1];
        Assert.StartsWith(Heading, frame[0]);
        Assert.Equal(full[..7], frame[..7]);
        Assert.Equal($"… {full.Length - 7} more lines", frame[^5].TrimEnd());
        Assert.True(full.Length - 7 > 10);
    }

    // AC-4
    [Fact]
    public void ShortTerminal_Cue_IsGrey()
    {
        string output = RunAnsi(c =>
        {
            c.Profile.Height = 12;
            c.Input.PushTextWithEnter("/help");
            c.Input.PushTextWithEnter("/quit");
        }, ManyCommands());

        Assert.Matches(@"\x1b\[(90|38;5;8)m… \d+ more lines", output);
    }

    // AC-5
    [Fact]
    public void DefaultHeight_Help_Fits_WithoutCue()
    {
        (_, string output) = Run(c =>
        {
            c.Input.PushTextWithEnter("/help");
            c.Input.PushTextWithEnter("/quit");
        });

        string[] frame = Frames(output)[1];
        AssertLayout(frame);
        Assert.DoesNotContain("more lines", output);
        Assert.Contains("/today", string.Join('\n', frame));
    }

    // AC-6, AC-7
    [Fact]
    public void ShortTerminal_PopupThenEsc_RestoresTheCueRow()
    {
        string output = RunAnsi(c =>
        {
            c.Profile.Height = 12;
            c.Input.PushTextWithEnter("/help");
            c.Input.PushText("/");
            c.Input.PushKey(ConsoleKey.Escape);
            c.Input.PushKey(ConsoleKey.Escape);
            c.Input.PushTextWithEnter("/quit");
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
        int popupAtCue = writes.FindIndex(w => w.Row == 8 && w.Text.StartsWith('/'));
        Assert.True(popupAtCue >= 0, "the popup should cover the cue row");
        AnsiWrite restored = writes.Skip(popupAtCue + 1).First(w => w.Row == 8);
        Assert.Equal(frame[7], restored.Text);
        Assert.StartsWith("… ", restored.Text);

        // The hint row (9) is never written with anything but the popup or its own restore.
        Assert.DoesNotContain(writes, w => w.Row > 9 && w.Text.Contains("more lines", StringComparison.Ordinal));
    }

    // Input placeholder (job 014) ----------------------------------------------------------

    private const string Placeholder = "Type /help for commands";

    // AC-1
    [Fact]
    public void FirstFrame_InputRow_ShowsGreyPlaceholderAfterPrompt()
    {
        string output = RunAnsi(c => c.Input.PushTextWithEnter("/quit"));

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
        string output = RunAnsi(c => c.Input.PushTextWithEnter("/quit"));

        List<AnsiWrite> writes = AnsiWrites.Parse(output);
        // Row height - 1 = 23; column 3 + "> ".Length = 5.
        Assert.Contains(writes, w => w.Row == 23 && w.Column == 5 && w.Text.StartsWith("/quit", StringComparison.Ordinal));
        Assert.Contains("\u001b[23;5H", output);
    }

    // AC-2
    [Fact]
    public void EveryRedrawnFrame_ShowsThePlaceholderAgain()
    {
        string output = RunAnsi(c =>
        {
            c.Input.PushTextWithEnter("/help");
            c.Input.PushTextWithEnter("/nope");
            c.Input.PushKey(ConsoleKey.Enter);
            c.Input.PushTextWithEnter("hello");
            c.Input.PushTextWithEnter("/quit");
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
        (int exitCode, string output) = RunCommands(string.Empty, "/quit");

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

        (int exitCode, string output) = RunCommands("/remove 2", "/quit");

        Assert.Equal(0, exitCode);
        Assert.Equal(Entries("arrival 08:00", "arrival 13:00"), ReadDay("2026-10-27"));
        Assert.Equal(
            [new DayEntry(EntryKind.Arrival, new TimeOnly(8, 0)), new DayEntry(EntryKind.Arrival, new TimeOnly(13, 0))],
            new DayStore(data.Path).Load(new DateOnly(2026, 10, 27)).Entries);

        // Before: the leaving at 12:00 closed 4 hours, so the 13:00 arrival had 4:00 left.
        string first = string.Join('\n', Frames(output)[0]);
        Assert.Matches(@"3 +Arrival +13:00 +left 4:00", first);

        string[] frame = LastLayout(output);
        string top = string.Join('\n', frame[..^4]);
        Assert.Matches(@"1 +Arrival +08:00 +left 8:00", top);
        Assert.Matches(@"2 +Arrival +13:00 +left 8:00", top);
        Assert.DoesNotContain("12:00", top);
        Assert.DoesNotMatch(@"\b3 +(Arrival|Leaving)", top);
        AssertLayout(frame);
        Assert.Equal(string.Empty, frame[^4].Trim());
    }

    // AC-1 (021)
    [Fact]
    public void Remove_AfterBookingInTheSameSession_UsesTheShownNumbers()
    {
        (int exitCode, string output) = RunCommands("/arrive 8:00", "/leave 12:00", "/arrive 13:00", "/remove 2", "/quit");

        Assert.Equal(0, exitCode);
        Assert.Equal(Entries("arrival 08:00", "arrival 13:00"), ReadDay("2026-10-27"));
        Assert.Matches(@"2 +Arrival +13:00", LastFrame(output));
    }

    // AC-2 (021)
    [Fact]
    public void Remove_ActsOnTheViewedDay_AndLeavesTodayUntouched()
    {
        File.WriteAllText(DayFile("2026-10-27"), ThreeEntries);
        File.WriteAllText(DayFile("2026-10-26"), Entries("arrival 09:00", "leaving 17:00"));
        byte[] today = File.ReadAllBytes(DayFile("2026-10-27"));

        (int exitCode, string output) = RunCommands("/prev", "/remove 1", "/quit");

        Assert.Equal(0, exitCode);
        Assert.Equal(Entries("leaving 17:00"), ReadDay("2026-10-26"));
        Assert.Equal(today, File.ReadAllBytes(DayFile("2026-10-27")));
        Assert.StartsWith("Monday, 2026-10-26", LastFrame(output));
        Assert.Matches(@"1 +Leaving +17:00", LastFrame(output));
    }

    // AC-3 (021)
    [Theory]
    [InlineData("/remove 0", "/remove: there is no entry 0.")]
    [InlineData("/remove 4", "/remove: there is no entry 4.")]
    [InlineData("/remove 99", "/remove: there is no entry 99.")]
    [InlineData("/remove 007", "/remove: there is no entry 7.")]
    public void Remove_UnknownNumber_ShowsHintAboveInputBox_AndChangesNothing(string typed, string expected)
    {
        File.WriteAllText(DayFile("2026-10-27"), ThreeEntries);
        byte[] before = File.ReadAllBytes(DayFile("2026-10-27"));

        (int exitCode, string output) = RunCommands("/help", typed, "/quit");

        Assert.Equal(0, exitCode);
        AssertOnHintRow(LastLayout(output), expected);
        Assert.Equal(before, File.ReadAllBytes(DayFile("2026-10-27")));
        Assert.Matches(@"3 +Arrival +13:00", LastFrame(output));
    }

    // AC-4 (021)
    [Theory]
    [InlineData("/remove", "/remove: say which entry to remove.")]
    [InlineData("/remove abc", "/remove: abc is not an entry number.")]
    [InlineData("/remove -1", "/remove: -1 is not an entry number.")]
    [InlineData("/remove +1", "/remove: +1 is not an entry number.")]
    [InlineData("/remove 1.5", "/remove: 1.5 is not an entry number.")]
    [InlineData("/remove 2 3", "/remove: 2 3 is not an entry number.")]
    [InlineData("/remove 99999999999", "/remove: 99999999999 is not an entry number.")]
    [InlineData("/remove [bold]", "/remove: [bold] is not an entry number.")]
    [InlineData("/remove [red]x[/]", "/remove: [red]x[/] is not an entry number.")]
    public void Remove_BadArgument_ShowsUsageHint_AndChangesNothing(string typed, string expected)
    {
        File.WriteAllText(DayFile("2026-10-27"), ThreeEntries);
        byte[] before = File.ReadAllBytes(DayFile("2026-10-27"));

        (int exitCode, string output) = RunCommands("/help", typed, "/quit");

        Assert.Equal(0, exitCode);
        string[] frame = LastLayout(output);
        AssertOnHintRow(frame, expected);
        AssertOnHintRow(frame, "Use /remove <number>.");
        Assert.Equal(before, File.ReadAllBytes(DayFile("2026-10-27")));
        Assert.DoesNotContain("failed:", output);
    }

    // AC-3, AC-4 (021)
    [Theory]
    [InlineData("/remove 4")]
    [InlineData("/remove abc")]
    [InlineData("/remove")]
    public void Remove_Hints_AreRed(string typed)
    {
        File.WriteAllText(DayFile("2026-10-27"), ThreeEntries);
        TestConsole console = new TestConsole().Interactive().EmitAnsiSequences();
        console.Input.PushTextWithEnter(typed);
        console.Input.PushTextWithEnter("/quit");

        Assert.Equal(0, new App(console, FixedTimeProvider.At20261027(), new DayStore(data.Path)).Run([]));
        Assert.Matches(@"\x1b\[(31|91|38;5;9)m/remove: ", console.Output);
    }

    // AC-5 (021)
    [Fact]
    public void Remove_OnDayWithoutFile_ShowsHint_AndCreatesNothing()
    {
        using TempFolder missing = new TempFolder(create: false);

        (int exitCode, string output) = Run(c =>
        {
            c.Input.PushTextWithEnter("/remove 1");
            c.Input.PushTextWithEnter("/quit");
        }, dataFolder: missing.Path);

        Assert.Equal(0, exitCode);
        AssertOnHintRow(LastLayout(output), "/remove: there is no entry 1.");
        Assert.False(Directory.Exists(missing.Path));

        (exitCode, output) = RunCommands("/remove 1", "/quit");

        Assert.Equal(0, exitCode);
        AssertOnHintRow(LastLayout(output), "/remove: there is no entry 1.");
        Assert.Empty(Directory.EnumerateFileSystemEntries(data.Path));
    }

    // AC-6 (021)
    [Fact]
    public void Remove_OnlyEntry_KeepsFileWithEmptyEntries_AndShowsNoEntriesYet()
    {
        (int exitCode, string output) = RunCommands("/arrive 8:00", "/remove 1", "/quit");

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

        (int exitCode, string output) = RunCommands("/remove 1", "/quit");

        Assert.Equal(0, exitCode);
        Assert.Equal(before, File.ReadAllBytes(DayFile("2026-10-27")));
        AssertOnHintRow(LastLayout(output), "/remove: The file for 2026-10-27 " + expected);
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
        (int exitCode, string output) = RunWide(120, "/remove 1", "/quit");

        Assert.Equal(0, exitCode);
        Assert.Equal(before, File.ReadAllBytes(DayFile("2026-10-27")));
        string[] frame = LastLayout(output);
        AssertOnHintRow(frame, "/remove: The file for 2026-10-27 is too large to be a day file. Nothing was removed.");
    }

    // AC-7 (021)
    [Fact]
    public void Remove_UnreadableFile_HintIsRed()
    {
        File.WriteAllText(DayFile("2026-10-27"), "{not json");
        TestConsole console = new TestConsole().Interactive().EmitAnsiSequences();
        console.Input.PushTextWithEnter("/remove 1");
        console.Input.PushTextWithEnter("/quit");

        Assert.Equal(0, new App(console, FixedTimeProvider.At20261027(), new DayStore(data.Path)).Run([]));
        Assert.Matches(@"\x1b\[(31|91|38;5;9)m/remove: The file for 2026-10-27", console.Output);
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
            console.Input.PushTextWithEnter("/remove 2");
            console.Input.PushTextWithEnter("/quit");

            Assert.Equal(0, new App(console, FixedTimeProvider.At20261027(), new DayStore(data.Path)).Run([]));
            Assert.Matches(@"\x1b\[(31|91|38;5;9)m/remove: could not save the day\.", console.Output);
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
        (_, string output) = RunCommands("/help", "/quit");

        Assert.Matches(@"/remove +│ Remove an entry from this day by its number", output);
    }

    // AC-8 (021)
    [Fact]
    public void Tab_CompletesRemove()
    {
        File.WriteAllText(DayFile("2026-10-27"), ThreeEntries);

        (int exitCode, string output) = Run(c =>
        {
            c.Input.PushText("/rem");
            c.Input.PushKey(ConsoleKey.Tab);
            c.Input.PushTextWithEnter(" 3");
            c.Input.PushTextWithEnter("/quit");
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
            c.Input.PushText("/rem");
            c.Input.PushKey(ConsoleKey.Escape);
            c.Input.PushKey(ConsoleKey.Escape);
            c.Input.PushTextWithEnter("/quit");
        });

        List<AnsiWrite> writes = TypingWrites(output, 0);
        List<int> bottoms = writes
            .Select((w, i) => (w, i))
            .Where(x => x.w.Row == 21 && (x.w.Text.StartsWith('/') || x.w.Text.StartsWith('…')))
            .Select(x => x.i)
            .ToList();
        int top = bottoms[3];
        while (top > 0 && writes[top - 1].Row == writes[top].Row - 1 && writes[top - 1].Text.StartsWith('/'))
        {
            top--;
        }

        AnsiWrite row = writes[top];
        Assert.StartsWith("/remove", row.Text);
        Assert.Contains("Remove an entry from this day by its number", row.Text);
    }

    // Change entry (job 023) ---------------------------------------------------------------

    // AC-1 (023)
    [Fact]
    public void Change_MiddleEntry_SavesNewTime_KeepsKind_AndRecomputesTimeLeft()
    {
        File.WriteAllText(DayFile("2026-10-27"), ThreeEntries);

        (int exitCode, string output) = RunCommands("/change 2 12:30", "/quit");

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
        Assert.Matches(@"2 +Leaving +12:00 +left 4:00", first);

        string[] frame = LastLayout(output);
        string top = string.Join('\n', frame[..^4]);
        Assert.Matches(@"1 +Arrival +08:00 +left 8:00", top);
        Assert.Matches(@"2 +Leaving +12:30 +left 3:30", top);
        Assert.Matches(@"3 +Arrival +13:00 +left 3:30", top);
        Assert.DoesNotContain("12:00", top);
        AssertLayout(frame);
        Assert.Equal(string.Empty, frame[^4].Trim());
    }

    // AC-2 (023)
    [Fact]
    public void Change_PastOtherEntries_ReSortsAndRenumbers()
    {
        File.WriteAllText(DayFile("2026-10-27"), ThreeEntries);

        (int exitCode, string output) = RunCommands("/change 1 14:00", "/quit");

        Assert.Equal(0, exitCode);
        Assert.Equal(Entries("leaving 12:00", "arrival 13:00", "arrival 14:00"), ReadDay("2026-10-27"));
        string[] frame = LastLayout(output);
        string top = string.Join('\n', frame[..^4]);
        Assert.Matches(@"1 +Leaving +12:00 +left 8:00", top);
        Assert.Matches(@"2 +Arrival +13:00 +left 8:00", top);
        Assert.Matches(@"3 +Arrival +14:00 +left 8:00", top);
        Assert.DoesNotContain("08:00", top);
        Assert.Equal(string.Empty, frame[^4].Trim());
    }

    // AC-1, AC-2 (023)
    [Fact]
    public void Change_AfterBookingInTheSameSession_UsesTheShownNumbers()
    {
        (int exitCode, string output) = RunCommands("/arrive 8:00", "/leave 12:00", "/arrive 13:00", "/change 3 12:45", "/quit");

        Assert.Equal(0, exitCode);
        Assert.Equal(Entries("arrival 08:00", "leaving 12:00", "arrival 12:45"), ReadDay("2026-10-27"));
        Assert.Matches(@"3 +Arrival +12:45", LastFrame(output));
    }

    // AC-3 (023)
    [Theory]
    [InlineData("/change 1 7:05")]
    [InlineData("/change 1 07:05")]
    [InlineData("/change   1    7:05")]
    [InlineData("  /change 1 07:05   ")]
    [InlineData("/change    1  07:05    ")]
    public void Change_AcceptsShortAndLongHours_AndExtraSpaces(string typed)
    {
        File.WriteAllText(DayFile("2026-10-27"), ThreeEntries);

        (int exitCode, string output) = RunCommands(typed, "/quit");

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

        (int exitCode, string output) = RunCommands("/prev", "/change 1 9:00", "/quit");

        Assert.Equal(0, exitCode);
        Assert.Equal(Entries("arrival 09:00", "leaving 17:00"), ReadDay("2026-10-26"));
        Assert.Equal(today, File.ReadAllBytes(DayFile("2026-10-27")));
        Assert.StartsWith("Monday, 2026-10-26", LastFrame(output));
        Assert.Matches(@"1 +Arrival +09:00", LastFrame(output));
    }

    // AC-5 (023)
    [Theory]
    [InlineData("/change 0 8:00", "/change: there is no entry 0.")]
    [InlineData("/change 4 8:00", "/change: there is no entry 4.")]
    [InlineData("/change 99 8:00", "/change: there is no entry 99.")]
    [InlineData("/change 004 8:00", "/change: there is no entry 4.")]
    public void Change_UnknownNumber_ShowsHintAboveInputBox_AndChangesNothing(string typed, string expected)
    {
        File.WriteAllText(DayFile("2026-10-27"), ThreeEntries);
        byte[] before = File.ReadAllBytes(DayFile("2026-10-27"));

        (int exitCode, string output) = RunCommands("/help", typed, "/quit");

        Assert.Equal(0, exitCode);
        AssertOnHintRow(LastLayout(output), expected);
        Assert.Equal(before, File.ReadAllBytes(DayFile("2026-10-27")));
        Assert.Matches(@"3 +Arrival +13:00", LastFrame(output));
    }

    // AC-6 (023)
    [Theory]
    [InlineData("/change 2 25:00", "/change: 25:00 is not a time.")]
    [InlineData("/change 2 8:5", "/change: 8:5 is not a time.")]
    [InlineData("/change 2 8.15", "/change: 8.15 is not a time.")]
    [InlineData("/change 2 abc", "/change: abc is not a time.")]
    [InlineData("/change 2 8:15:00", "/change: 8:15:00 is not a time.")]
    [InlineData("/change 2 [red]x", "/change: [red]x is not a time.")]
    public void Change_InvalidTime_ShowsHintWithFormat_AndChangesNothing(string typed, string expected)
    {
        File.WriteAllText(DayFile("2026-10-27"), ThreeEntries);
        byte[] before = File.ReadAllBytes(DayFile("2026-10-27"));

        (int exitCode, string output) = RunCommands("/help", typed, "/quit");

        Assert.Equal(0, exitCode);
        string[] frame = LastLayout(output);
        AssertOnHintRow(frame, expected);
        AssertOnHintRow(frame, "Use H:mm, e.g. 8:15.");
        Assert.Equal(before, File.ReadAllBytes(DayFile("2026-10-27")));
        Assert.DoesNotContain("failed:", output);
    }

    // AC-7 (023)
    [Theory]
    [InlineData("/change", "/change: say which entry and its new time.")]
    [InlineData("/change 2", "/change: say which entry and its new time.")]
    [InlineData("/change 2 8:00 9:00", "/change: say which entry and its new time.")]
    [InlineData("/change abc 8:00", "/change: abc is not an entry number.")]
    [InlineData("/change -1 8:00", "/change: -1 is not an entry number.")]
    [InlineData("/change +1 8:00", "/change: +1 is not an entry number.")]
    [InlineData("/change 99999999999 8:00", "/change: 99999999999 is not an entry number.")]
    [InlineData("/change [bold] 8:00", "/change: [bold] is not an entry number.")]
    [InlineData("/change [red]x[/] 8:00", "/change: [red]x[/] is not an entry number.")]
    public void Change_MissingOrMalformedArgument_ShowsUsageHint_AndChangesNothing(string typed, string expected)
    {
        File.WriteAllText(DayFile("2026-10-27"), ThreeEntries);
        byte[] before = File.ReadAllBytes(DayFile("2026-10-27"));

        (int exitCode, string output) = RunCommands("/help", typed, "/quit");

        Assert.Equal(0, exitCode);
        string[] frame = LastLayout(output);
        AssertOnHintRow(frame, expected);
        AssertOnHintRow(frame, "Use /change <number> <H:mm>.");
        Assert.Equal(before, File.ReadAllBytes(DayFile("2026-10-27")));
        Assert.DoesNotContain("failed:", output);
    }

    // AC-5, AC-6, AC-7 (023)
    [Theory]
    [InlineData("/change 4 8:00")]
    [InlineData("/change 2 25:00")]
    [InlineData("/change abc 8:00")]
    [InlineData("/change")]
    public void Change_Hints_AreRed(string typed)
    {
        File.WriteAllText(DayFile("2026-10-27"), ThreeEntries);
        TestConsole console = new TestConsole().Interactive().EmitAnsiSequences();
        console.Input.PushTextWithEnter(typed);
        console.Input.PushTextWithEnter("/quit");

        Assert.Equal(0, new App(console, FixedTimeProvider.At20261027(), new DayStore(data.Path)).Run([]));
        Assert.Matches(@"\x1b\[(31|91|38;5;9)m/change: ", console.Output);
    }

    // AC-8 (023)
    [Fact]
    public void Change_OnDayWithoutFile_ShowsHint_AndCreatesNothing()
    {
        using TempFolder missing = new TempFolder(create: false);

        (int exitCode, string output) = Run(c =>
        {
            c.Input.PushTextWithEnter("/change 1 8:00");
            c.Input.PushTextWithEnter("/quit");
        }, dataFolder: missing.Path);

        Assert.Equal(0, exitCode);
        AssertOnHintRow(LastLayout(output), "/change: there is no entry 1.");
        Assert.False(Directory.Exists(missing.Path));

        (exitCode, output) = RunCommands("/change 1 8:00", "/quit");

        Assert.Equal(0, exitCode);
        AssertOnHintRow(LastLayout(output), "/change: there is no entry 1.");
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

        (int exitCode, string output) = RunCommands("/change 1 8:00", "/quit");

        Assert.Equal(0, exitCode);
        Assert.Equal(before, File.ReadAllBytes(DayFile("2026-10-27")));
        AssertOnHintRow(LastLayout(output), "/change: The file for 2026-10-27 is not a valid day file. Nothing was changed.");
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
        console.Input.PushTextWithEnter("/change 1 8:00");
        console.Input.PushTextWithEnter("/quit");

        Assert.Equal(0, new App(console, FixedTimeProvider.At20261027(), new DayStore(data.Path)).Run([]));
        Assert.Matches(@"\x1b\[(31|91|38;5;9)m/change: The file for 2026-10-27", console.Output);
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
            console.Input.PushTextWithEnter("/change 2 12:30");
            console.Input.PushTextWithEnter("/quit");

            Assert.Equal(0, new App(console, FixedTimeProvider.At20261027(), new DayStore(data.Path)).Run([]));
            Assert.Matches(@"\x1b\[(31|91|38;5;9)m/change: could not save the day\.", console.Output);
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
        (_, string output) = RunWide(120, "/help", "/quit");

        Assert.Matches(@"/change +│ Change the time of an entry on this day: <number> <H:mm>", output);
    }

    // AC-10 (023)
    [Fact]
    public void Tab_CompletesChange()
    {
        File.WriteAllText(DayFile("2026-10-27"), ThreeEntries);

        (int exitCode, string output) = Run(c =>
        {
            c.Input.PushText("/cha");
            c.Input.PushKey(ConsoleKey.Tab);
            c.Input.PushTextWithEnter(" 2 12:30");
            c.Input.PushTextWithEnter("/quit");
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
            c.Input.PushText("/cha");
            c.Input.PushKey(ConsoleKey.Escape);
            c.Input.PushKey(ConsoleKey.Escape);
            c.Input.PushTextWithEnter("/quit");
        });

        List<AnsiWrite> writes = TypingWrites(output, 0);
        List<int> bottoms = writes
            .Select((w, i) => (w, i))
            .Where(x => x.w.Row == 21 && (x.w.Text.StartsWith('/') || x.w.Text.StartsWith('…')))
            .Select(x => x.i)
            .ToList();
        int top = bottoms[3];
        while (top > 0 && writes[top - 1].Row == writes[top].Row - 1 && writes[top - 1].Text.StartsWith('/'))
        {
            top--;
        }

        AnsiWrite row = writes[top];
        Assert.StartsWith("/change", row.Text);
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
        console.Input.PushTextWithEnter("/quit");

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

    /// <summary>Runs <c>/help</c> then <c>/quit</c> on a terminal wide enough that the data-folder line never wraps.</summary>
    private static string RunHelp(string folder, bool folderFromEnvironment, bool ansi = false)
    {
        TestConsole console = new TestConsole().Interactive();
        if (ansi)
        {
            console.EmitAnsiSequences();
        }

        console.Profile.Width = 300;
        console.Input.PushTextWithEnter("/help");
        console.Input.PushTextWithEnter("/quit");
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
    [InlineData("")]
    public void InvalidOverride_ReturnsTwo_NamesTheVariable_AndCreatesNothing(string folder)
    {
        string[] before = WorkingDirectoryEntries();
        TestConsole console = new TestConsole().Interactive().EmitAnsiSequences();
        console.Profile.Width = 300; // keep the error on one line, so the text can be matched as a whole
        console.Input.PushTextWithEnter("/arrive 8:15");

        int exitCode = new App(console, FixedTimeProvider.At20261027(), new DayStore(folder), folderFromEnvironment: true).Run([]);

        Assert.Equal(2, exitCode);
        Assert.Matches(@"\x1b\[(31|91|38;5;9)mTimes cannot start", console.Output);
        string text = StripAnsi(console.Output).TrimEnd();
        Assert.Contains($"the data folder {folder} from TIMES_DATA_DIR is not an absolute path", text);
        Assert.EndsWith("Nothing was saved.", text);
        Assert.DoesNotContain("app-data folder could not be found", text);
        Assert.DoesNotContain(Heading, console.Output);
        Assert.Equal(before, WorkingDirectoryEntries());
    }

    // AC-4
    [Fact]
    public void InvalidOverride_ShowsMarkupInTheValueLiterally()
    {
        TestConsole console = new TestConsole().Interactive();
        console.Profile.Width = 300;

        int exitCode = new App(console, FixedTimeProvider.At20261027(), new DayStore("[red]x[/]"), folderFromEnvironment: true).Run([]);

        Assert.Equal(2, exitCode);
        Assert.Contains("the data folder [red]x[/] from TIMES_DATA_DIR", console.Output);
    }

    // AC-5
    [Fact]
    public void Help_ShowsDefaultDataFolder_WithoutSource()
    {
        string output = RunHelp(data.Path, folderFromEnvironment: false);

        string[] frame = Frames(output)[1];
        int line = Array.FindIndex(frame, l => l.Contains("Data folder:", StringComparison.Ordinal));
        Assert.True(line > 0);
        Assert.Equal($"Data folder: {data.Path}", frame[line].TrimEnd());
        Assert.StartsWith("╰", frame[line - 1]);
        foreach (Command command in CommandRegistry.CreateDefault().Commands.Concat([new Command("/arrive", "", _ => CommandResult.None)]))
        {
            Assert.Contains(command.Name, string.Join('\n', frame));
        }

        Assert.DoesNotContain("(from TIMES_DATA_DIR)", output);
    }

    // AC-5
    [Fact]
    public void Help_ShowsOverrideDataFolder_WithGreySource()
    {
        string output = RunHelp(data.Path, folderFromEnvironment: true);

        string[] frame = Frames(output)[1];
        Assert.Contains(frame, l => l.TrimEnd() == $"Data folder: {data.Path} (from TIMES_DATA_DIR)");

        string ansi = RunHelp(data.Path, folderFromEnvironment: true, ansi: true);
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
        console.Input.PushTextWithEnter("/arrive 8:15");
        console.Input.PushTextWithEnter("/help");
        console.Input.PushTextWithEnter("/quit");

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
}
