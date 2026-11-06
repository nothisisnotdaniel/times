using System.Globalization;

namespace Times.Tests;

public sealed class VacationAllowanceTests : IDisposable
{
    private static readonly DateOnly Today = new DateOnly(2026, 6, 15);

    private readonly TempFolder data = new();

    public void Dispose() => data.Dispose();

    private DayStore Store => new DayStore(data.Path);

    private static DateOnly D(string date) => DateOnly.Parse(date, CultureInfo.InvariantCulture);

    private string DayFile(string date) => Path.Combine(data.Path, date + ".json");

    private void SaveAbsence(string date, AbsenceKind kind) => Store.Save(D(date), new DayRecord([], kind));

    private void SaveBooked(string date) =>
        Store.Save(D(date), new DayRecord([new DayEntry(EntryKind.Arrival, new TimeOnly(8, 0)), new DayEntry(EntryKind.Leaving, new TimeOnly(16, 0))]));

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

    // AC-2
    [Fact]
    public void Calculate_CountsOnlyVacationDaysInTheYear_TakenUpToTodayAndPlannedAfter()
    {
        SaveAbsence("2025-12-31", AbsenceKind.Vacation);
        SaveAbsence("2026-01-02", AbsenceKind.Vacation);
        SaveAbsence("2026-06-15", AbsenceKind.Vacation);
        SaveAbsence("2026-08-03", AbsenceKind.Vacation);
        SaveAbsence("2026-03-10", AbsenceKind.Sick);
        SaveAbsence("2026-10-03", AbsenceKind.PublicHoliday);
        SaveBooked("2026-05-01");
        SaveAbsence("2027-01-04", AbsenceKind.Vacation);

        VacationAllowance result = VacationAllowance.Calculate(Store, 2026, Today);

        Assert.Equal(2, result.Taken);
        Assert.Equal(1, result.Planned);
        Assert.Equal(0, result.SkippedFiles);
    }

    // AC-2
    [Fact]
    public void Calculate_EmptyOrMissingFolder_CountsNothing()
    {
        VacationAllowance empty = VacationAllowance.Calculate(Store, 2026, Today);
        VacationAllowance missing = VacationAllowance.Calculate(new DayStore(Path.Combine(data.Path, "missing")), 2026, Today);

        Assert.Equal((0, 0, 0), (empty.Taken, empty.Planned, empty.SkippedFiles));
        Assert.Equal((0, 0, 0), (missing.Taken, missing.Planned, missing.SkippedFiles));
        Assert.False(Directory.Exists(Path.Combine(data.Path, "missing")));
    }

    // AC-2: today is passed in, so the same files give other counts on another day.
    [Fact]
    public void Calculate_TodayDecidesBetweenTakenAndPlanned()
    {
        SaveAbsence("2026-01-02", AbsenceKind.Vacation);
        SaveAbsence("2026-08-03", AbsenceKind.Vacation);

        VacationAllowance early = VacationAllowance.Calculate(Store, 2026, D("2026-01-01"));
        VacationAllowance late = VacationAllowance.Calculate(Store, 2026, D("2026-12-31"));

        Assert.Equal((0, 2), (early.Taken, early.Planned));
        Assert.Equal((2, 0), (late.Taken, late.Planned));
    }

    // AC-3
    [Fact]
    public void Calculate_SkipsAndCountsUnreadableFilesInTheYear_AndOtherDaysStillCount()
    {
        SaveAbsence("2026-01-02", AbsenceKind.Vacation);
        SaveAbsence("2026-08-03", AbsenceKind.Vacation);
        File.WriteAllText(DayFile("2026-02-02"), "{not json");
        Directory.CreateDirectory(DayFile("2026-02-03"));

        VacationAllowance result = VacationAllowance.Calculate(Store, 2026, Today);

        Assert.Equal(1, result.Taken);
        Assert.Equal(1, result.Planned);
        Assert.Equal(2, result.SkippedFiles);
    }

    // AC-3: broken files in other years are never loaded, so they are not counted as skipped.
    [Fact]
    public void Calculate_NeverLoadsFilesOutsideTheYear()
    {
        SaveAbsence("2026-01-02", AbsenceKind.Vacation);
        File.WriteAllText(DayFile("2025-12-31"), "{not json");
        File.WriteAllText(DayFile("2027-01-01"), "{not json");
        Directory.CreateDirectory(DayFile("2025-06-01"));

        VacationAllowance result = VacationAllowance.Calculate(Store, 2026, Today);

        Assert.Equal(1, result.Taken);
        Assert.Equal(0, result.SkippedFiles);
    }

    // AC-3
    [Fact]
    public void Calculate_NeverCreatesChangesOrDeletesAFile()
    {
        SaveAbsence("2026-01-02", AbsenceKind.Vacation);
        SaveAbsence("2026-08-03", AbsenceKind.Vacation);
        SaveAbsence("2026-03-10", AbsenceKind.Sick);
        SaveBooked("2026-05-01");
        File.WriteAllText(DayFile("2026-02-02"), "{not json");
        Directory.CreateDirectory(DayFile("2026-02-03"));
        File.WriteAllText(Path.Combine(data.Path, "notes.txt"), "keep");
        SortedDictionary<string, string> before = Snapshot();

        VacationAllowance.Calculate(Store, 2026, Today);

        Assert.Equal(before, Snapshot());
    }
}
