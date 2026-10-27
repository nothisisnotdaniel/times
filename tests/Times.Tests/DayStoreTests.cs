using System.Globalization;

namespace Times.Tests;

public class DayStoreTests
{
    private static readonly DateOnly Day = new(2026, 10, 27);

    // AC-11
    [Fact]
    public void PathFor_UsesInvariantFileName_UnderGermanCulture()
    {
        using var temp = new TempFolder(create: false);
        var previous = CultureInfo.CurrentCulture;
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
        using var temp = new TempFolder(create: false);

        Assert.False(new DayStore(temp.Path).Exists(Day));
        Assert.False(Directory.Exists(temp.Path));
    }

    // AC-11
    [Fact]
    public void Exists_IsFalse_WhenFileIsMissing()
    {
        using var temp = new TempFolder();

        Assert.False(new DayStore(temp.Path).Exists(Day));
        Assert.Empty(Directory.EnumerateFileSystemEntries(temp.Path));
    }

    // AC-11
    [Fact]
    public void Exists_IsTrue_AfterTheFileIsWritten()
    {
        using var temp = new TempFolder();
        var store = new DayStore(temp.Path);
        File.WriteAllText(Path.Combine(temp.Path, "2026-10-27.json"), "{}");

        Assert.True(store.Exists(Day));
        Assert.False(store.Exists(Day.AddDays(1)));
    }
}
