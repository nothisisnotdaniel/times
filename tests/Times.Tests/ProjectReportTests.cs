using System.Diagnostics;

namespace Times.Tests;

public sealed class ProjectReportTests : IDisposable
{
    private static readonly DateOnly Today = new DateOnly(2026, 11, 4);
    private static readonly TimeOnly Now = new TimeOnly(18, 30);

    private readonly TempFolder data = new();

    public void Dispose() => data.Dispose();

    private DayStore Store => new DayStore(data.Path);

    private void Write(string date, params string[] entries) =>
        File.WriteAllText(Path.Combine(data.Path, date + ".json"), "{\"entries\":[" + string.Join(',', entries) + "]}");

    private static string Cp(string time, string name, int? workItem = null) =>
        workItem is { } w
            ? $"{{\"kind\":\"checkpoint\",\"time\":\"{time}\",\"name\":\"{name}\",\"workItem\":{w}}}"
            : $"{{\"kind\":\"checkpoint\",\"time\":\"{time}\",\"name\":\"{name}\"}}";

    private static string Leave(string time) => $"{{\"kind\":\"leaving\",\"time\":\"{time}\"}}";

    private ProjectReport Calculate(string from, string to) =>
        ProjectReport.Calculate(Store, DateOnly.Parse(from), DateOnly.Parse(to), Today, Now);

    private string[] Entries() =>
        Directory.EnumerateFileSystemEntries(data.Path).Select(p => Path.GetFileName(p)!).Order(StringComparer.Ordinal).ToArray();

    // AC-1
    [Fact]
    public void Range_IsInclusive_AndDaysOutsideAreNotCounted()
    {
        Write("2026-11-01", Cp("09:00", "Fix", 1), Leave("10:00"));
        Write("2026-11-02", Cp("09:00", "Fix", 1), Leave("10:00"));
        Write("2026-11-03", Cp("09:00", "Fix", 1), Leave("11:00"));
        Write("2026-11-04", Cp("09:00", "Fix", 1), Leave("09:30"));
        Write("2026-11-05", Cp("09:00", "Fix", 1), Leave("17:00"));

        ProjectReport report = Calculate("2026-11-02", "2026-11-04");

        ReportRow row = Assert.Single(report.Rows);
        Assert.Equal(1, row.WorkItem);
        Assert.Equal(new TimeSpan(3, 30, 0), row.Total);
        Assert.Equal(new TimeSpan(3, 30, 0), report.Total);
        Assert.Equal(0, report.SkippedFiles);
    }

    // AC-1, AC-2
    [Fact]
    public void WorkItems_AreSummedAcrossDays_InAscendingOrder_WithNamesInFirstBookedOrder()
    {
        Write("2026-11-02", Cp("09:00", "Review", 99), Cp("10:00", "Fix", 5), Leave("12:00"));
        Write("2026-11-03", Cp("09:00", "Plan", 99), Cp("10:00", "Review", 99), Leave("11:00"));

        ProjectReport report = Calculate("2026-11-02", "2026-11-03");

        Assert.Equal([5, 99], report.Rows.Select(r => r.WorkItem));
        Assert.Equal(TimeSpan.FromHours(2), report.Rows[0].Total);
        Assert.Equal(TimeSpan.FromHours(3), report.Rows[1].Total);
        Assert.Equal(["Review", "Plan"], report.Rows[1].Names);
        Assert.Equal(TimeSpan.FromHours(5), report.Total);
    }

    // AC-4
    [Fact]
    public void Checkpoints_WithoutWorkItem_ArePerExactName_AfterWorkItems_InOrdinalOrder()
    {
        Write("2026-11-02", Cp("08:00", "b", null), Cp("09:00", "B", null), Cp("10:00", "Fix", 7), Cp("11:00", "a", null), Leave("12:00"));
        Write("2026-11-03", Cp("08:00", "b", null), Leave("09:00"));

        ProjectReport report = Calculate("2026-11-02", "2026-11-03");

        Assert.Equal([7, null, null, null], report.Rows.Select(r => r.WorkItem));
        Assert.Equal(["B", "a", "b"], report.Rows.Skip(1).Select(r => Assert.Single(r.Names)));
        Assert.Equal(TimeSpan.FromHours(2), report.Rows[3].Total);
        Assert.Equal(TimeSpan.FromHours(5), report.Total);
    }

    // AC-5
    [Fact]
    public void OpenCheckpoint_CountsToNowOnToday_AndIsOpenOnOtherDays()
    {
        Write("2026-11-03", Cp("09:00", "Past", 1));
        Write("2026-11-04", Cp("17:00", "Now", 2));

        ProjectReport report = Calculate("2026-11-03", "2026-11-04");

        Assert.Equal(TimeSpan.Zero, report.Rows[0].Total);
        Assert.True(report.Rows[0].Open);
        Assert.Equal(new TimeSpan(1, 30, 0), report.Rows[1].Total);
        Assert.False(report.Rows[1].Open);
    }

    // AC-6
    [Fact]
    public void AbsenceAndEmptyDays_AddNothing_UnreadableFilesAreSkipped_AndNothingIsCreated()
    {
        File.WriteAllText(Path.Combine(data.Path, "2026-11-02.json"), "{\"absence\":\"vacation\"}");
        File.WriteAllText(Path.Combine(data.Path, "2026-11-03.json"), "{\"entries\":[{\"kind\":\"arrival\",\"time\":\"08:00\"}]}");
        File.WriteAllText(Path.Combine(data.Path, "2026-11-04.json"), "{not json");
        File.WriteAllText(Path.Combine(data.Path, "2026-11-05.json"), "{not json");
        string[] before = Entries();

        ProjectReport report = Calculate("2026-11-01", "2026-11-04");

        Assert.Empty(report.Rows);
        Assert.Equal(TimeSpan.Zero, report.Total);
        Assert.Equal(1, report.SkippedFiles);
        Assert.Equal(before, Entries());
    }

    // AC-6
    [Fact]
    public void MissingFolder_GivesAnEmptyReport_AndCreatesNoFolder()
    {
        using TempFolder missing = new TempFolder(create: false);

        ProjectReport report = ProjectReport.Calculate(new DayStore(missing.Path), DateOnly.MinValue, DateOnly.MaxValue, Today, Now);

        Assert.Empty(report.Rows);
        Assert.False(Directory.Exists(missing.Path));
    }

    // AC-9
    [Fact]
    public void FullRange_LoadsOnlyTheListedDays_AndFinishesQuickly()
    {
        Write("2026-11-02", Cp("09:00", "Fix", 1), Leave("10:00"));
        Write("2026-11-03", Cp("09:00", "Fix", 1), Leave("10:00"));
        Write("2026-11-04", Cp("09:00", "Fix", 1), Leave("10:00"));

        Stopwatch watch = Stopwatch.StartNew();
        ProjectReport report = ProjectReport.Calculate(Store, DateOnly.MinValue, DateOnly.MaxValue, Today, Now);
        watch.Stop();

        Assert.Equal(TimeSpan.FromHours(3), Assert.Single(report.Rows).Total);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.Elapsed}");
    }

    private ProjectReport Calculate(string from, string to, int step) =>
        ProjectReport.Calculate(Store, DateOnly.Parse(from), DateOnly.Parse(to), Today, Now, step);

    // A checkpoint at 9:00 that ends after the given minutes.
    private void WriteMinutes(string date, int minutes, int? workItem = 1) =>
        Write(date, Cp("09:00", "Fix", workItem), Leave(new TimeOnly(9, 0).AddMinutes(minutes).ToString("HH:mm")));

    // AC-2 (104)
    [Fact]
    public void StepZero_GivesTheSameRowsAndTotals_AsWithoutAStep()
    {
        Write("2026-11-02", Cp("09:00", "Review", 99), Cp("09:07", "Fix", 5), Cp("09:30", "b", null), Leave("10:01"));
        Write("2026-11-03", Cp("09:00", "Plan", 99), Cp("09:13", "Review", 99), Cp("09:44", "x", null));
        Write("2026-11-04", Cp("18:07", "Fix", 5));

        ProjectReport exact = Calculate("2026-11-01", "2026-11-05");
        ProjectReport zero = Calculate("2026-11-01", "2026-11-05", 0);

        Assert.Equal(exact.Rows.Select(r => (r.WorkItem, string.Join('|', r.Names), r.Total, r.Open)),
            zero.Rows.Select(r => (r.WorkItem, string.Join('|', r.Names), r.Total, r.Open)));
        Assert.Equal(exact.Total, zero.Total);
        Assert.Equal(TimeSpan.FromMinutes(46), zero.Rows[0].Total);
    }

    // AC-3 (104)
    [Theory]
    [InlineData(15, 7, 0)]
    [InlineData(15, 8, 15)]
    [InlineData(15, 22, 15)]
    [InlineData(15, 23, 30)]
    [InlineData(15, 60, 60)]
    [InlineData(10, 5, 10)]
    [InlineData(10, 4, 0)]
    [InlineData(5, 2, 0)]
    [InlineData(5, 3, 5)]
    [InlineData(5, 0, 0)]
    [InlineData(15, 0, 0)]
    public void DayValue_IsRoundedToTheNearestStep_HalfRoundsUp(int step, int minutes, int expected)
    {
        WriteMinutes("2026-11-02", minutes);

        ProjectReport report = Calculate("2026-11-02", "2026-11-02", step);

        ReportRow row = Assert.Single(report.Rows);
        Assert.Equal(TimeSpan.FromMinutes(expected), row.Total);
        Assert.Equal(TimeSpan.FromMinutes(expected), report.Total);
    }

    // AC-3 (104)
    [Theory]
    [InlineData(15, 7, 0)]
    [InlineData(15, 450, 450)]
    [InlineData(15, 452, 450)]
    [InlineData(15, 457, 450)]
    [InlineData(15, 458, 465)]
    [InlineData(10, 5, 10)]
    [InlineData(10, 4, 0)]
    [InlineData(5, 3, 5)]
    [InlineData(0, 3, 3)]
    public void Round_RoundsToTheNearestMultiple(int step, int minutes, int expected)
    {
        Assert.Equal(TimeSpan.FromMinutes(expected), ProjectReport.Round(TimeSpan.FromMinutes(minutes), step));
    }

    // AC-4 (104)
    [Fact]
    public void Rounding_IsPerRowAndDay_NotPerCheckpoint()
    {
        Write("2026-11-02", Cp("09:00", "Fix", 1), Cp("09:04", "Review", 1), Leave("09:08"));

        ProjectReport report = Calculate("2026-11-02", "2026-11-02", 5);

        ReportRow row = Assert.Single(report.Rows);
        Assert.Equal(TimeSpan.FromMinutes(10), row.Total);
        Assert.Equal(["Fix", "Review"], row.Names);
    }

    // AC-4 (104)
    [Fact]
    public void Rounding_IsPerDay_NotOnTheRangeSum()
    {
        WriteMinutes("2026-11-02", 8);
        WriteMinutes("2026-11-03", 8);

        ProjectReport report = Calculate("2026-11-02", "2026-11-03", 5);

        ReportRow row = Assert.Single(report.Rows);
        Assert.Equal(TimeSpan.FromMinutes(20), row.Total);
        Assert.Equal(TimeSpan.FromMinutes(20), report.Total);
    }

    // AC-4 (104)
    [Fact]
    public void NameRows_AreRoundedPerDayToo()
    {
        Write("2026-11-02", Cp("09:00", "a", null), Cp("09:04", "a", null), Leave("09:08"));
        Write("2026-11-03", Cp("09:00", "a", null), Leave("09:08"));

        ProjectReport report = Calculate("2026-11-02", "2026-11-03", 5);

        ReportRow row = Assert.Single(report.Rows);
        Assert.Null(row.WorkItem);
        Assert.Equal(TimeSpan.FromMinutes(20), row.Total);
    }

    // AC-5 (104)
    [Fact]
    public void Total_IsTheSumOfTheRoundedRows_AndAZeroRowStays()
    {
        Write("2026-11-02", Cp("09:00", "Fix", 1), Cp("09:07", "Plan", 2), Cp("09:30", "b", null), Leave("09:53"));

        ProjectReport report = Calculate("2026-11-02", "2026-11-02", 15);

        Assert.Equal([1, 2, null], report.Rows.Select(r => r.WorkItem));
        Assert.Equal(TimeSpan.Zero, report.Rows[0].Total);
        Assert.Equal(TimeSpan.FromMinutes(30), report.Rows[1].Total);
        Assert.Equal(TimeSpan.FromMinutes(30), report.Rows[2].Total);
        Assert.Equal(report.Rows.Aggregate(TimeSpan.Zero, (sum, r) => sum + r.Total), report.Total);
        Assert.Equal(TimeSpan.FromHours(1), report.Total);
    }

    // AC-5 (104)
    [Fact]
    public void OpenRow_StaysZero_AndKeepsItsOpenMarker()
    {
        Write("2026-11-02", Cp("09:00", "Fix", 1));

        ProjectReport report = Calculate("2026-11-02", "2026-11-02", 15);

        ReportRow row = Assert.Single(report.Rows);
        Assert.True(row.Open);
        Assert.Equal(TimeSpan.Zero, row.Total);
        Assert.Equal(TimeSpan.Zero, report.Total);
    }

    // AC-5 (104)
    [Fact]
    public void TodaysRunningCheckpoint_IsRoundedLikeAnyOther()
    {
        Write("2026-11-04", Cp("18:07", "Fix", 1));

        ProjectReport report = Calculate("2026-11-04", "2026-11-04", 15);

        ReportRow row = Assert.Single(report.Rows);
        Assert.False(row.Open);
        Assert.Equal(TimeSpan.FromMinutes(30), row.Total);
    }

    [Fact]
    public void NegativeStep_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Calculate("2026-11-02", "2026-11-02", -5));
    }
}
