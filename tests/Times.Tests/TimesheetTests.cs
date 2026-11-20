using System.Globalization;

namespace Times.Tests;

public sealed class TimesheetTests : IDisposable
{
    private static readonly TimeSpan EightHours = TimeSpan.FromHours(8);

    private readonly TempFolder data = new();

    public void Dispose() => data.Dispose();

    private DayStore Store => new DayStore(data.Path);

    private static string? NoHoliday(DateOnly day) => null;

    private static DayRecord Worked(string arrival, string breakStart, string breakEnd, string? leaving)
    {
        List<DayEntry> entries =
        [
            new DayEntry(EntryKind.Arrival, TimeOnly.ParseExact(arrival, "HH:mm", CultureInfo.InvariantCulture)),
            new DayEntry(EntryKind.BreakStart, TimeOnly.ParseExact(breakStart, "HH:mm", CultureInfo.InvariantCulture)),
            new DayEntry(EntryKind.BreakEnd, TimeOnly.ParseExact(breakEnd, "HH:mm", CultureInfo.InvariantCulture)),
        ];
        if (leaving is not null)
        {
            entries.Add(new DayEntry(EntryKind.Leaving, TimeOnly.ParseExact(leaving, "HH:mm", CultureInfo.InvariantCulture)));
        }

        return new DayRecord(entries);
    }

    // October 2026: a full day, a vacation day, a day with a correction, an open day and a broken file,
    // plus a September day that only the end-of-month balance sees.
    private void WriteOctober()
    {
        DayStore store = Store;
        store.Save(new DateOnly(2026, 9, 30), Worked("08:00", "12:00", "12:30", "17:30"));
        store.Save(new DateOnly(2026, 10, 1), Worked("08:00", "12:00", "12:30", "17:00"));
        store.Save(new DateOnly(2026, 10, 2), new DayRecord([], AbsenceKind.Vacation));
        store.Save(new DateOnly(2026, 10, 5), new DayRecord(
            Worked("09:00", "12:00", "13:00", "16:00").Entries, null, [new BalanceCorrection(45, "Doctor")]));
        store.Save(new DateOnly(2026, 10, 6), Worked("08:15", "11:00", "11:15", null));
        File.WriteAllText(store.PathFor(new DateOnly(2026, 10, 7)), "{not json");
    }

    // AC-3
    [Theory]
    [InlineData(2028, 2, 29)]
    [InlineData(2026, 2, 28)]
    [InlineData(2026, 10, 31)]
    [InlineData(2026, 11, 30)]
    public void Calculate_GivesOneDayPerCalendarDay_InOrder(int year, int month, int count)
    {
        Timesheet sheet = Timesheet.Calculate(Store, year, month, EightHours, NoHoliday);

        Assert.Equal(count, sheet.Days.Count);
        for (int i = 0; i < count; i++)
        {
            Assert.Equal(new DateOnly(year, month, i + 1), sheet.Days[i].Date);
        }
    }

    // AC-3
    [Fact]
    public void Calculate_LeapFebruary_HasItsDays()
    {
        Store.Save(new DateOnly(2028, 2, 29), Worked("07:30", "12:00", "12:45", "16:00"));

        Timesheet sheet = Timesheet.Calculate(Store, 2028, 2, EightHours, NoHoliday);

        TimesheetDay last = sheet.Days[^1];
        Assert.Equal(new DateOnly(2028, 2, 29), last.Date);
        Assert.Equal(new TimeOnly(7, 30), last.Arrival);
        Assert.Equal(new TimeOnly(16, 0), last.Leaving);
        Assert.Equal(TimeSpan.FromMinutes(45), last.BreakTime);
        Assert.Equal(new TimeSpan(7, 45, 0), last.WorkTime);
        Assert.Equal(new TimeSpan(7, 45, 0), sheet.MonthWork);
    }

    // AC-3, AC-4
    [Fact]
    public void Calculate_FillsEachDay()
    {
        WriteOctober();

        Timesheet sheet = Timesheet.Calculate(Store, 2026, 10, EightHours, NoHoliday);

        TimesheetDay full = sheet.Days[0];
        Assert.Equal(new TimeOnly(8, 0), full.Arrival);
        Assert.Equal(new TimeOnly(17, 0), full.Leaving);
        Assert.Equal(TimeSpan.FromMinutes(30), full.BreakTime);
        Assert.Equal(new TimeSpan(8, 30, 0), full.WorkTime);

        TimesheetDay vacation = sheet.Days[1];
        Assert.Equal(AbsenceKind.Vacation, vacation.Absence);
        Assert.Null(vacation.WorkTime);
        Assert.Null(vacation.Arrival);

        TimesheetDay empty = sheet.Days[2];
        Assert.True(empty.IsEmpty);
        Assert.Null(empty.Arrival);
        Assert.Null(empty.BreakTime);

        TimesheetDay corrected = sheet.Days[4];
        Assert.Equal(TimeSpan.FromMinutes(45), corrected.CorrectionTotal);
        Assert.Equal(["Doctor"], corrected.CorrectionReasons);

        TimesheetDay open = sheet.Days[5];
        Assert.Equal(new TimeOnly(8, 15), open.Arrival);
        Assert.Null(open.Leaving);
        Assert.Equal(new TimeSpan(2, 45, 0), open.WorkTime);
    }

    // AC-5, AC-6
    [Fact]
    public void Calculate_Totals_FollowTheFlexitimeRule_AndSkipTheBrokenFile()
    {
        WriteOctober();
        DayStore store = Store;

        Timesheet sheet = Timesheet.Calculate(store, 2026, 10, EightHours, NoHoliday);

        // 8:30 + 6:00 + 2:45 (open day, no live minute).
        Assert.Equal(new TimeSpan(17, 15, 0), sheet.MonthWork);
        // +0:30, vacation 0, -2:00 + 0:45, -5:15.
        Assert.Equal(TimeSpan.FromMinutes(30 - 120 + 45 - 315), sheet.MonthBalance);
        FlexitimeBalance end = FlexitimeBalance.Compute(store, new DateOnly(2026, 10, 31), EightHours);
        FlexitimeBalance before = FlexitimeBalance.Compute(store, new DateOnly(2026, 9, 30), EightHours);
        Assert.Equal(end.Total, sheet.EndBalance.Total);
        Assert.Equal(end.Total - before.Total, sheet.MonthBalance);
        Assert.Equal(1, sheet.SkippedFiles);
        Assert.True(sheet.Days[6].Unreadable);
        Assert.Null(sheet.Days[6].WorkTime);
    }

    // AC-4
    [Fact]
    public void Calculate_NamesHolidays_FromTheGivenFunction()
    {
        Timesheet sheet = Timesheet.Calculate(Store, 2026, 10, EightHours,
            day => PublicHolidays.NameOf(day, PublicHolidays.Germany, PublicHolidays.None));

        Assert.Equal("German Unity Day", sheet.Days[2].Holiday);
        Assert.Null(sheet.Days[3].Holiday);
    }

    // Reads only: the data folder is unchanged and a missing folder is not created.
    [Fact]
    public void Calculate_OnlyReads()
    {
        WriteOctober();
        Dictionary<string, byte[]> before = Directory.EnumerateFiles(data.Path).ToDictionary(p => p, File.ReadAllBytes);

        Timesheet.Calculate(Store, 2026, 10, EightHours, NoHoliday);

        Dictionary<string, byte[]> after = Directory.EnumerateFiles(data.Path).ToDictionary(p => p, File.ReadAllBytes);
        Assert.Equal(before.Keys.Order(StringComparer.Ordinal), after.Keys.Order(StringComparer.Ordinal));
        foreach (KeyValuePair<string, byte[]> file in before)
        {
            Assert.Equal(file.Value, after[file.Key]);
        }

        string missing = Path.Combine(data.Path, "missing");
        Timesheet empty = Timesheet.Calculate(new DayStore(missing), 2026, 10, EightHours, NoHoliday);
        Assert.False(Directory.Exists(missing));
        Assert.Equal(TimeSpan.Zero, empty.MonthWork);
        Assert.All(empty.Days, d => Assert.True(d.IsEmpty));
    }
}
