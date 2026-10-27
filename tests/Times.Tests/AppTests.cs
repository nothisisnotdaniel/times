using System.Text.RegularExpressions;
using Spectre.Console.Testing;

namespace Times.Tests;

public sealed class AppTests : IDisposable
{
    private const string Heading = "Tuesday, 2026-10-27";

    private readonly TempFolder data = new();

    public void Dispose() => data.Dispose();

    private (int ExitCode, string Output) Run(Action<TestConsole> input, CommandRegistry? registry = null, TimeProvider? time = null, string? dataFolder = null)
    {
        var console = new TestConsole().Interactive();
        input(console);
        var exitCode = new App(console, time ?? FixedTimeProvider.At20261027(), new DayStore(dataFolder ?? data.Path), registry).Run([]);
        return (exitCode, console.Output);
    }

    private (int ExitCode, string Output) RunCommands(params string[] commands) => RunCommands(null, commands);

    private (int ExitCode, string Output) RunCommands(TimeProvider? time, params string[] commands) =>
        Run(c =>
        {
            foreach (var command in commands)
            {
                c.Input.PushTextWithEnter(command);
            }
        }, time: time);

    /// <summary>The heading of the last drawn frame.</summary>
    private static string LastHeading(string output) =>
        Regex.Matches(output, @"[A-Z][a-z]+day, \d{4}-\d{2}-\d{2}").Last().Value;

    /// <summary>Everything from the last drawn heading on: the last frame.</summary>
    private static string LastFrame(string output) => output[output.LastIndexOf(LastHeading(output), StringComparison.Ordinal)..];

    private static int Count(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    // AC-1
    [Fact]
    public void Quit_ReturnsZero_AndShowsDayHeadingAndEmptyState()
    {
        var (exitCode, output) = Run(c => c.Input.PushTextWithEnter("/quit"));

        Assert.Equal(0, exitCode);
        Assert.Contains(Heading, output);
        Assert.Contains("No entries yet.", output);
    }

    // AC-2
    [Fact]
    public void StarterOutput_IsGone()
    {
        var (_, output) = Run(c => c.Input.PushTextWithEnter("/quit"));

        Assert.DoesNotContain("_____", output); // Figlet "Times" banner
        Assert.DoesNotContain("Zone", output);
        Assert.DoesNotContain("18:30:00", output);
    }

    // AC-3
    [Fact]
    public void InputBox_IsRoundedPanel_BelowDayView_AtBottom()
    {
        var (_, output) = Run(c => c.Input.PushTextWithEnter("/quit"));

        var heading = output.IndexOf(Heading, StringComparison.Ordinal);
        var top = output.IndexOf('╭', heading);
        var bottom = output.IndexOf('╰', top);
        Assert.True(heading >= 0 && top > heading && bottom > top);

        // The layout fills the 24-line test terminal; the panel occupies the last 3 lines.
        var frame = output[..(output.IndexOf('╯', bottom) + 1)].Split('\n');
        Assert.Equal(24, frame.Length);
        Assert.StartsWith("╭", frame[^3]);
        Assert.StartsWith("│ > ", frame[^2]);
        Assert.StartsWith("╰", frame[^1]);
    }

    // AC-4
    [Fact]
    public void Help_ListsEveryRegisteredCommand_IncludingOnesAddedInTest()
    {
        var registry = CommandRegistry.CreateDefault();
        registry.Register("/demo", "A command from the test", _ => CommandResult.None);

        var (exitCode, output) = Run(c =>
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
        var (exitCode, output) = Run(c =>
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
        var console = new TestConsole().Interactive().EmitAnsiSequences();
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
        var (exitCode, output) = Run(c =>
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
        var (exitCode, output) = Run(c =>
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
        var registry = CommandRegistry.CreateDefault();
        registry.Register("/boom", "Throws", _ => throw new InvalidOperationException("bad [thing]"));

        var (exitCode, output) = Run(c =>
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
        var (exitCode, output) = Run(c =>
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
        var (exitCode, output) = Run(c =>
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
        var (exitCode, _) = Run(c => c.Input.PushTextWithEnter("/QUIT"));

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public void TextAfterName_IsPassedToHandler()
    {
        string? received = null;
        var registry = CommandRegistry.CreateDefault();
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
        var (_, output) = Run(c =>
        {
            c.Input.PushTextWithEnter("/nope");
            c.Input.PushTextWithEnter("/quit");
        });

        Assert.True(Count(output, Heading) >= 2);
    }

    [Fact]
    public void Message_StaysInTopArea_UntilNextCommand()
    {
        var (_, output) = Run(c =>
        {
            c.Input.PushTextWithEnter("/help");
            c.Input.PushTextWithEnter("/nope");
            c.Input.PushTextWithEnter("");
            c.Input.PushTextWithEnter("/quit");
        });

        var frames = output.Split(Heading).Skip(1).ToArray();
        Assert.Equal(4, frames.Length);
        Assert.Contains("List all commands", frames[1]);           // after /help
        Assert.DoesNotContain("List all commands", frames[2]);     // replaced by the hint
        Assert.Contains("Unknown command /nope", frames[2]);
        Assert.DoesNotContain("Unknown command", frames[3]);       // cleared by the empty line

        // The message sits above the input panel.
        Assert.True(frames[1].IndexOf("List all commands", StringComparison.Ordinal) < frames[1].IndexOf("│ > ", StringComparison.Ordinal));
    }

    // AC-10
    [Fact]
    public void Backspace_EditsTheLine()
    {
        var (exitCode, output) = Run(c =>
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
        var (exitCode, output) = Run(c =>
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
        var (exitCode, output) = Run(c =>
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
        var (exitCode, output) = Run(c =>
        {
            c.Input.PushTextWithEnter("/help");
            c.Input.PushKey(ConsoleKey.UpArrow);
            c.Input.PushKey(ConsoleKey.Enter);
            c.Input.PushTextWithEnter("/quit");
        });

        Assert.Equal(0, exitCode);
        var frames = output.Split(Heading).Skip(1).ToArray();
        Assert.Equal(3, frames.Length);
        Assert.Contains("List all commands", frames[1]);
        Assert.Contains("List all commands", frames[2]);
    }

    // Day navigation (job 002) ------------------------------------------------------------

    // AC-1
    [Fact]
    public void Prev_MovesToPreviousDay()
    {
        var (exitCode, output) = RunCommands("/prev", "/quit");

        Assert.Equal(0, exitCode);
        Assert.Equal("Monday, 2026-10-26", LastHeading(output));
    }

    // AC-2
    [Fact]
    public void Next_MovesToNextDay()
    {
        var (exitCode, output) = RunCommands("/next", "/quit");

        Assert.Equal(0, exitCode);
        Assert.Equal("Wednesday, 2026-10-28", LastHeading(output));
    }

    // AC-3
    [Fact]
    public void Today_JumpsBackToToday()
    {
        var (exitCode, output) = RunCommands("/prev", "/prev", "/today", "/quit");

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
        var time = new SettableTimeProvider(new DateTimeOffset(year, month, day, 12, 0, 0, TimeSpan.Zero));

        var (exitCode, output) = RunCommands(time, command, "/quit");

        Assert.Equal(0, exitCode);
        Assert.Equal(expected, LastHeading(output));
    }

    // AC-5
    [Fact]
    public void Today_UsesTheClockWhenTheCommandRuns()
    {
        var time = new SettableTimeProvider(new DateTimeOffset(2026, 10, 27, 23, 0, 0, TimeSpan.Zero));
        var registry = CommandRegistry.CreateDefault();
        registry.Register("/tick", "Advance the test clock", _ =>
        {
            time.UtcNow = time.UtcNow.AddHours(2);
            return CommandResult.None;
        });

        var (exitCode, output) = Run(c =>
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
        var (_, output) = RunCommands("/next", "/quit");

        var frame = LastFrame(output);
        Assert.StartsWith("Wednesday, 2026-10-28", frame);
        Assert.Contains("No entries yet.", frame);
    }

    // AC-7
    [Fact]
    public void Browsing_LeavesExistingFolderEmpty()
    {
        var (exitCode, _) = RunCommands("/prev", "/next", "/next", "/today", "/quit");

        Assert.Equal(0, exitCode);
        Assert.True(Directory.Exists(data.Path));
        Assert.Empty(Directory.EnumerateFileSystemEntries(data.Path));
    }

    // AC-7
    [Fact]
    public void Browsing_DoesNotCreateMissingFolder()
    {
        using var missing = new TempFolder(create: false);

        var (exitCode, _) = Run(c =>
        {
            foreach (var command in new[] { "/prev", "/next", "/next", "/today", "/quit" })
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
        var (_, output) = RunCommands("/help", "/quit");

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
        var (exitCode, output) = RunCommands("/NEXT", "/quit");

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
        var (exitCode, output) = Run(c =>
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
        var (exitCode, output) = RunCommands(typed, "/quit");

        Assert.Equal(0, exitCode);
        Assert.Equal(Heading, LastHeading(output));
        Assert.Contains($"{name} takes no arguments.", LastFrame(output));
    }

    // AC-9
    [Fact]
    public void NavigationWithArgument_HintIsRed_AndLoopContinues()
    {
        var console = new TestConsole().Interactive().EmitAnsiSequences();
        console.Input.PushTextWithEnter("/next 3");
        console.Input.PushTextWithEnter("/prev");
        console.Input.PushTextWithEnter("/quit");

        var exitCode = new App(console, FixedTimeProvider.At20261027(), new DayStore(data.Path)).Run([]);

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
        var (exitCode, output) = RunCommands(new SettableTimeProvider(DateTimeOffset.MinValue), "/prev", "/quit");

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
        var (exitCode, output) = RunCommands(new SettableTimeProvider(DateTimeOffset.MaxValue), "/next", "/quit");

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
        var console = new TestConsole().Interactive().EmitAnsiSequences();
        console.Input.PushTextWithEnter("/next");
        console.Input.PushTextWithEnter("/quit");

        new App(console, new SettableTimeProvider(DateTimeOffset.MaxValue), new DayStore(data.Path)).Run([]);

        Assert.Matches(@"\x1b\[(31|91|38;5;9)(;1)?m(\x1b\[[0-9;]*m)*There is no day after this one\.", console.Output);
    }
}
