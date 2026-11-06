using System.Text;

namespace Times.Tests;

public sealed class OldDataImportTests : IDisposable
{
    private static readonly DateOnly Day = new DateOnly(2026, 10, 28);

    private readonly TempFolder folder = new();

    public void Dispose() => folder.Dispose();

    /// <summary>The sample from <c>import-samples/</c>, copied to the test output by the test project.</summary>
    internal static string SamplePath => Path.Combine(AppContext.BaseDirectory, "import-samples", "2026-10-28.json");

    internal static byte[] Sample() => File.ReadAllBytes(SamplePath);

    private static ImportParse Parse(string json) => OldDataImport.Parse(Day, Encoding.UTF8.GetBytes(json));

    private static string Error(string json) => Parse(json).Error ?? "(no error)";

    /// <summary>A file with one arrival, then <paramref name="entry"/> as entry 2.</summary>
    private static string WithEntry(string entry) =>
        "{\"Recordings\":[{\"Time\":\"08:00:00\",\"Type\":0,\"Description\":null,\"Booked\":null,\"UsedTime\":null}," + entry + "]}";

    private static string Checkpoint(string time, string title, string booked = "null", string used = "null") =>
        $"{{\"Time\":\"{time}\",\"Type\":4,\"Description\":\"{title}\",\"Booked\":{booked},\"UsedTime\":{used}}}";

    // AC-3. The spec's counts (18 checkpoints, 11 booked) do not add up to its 24 entries; the sample itself
    // holds 16 checkpoints, 9 of them booked, and those are the numbers checked here.
    [Fact]
    public void Sample_GivesTheDayWithAllEntries()
    {
        ImportParse parsed = OldDataImport.Parse(Day, Sample());

        Assert.Null(parsed.Error);
        DayRecord record = parsed.Record!;
        Assert.Equal(24, record.Entries.Count);
        Assert.Equal(["07:22", "19:06"], Times(record, EntryKind.Arrival));
        Assert.Equal(["16:56", "19:52"], Times(record, EntryKind.Leaving));
        Assert.Equal(["12:07", "12:34"], Times(record, EntryKind.BreakStart));
        Assert.Equal(["12:15", "12:55"], Times(record, EntryKind.BreakEnd));
        List<DayEntry> checkpoints = record.Entries.Where(e => e.Kind == EntryKind.Checkpoint).ToList();
        Assert.Equal(16, checkpoints.Count);
        Assert.Equal("Post & Rückfragen", checkpoints[0].Name);
        Assert.Equal(9, checkpoints.Count(c => c.Booked));
        Assert.All(checkpoints, c => Assert.Null(c.WorkItem));
        Assert.Equal(6, checkpoints.Count(c => c.Name == "Datenpumpe"));
        Assert.True(checkpoints.Where(c => c.Name == "Datenpumpe").All(c => c.Booked));
    }

    private static string[] Times(DayRecord record, EntryKind kind) =>
        record.Entries.Where(e => e.Kind == kind).Select(e => e.Time.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture)).ToArray();

    // AC-4
    [Fact]
    public void Sample_KeepsTheFileOrderForEqualTimes()
    {
        IReadOnlyList<DayEntry> entries = OldDataImport.Parse(Day, Sample()).Record!.Entries;

        Assert.Equal(EntryKind.Arrival, entries[0].Kind);
        Assert.Equal("Post & Rückfragen", entries[1].Name);
        int breakEnd = entries.ToList().FindIndex(e => e.Kind == EntryKind.BreakEnd);
        Assert.Equal(new TimeOnly(12, 15), entries[breakEnd].Time);
        Assert.Equal("Datenpumpe", entries[breakEnd + 1].Name);
        Assert.Equal(new TimeOnly(12, 15), entries[breakEnd + 1].Time);
    }

    // AC-4
    [Fact]
    public void OutOfOrderTimes_AreSortedByDayRecordsRule()
    {
        string json = "{\"Recordings\":[" + Checkpoint("09:00:00", "Later") + ","
            + "{\"Time\":\"08:00:00\",\"Type\":0}," + Checkpoint("08:00:00", "Same time") + "]}";

        IReadOnlyList<DayEntry> entries = Parse(json).Record!.Entries;

        Assert.Equal(EntryKind.Arrival, entries[0].Kind);
        Assert.Equal("Same time", entries[1].Name);
        Assert.Equal("Later", entries[2].Name);
    }

    // AC-4: a checkpoint before an arrival at the same time moves after it, as typed entries do.
    [Fact]
    public void CheckpointBeforeArrivalAtTheSameTime_MovesAfterIt()
    {
        string json = "{\"Recordings\":[" + Checkpoint("08:00:00", "Early") + ",{\"Time\":\"08:00:00\",\"Type\":0}]}";

        IReadOnlyList<DayEntry> entries = Parse(json).Record!.Entries;

        Assert.Equal(EntryKind.Arrival, entries[0].Kind);
        Assert.Equal("Early", entries[1].Name);
    }

    // AC-5
    [Theory]
    [InlineData("{not json", OldDataImport.NotJson)]
    [InlineData("{\"Recordings\":[],}", OldDataImport.NotJson)]
    [InlineData("// c\n{\"Recordings\":[]}", OldDataImport.NotJson)]
    [InlineData("{\"Recordings\":[],\"Recordings\":[]}", OldDataImport.NotJson)]
    [InlineData("{\"Recordings\":[{\"Time\":\"08:00:00\",\"Time\":\"08:00:00\",\"Type\":0}]}", OldDataImport.NotJson)]
    [InlineData("{\"Recordings\":[{\"Time\":\"08:00:00\",\"Type\":0,\"X\":\"\\ud800\"}]}", OldDataImport.BrokenCharacters)]
    [InlineData("{\"\\udc00\":1,\"Recordings\":[{\"Time\":\"08:00:00\",\"Type\":0}]}", OldDataImport.BrokenCharacters)]
    [InlineData("[]", OldDataImport.NoRecordings)]
    [InlineData("{}", OldDataImport.NoRecordings)]
    [InlineData("{\"recordings\":[]}", OldDataImport.NoRecordings)]
    [InlineData("{\"Recordings\":{}}", OldDataImport.NoRecordings)]
    [InlineData("{\"Recordings\":null}", OldDataImport.NoRecordings)]
    [InlineData("{\"Recordings\":[]}", OldDataImport.EmptyRecordings)]
    public void WholeFileErrors(string json, string reason)
    {
        ImportParse parsed = Parse(json);

        Assert.Null(parsed.Record);
        Assert.Empty(parsed.Warnings);
        Assert.Equal(reason, parsed.Error);
    }

    // AC-5
    [Fact]
    public void InvalidUtf8_IsAnError()
    {
        byte[] bytes = Encoding.UTF8.GetBytes(WithEntry(Checkpoint("08:00:00", "XX")));
        int at = Array.IndexOf(bytes, (byte)'X');
        bytes[at] = 0xC3;
        bytes[at + 1] = 0x28;

        Assert.Equal(OldDataImport.NotJson, OldDataImport.Parse(Day, bytes).Error);
    }

    // AC-5: a byte order mark is still UTF-8.
    [Fact]
    public void ByteOrderMark_IsAccepted()
    {
        byte[] bytes = [0xEF, 0xBB, 0xBF, .. Sample()];

        Assert.Equal(24, OldDataImport.Parse(Day, bytes).Record!.Entries.Count);
    }

    // AC-5
    [Theory]
    [InlineData("1", "entry 2 is not an object")]
    [InlineData("null", "entry 2 is not an object")]
    [InlineData("{\"Type\":1}", "entry 2 has no Time as HH:mm:ss")]
    [InlineData("{\"Time\":null,\"Type\":1}", "entry 2 has no Time as HH:mm:ss")]
    [InlineData("{\"Time\":\"09:00\",\"Type\":1}", "entry 2 has no Time as HH:mm:ss")]
    [InlineData("{\"Time\":\"9:00:00\",\"Type\":1}", "entry 2 has no Time as HH:mm:ss")]
    [InlineData("{\"Time\":\" 09:00:00\",\"Type\":1}", "entry 2 has no Time as HH:mm:ss")]
    [InlineData("{\"Time\":\"24:00:00\",\"Type\":1}", "entry 2 has no Time as HH:mm:ss")]
    [InlineData("{\"Time\":\"０9:00:00\",\"Type\":1}", "entry 2 has no Time as HH:mm:ss")]
    [InlineData("{\"Time\":32400,\"Type\":1}", "entry 2 has no Time as HH:mm:ss")]
    [InlineData("{\"Time\":\"09:00:30\",\"Type\":1}", "entry 2 has a Time with seconds")]
    [InlineData("{\"Time\":\"09:00:00\"}", "entry 2 has no Type from 0 to 4")]
    [InlineData("{\"Time\":\"09:00:00\",\"Type\":\"1\"}", "entry 2 has no Type from 0 to 4")]
    [InlineData("{\"Time\":\"09:00:00\",\"Type\":1.5}", "entry 2 has no Type from 0 to 4")]
    [InlineData("{\"Time\":\"09:00:00\",\"Type\":5}", "entry 2 has no Type from 0 to 4")]
    [InlineData("{\"Time\":\"09:00:00\",\"Type\":-1}", "entry 2 has no Type from 0 to 4")]
    [InlineData("{\"Time\":\"09:00:00\",\"Type\":4}", "entry 2 is a checkpoint without a valid Description")]
    [InlineData("{\"Time\":\"09:00:00\",\"Type\":4,\"Description\":null}", "entry 2 is a checkpoint without a valid Description")]
    [InlineData("{\"Time\":\"09:00:00\",\"Type\":4,\"Description\":7}", "entry 2 is a checkpoint without a valid Description")]
    [InlineData("{\"Time\":\"09:00:00\",\"Type\":4,\"Description\":\"   \"}", "entry 2 is a checkpoint without a valid Description")]
    [InlineData("{\"Time\":\"09:00:00\",\"Type\":4,\"Description\":\"a\\u0007b\"}", "entry 2 is a checkpoint without a valid Description")]
    [InlineData("{\"Time\":\"09:00:00\",\"Type\":4,\"Description\":\"a\\u200Bb\"}", "entry 2 is a checkpoint without a valid Description")]
    [InlineData("{\"Time\":\"09:00:00\",\"Type\":4,\"Description\":\"a\\u202Eb\"}", "entry 2 is a checkpoint without a valid Description")]
    [InlineData("{\"Time\":\"09:00:00\",\"Type\":1,\"Description\":\"Leaving\"}", "entry 2 has a Description but is not a checkpoint")]
    [InlineData("{\"Time\":\"09:00:00\",\"Type\":1,\"Description\":\"\"}", "entry 2 has a Description but is not a checkpoint")]
    [InlineData("{\"Time\":\"09:00:00\",\"Type\":4,\"Description\":\"A\",\"Booked\":1}", "entry 2 has a Booked that is not true, false or null")]
    [InlineData("{\"Time\":\"09:00:00\",\"Type\":4,\"Description\":\"A\",\"Booked\":\"true\"}", "entry 2 has a Booked that is not true, false or null")]
    [InlineData("{\"Time\":\"09:00:00\",\"Type\":1,\"Booked\":true}", "entry 2 is booked but is not a checkpoint")]
    public void EntryErrors_NameTheEntryNumber(string entry, string reason)
    {
        ImportParse parsed = Parse(WithEntry(entry));

        Assert.Null(parsed.Record);
        Assert.Equal(reason, parsed.Error);
    }

    // AC-5
    [Fact]
    public void TitleOver200Characters_IsAnError_200IsFine()
    {
        Assert.Equal("entry 2 is a checkpoint without a valid Description", Error(WithEntry(Checkpoint("09:00:00", new string('a', 201)))));
        Assert.Null(Parse(WithEntry(Checkpoint("09:00:00", new string('a', 200)))).Error);
    }

    // AC-5: titles are trimmed before the checks, and stored trimmed.
    [Fact]
    public void Title_IsTrimmed()
    {
        DayRecord record = Parse(WithEntry(Checkpoint("09:00:00", "  Mail  "))).Record!;

        Assert.Equal("Mail", record.Entries[1].Name);
    }

    // AC-5
    [Fact]
    public void BookedFalseOrMissing_IsNotBooked_AndFalseOnAnotherTypeIsFine()
    {
        DayRecord record = Parse("{\"Recordings\":[{\"Time\":\"08:00:00\",\"Type\":0,\"Booked\":false},"
            + Checkpoint("09:00:00", "A", "false") + ",{\"Time\":\"09:30:00\",\"Type\":4,\"Description\":\"B\"}]}").Record!;

        Assert.All(record.Entries, e => Assert.False(e.Booked));
    }

    // AC-5
    [Fact]
    public void UnknownProperties_AreIgnored()
    {
        ImportParse parsed = Parse("{\"Version\":3,\"Recordings\":[{\"Time\":\"08:00:00\",\"Type\":0,\"Note\":{\"x\":[1]}}]}");

        Assert.Null(parsed.Error);
        Assert.Single(parsed.Record!.Entries);
    }

    // AC-5
    [Fact]
    public void ResultingDayFileOver64KiB_IsAnError()
    {
        // Raw UTF-8 euro signs take 3 bytes here and 6 (\u20AC) in a day file.
        string title = new string('€', 200);
        string json = "{\"Recordings\":[" + string.Join(',', Enumerable.Range(0, 90).Select(_ => Checkpoint("09:00:00", title))) + "]}";
        Assert.True(Encoding.UTF8.GetByteCount(json) <= DayStore.MaxFileBytes);

        Assert.Equal(OldDataImport.TooLargeDay, Error(json));
    }

    // AC-6
    [Fact]
    public void Sample_GivesNoWarning()
    {
        Assert.Empty(OldDataImport.Parse(Day, Sample()).Warnings);
    }

    // AC-6
    [Fact]
    public void ChangedUsedTimeInTheSample_GivesExactlyOneWarning()
    {
        string json = Encoding.UTF8.GetString(Sample()).Replace("\"UsedTime\":\"00:36:00\"", "\"UsedTime\":\"00:40:00\"", StringComparison.Ordinal);

        ImportParse parsed = OldDataImport.Parse(Day, Encoding.UTF8.GetBytes(json));

        ImportWarning warning = Assert.Single(parsed.Warnings);
        Assert.Equal(new TimeOnly(7, 24), warning.Time);
        Assert.Equal("KLM", warning.Title);
        Assert.Equal(TimeSpan.FromMinutes(40), warning.Stated);
        Assert.Equal(TimeSpan.FromMinutes(36), warning.Actual);
        Assert.Equal(24, parsed.Record!.Entries.Count);
    }

    // AC-6
    [Theory]
    [InlineData("\"0:10:00\"")]
    [InlineData("\"soon\"")]
    [InlineData("600")]
    [InlineData("true")]
    public void UsedTimeThatIsNotATime_Warns(string used)
    {
        ImportParse parsed = Parse(WithEntry(Checkpoint("09:00:00", "A", used: used) + ",{\"Time\":\"09:10:00\",\"Type\":1}"));

        ImportWarning warning = Assert.Single(parsed.Warnings);
        Assert.Null(warning.Stated);
        Assert.NotNull(parsed.Record);
    }

    // AC-6: no next entry, or no UsedTime, gives no warning; UsedTime is never imported.
    [Fact]
    public void LastCheckpointOrNullUsedTime_GivesNoWarning()
    {
        ImportParse parsed = Parse(WithEntry(Checkpoint("09:00:00", "A") + "," + Checkpoint("09:10:00", "B", used: "\"05:00:00\"")));

        Assert.Empty(parsed.Warnings);
        Assert.DoesNotContain("05:00", parsed.Record!.ToJson(), StringComparison.Ordinal);
    }

    // AC-6
    [Fact]
    public void Duration_IsHoursAndMinutes()
    {
        Assert.Equal("0:02", OldDataImport.Duration(TimeSpan.FromMinutes(2)));
        Assert.Equal("1:55", OldDataImport.Duration(TimeSpan.FromMinutes(115)));
    }

    // AC-2
    [Fact]
    public void Scan_ListsCandidatesByDate_AndEveryOtherNameInOrdinalOrder()
    {
        File.WriteAllText(Path.Combine(folder.Path, "2026-10-29.json"), "{}");
        File.WriteAllText(Path.Combine(folder.Path, "2026-10-28.json"), "{}");
        File.WriteAllText(Path.Combine(folder.Path, "notes.txt"), "x");
        File.WriteAllText(Path.Combine(folder.Path, "2026-02-30.json"), "{}");
        File.WriteAllText(Path.Combine(folder.Path, "2026-10-27.JSON"), "{}");
        Directory.CreateDirectory(Path.Combine(folder.Path, "Archive"));
        Directory.CreateDirectory(Path.Combine(folder.Path, "sub"));
        File.WriteAllText(Path.Combine(folder.Path, "sub", "2026-10-30.json"), "{}");

        ImportScan scan = OldDataImport.Scan(folder.Path);

        Assert.Equal([new DateOnly(2026, 10, 28), new DateOnly(2026, 10, 29)], scan.Candidates.Select(c => c.Day));
        Assert.Equal(["2026-10-28.json", "2026-10-29.json"], scan.Candidates.Select(c => c.Name));
        Assert.Equal(["2026-02-30.json", "2026-10-27.JSON", "Archive", "notes.txt", "sub"], scan.Skipped);
    }

    // AC-3
    [Fact]
    public void Read_TooLargeOrNotARegularFile_GivesAFixedReason()
    {
        string large = Path.Combine(folder.Path, "2026-10-28.json");
        File.WriteAllBytes(large, new byte[DayStore.MaxFileBytes + 1]);
        string directory = Path.Combine(folder.Path, "2026-10-29.json");
        Directory.CreateDirectory(directory);

        Assert.Equal("is larger than 64 KiB", OldDataImport.Read(large).Error);
        Assert.Equal("is not a regular file", OldDataImport.Read(directory).Error);
        Assert.Equal(Sample(), OldDataImport.Read(SamplePath).Bytes);
    }

    // AC-3: a FIFO, or a link to one, is refused without blocking (review-security nit 3).
    [Fact]
    public void Read_FifoOrLinkToFifo_IsNotARegularFile_AndDoesNotBlock()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string fifo = Path.Combine(folder.Path, "2026-10-28.json");
        SpecialFiles.MakeFifo(fifo);
        string link = Path.Combine(folder.Path, "2026-10-29.json");
        File.CreateSymbolicLink(link, fifo);
        ImportRead? direct = null;
        ImportRead? linked = null;

        Exception? thrown = SpecialFiles.RunWithin(TimeSpan.FromSeconds(10), () =>
        {
            direct = OldDataImport.Read(fifo);
            linked = OldDataImport.Read(link);
        });

        Assert.Null(thrown);
        Assert.Equal("is not a regular file", direct!.Error);
        Assert.Equal("is not a regular file", linked!.Error);
        Assert.True(SpecialFiles.IsFifo(fifo));
    }
}
