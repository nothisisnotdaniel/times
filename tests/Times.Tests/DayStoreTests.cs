using System.Globalization;

namespace Times.Tests;

public class DayStoreTests
{
    private static readonly DateOnly Day = new(2026, 10, 27);

    // AC-11
    [Fact]
    public void PathFor_UsesInvariantFileName_UnderGermanCulture()
    {
        using TempFolder temp = new TempFolder(create: false);
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal(Path.Combine(temp.Path, "2026-10-27.json"), new DayStore(temp.Path).PathFor(Day));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    // AC-11
    [Fact]
    public void Exists_IsFalse_WhenFolderIsMissing_AndDoesNotCreateIt()
    {
        using TempFolder temp = new TempFolder(create: false);

        Assert.False(new DayStore(temp.Path).Exists(Day));
        Assert.False(Directory.Exists(temp.Path));
    }

    // AC-11
    [Fact]
    public void Exists_IsFalse_WhenFileIsMissing()
    {
        using TempFolder temp = new TempFolder();

        Assert.False(new DayStore(temp.Path).Exists(Day));
        Assert.Empty(Directory.EnumerateFileSystemEntries(temp.Path));
    }

    // AC-11
    [Fact]
    public void Exists_IsTrue_AfterTheFileIsWritten()
    {
        using TempFolder temp = new TempFolder();
        DayStore store = new DayStore(temp.Path);
        File.WriteAllText(Path.Combine(temp.Path, "2026-10-27.json"), "{}");

        Assert.True(store.Exists(Day));
        Assert.False(store.Exists(Day.AddDays(1)));
    }

    // Snapshot of the working directory, to prove nothing was written relative to it.
    private static string[] WorkingDirectoryEntries() =>
        Directory.EnumerateFileSystemEntries(Directory.GetCurrentDirectory()).Order(StringComparer.Ordinal).ToArray();

    // AC-8
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Times")]
    [InlineData("./data")]
    [InlineData("data/Times")]
    public void IsValidFolder_RejectsEmptyWhitespaceAndRelative(string folder)
    {
        Assert.False(DayStore.IsValidFolder(folder));
    }

    // AC-8
    [Fact]
    public void IsValidFolder_AcceptsAbsolutePath()
    {
        using TempFolder temp = new TempFolder(create: false);

        Assert.True(DayStore.IsValidFolder(temp.Path));
    }

    // AC-8
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Times")]
    [InlineData("./data")]
    public void Save_WithInvalidFolder_ThrowsAndWritesNothing(string folder)
    {
        string[] before = WorkingDirectoryEntries();

        Assert.Throws<InvalidOperationException>(() =>
            new DayStore(folder).Save(Day, DayRecord.Empty.Add(Arrival(8, 15))));

        Assert.Equal(before, WorkingDirectoryEntries());
    }

    // 051 AC-1
    [Theory]
    [InlineData("", " ")]
    [InlineData("", "\t")]
    [InlineData("", "\u00a0")]
    [InlineData("", "\u2003")]
    [InlineData("", "\n")]
    [InlineData(" ", "")]
    [InlineData("\t", " ")]
    public void IsValidFolder_RejectsEdgeWhitespace(string before, string after)
    {
        using TempFolder temp = new TempFolder(create: false);

        Assert.False(DayStore.IsValidFolder(before + temp.Path + after));
    }

    // 051 AC-1
    [Fact]
    public void IsValidFolder_AcceptsASpaceInsideThePath()
    {
        using TempFolder temp = new TempFolder(create: false);

        Assert.True(DayStore.IsValidFolder(Path.Combine(temp.Path, "my times")));
    }

    // 051 AC-6
    [Theory]
    [InlineData("", " ")]
    [InlineData("", "\t")]
    [InlineData("", "\u00a0")]
    [InlineData(" ", "")]
    public void Save_WithEdgeWhitespace_ThrowsAndCreatesNothing(string before, string after)
    {
        using TempFolder temp = new TempFolder(create: false);
        string padded = before + temp.Path + after;

        Assert.Throws<InvalidOperationException>(() =>
            new DayStore(padded).Save(Day, DayRecord.Empty.Add(Arrival(8, 15))));

        Assert.False(Directory.Exists(padded));
        Assert.False(File.Exists(padded));
        Assert.False(Directory.Exists(temp.Path));
    }

    // 051 AC-6
    [Theory]
    [InlineData("", " ")]
    [InlineData("", "\t")]
    [InlineData(" ", "")]
    public void ProjectStoreSave_WithEdgeWhitespace_ThrowsAndCreatesNothing(string before, string after)
    {
        using TempFolder temp = new TempFolder(create: false);
        string padded = before + temp.Path + after;

        Assert.Throws<InvalidOperationException>(() =>
            new ProjectStore(padded).Save(ProjectList.Empty.Add(new Project(1, "first"))));

        Assert.False(Directory.Exists(padded));
        Assert.False(File.Exists(padded));
        Assert.False(Directory.Exists(temp.Path));
    }

    // AC-1, AC-4
    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        using TempFolder temp = new TempFolder();
        DayStore store = new DayStore(temp.Path);
        DayRecord record = new DayRecord([Arrival(8, 15), Leaving(12, 0), Arrival(13, 0), Leaving(17, 30)]);

        store.Save(Day, record);

        Assert.Equal(record, store.Load(Day));
        Assert.Equal(DayRecord.Empty, store.Load(Day.AddDays(1)));
    }

    // AC-1, AC-3 (017)
    [Fact]
    public void Save_WritesEntriesFormat_InTimeOrder_UnderGermanCulture()
    {
        using TempFolder temp = new TempFolder();
        DayStore store = new DayStore(temp.Path);
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            store.Save(Day, DayRecord.Empty.Add(Leaving(12, 0)).Add(Arrival(8, 15)));
            Assert.Equal(
                "{\"entries\":[{\"kind\":\"arrival\",\"time\":\"08:15\"},{\"kind\":\"leaving\",\"time\":\"12:00\"}]}",
                File.ReadAllText(store.PathFor(Day)));

            store.Save(Day, DayRecord.Empty.Add(Arrival(8, 5)));
            Assert.Equal("{\"entries\":[{\"kind\":\"arrival\",\"time\":\"08:05\"}]}", File.ReadAllText(store.PathFor(Day)));
            Assert.Equal(DayRecord.Empty.Add(Arrival(8, 5)), store.Load(Day));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    // AC-6 (017)
    [Theory]
    [InlineData("{\"arrival\":\"08:15\",\"leaving\":\"17:30\"}", "A08:15 L17:30")]
    [InlineData("{\"leaving\":\"17:30\",\"arrival\":\"08:15\"}", "A08:15 L17:30")]
    [InlineData("{\"arrival\":\"08:15\"}", "A08:15")]
    [InlineData("{\"leaving\":\"17:30\"}", "L17:30")]
    [InlineData("{\"arrival\":\"17:30\",\"leaving\":\"08:15\"}", "L08:15 A17:30")]
    [InlineData("{}", "")]
    [InlineData("{\"entries\":[]}", "")]
    [InlineData("{\"entries\":[{\"kind\":\"leaving\",\"time\":\"12:00\"},{\"kind\":\"arrival\",\"time\":\"08:00\"}]}", "A08:00 L12:00")]
    public void Load_ReadsLegacyAndNewFormat_InTimeOrder(string contents, string expected)
    {
        using TempFolder temp = new TempFolder();
        DayStore store = new DayStore(temp.Path);
        File.WriteAllText(store.PathFor(Day), contents);

        Assert.Equal(expected, Describe(store.Load(Day)));
    }

    // AC-7 (017)
    [Fact]
    public void Load_LegacyFile_DoesNotRewriteIt()
    {
        using TempFolder temp = new TempFolder();
        DayStore store = new DayStore(temp.Path);
        File.WriteAllText(store.PathFor(Day), "{\"arrival\":\"08:15\"}");
        DateTime written = File.GetLastWriteTimeUtc(store.PathFor(Day));

        store.Load(Day);

        Assert.Equal("{\"arrival\":\"08:15\"}", File.ReadAllText(store.PathFor(Day)));
        Assert.Equal(written, File.GetLastWriteTimeUtc(store.PathFor(Day)));
    }

    // AC-9
    [Fact]
    public void Load_WhenFolderIsMissing_GivesEmptyDay_AndCreatesNothing()
    {
        using TempFolder temp = new TempFolder(create: false);

        Assert.Equal(DayRecord.Empty, new DayStore(temp.Path).Load(Day));
        Assert.False(Directory.Exists(temp.Path));
    }

    // AC-9
    [Fact]
    public void Save_CreatesMissingFolder_AndLeavesNoTempFile()
    {
        using TempFolder temp = new TempFolder(create: false);
        DayStore store = new DayStore(temp.Path);

        store.Save(Day, DayRecord.Empty.Add(Leaving(17, 30)));
        store.Save(Day, DayRecord.Empty.Add(Leaving(17, 45)));

        Assert.Equal([store.PathFor(Day)], Directory.EnumerateFileSystemEntries(temp.Path));
        Assert.Equal([Leaving(17, 45)], store.Load(Day).Entries);
    }

    // AC-10, AC-8 (017)
    [Theory]
    [InlineData("{not json")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"arrival\":\"8:15\"}")]
    [InlineData("{\"arrival\":\"08:15:00\"}")]
    [InlineData("{\"arrival\":815}")]
    [InlineData("{\"entries\":[{\"kind\":\"lunch\",\"time\":\"08:15\"}]}")]
    [InlineData("{\"entries\":[{\"kind\":\"Arrival\",\"time\":\"08:15\"}]}")]
    [InlineData("{\"entries\":[{\"kind\":1,\"time\":\"08:15\"}]}")]
    [InlineData("{\"entries\":[{\"time\":\"08:15\"}]}")]
    [InlineData("{\"entries\":[{\"kind\":\"arrival\"}]}")]
    [InlineData("{\"entries\":[{\"kind\":\"arrival\",\"time\":\"8:15\"}]}")]
    [InlineData("{\"entries\":[{\"kind\":\"arrival\",\"time\":\"08:15:00\"}]}")]
    [InlineData("{\"entries\":[{\"kind\":\"arrival\",\"time\":null}]}")]
    [InlineData("{\"entries\":[{\"kind\":\"arrival\",\"time\":815}]}")]
    [InlineData("{\"entries\":[{\"kind\":\"arrival\",\"time\":\"08:15\",\"time\":\"09:00\"}]}")]
    [InlineData("{\"entries\":[\"08:15\"]}")]
    [InlineData("{\"entries\":{}}")]
    [InlineData("{\"entries\":\"08:15\"}")]
    [InlineData("{\"entries\":null}")]
    [InlineData("{\"entries\":[],\"entries\":[]}")]
    [InlineData("{\"entries\":[],\"arrival\":\"08:15\"}")]
    [InlineData("{\"leaving\":\"17:30\",\"entries\":[]}")]
    [InlineData("{\"entries\":[{\"kind\":\"arrival\",\"time\":\"08:15\"}]} []")]
    [InlineData("{\"entries\":[{\"kind\":\"checkpoint\",\"time\":\"09:00\",\"name\":\"abc\u202Edef\"}]}")] // AC-8 (056)
    [InlineData("{\"entries\":[{\"kind\":\"checkpoint\",\"time\":\"09:00\",\"name\":\"abc\u200Bdef\"}]}")]
    [InlineData("{\"entries\":[{\"kind\":\"checkpoint\",\"time\":\"09:00\",\"name\":\"abc\\u202edef\"}]}")]
    [InlineData("{\"entries\":[{\"kind\":\"checkpoint\",\"time\":\"09:00\",\"name\":\"abc\\ud800def\"}]}")]
    [InlineData("{\"entries\":[{\"kind\":\"checkpoint\",\"time\":\"09:00\",\"name\":\"abc\\udc00def\"}]}")]
    public void Load_InvalidFile_ThrowsInvalidData(string contents)
    {
        using TempFolder temp = new TempFolder();
        DayStore store = new DayStore(temp.Path);
        File.WriteAllText(store.PathFor(Day), contents);

        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => store.Load(Day));
        Assert.Equal("The file for 2026-10-27 is not a valid day file.", ex.Message);
    }

    // AC-10
    [Fact]
    public void Load_TooLargeFile_ThrowsInvalidData()
    {
        using TempFolder temp = new TempFolder();
        DayStore store = new DayStore(temp.Path);
        File.WriteAllText(store.PathFor(Day), new string(' ', (int)DayStore.MaxFileBytes + 1) + "{}");

        Assert.Throws<InvalidDataException>(() => store.Load(Day));
    }

    // AC-8
    [Theory]
    [InlineData("")]
    [InlineData("Times")]
    public void Load_WithInvalidFolder_Throws(string folder)
    {
        Assert.Throws<InvalidOperationException>(() => new DayStore(folder).Load(Day));
    }

    // AC-6 (036)
    [Fact]
    public void SaveThenLoad_RoundTripsBreaksAndCheckpoints()
    {
        using TempFolder temp = new TempFolder();
        DayStore store = new DayStore(temp.Path);
        DayRecord record = new DayRecord(
        [
            Arrival(8, 0),
            new DayEntry(EntryKind.BreakStart, new TimeOnly(12, 0)),
            new DayEntry(EntryKind.BreakEnd, new TimeOnly(12, 30)),
            new DayEntry(EntryKind.Checkpoint, new TimeOnly(9, 30), "Standup [x]", 5678),
            new DayEntry(EntryKind.Checkpoint, new TimeOnly(15, 0), "2024 review", null),
            Leaving(17, 0),
        ]);

        store.Save(Day, record);

        Assert.Equal(record, store.Load(Day));
        Assert.Equal(
            "{\"entries\":[{\"kind\":\"arrival\",\"time\":\"08:00\"}," +
            "{\"kind\":\"checkpoint\",\"time\":\"09:30\",\"name\":\"Standup [x]\",\"workItem\":5678}," +
            "{\"kind\":\"breakStart\",\"time\":\"12:00\"},{\"kind\":\"breakEnd\",\"time\":\"12:30\"}," +
            "{\"kind\":\"checkpoint\",\"time\":\"15:00\",\"name\":\"2024 review\"}," +
            "{\"kind\":\"leaving\",\"time\":\"17:00\"}]}",
            File.ReadAllText(store.PathFor(Day)));
    }

    // AC-7 (036)
    [Theory]
    [InlineData("{\"entries\":[{\"kind\":\"checkpoint\",\"time\":\"09:00\"}]}")]
    [InlineData("{\"entries\":[{\"kind\":\"checkpoint\",\"time\":\"09:00\",\"name\":\"A\",\"workItem\":0}]}")]
    [InlineData("{\"entries\":[{\"kind\":\"lunch\",\"time\":\"09:00\"}]}")]
    public void Load_InvalidCheckpointOrKind_ThrowsInvalidData(string contents)
    {
        using TempFolder temp = new TempFolder();
        DayStore store = new DayStore(temp.Path);
        File.WriteAllText(store.PathFor(Day), contents);

        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => store.Load(Day));
        Assert.Equal("The file for 2026-10-27 is not a valid day file.", ex.Message);
    }

    private static DayEntry Arrival(int hour, int minute) => new(EntryKind.Arrival, new TimeOnly(hour, minute));

    private static DayEntry Leaving(int hour, int minute) => new(EntryKind.Leaving, new TimeOnly(hour, minute));

    private static string Describe(DayRecord record) =>
        string.Join(' ', record.Entries.Select(e =>
            (e.Kind == EntryKind.Arrival ? "A" : "L") + e.Time.ToString("HH:mm", CultureInfo.InvariantCulture)));
}
