using System.Text.RegularExpressions;
using Spectre.Console;
using Spectre.Console.Testing;

namespace Times.Tests;

public sealed class OneShotTests : IDisposable
{
    private readonly TempFolder data = new();
    private readonly TempFolder other = new();

    public void Dispose()
    {
        data.Dispose();
        other.Dispose();
    }

    private string TodayFile(string folder) => Path.Combine(folder, "2026-10-27.json");

    private static (int Exit, string Output) Shot(string folder, params string[] args)
    {
        // No input is queued: reading a line would throw instead of hanging.
        TestConsole console = new TestConsole().Interactive();
        console.Profile.Width = 120;
        int exit = new App(console, FixedTimeProvider.At20261027(), new DayStore(folder)).Run(args);
        return (exit, console.Output);
    }

    private static void Session(string folder, params string[] lines)
    {
        TestConsole console = new TestConsole().Interactive();
        foreach (string line in lines)
        {
            console.Input.PushTextWithEnter(line);
        }

        console.Input.PushTextWithEnter("quit");
        Assert.Equal(0, new App(console, FixedTimeProvider.At20261027(), new DayStore(folder)).Run([]));
    }

    // AC-1
    [Fact]
    public void Arrive_BooksOnTodaysFile_LikeTheSession()
    {
        (int exit, _) = Shot(data.Path, "arrive", "8:15");
        Session(other.Path, "arrive 8:15");

        Assert.Equal(0, exit);
        Assert.Equal(File.ReadAllText(TodayFile(other.Path)), File.ReadAllText(TodayFile(data.Path)));
    }

    // AC-2
    [Fact]
    public void SeveralArgs_AreJoined_AliasAndSlashWork_DoubleSlashIsUnknown()
    {
        Assert.Equal(0, Shot(data.Path, "addproject", "1234", "Design").Exit);
        (int cpExit, string cpOut) = Shot(data.Path, "checkpoint", "8:30", "#1", "Design review");
        Assert.True(cpExit == 0, cpOut);
        Session(other.Path, "addproject 1234 Design", "checkpoint 8:30 #1 Design review");
        Assert.Equal(File.ReadAllText(TodayFile(other.Path)), File.ReadAllText(TodayFile(data.Path)));

        using TempFolder alias = new();
        Assert.Equal(0, Shot(alias.Path, "a", "9:00").Exit);
        using TempFolder slash = new();
        Assert.Equal(0, Shot(slash.Path, "/arrive", "9:00").Exit);
        Assert.Equal(File.ReadAllText(TodayFile(alias.Path)), File.ReadAllText(TodayFile(slash.Path)));

        using TempFolder bad = new();
        (int exit, string output) = Shot(bad.Path, "//a", "9:00");
        Assert.Equal(1, exit);
        Assert.Contains("Unknown command", output);
        Assert.False(File.Exists(TodayFile(bad.Path)));
    }

    // AC-3
    [Fact]
    public void Leave_BooksAtTheInjectedTime()
    {
        Shot(data.Path, "arrive", "8:15");

        Assert.Equal(0, Shot(data.Path, "leave").Exit);

        string json = File.ReadAllText(TodayFile(data.Path));
        Assert.Contains("18:30", json);
    }

    // AC-4
    [Fact]
    public void Status_PrintsTheDayView_AndWritesNothingWhenTodayHasNoFile()
    {
        (int empty, _) = Shot(data.Path, "status");
        Assert.Equal(0, empty);
        Assert.False(File.Exists(TodayFile(data.Path)));

        Shot(data.Path, "arrive", "8:15");
        (int exit, string output) = Shot(data.Path, "status");
        Assert.Equal(0, exit);
        Assert.Contains("08:15", output);
        Assert.Contains("AR", output);
    }

    // AC-5
    [Theory]
    [InlineData("nonsense")]
    [InlineData("arrive 25:00")]
    public void Errors_ReturnOne_PrintRed_SaveNothing(string line)
    {
        TestConsole console = new TestConsole().Interactive().EmitAnsiSequences();
        console.Profile.Width = 120;

        int exit = new App(console, FixedTimeProvider.At20261027(), new DayStore(data.Path)).Run(line.Split(' '));

        Assert.Equal(1, exit);
        Assert.Matches(@"\x1b\[(31|91|38;5;9)m", console.Output);
        Assert.False(File.Exists(TodayFile(data.Path)));
    }

    [Fact]
    public void AbsenceDay_RefusesBooking_WithOne()
    {
        Shot(data.Path, "absence", "sick");
        string before = File.ReadAllText(TodayFile(data.Path));

        TestConsole console = new TestConsole().Interactive().EmitAnsiSequences();
        console.Profile.Width = 120;
        int exit = new App(console, FixedTimeProvider.At20261027(), new DayStore(data.Path)).Run(["arrive", "8:00"]);
        string output = console.Output;

        Assert.Equal(1, exit);
        Assert.Matches(@"\x1b\[(31|91|38;5;9)m", output);
        Assert.Contains("absence", output);
        Assert.Equal(before, File.ReadAllText(TodayFile(data.Path)));
    }

    // AC-6
    [Fact]
    public void Output_IsPlain()
    {
        TestConsole console = new TestConsole().Interactive().EmitAnsiSequences();
        console.Profile.Width = 120;
        App app = new App(console, FixedTimeProvider.At20261027(), new DayStore(data.Path), mouse: new MouseReporting(console));

        Assert.Equal(0, app.Run(["arrive", "8:15"]));
        Assert.Equal(0, app.Run(["help"]));
        Assert.Equal(0, app.Run(["status"]));

        string output = console.Output;
        Assert.DoesNotContain("\u001b[?1000h", output);
        Assert.DoesNotContain("\u001b[?2004h", output);
        Assert.DoesNotContain("Type help for commands", output);
        Assert.DoesNotContain("│ > ", output);
        Assert.DoesNotContain("\u001b[2J", output);
        Assert.DoesNotMatch(@"\x1b\[H", output);
        Assert.DoesNotMatch(@"\x1b\[\d*;\d*H", output);
        Assert.Contains("08:15", output);
    }

    // AC-6: the same console does emit a clear in the session, so the check above can fail.
    [Fact]
    public void Session_EmitsAClear_SoTheOneShotCheckIsMeaningful()
    {
        TestConsole console = new TestConsole().Interactive().EmitAnsiSequences();
        console.Profile.Width = 120;
        console.Input.PushTextWithEnter("quit");

        new App(console, FixedTimeProvider.At20261027(), new DayStore(data.Path)).Run([]);

        Assert.Matches(@"\x1b\[(2J|H|\d+;\d+H)", console.Output);
    }

    // AC-7
    [Theory]
    [InlineData("quit")]
    [InlineData("settings")]
    [InlineData("prev")]
    [InlineData("next")]
    [InlineData("today")]
    [InlineData("update")]
    [InlineData("import /tmp/x")]
    [InlineData("devops connect contoso")]
    [InlineData("dv connect contoso")]
    public void SessionOnly_IsRefused_ChangesNothing(string line)
    {
        (int exit, string output) = Shot(data.Path, line.Split(' '));

        Assert.Equal(1, exit);
        Assert.Contains("needs the interactive session. Run times without arguments.", output);
        Assert.Empty(Directory.EnumerateFileSystemEntries(data.Path));
    }

    [Fact]
    public void Help_IsAllowed()
    {
        (int exit, string output) = Shot(data.Path, "help");

        Assert.Equal(0, exit);
        Assert.Contains("arrive", output);
        Assert.DoesNotContain("status", output);
    }

    // AC-8
    [Fact]
    public void DataFolderRules_AreShared()
    {
        Assert.Equal(2, Shot("relative", "arrive", "8:00").Exit);

        using InstanceLock held = new InstanceLock(data.Path);
        Assert.Equal(InstanceLockStatus.Acquired, held.TryAcquire().Status);
        Assert.Equal(3, Shot(data.Path, "arrive", "8:00").Exit);

        Assert.Equal(0, Shot(data.Path, "--version").Exit);
        Assert.Equal(0, Shot("relative", "arrive", "--version").Exit);
    }

    [Fact]
    public void LockFailure_ExitsFour()
    {
        string file = Path.Combine(other.Path, "afile");
        File.WriteAllText(file, "x");
        string folder = Path.Combine(file, "sub");

        Assert.Equal(4, Shot(folder, "arrive", "8:00").Exit);
    }

    [Theory]
    [InlineData("--Version")]
    [InlineData("-v")]
    public void DashArguments_StillStartTheSession(string arg)
    {
        TestConsole console = new TestConsole().Interactive();
        console.Input.PushTextWithEnter("quit");

        Assert.Equal(0, new App(console, FixedTimeProvider.At20261027(), new DayStore(data.Path)).Run([arg]));
        Assert.Contains("Type help for commands", console.Output);
    }

    // AC-9
    [Fact]
    public void NoArguments_StartsTheSession()
    {
        TestConsole console = new TestConsole().Interactive();
        console.Input.PushTextWithEnter("quit");

        Assert.Equal(0, new App(console, FixedTimeProvider.At20261027(), new DayStore(data.Path)).Run([]));
        Assert.Contains("Type help for commands", console.Output);
    }

    [Fact]
    public void OneShot_EnablesNoMouse_NoLiveClock_NoUpdateCheck()
    {
        using TestSigner signer = new();
        FakeReleaseSource source = new([]);
        UpdateService service = new(source, signer.Signature, null, new UpdateApplier(new FakeLauncher(), windows: false), "1.0.0");
        TestConsole console = new TestConsole().Interactive();
        ScriptedEvents events = new();
        App app = new(console, FixedTimeProvider.At20261027(), new DayStore(data.Path), mouse: new MouseReporting(console),
            updates: service, events: events, liveClock: true);

        Assert.Equal(0, app.Run(["arrive", "8:15"]));

        Assert.Equal(0, source.ListCalls);
        Assert.Empty(source.Downloads);
        Assert.Equal(0, events.Waits);
        Assert.False(events.TryTake(out _));
        Assert.DoesNotContain("\u001b[?1000h", console.Output);
        Assert.DoesNotContain("\u001b[?1000l", console.Output);
    }

    // AC-10
    [Fact]
    public void Lock_IsReleasedAfterAOneShot()
    {
        Assert.Equal(0, Shot(data.Path, "arrive", "8:15").Exit);
        Assert.Equal(0, Shot(data.Path, "leave").Exit);
        Assert.Equal(InstanceLockStatus.Acquired, new InstanceLock(data.Path).TryAcquire().Status);
    }

    // AC-9 (128): one-shot commands never clear the screen, draw the layout or poll the size.
    [Theory]
    [InlineData("status")]
    [InlineData("arrive", "8:15")]
    public void OneShot_HasNoClear_AndNeverAsksForAKey(params string[] args)
    {
        TestConsole console = new TestConsole().Interactive().EmitAnsiSequences();
        console.Profile.Width = 120;
        HookedInput input = new HookedInput(console.Input);

        int exit = new App(new InputOverrideConsole(console, input), FixedTimeProvider.At20261027(), new DayStore(data.Path),
            watchResize: true).Run(args);

        Assert.Equal(0, exit);
        Assert.DoesNotContain("\u001b[2J", console.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("╭", console.Output, StringComparison.Ordinal);
        Assert.Equal(0, input.KeyAvailableCalls);
    }
}
