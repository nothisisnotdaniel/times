using System.Text.Json;
using System.Text.RegularExpressions;
using Spectre.Console.Testing;

namespace Times.Tests;

// Job 306: tm status --json (StatusJson through App.RunOneShot). The clock is 2026-10-27 18:30 (UTC).
public sealed class StatusJsonTests : IDisposable
{
    private readonly TempFolder data = new();

    public void Dispose() => data.Dispose();

    private string TodayFile => Path.Combine(data.Path, "2026-10-27.json");

    private static (int Exit, string Output) Shot(string folder, params string[] args)
    {
        TestConsole console = new TestConsole().Interactive().EmitAnsiSequences();
        console.Profile.Width = 400;
        int exit = new App(console, FixedTimeProvider.At20261027(), new DayStore(folder)).Run(args);
        return (exit, console.Output);
    }

    private void WriteToday(params string[] entries) =>
        File.WriteAllText(TodayFile, "{\"entries\":[" + string.Join(',', entries) + "]}");

    private static string Entry(string kind, string time) => $"{{\"kind\":\"{kind}\",\"time\":\"{time}\"}}";

    private static string Checkpoint(string time, string name, int? workItem) =>
        workItem is { } w
            ? $"{{\"kind\":\"checkpoint\",\"time\":\"{time}\",\"name\":{JsonSerializer.Serialize(name)},\"workItem\":{w}}}"
            : $"{{\"kind\":\"checkpoint\",\"time\":\"{time}\",\"name\":{JsonSerializer.Serialize(name)}}}";

    private static string Expected(string state, string since, string checkpoint, int worked, int target, int left) =>
        "{\n" +
        "  \"schemaVersion\": 1,\n" +
        "  \"date\": \"2026-10-27\",\n" +
        $"  \"state\": \"{state}\",\n" +
        $"  \"since\": {since},\n" +
        $"  \"checkpoint\": {checkpoint},\n" +
        $"  \"workedMinutes\": {worked},\n" +
        $"  \"targetMinutes\": {target},\n" +
        $"  \"leftMinutes\": {left}\n" +
        "}\n";

    // AC-1: out, on a day with no file.
    [Fact]
    public void Out_NoFile_ExactDocument()
    {
        (int exit, string output) = Shot(data.Path, "status", "--json");

        Assert.Equal(0, exit);
        Assert.Equal(Expected("out", "null", "null", 0, 480, 480), output);
    }

    // AC-1: out after leaving: since is the leaving.
    [Fact]
    public void Out_AfterLeaving_SinceIsTheLeaving()
    {
        WriteToday(Entry("arrival", "08:00"), Entry("leaving", "16:00"));

        (int exit, string output) = Shot(data.Path, "status", "--json");

        Assert.Equal(0, exit);
        Assert.Equal(Expected("out", "\"16:00\"", "null", 480, 480, 0), output);
    }

    // AC-1: working, counted up to now, left negative past the target.
    [Fact]
    public void Working_CountsToNow_LeftCanBeNegative()
    {
        WriteToday(Entry("arrival", "08:15"));

        (int exit, string output) = Shot(data.Path, "status", "--json");

        Assert.Equal(0, exit);
        Assert.Equal(Expected("working", "\"08:15\"", "null", 615, 480, -135), output);
    }

    // AC-1: on a break since its start, with the daily-target setting.
    [Fact]
    public void Break_SinceTheBreakStart_TargetFromSettings()
    {
        File.WriteAllText(Path.Combine(data.Path, "settings.json"), "{\"settings\":{\"daily-target\":420}}");
        WriteToday(Entry("arrival", "08:00"), Entry("breakStart", "12:00"));

        (int exit, string output) = Shot(data.Path, "status", "--json");

        Assert.Equal(0, exit);
        Assert.Equal(Expected("break", "\"12:00\"", "null", 240, 420, 180), output);
    }

    // AC-1: working with a running checkpoint.
    [Fact]
    public void Working_WithCheckpoint_NamesIt()
    {
        WriteToday(Entry("arrival", "08:00"), Checkpoint("08:30", "Mail", null), Checkpoint("09:00", "Design \"review\"", 1234));

        (int exit, string output) = Shot(data.Path, "status", "--json");

        string checkpoint = "{\n    \"name\": \"Design \\u0022review\\u0022\",\n    \"workItem\": 1234,\n    \"since\": \"09:00\"\n  }";
        Assert.Equal(0, exit);
        Assert.Equal(Expected("working", "\"08:00\"", checkpoint, 630, 480, -150), output);
        using JsonDocument document = JsonDocument.Parse(output);
        Assert.Equal("Design \"review\"", document.RootElement.GetProperty("checkpoint").GetProperty("name").GetString());
    }

    // AC-1: a checkpoint without a work item gives null; one ended by a break is not running after the break end.
    [Fact]
    public void Checkpoint_WithoutWorkItem_IsNull_EndedOne_IsLeftOut()
    {
        WriteToday(Entry("arrival", "08:00"), Checkpoint("09:00", "Mail", null));
        (_, string running) = Shot(data.Path, "status", "--json");
        string checkpoint = "{\n    \"name\": \"Mail\",\n    \"workItem\": null,\n    \"since\": \"09:00\"\n  }";
        Assert.Equal(Expected("working", "\"08:00\"", checkpoint, 630, 480, -150), running);

        WriteToday(Entry("arrival", "08:00"), Checkpoint("09:00", "Mail", null), Entry("breakStart", "12:00"),
            Entry("breakEnd", "12:30"));
        (_, string ended) = Shot(data.Path, "status", "--json");
        Assert.Equal(Expected("working", "\"12:30\"", "null", 600, 480, -120), ended);
    }

    // AC-1: an absence day.
    [Fact]
    public void Absent_StateAbsent_SinceNull()
    {
        File.WriteAllText(TodayFile, "{\"absence\":\"vacation\",\"entries\":[]}");

        (int exit, string output) = Shot(data.Path, "status", "--json");

        Assert.Equal(0, exit);
        Assert.Equal(Expected("absent", "null", "null", 0, 480, 480), output);
    }

    // AC-1: no markup or ANSI, exactly one document plus \n.
    [Fact]
    public void Output_IsOneDocument_NoAnsi()
    {
        WriteToday(Entry("arrival", "08:15"), Checkpoint("09:00", "[bold]x[/]", null));

        (int exit, string output) = Shot(data.Path, "status", "--json");

        Assert.Equal(0, exit);
        Assert.DoesNotContain("\u001b", output, StringComparison.Ordinal);
        Assert.EndsWith("}\n", output, StringComparison.Ordinal);
        using JsonDocument document = JsonDocument.Parse(output);
        Assert.Equal(
            ["schemaVersion", "date", "state", "since", "checkpoint", "workedMinutes", "targetMinutes", "leftMinutes"],
            document.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal("[bold]x[/]", document.RootElement.GetProperty("checkpoint").GetProperty("name").GetString());
    }

    // AC-2: reading only: no day file or other entry is left behind (times.lock is gone again after the run).
    [Fact]
    public void NoFile_CreatesNothing()
    {
        List<string> before = Directory.EnumerateFileSystemEntries(data.Path).ToList();

        Assert.Equal(0, Shot(data.Path, "status", "--json").Exit);

        Assert.Empty(before);
        Assert.Empty(Directory.EnumerateFileSystemEntries(data.Path));
    }

    // AC-2: an existing file stays byte for byte.
    [Fact]
    public void ExistingFile_IsNotChanged()
    {
        WriteToday(Entry("arrival", "08:15"));
        byte[] before = File.ReadAllBytes(TodayFile);

        Assert.Equal(0, Shot(data.Path, "status", "--json").Exit);

        Assert.Equal(before, File.ReadAllBytes(TodayFile));
        Assert.Equal([TodayFile], Directory.EnumerateFileSystemEntries(data.Path).ToList());
    }

    // AC-2: an unreadable day file is one red line and exit 1.
    [Fact]
    public void UnreadableFile_OneRedLine_ExitOne()
    {
        File.WriteAllText(TodayFile, "{ broken");

        (int exit, string output) = Shot(data.Path, "status", "--json");

        Assert.Equal(1, exit);
        Assert.Matches(@"^\x1b\[(31|91|38;5;9)m", output);
        Assert.Contains("status: today's day file cannot be read", output, StringComparison.Ordinal);
        Assert.Single(output.TrimEnd('\n').Split('\n'));
        Assert.DoesNotContain("{\n", output, StringComparison.Ordinal);
    }

    // AC-2: any other argument, or a second --json, is a red usage line and exit 1; nothing is written.
    [Theory]
    [InlineData("status", "--json", "--json")]
    [InlineData("status", "foo")]
    [InlineData("status", "--json", "foo")]
    [InlineData("status", "--JSON")]
    [InlineData("status", "--from", "2026-10-01")]
    public void OtherArguments_RedUsage_ExitOne(params string[] args)
    {
        (int exit, string output) = Shot(data.Path, args);

        Assert.Equal(1, exit);
        Assert.Matches(@"^\x1b\[(31|91|38;5;9)m", output);
        Assert.Contains("status: use status or status --json, nothing else.",
            Regex.Replace(output, @"\x1b\[[0-9;]*m", string.Empty), StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(data.Path));
    }

    // AC-2: plain status still prints the day view, not JSON.
    [Fact]
    public void PlainStatus_StillPrintsTheDayView()
    {
        WriteToday(Entry("arrival", "08:15"));

        (int exit, string output) = Shot(data.Path, "status");

        Assert.Equal(0, exit);
        Assert.Contains("08:15", output, StringComparison.Ordinal);
        Assert.DoesNotContain("schemaVersion", output, StringComparison.Ordinal);
    }

    // DayRecord.StateEntry and IsOnBreak, which the document uses.
    [Fact]
    public void StateEntry_IsTheEntryThatChangedTheState()
    {
        DayRecord day = new DayRecord([
            new DayEntry(EntryKind.Arrival, new TimeOnly(8, 0)),
            new DayEntry(EntryKind.Checkpoint, new TimeOnly(9, 0), "Mail", null),
            new DayEntry(EntryKind.BreakStart, new TimeOnly(12, 0)),
        ]);

        Assert.True(day.IsOnBreak);
        Assert.Equal(new TimeOnly(12, 0), day.StateEntry()?.Time);
        Assert.Null(DayRecord.Empty.StateEntry());
        Assert.False(DayRecord.Empty.IsOnBreak);
    }
}
