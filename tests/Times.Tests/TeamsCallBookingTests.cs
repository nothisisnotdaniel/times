namespace Times.Tests;

// Job 327, AC-6 and AC-7: the pure start and end rules.
public sealed class TeamsCallBookingTests
{
    private static readonly DateOnly Today = new DateOnly(2026, 10, 27);

    private static DayEntry At(EntryKind kind, int hour, int minute) => new DayEntry(kind, new TimeOnly(hour, minute));

    private static DayEntry Checkpoint(int hour, int minute, string name, int? workItem) =>
        new DayEntry(EntryKind.Checkpoint, new TimeOnly(hour, minute), name, workItem);

    private static DayRecord Working() =>
        new DayRecord([At(EntryKind.Arrival, 8, 0), Checkpoint(8, 30, "Feature", 1234)]);

    // AC-6
    [Fact]
    public void Start_WhileWorking_BooksACall_AndRemembersTheOpenCheckpoint()
    {
        (DayRecord updated, TeamsCall call) = TeamsCallBooking.Start(Today, Working(), new TimeOnly(10, 15))!.Value;

        DayEntry booked = updated.Entries[^1];
        Assert.Equal(EntryKind.Checkpoint, booked.Kind);
        Assert.Equal("Call", booked.Name);
        Assert.Null(booked.WorkItem);
        Assert.Equal(new TimeOnly(10, 15), booked.Time);
        Assert.Equal(Today, call.Date);
        Assert.Equal("Feature", call.Previous!.Name);
        Assert.Equal(1234, call.Previous.WorkItem);
    }

    // AC-6: working with no checkpoint: the call is booked, nothing is remembered.
    [Fact]
    public void Start_WithNoOpenCheckpoint_RemembersNothing()
    {
        DayRecord day = new DayRecord([At(EntryKind.Arrival, 8, 0)]);

        (DayRecord updated, TeamsCall call) = TeamsCallBooking.Start(Today, day, new TimeOnly(10, 15))!.Value;

        Assert.Equal("Call", updated.Entries[^1].Name);
        Assert.Null(call.Previous);
    }

    // AC-6: a checkpoint ended by a break and a break end is not open any more.
    [Fact]
    public void Start_AfterABreak_RemembersNothing()
    {
        DayRecord day = new DayRecord([
            At(EntryKind.Arrival, 8, 0), Checkpoint(8, 30, "Feature", 1234), At(EntryKind.BreakStart, 12, 0), At(EntryKind.BreakEnd, 12, 30)]);

        Assert.Null(TeamsCallBooking.Start(Today, day, new TimeOnly(13, 0))!.Value.Call.Previous);
    }

    // AC-6
    [Fact]
    public void Start_NotWorking_BooksNothing()
    {
        Assert.Null(TeamsCallBooking.Start(Today, DayRecord.Empty, new TimeOnly(10, 0)));
        Assert.Null(TeamsCallBooking.Start(Today, new DayRecord([At(EntryKind.Arrival, 8, 0), At(EntryKind.BreakStart, 10, 0)]), new TimeOnly(10, 5)));
        Assert.Null(TeamsCallBooking.Start(Today, new DayRecord([At(EntryKind.Arrival, 8, 0), At(EntryKind.Leaving, 9, 0)]), new TimeOnly(10, 5)));
    }

    // AC-6
    [Fact]
    public void Start_OnAnAbsenceDay_BooksNothing()
    {
        Assert.Null(TeamsCallBooking.Start(Today, new DayRecord([], AbsenceKind.Vacation), new TimeOnly(10, 0)));
    }

    // AC-7
    [Fact]
    public void End_WithTheCallStillOpen_BooksTheRememberedCheckpointAgain()
    {
        (DayRecord started, TeamsCall call) = TeamsCallBooking.Start(Today, Working(), new TimeOnly(10, 15))!.Value;

        DayRecord? ended = TeamsCallBooking.End(Today, started, new TimeOnly(10, 45), call);

        Assert.NotNull(ended);
        DayEntry booked = ended.Entries[^1];
        Assert.Equal(EntryKind.Checkpoint, booked.Kind);
        Assert.Equal("Feature", booked.Name);
        Assert.Equal(1234, booked.WorkItem);
        Assert.Equal(new TimeOnly(10, 45), booked.Time);
        Assert.False(booked.Booked);
    }

    // AC-7
    [Fact]
    public void End_WithNothingRemembered_BooksNothing()
    {
        (DayRecord started, TeamsCall call) = TeamsCallBooking.Start(Today, new DayRecord([At(EntryKind.Arrival, 8, 0)]), new TimeOnly(10, 15))!.Value;

        Assert.Null(TeamsCallBooking.End(Today, started, new TimeOnly(10, 45), call));
        Assert.Null(TeamsCallBooking.End(Today, started, new TimeOnly(10, 45), null));
    }

    // AC-7: the user booked a checkpoint during the call.
    [Fact]
    public void End_AfterTheUserBookedSomething_BooksNothing()
    {
        (DayRecord started, TeamsCall call) = TeamsCallBooking.Start(Today, Working(), new TimeOnly(10, 15))!.Value;
        DayRecord changed = started.Add(Checkpoint(10, 30, "Review", null));

        Assert.Null(TeamsCallBooking.End(Today, changed, new TimeOnly(10, 45), call));
    }

    // AC-7: the user took a break (not working any more) or the entries changed in any other way.
    [Fact]
    public void End_NotWorking_OrChanged_BooksNothing()
    {
        (DayRecord started, TeamsCall call) = TeamsCallBooking.Start(Today, Working(), new TimeOnly(10, 15))!.Value;

        Assert.Null(TeamsCallBooking.End(Today, started.Add(At(EntryKind.BreakStart, 10, 30)), new TimeOnly(10, 45), call));
        Assert.Null(TeamsCallBooking.End(Today, started.RemoveAt(2), new TimeOnly(10, 45), call));
    }

    // AC-7: after midnight the call belongs to another day.
    [Fact]
    public void End_OnAnotherDay_BooksNothing()
    {
        (DayRecord started, TeamsCall call) = TeamsCallBooking.Start(Today, Working(), new TimeOnly(10, 15))!.Value;

        Assert.Null(TeamsCallBooking.End(Today.AddDays(1), started, new TimeOnly(0, 5), call));
    }

    [Fact]
    public void OpenCheckpoint_IsTheLastUnendedCheckpoint()
    {
        Assert.Equal("Feature", TeamsCallBooking.OpenCheckpoint(Working())!.Name);
        Assert.Null(TeamsCallBooking.OpenCheckpoint(Working().Add(At(EntryKind.Leaving, 17, 0))));
        Assert.Null(TeamsCallBooking.OpenCheckpoint(DayRecord.Empty));
    }
}
