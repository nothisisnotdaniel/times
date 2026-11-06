namespace Times.Tests;

public sealed class CheckpointSearchTests : IDisposable
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

    private CheckpointSearch Search(string text, ProjectList? projects = null) =>
        CheckpointSearch.Calculate(Store, projects, text, Today, Now);

    // AC-3
    [Fact]
    public void Names_MatchIgnoringCase_AndOtherGroupsAreLeftOut()
    {
        Write("2026-11-02", Cp("09:00", "Deploy prod"), Cp("10:00", "predeploy", 7), Cp("11:00", "Mail"), Leave("12:00"));

        CheckpointSearch search = Search("DEPLOY");

        Assert.Equal(["Deploy prod", "predeploy"], search.Matches.Select(m => Assert.Single(m.Names)));
        Assert.Equal([null, 7], search.Matches.Select(m => m.WorkItem));
        Assert.All(search.Matches, m => Assert.Equal(TimeSpan.FromHours(1), m.Total));
        Assert.Equal(0, search.SkippedFiles);
    }

    // AC-3
    [Fact]
    public void NoGroupContainsTheText_GivesNoMatches()
    {
        Write("2026-11-02", Cp("09:00", "Mail"), Leave("10:00"));

        Assert.Empty(Search("deploy").Matches);
    }

    // AC-4
    [Fact]
    public void QuicklistTitle_Matches_EvenIfNoNameDoes()
    {
        Write("2026-11-02", Cp("09:00", "Fix", 1234), Cp("10:00", "Mail", 99), Leave("11:00"));
        ProjectList projects = ProjectList.Empty.Add(new Project(1234, "Website Relaunch")).Add(new Project(99, "Support"));

        CheckpointSearch search = Search("relaunch", projects);

        SearchMatch match = Assert.Single(search.Matches);
        Assert.Equal(1234, match.WorkItem);
        Assert.Equal("Website Relaunch", match.Title(projects));
    }

    // AC-4
    [Fact]
    public void NullProjects_SearchesNamesOnly()
    {
        Write("2026-11-02", Cp("09:00", "Fix", 1234), Leave("10:00"));

        Assert.Empty(Search("relaunch").Matches);
        SearchMatch match = Assert.Single(Search("fix").Matches);
        Assert.Equal("Fix", match.Title(null));
    }

    // AC-5
    [Fact]
    public void Matches_AreByDateAscending_ThenByGroupOrderOnTheDay()
    {
        // Written out of order, so the folder listing order does not decide.
        Write("2026-11-03", Cp("09:00", "Review b"), Cp("10:00", "Review a"), Leave("11:00"));
        Write("2025-01-15", Cp("09:00", "Review old"), Leave("09:15"));
        Write("2026-11-01", Cp("08:00", "Mail"), Cp("09:00", "review c", 5), Leave("10:00"));

        CheckpointSearch search = Search("review");

        Assert.Equal(
            ["2025-01-15", "2026-11-01", "2026-11-03", "2026-11-03"],
            search.Matches.Select(m => m.Date.ToString("yyyy-MM-dd")));
        Assert.Equal(["Review old", "review c", "Review b", "Review a"], search.Matches.Select(m => m.Names[0]));
        Assert.Equal(TimeSpan.FromMinutes(15), search.Matches[0].Total);
    }

    // AC-6
    [Fact]
    public void OpenCheckpoint_CountsToNowOnToday_AndIsOpenOnOtherDays()
    {
        Write("2026-11-03", Cp("09:00", "Plan", 1));
        Write("2026-11-04", Cp("17:00", "Plan", 1));

        CheckpointSearch search = Search("plan");

        Assert.Equal(2, search.Matches.Count);
        Assert.Equal(TimeSpan.Zero, search.Matches[0].Total);
        Assert.True(search.Matches[0].Open);
        Assert.Equal(new TimeSpan(1, 30, 0), search.Matches[1].Total);
        Assert.False(search.Matches[1].Open);
    }

    // AC-6
    [Fact]
    public void Total_EqualsTheCheckpointSummaryTotal()
    {
        Write("2026-11-02", Cp("09:00", "Fix", 3), Cp("09:40", "Mail"), Cp("10:00", "Fix again", 3), Leave("12:10"));
        DayRecord record = Store.Load(new DateOnly(2026, 11, 2));
        CheckpointGroup expected = CheckpointSummary.Calculate(record, false, Now).Single(g => g.WorkItem == 3);

        SearchMatch match = Assert.Single(Search("fix").Matches);

        Assert.Equal(expected.Total, match.Total);
        Assert.Equal(expected.Names, match.Names);
    }

    // AC-8
    [Fact]
    public void UnreadableDayFiles_AreSkippedAndCounted()
    {
        Write("2026-11-02", Cp("09:00", "Fix"), Leave("10:00"));
        File.WriteAllText(Path.Combine(data.Path, "2026-11-03.json"), "{not json");
        File.WriteAllText(Path.Combine(data.Path, "2026-11-04.json"), "[1,2]");

        CheckpointSearch search = Search("fix");

        Assert.Single(search.Matches);
        Assert.Equal(2, search.SkippedFiles);
    }

    // AC-8
    [Fact]
    public void MissingFolder_GivesNoMatches_AndCreatesNoFolder()
    {
        using TempFolder missing = new TempFolder(create: false);

        CheckpointSearch search = CheckpointSearch.Calculate(new DayStore(missing.Path), null, "x", Today, Now);

        Assert.Empty(search.Matches);
        Assert.False(Directory.Exists(missing.Path));
    }

    // AC-10
    [Fact]
    public void Search_ChangesNoFile()
    {
        Write("2026-11-02", Cp("09:00", "Fix", 1), Leave("10:00"));
        Write("2026-11-04", Cp("17:00", "Fix", 1));
        File.WriteAllText(Path.Combine(data.Path, "2026-11-03.json"), "{not json");
        File.WriteAllText(Path.Combine(data.Path, "projects.json"), "{\"projects\":[{\"workItem\":1,\"title\":\"Fixes\"}]}");
        File.WriteAllText(Path.Combine(data.Path, "settings.json"), "{\"settings\":{}}");
        DateTime old = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        foreach (string file in Directory.EnumerateFiles(data.Path))
        {
            File.SetLastWriteTimeUtc(file, old);
        }

        (string, DateTime, long)[] before = Snapshot();

        Search("fix", ProjectList.Empty.Add(new Project(1, "Fixes")));

        Assert.Equal(before, Snapshot());
    }

    private (string, DateTime, long)[] Snapshot() =>
        Directory.EnumerateFileSystemEntries(data.Path)
            .Select(p => (Path.GetFileName(p)!, File.GetLastWriteTimeUtc(p), new FileInfo(p).Length))
            .OrderBy(t => t.Item1, StringComparer.Ordinal)
            .ToArray();
}
