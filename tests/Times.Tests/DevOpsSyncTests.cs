namespace Times.Tests;

public sealed class DevOpsSyncTests : IDisposable
{
    private static readonly DateOnly Day1 = new(2026, 11, 2);
    private static readonly DateOnly Day2 = new(2026, 11, 3);

    private readonly TempFolder data = new();

    public void Dispose() => data.Dispose();

    private static DayEntry Cp(int hour, int minute, string name, int? workItem, bool booked = false) =>
        new(EntryKind.Checkpoint, new TimeOnly(hour, minute), name, workItem, booked);

    private static DayEntry At(EntryKind kind, int hour, int minute) => new(kind, new TimeOnly(hour, minute));

    private static SyncPlan Plan(params (DateOnly, DayRecord)[] days) => DevOpsSync.Plan(days, 0);

    // AC-5
    [Fact]
    public void CountsOnlyEndedUnbookedCheckpointsWithAWorkItem()
    {
        DayRecord day = new DayRecord([
            At(EntryKind.Arrival, 8, 0),
            Cp(8, 0, "Review", 12),             // 1:00 (ended by the next checkpoint)
            Cp(9, 0, "Mail", null),             // no work item: skipped (ends at the break)
            At(EntryKind.BreakStart, 10, 0),
            At(EntryKind.BreakEnd, 10, 30),
            Cp(10, 30, "Booked", 12, booked: true), // booked: skipped
            Cp(11, 0, "Zero", 7),               // 0 minutes: skipped
            Cp(11, 0, "Fix", 12),               // 1:30, ended by leaving
            At(EntryKind.Leaving, 12, 30),
        ]);

        SyncPlan plan = Plan((Day1, day));

        SyncItem item = Assert.Single(plan.Items);
        Assert.Equal(12, item.WorkItem);
        Assert.Equal(150, item.Minutes);
        Assert.Equal(2.5, item.Hours);
        Assert.Equal(["Review", "Fix"], item.Names);
        Assert.Equal([2, 8], item.Checkpoints.Select(c => c.Number));
        Assert.All(item.Checkpoints, c => Assert.Equal(Day1, c.Day));
    }

    // AC-5
    [Fact]
    public void RunningOrOpenCheckpoint_IsSkipped()
    {
        // The last checkpoint has no end: running (today) or open (another day). Arrivals and break ends never end it.
        DayRecord day = new DayRecord([
            Cp(9, 0, "Running", 5),
            At(EntryKind.BreakEnd, 10, 0),
        ]);

        Assert.Empty(Plan((Day1, day)).Items);
    }

    [Fact]
    public void GroupsPerWorkItem_Ascending_OverDays_AndRoundsHoursToTwoDecimals()
    {
        DayRecord first = new DayRecord([Cp(9, 0, "B", 200), Cp(9, 20, "A", 100), At(EntryKind.Leaving, 9, 30)]);
        DayRecord second = new DayRecord([Cp(9, 0, "B2", 200), At(EntryKind.Leaving, 9, 1)]);

        SyncPlan plan = Plan((Day2, second), (Day1, first));

        Assert.Equal([100, 200], plan.Items.Select(i => i.WorkItem));
        SyncItem b = plan.Items[1];
        Assert.Equal(21, b.Minutes);
        Assert.Equal(Math.Round(21 / 60.0, 2), b.Hours);
        Assert.Equal(0.35, b.Hours);
        Assert.Equal([Day1, Day2], b.Checkpoints.Select(c => c.Day));
        Assert.Equal(0.17, plan.Items[0].Hours); // 10 minutes
    }

    [Fact]
    public void Title_FromQuicklist_OrNames()
    {
        SyncItem item = new SyncItem(5, ["One", "Two"], [new SyncCheckpoint(Day1, 1, 30)]);
        ProjectList projects = ProjectList.FromJson("{\"projects\":[{\"workItem\":5,\"title\":\"Times\"}]}");

        Assert.Equal("Times", item.Title(projects));
        Assert.Equal("One, Two", item.Title(null));
    }

    // AC-9
    [Fact]
    public void FromStore_ReadsOnlyTheRange_SkipsAndCountsUnreadableFiles_AndWritesNothing()
    {
        DayStore store = new DayStore(data.Path);
        store.Save(Day1, new DayRecord([Cp(9, 0, "A", 1), At(EntryKind.Leaving, 10, 0)]));
        store.Save(new DateOnly(2026, 11, 5), new DayRecord([Cp(9, 0, "Out", 2), At(EntryKind.Leaving, 10, 0)]));
        File.WriteAllText(store.PathFor(Day2), "not json");
        string[] before = Directory.GetFiles(data.Path).Order().ToArray();
        DateTime[] times = before.Select(File.GetLastWriteTimeUtc).ToArray();

        SyncPlan plan = DevOpsSync.Plan(store, Day1, new DateOnly(2026, 11, 4));

        Assert.Equal(1, plan.SkippedFiles);
        Assert.Equal([1], plan.Items.Select(i => i.WorkItem));
        Assert.Equal(before, Directory.GetFiles(data.Path).Order().ToArray());
        Assert.Equal(times, before.Select(File.GetLastWriteTimeUtc).ToArray());
        Assert.Equal("not json", File.ReadAllText(store.PathFor(Day2)));
    }

    // CheckpointSummary's results do not change with the shared end rule.
    [Fact]
    public void CheckpointSummary_EndOf_IsTheSharedRule()
    {
        IReadOnlyList<DayEntry> entries = [
            Cp(9, 0, "A", 1),
            At(EntryKind.Arrival, 9, 10),
            At(EntryKind.BreakEnd, 9, 20),
            At(EntryKind.BreakStart, 9, 30),
            Cp(10, 0, "B", 1),
        ];

        Assert.Equal(new TimeOnly(9, 30), CheckpointSummary.EndOf(entries, 0)!.Time);
        Assert.Null(CheckpointSummary.EndOf(entries, 4));
    }
}
