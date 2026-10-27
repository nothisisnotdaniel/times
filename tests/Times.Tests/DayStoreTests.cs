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
            new DayStore(folder).Save(Day, new DayRecord { Arrival = new TimeOnly(8, 15) }));

        Assert.Equal(before, WorkingDirectoryEntries());
    }

    // AC-1, AC-4
    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        using TempFolder temp = new TempFolder();
        DayStore store = new DayStore(temp.Path);
        DayRecord record = new DayRecord { Arrival = new TimeOnly(8, 15), Leaving = new TimeOnly(17, 30) };

        store.Save(Day, record);

        Assert.Equal(record, store.Load(Day));
        Assert.Equal(DayRecord.Empty, store.Load(Day.AddDays(1)));
    }

    // AC-1
    [Fact]
    public void Save_WritesInvariantHHmm_UnderGermanCulture_AndLeavesOutNulls()
    {
        using TempFolder temp = new TempFolder();
        DayStore store = new DayStore(temp.Path);
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            store.Save(Day, new DayRecord { Arrival = new TimeOnly(8, 15), Leaving = new TimeOnly(17, 30) });
            Assert.Equal("{\"arrival\":\"08:15\",\"leaving\":\"17:30\"}", File.ReadAllText(store.PathFor(Day)));

            store.Save(Day, new DayRecord { Arrival = new TimeOnly(8, 5) });
            Assert.Equal("{\"arrival\":\"08:05\"}", File.ReadAllText(store.PathFor(Day)));
            Assert.Equal(new DayRecord { Arrival = new TimeOnly(8, 5) }, store.Load(Day));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
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

        store.Save(Day, new DayRecord { Leaving = new TimeOnly(17, 30) });
        store.Save(Day, new DayRecord { Leaving = new TimeOnly(17, 45) });

        Assert.Equal([store.PathFor(Day)], Directory.EnumerateFileSystemEntries(temp.Path));
        Assert.Equal(new TimeOnly(17, 45), store.Load(Day).Leaving);
    }

    // AC-10
    [Theory]
    [InlineData("{not json")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"arrival\":\"8:15\"}")]
    [InlineData("{\"arrival\":\"08:15:00\"}")]
    [InlineData("{\"arrival\":815}")]
    public void Load_InvalidFile_ThrowsInvalidData(string contents)
    {
        using TempFolder temp = new TempFolder();
        DayStore store = new DayStore(temp.Path);
        File.WriteAllText(store.PathFor(Day), contents);

        Assert.Throws<InvalidDataException>(() => store.Load(Day));
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
}
