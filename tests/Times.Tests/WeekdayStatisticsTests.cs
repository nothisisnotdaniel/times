namespace Times.Tests;

public sealed class WeekdayStatisticsTests : IDisposable
{
    // 2026-11-02 is a Monday.
    private static readonly DateOnly From = new DateOnly(2026, 11, 2);
    private static readonly DateOnly To = new DateOnly(2026, 11, 8);

    private readonly TempFolder data = new();

    public void Dispose() => data.Dispose();

    private DayStore Store => new DayStore(data.Path);

    private static DayEntry E(EntryKind kind, string time) => new DayEntry(kind, TimeOnly.Parse(time, System.Globalization.CultureInfo.InvariantCulture));

    private void Save(string date, params DayEntry[] entries) =>
        Store.Save(DateOnly.Parse(date, System.Globalization.CultureInfo.InvariantCulture), new DayRecord(entries));

    private void SaveDay(string date, string arrival, string leaving) =>
        Save(date, E(EntryKind.Arrival, arrival), E(EntryKind.Leaving, leaving));

    private WeekdayStatistics Calculate(DateOnly? from = null, DateOnly? to = null) =>
        WeekdayStatistics.Calculate(Store, from ?? From, to ?? To);

    // AC-1
    [Fact]
    public void Range_IsInclusive_AndDaysOutsideAreIgnored()
    {
        SaveDay("2026-11-01", "06:00", "10:00");
        SaveDay("2026-11-02", "08:00", "16:00");
        SaveDay("2026-11-08", "10:00", "12:00");
        SaveDay("2026-11-09", "06:00", "10:00");

        WeekdayStatistics stats = Calculate();

        Assert.Equal([DayOfWeek.Monday, DayOfWeek.Sunday], stats.Rows.Select(r => r.Weekday));
        Assert.All(stats.Rows, r => Assert.Equal(1, r.Days));
        Assert.Equal(new TimeOnly(8, 0), stats.Rows[0].Arrival);
        Assert.Equal(new TimeOnly(10, 0), stats.Rows[1].Arrival);
        WeekdayRow all = Assert.IsType<WeekdayRow>(stats.All);
        Assert.Null(all.Weekday);
        Assert.Equal(2, all.Days);
        Assert.Equal(0, stats.SkippedFiles);
    }

    // AC-1
    [Fact]
    public void AbsenceDays_AndDaysWithoutArrival_DoNotCount()
    {
        Store.Save(new DateOnly(2026, 11, 2), new DayRecord([], AbsenceKind.Vacation));
        Save("2026-11-03", new DayEntry(EntryKind.Checkpoint, new TimeOnly(9, 0), "Mail", null));
        Save("2026-11-04", E(EntryKind.BreakStart, "12:00"), E(EntryKind.BreakEnd, "12:30"));
        Save("2026-11-05", E(EntryKind.Leaving, "17:00"));

        WeekdayStatistics stats = Calculate();

        Assert.Empty(stats.Rows);
        Assert.Null(stats.All);
        Assert.Equal(0, stats.SkippedFiles);
    }

    // AC-1
    [Fact]
    public void UnreadableFiles_InTheRange_AreSkippedAndCounted()
    {
        SaveDay("2026-11-02", "08:00", "16:00");
        File.WriteAllText(Path.Combine(data.Path, "2026-11-03.json"), "{not json");
        File.WriteAllText(Path.Combine(data.Path, "2026-11-04.json"), "{\"entries\":[{\"kind\":\"arrival\",\"time\":\"25:00\"}]}");
        File.WriteAllText(Path.Combine(data.Path, "2026-11-20.json"), "{not json");

        WeekdayStatistics stats = Calculate();

        Assert.Equal(2, stats.SkippedFiles);
        Assert.Equal(1, Assert.IsType<WeekdayRow>(stats.All).Days);
    }

    // AC-2
    [Fact]
    public void FirstArrival_LastLeaving_AndWorkTime_WithALunchGap()
    {
        Save(
            "2026-11-02",
            E(EntryKind.Arrival, "08:00"),
            E(EntryKind.Leaving, "12:00"),
            E(EntryKind.Arrival, "13:00"),
            E(EntryKind.Leaving, "17:30"));

        WeekdayRow row = Assert.Single(Calculate().Rows);

        Assert.Equal(new TimeOnly(8, 0), row.Arrival);
        Assert.Equal(new TimeOnly(17, 30), row.Leaving);
        Assert.Equal(TimeSpan.FromMinutes(8 * 60 + 30), row.WorkTime);
    }

    // AC-2
    [Fact]
    public void OpenDay_CountsInDaysAndArrivalOnly()
    {
        SaveDay("2026-11-02", "08:00", "16:00");
        Save("2026-11-09", E(EntryKind.Arrival, "09:00"), E(EntryKind.BreakStart, "12:00"));
        Save("2026-11-10", E(EntryKind.Arrival, "07:00"));

        WeekdayStatistics stats = Calculate(to: new DateOnly(2026, 11, 10));

        WeekdayRow monday = stats.Rows[0];
        Assert.Equal(DayOfWeek.Monday, monday.Weekday);
        Assert.Equal(2, monday.Days);
        Assert.Equal(new TimeOnly(8, 30), monday.Arrival);
        Assert.Equal(new TimeOnly(16, 0), monday.Leaving);
        Assert.Equal(TimeSpan.FromHours(8), monday.WorkTime);

        WeekdayRow tuesday = stats.Rows[1];
        Assert.Equal(DayOfWeek.Tuesday, tuesday.Weekday);
        Assert.Equal(1, tuesday.Days);
        Assert.Equal(new TimeOnly(7, 0), tuesday.Arrival);
        Assert.Null(tuesday.Leaving);
        Assert.Null(tuesday.WorkTime);

        WeekdayRow all = Assert.IsType<WeekdayRow>(stats.All);
        Assert.Equal(3, all.Days);
        Assert.Equal(new TimeOnly(8, 0), all.Arrival);
        Assert.Equal(new TimeOnly(16, 0), all.Leaving);
        Assert.Equal(TimeSpan.FromHours(8), all.WorkTime);
    }

    // AC-3
    [Theory]
    [InlineData("08:00", "08:15", 8, 8)]
    [InlineData("08:00", "08:01", 8, 1)]
    [InlineData("08:00", "08:02", 8, 1)]
    [InlineData("08:00", "08:03", 8, 2)]
    public void Averages_RoundToTheNearestMinute_HalfUp(string first, string second, int hour, int minute)
    {
        SaveDay("2026-11-02", first, "16:00");
        SaveDay("2026-11-09", second, "16:00");

        WeekdayRow row = Assert.Single(Calculate(to: new DateOnly(2026, 11, 9)).Rows);

        Assert.Equal(new TimeOnly(hour, minute), row.Arrival);
    }

    // AC-3
    [Fact]
    public void LeavingAndWorkTime_AreRoundedHalfUp()
    {
        SaveDay("2026-11-02", "08:00", "16:00");
        SaveDay("2026-11-09", "08:00", "16:15");

        WeekdayRow row = Assert.Single(Calculate(to: new DateOnly(2026, 11, 9)).Rows);

        Assert.Equal(new TimeOnly(16, 8), row.Leaving);
        Assert.Equal(TimeSpan.FromMinutes(8 * 60 + 8), row.WorkTime);
    }

    // AC-3
    [Theory]
    [InlineData(1L, 3, 0L)]
    [InlineData(2L, 3, 1L)]
    [InlineData(3L, 2, 2L)]
    [InlineData(5L, 2, 3L)]
    [InlineData(0L, 4, 0L)]
    public void RoundedMean_RoundsHalfUp(long sum, int count, long expected) =>
        Assert.Equal(expected, WeekdayStatistics.RoundedMean(sum, count));

    // AC-3
    [Fact]
    public void AllRow_AveragesEveryDay_NotTheWeekdayAverages()
    {
        SaveDay("2026-11-02", "08:00", "16:00");
        SaveDay("2026-11-09", "08:00", "16:00");
        SaveDay("2026-11-16", "08:00", "16:00");
        SaveDay("2026-11-03", "10:00", "14:00");

        WeekdayStatistics stats = Calculate(to: new DateOnly(2026, 11, 30));

        // Mean of the weekday means would be 9:00 and 6:00.
        WeekdayRow all = Assert.IsType<WeekdayRow>(stats.All);
        Assert.Equal(4, all.Days);
        Assert.Equal(new TimeOnly(8, 30), all.Arrival);
        Assert.Equal(new TimeOnly(15, 30), all.Leaving);
        Assert.Equal(TimeSpan.FromHours(7), all.WorkTime);
    }

    // AC-3
    [Fact]
    public void Rows_GroupByWeekdayAcrossWeeks_MondayFirstSundayLast()
    {
        SaveDay("2026-11-15", "11:00", "13:00");
        SaveDay("2026-11-11", "09:00", "17:00");
        SaveDay("2026-11-09", "07:00", "15:00");
        SaveDay("2026-11-04", "09:30", "17:30");
        SaveDay("2026-11-02", "08:00", "16:00");

        WeekdayStatistics stats = Calculate(to: new DateOnly(2026, 11, 15));

        Assert.Equal([DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Sunday], stats.Rows.Select(r => r.Weekday));
        Assert.Equal([2, 2, 1], stats.Rows.Select(r => r.Days));
        Assert.Equal(new TimeOnly(7, 30), stats.Rows[0].Arrival);
        Assert.Equal(new TimeOnly(9, 15), stats.Rows[1].Arrival);
        Assert.Equal(new TimeOnly(11, 0), stats.Rows[2].Arrival);
    }

    // AC-9
    [Fact]
    public void Calculate_WritesNothing()
    {
        SaveDay("2026-11-02", "08:00", "16:00");
        File.WriteAllText(Path.Combine(data.Path, "2026-11-03.json"), "{not json");
        Dictionary<string, byte[]> before = Directory.EnumerateFileSystemEntries(data.Path).ToDictionary(p => p, File.ReadAllBytes);

        Calculate();

        Dictionary<string, byte[]> after = Directory.EnumerateFileSystemEntries(data.Path).ToDictionary(p => p, File.ReadAllBytes);
        Assert.Equal(before.Keys.Order(StringComparer.Ordinal), after.Keys.Order(StringComparer.Ordinal));
        Assert.All(before, pair => Assert.Equal(pair.Value, after[pair.Key]));
    }
}
