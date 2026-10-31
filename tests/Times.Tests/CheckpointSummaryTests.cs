namespace Times.Tests;

public sealed class CheckpointSummaryTests
{
    private static readonly TimeOnly Evening = new TimeOnly(18, 0);

    private static DayEntry At(EntryKind kind, int hour, int minute) => new(kind, new TimeOnly(hour, minute));

    private static DayEntry Checkpoint(int hour, int minute, string name, int? workItem = null) =>
        new(EntryKind.Checkpoint, new TimeOnly(hour, minute), name, workItem);

    private static IReadOnlyList<CheckpointGroup> Past(params DayEntry[] entries) =>
        CheckpointSummary.Calculate(new DayRecord(entries), isToday: false, Evening);

    // AC-1
    [Theory]
    [InlineData((int)EntryKind.Checkpoint)]
    [InlineData((int)EntryKind.BreakStart)]
    [InlineData((int)EntryKind.Leaving)]
    public void Checkpoint_EndsAtTheNextCheckpointBreakStartOrLeaving(int endKind)
    {
        // EntryKind is internal, so the theory takes it as an int.
        EntryKind kind = (EntryKind)endKind;
        DayEntry end = kind == EntryKind.Checkpoint ? Checkpoint(10, 30, "Other") : At(kind, 10, 30);

        IReadOnlyList<CheckpointGroup> groups = Past(Checkpoint(9, 0, "Fix"), end, At(EntryKind.Leaving, 11, 0));

        Assert.Equal(TimeSpan.FromMinutes(90), groups[0].Total);
        Assert.False(groups[0].Open);
    }

    // AC-1
    [Fact]
    public void ArrivalAndBreakEnd_InBetween_DoNotEndTheCheckpoint()
    {
        IReadOnlyList<CheckpointGroup> groups = Past(
            Checkpoint(9, 0, "Fix"),
            At(EntryKind.Arrival, 9, 30),
            At(EntryKind.BreakEnd, 10, 0),
            At(EntryKind.Leaving, 11, 15));

        CheckpointGroup group = Assert.Single(groups);
        Assert.Equal(new TimeSpan(2, 15, 0), group.Total);
    }

    // AC-1
    [Fact]
    public void BreakEnd_DoesNotRestartTheCheckpoint()
    {
        IReadOnlyList<CheckpointGroup> groups = Past(
            Checkpoint(9, 0, "Fix"),
            At(EntryKind.BreakStart, 10, 0),
            At(EntryKind.BreakEnd, 10, 30),
            At(EntryKind.Leaving, 12, 0));

        Assert.Equal(TimeSpan.FromHours(1), Assert.Single(groups).Total);
    }

    // AC-1
    [Fact]
    public void NextEntryAtTheSameTime_GivesZero()
    {
        IReadOnlyList<CheckpointGroup> groups = Past(Checkpoint(9, 0, "Fix"), At(EntryKind.Leaving, 9, 0));

        Assert.Equal(TimeSpan.Zero, Assert.Single(groups).Total);
        Assert.False(groups[0].Open);
    }

    // AC-1
    [Fact]
    public void TwoCheckpointsAtTheSameTime_TheFirstTakesZero()
    {
        IReadOnlyList<CheckpointGroup> groups = Past(
            Checkpoint(9, 0, "A"),
            Checkpoint(9, 0, "B"),
            At(EntryKind.Leaving, 10, 0));

        Assert.Equal(TimeSpan.Zero, groups[0].Total);
        Assert.Equal(TimeSpan.FromHours(1), groups[1].Total);
    }

    // AC-2
    [Fact]
    public void TwoNamesOnOneWorkItem_AreOneGroup_WithDistinctNamesInFirstBookedOrder()
    {
        IReadOnlyList<CheckpointGroup> groups = Past(
            Checkpoint(9, 0, "Review", 1234),
            Checkpoint(10, 0, "Fix", 1234),
            Checkpoint(11, 0, "Review", 1234),
            At(EntryKind.Leaving, 11, 30));

        CheckpointGroup group = Assert.Single(groups);
        Assert.Equal(1234, group.WorkItem);
        Assert.Equal(["Review", "Fix"], group.Names);
        Assert.Equal(TimeSpan.FromMinutes(150), group.Total);
    }

    // AC-2
    [Fact]
    public void WorkItemGroupAndNameGroup_WithTheSameName_StayApart()
    {
        IReadOnlyList<CheckpointGroup> groups = Past(
            Checkpoint(9, 0, "Fix", 1234),
            Checkpoint(10, 0, "Fix"),
            At(EntryKind.Leaving, 10, 45));

        Assert.Equal(2, groups.Count);
        Assert.Equal(1234, groups[0].WorkItem);
        Assert.Equal(TimeSpan.FromHours(1), groups[0].Total);
        Assert.Null(groups[1].WorkItem);
        Assert.Equal(["Fix"], groups[1].Names);
        Assert.Equal(TimeSpan.FromMinutes(45), groups[1].Total);
    }

    // AC-2
    [Fact]
    public void NamesWithoutWorkItem_AreGroupedCaseSensitively_AndSummed()
    {
        IReadOnlyList<CheckpointGroup> groups = Past(
            Checkpoint(9, 0, "Fix"),
            Checkpoint(9, 30, "fix"),
            Checkpoint(10, 0, "Fix"),
            At(EntryKind.Leaving, 10, 20));

        Assert.Equal(2, groups.Count);
        Assert.Equal(["Fix"], groups[0].Names);
        Assert.Equal(TimeSpan.FromMinutes(50), groups[0].Total);
        Assert.Equal(["fix"], groups[1].Names);
        Assert.Equal(TimeSpan.FromMinutes(30), groups[1].Total);
    }

    // AC-2
    [Fact]
    public void DifferentWorkItems_AreSeparateGroups()
    {
        IReadOnlyList<CheckpointGroup> groups = Past(
            Checkpoint(9, 0, "Fix", 1),
            Checkpoint(10, 0, "Fix", 2),
            At(EntryKind.Leaving, 11, 0));

        Assert.Equal([1, 2], groups.Select(g => g.WorkItem!.Value));
    }

    // AC-3
    [Fact]
    public void Groups_AreInTheOrderOfTheirFirstCheckpointsTime()
    {
        IReadOnlyList<CheckpointGroup> groups = Past(
            Checkpoint(11, 0, "Late"),
            Checkpoint(8, 0, "Early", 7),
            Checkpoint(9, 0, "Middle"),
            Checkpoint(12, 0, "Early again", 7),
            At(EntryKind.Leaving, 13, 0));

        Assert.Equal(["Early", "Middle", "Late"], groups.Select(g => g.Names[0]));
    }

    // AC-7 (calculation side)
    [Fact]
    public void DayWithoutCheckpoints_HasNoGroups()
    {
        Assert.Empty(Past(At(EntryKind.Arrival, 8, 0), At(EntryKind.Leaving, 17, 0)));
        Assert.Empty(Past());
    }

    // AC-4
    [Fact]
    public void OnToday_LastCheckpoint_CountsToTheCurrentMinute_AndIsNotOpen()
    {
        DayRecord day = new DayRecord([Checkpoint(9, 0, "Fix"), At(EntryKind.Arrival, 9, 10)]);

        IReadOnlyList<CheckpointGroup> groups = CheckpointSummary.Calculate(day, isToday: true, new TimeOnly(10, 25));

        CheckpointGroup group = Assert.Single(groups);
        Assert.Equal(new TimeSpan(1, 25, 0), group.Total);
        Assert.False(group.Open);
    }

    // AC-4
    [Fact]
    public void OnToday_CurrentTimeBeforeTheCheckpoint_CountsZero_AndIsNotOpen()
    {
        DayRecord day = new DayRecord([Checkpoint(9, 0, "Fix")]);

        IReadOnlyList<CheckpointGroup> groups = CheckpointSummary.Calculate(day, isToday: true, new TimeOnly(8, 0));

        Assert.Equal(TimeSpan.Zero, Assert.Single(groups).Total);
        Assert.False(groups[0].Open);
    }

    // AC-5
    [Fact]
    public void NotToday_LastCheckpoint_CountsNothing_AndIsOpen()
    {
        DayRecord day = new DayRecord([Checkpoint(9, 0, "Fix", 5), Checkpoint(10, 0, "Fix", 5)]);

        IReadOnlyList<CheckpointGroup> groups = CheckpointSummary.Calculate(day, isToday: false, new TimeOnly(23, 0));

        CheckpointGroup group = Assert.Single(groups);
        Assert.Equal(TimeSpan.FromHours(1), group.Total);
        Assert.True(group.Open);
    }

    // AC-5
    [Fact]
    public void NotToday_OnlyCheckpointOpen_GivesZero()
    {
        IReadOnlyList<CheckpointGroup> groups = Past(At(EntryKind.Arrival, 8, 0), Checkpoint(9, 0, "Fix"));

        Assert.Equal(TimeSpan.Zero, Assert.Single(groups).Total);
        Assert.True(groups[0].Open);
    }

    // AC-5
    [Fact]
    public void NotToday_ClosedGroups_AreNotOpen()
    {
        IReadOnlyList<CheckpointGroup> groups = Past(Checkpoint(9, 0, "A"), Checkpoint(10, 0, "B"));

        Assert.False(groups[0].Open);
        Assert.True(groups[1].Open);
    }
}
