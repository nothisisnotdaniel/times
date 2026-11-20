using System.Globalization;

namespace Times.Tests;

public sealed class HomeOfficeCountTests : IDisposable
{
    private readonly TempFolder data = new();

    public void Dispose() => data.Dispose();

    private DayStore Store => new DayStore(data.Path);

    private static DateOnly D(string date) => DateOnly.Parse(date, CultureInfo.InvariantCulture);

    private string DayFile(string date) => Path.Combine(data.Path, date + ".json");

    private void SaveMark(string date, Workplace workplace) =>
        Store.Save(D(date), new DayRecord([new DayEntry(EntryKind.Arrival, new TimeOnly(8, 0))], null, [], workplace));

    // Names and bytes of every entry in the data folder (directories by name only).
    private SortedDictionary<string, string> Snapshot()
    {
        SortedDictionary<string, string> snapshot = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (string path in Directory.EnumerateFileSystemEntries(data.Path))
        {
            snapshot.Add(Path.GetFileName(path), Directory.Exists(path) ? "<dir>" : Convert.ToBase64String(File.ReadAllBytes(path)));
        }

        return snapshot;
    }

    // AC-6
    [Fact]
    public void Calculate_CountsOnlyHomeMarksInTheRange_SkipsUnreadable_AndWritesNothing()
    {
        SaveMark("2026-10-31", Workplace.Home);
        SaveMark("2026-11-02", Workplace.Home);
        SaveMark("2026-11-03", Workplace.Office);
        SaveMark("2026-11-30", Workplace.Home);
        SaveMark("2026-12-01", Workplace.Home);
        Store.Save(D("2026-11-04"), new DayRecord([new DayEntry(EntryKind.Arrival, new TimeOnly(8, 0))]));
        Store.Save(D("2026-11-05"), new DayRecord([], AbsenceKind.Vacation));
        File.WriteAllText(DayFile("2026-11-06"), "{\"workplace\":\"Home\",\"entries\":[]}");
        File.WriteAllText(DayFile("2026-11-09"), "not json");
        SortedDictionary<string, string> before = Snapshot();

        int count = HomeOfficeCount.Calculate(Store, D("2026-11-01"), D("2026-11-30"));

        Assert.Equal(2, count);
        Assert.Equal(before, Snapshot());
    }

    // AC-6
    [Fact]
    public void Calculate_RangeOfOneDay_IsInclusive()
    {
        SaveMark("2026-11-20", Workplace.Home);

        Assert.Equal(1, HomeOfficeCount.Calculate(Store, D("2026-11-20"), D("2026-11-20")));
        Assert.Equal(0, HomeOfficeCount.Calculate(Store, D("2026-11-21"), D("2026-11-30")));
    }

    // AC-6
    [Fact]
    public void Calculate_MissingFolder_CountsNothing_AndCreatesNothing()
    {
        string missing = Path.Combine(data.Path, "missing");

        Assert.Equal(0, HomeOfficeCount.Calculate(new DayStore(missing), D("2026-01-01"), D("2026-12-31")));
        Assert.False(Directory.Exists(missing));
    }
}
