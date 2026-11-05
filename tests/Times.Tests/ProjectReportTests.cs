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
}
