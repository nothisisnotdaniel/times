namespace Times.Tests;

public class WorkingTimeChecksTests
{
    private static DayEntry E(EntryKind kind, int hour, int minute) => new(kind, new TimeOnly(hour, minute));

    // Arrival to leaving with no break: work time is leaving minus arrival.
    private static DayRecord Span(int fromHour, int fromMinute, int toHour, int toMinute) => new(
        [E(EntryKind.Arrival, fromHour, fromMinute), E(EntryKind.Leaving, toHour, toMinute)]);

    // 10:00 + extra minutes of work with a 45-minute break, so only the work check can fire.
    private static DayRecord LongDay(int extraMinutes) => new(
    [
        E(EntryKind.Arrival, 7, 0),
        E(EntryKind.BreakStart, 12, 0),
        E(EntryKind.BreakEnd, 12, 45),
        new DayEntry(EntryKind.Leaving, new TimeOnly(17, 45).AddMinutes(extraMinutes)),
    ]);

    private static readonly WorkingTimeCheckOptions WorkOnly = new(false, true, false);
    private static readonly WorkingTimeCheckOptions RestOnly = new(false, false, true);
    private static readonly WorkingTimeCheckOptions BreaksOnly = new(true, false, false);

    // AC-1
    [Fact]
    public void Breaks_ShortBreak_GivesTheMissingTime_OnlyWhileOn()
    {
        DayRecord day = new DayRecord(
        [
            E(EntryKind.Arrival, 8, 0), E(EntryKind.BreakStart, 12, 0), E(EntryKind.BreakEnd, 12, 20), E(EntryKind.Leaving, 17, 0),
        ]);

        Assert.Equal(TimeSpan.FromMinutes(10), WorkingTimeChecks.Check(day, null, BreaksOnly).BreakMissing);
        Assert.Null(WorkingTimeChecks.Check(day, null, WorkingTimeCheckOptions.None).BreakMissing);
        Assert.False(WorkingTimeChecks.Check(day, null, WorkingTimeCheckOptions.None).HasWarning);
    }

    // AC-1: the break rule stays on WorkTime(), never live.
    [Fact]
    public void Breaks_UseWorkTime_NotTheLiveMinute()
    {
        DayRecord day = new DayRecord([E(EntryKind.Arrival, 8, 0)]);

        Assert.Null(WorkingTimeChecks.Check(day, null, BreaksOnly, new TimeOnly(18, 0)).BreakMissing);
    }

    // AC-2
    [Fact]
    public void Work_ExactlyTenHours_NoWarning_OneMinuteMore_Warns()
    {
        Assert.Null(WorkingTimeChecks.Check(LongDay(0), null, WorkOnly).OverWorked);
        Assert.Equal(new TimeSpan(10, 1, 0), WorkingTimeChecks.Check(LongDay(1), null, WorkOnly).OverWorked);
        Assert.False(WorkingTimeChecks.Check(LongDay(0), null, WorkingTimeCheckOptions.All).HasWarning);
    }

    // AC-2
    [Fact]
    public void Work_Off_NoWarning()
    {
        Assert.Null(WorkingTimeChecks.Check(LongDay(30), null, new WorkingTimeCheckOptions(true, false, true)).OverWorked);
    }

    // AC-2: a live day counts up to the minute.
    [Fact]
    public void Work_LiveMinute_UsesWorkTimeAt()
    {
        DayRecord day = new DayRecord([E(EntryKind.Arrival, 7, 0)]);

        Assert.Null(WorkingTimeChecks.Check(day, null, WorkOnly, new TimeOnly(17, 0)).OverWorked);
        Assert.Equal(new TimeSpan(10, 25, 0), WorkingTimeChecks.Check(day, null, WorkOnly, new TimeOnly(17, 25)).OverWorked);
        Assert.Null(WorkingTimeChecks.Check(day, null, WorkOnly).OverWorked);
    }

    // AC-3
    [Fact]
    public void Rest_ExactlyElevenHours_NoWarning_OneMinuteLess_Warns()
    {
        DayRecord previous = Span(8, 0, 20, 0);

        Assert.Null(WorkingTimeChecks.Check(Span(7, 0, 12, 0), previous, RestOnly).ShortRest);
        Assert.Equal(new TimeSpan(10, 59, 0), WorkingTimeChecks.Check(Span(6, 59, 12, 0), previous, RestOnly).ShortRest);
    }

    // AC-3
    [Fact]
    public void Rest_MissingAbsenceOrNoLeavingPreviousDay_NoWarning()
    {
        DayRecord day = Span(6, 0, 12, 0);

        Assert.Null(WorkingTimeChecks.Check(day, null, RestOnly).ShortRest);
        Assert.Null(WorkingTimeChecks.Check(day, DayRecord.Empty, RestOnly).ShortRest);
        Assert.Null(WorkingTimeChecks.Check(day, DayRecord.Empty.WithAbsence(AbsenceKind.Vacation), RestOnly).ShortRest);
        Assert.Null(WorkingTimeChecks.Check(day, new DayRecord([E(EntryKind.Arrival, 20, 0)]), RestOnly).ShortRest);
    }

    // AC-3
    [Fact]
    public void Rest_PreviousDayWithTwoSpans_UsesTheLastLeaving()
    {
        DayRecord previous = new DayRecord(
        [
            E(EntryKind.Arrival, 8, 0), E(EntryKind.Leaving, 12, 0), E(EntryKind.Arrival, 20, 0), E(EntryKind.Leaving, 22, 30),
        ]);

        Assert.Equal(new TimeSpan(9, 30, 0), WorkingTimeChecks.Check(Span(8, 0, 12, 0), previous, RestOnly).ShortRest);
    }

    // AC-3
    [Fact]
    public void Rest_NoArrivalOnTheDay_OrOff_NoWarning()
    {
        DayRecord previous = Span(8, 0, 23, 0);
        DayRecord checkpointOnly = new DayRecord([new DayEntry(EntryKind.Checkpoint, new TimeOnly(6, 0), "x", null)]);

        Assert.Null(WorkingTimeChecks.Check(checkpointOnly, previous, RestOnly).ShortRest);
        Assert.Null(WorkingTimeChecks.Check(Span(6, 0, 12, 0), previous, WorkingTimeCheckOptions.None).ShortRest);
    }

    // AC-5
    [Fact]
    public void Absence_And_EmptyDay_HaveNoWarning()
    {
        Assert.False(WorkingTimeChecks.Check(DayRecord.Empty, Span(8, 0, 23, 0), WorkingTimeCheckOptions.All).HasWarning);
        Assert.False(WorkingTimeChecks.Check(DayRecord.Empty.WithAbsence(AbsenceKind.Sick), null, WorkingTimeCheckOptions.All).HasWarning);
    }

    // AC-5: 2026-10-27 is a Tuesday; its week runs 10-26 to 11-01.
    [Fact]
    public void WeekWarnings_ChecksMondayToSunday_EachAgainstItsOwnPreviousDay()
    {
        Dictionary<DateOnly, DayRecord?> files = new()
        {
            [new DateOnly(2026, 10, 25)] = Span(8, 0, 23, 0), // Sunday before: no warning itself, but short rest for Monday.
            [new DateOnly(2026, 10, 26)] = Span(7, 0, 12, 0),
            [new DateOnly(2026, 10, 27)] = Span(8, 0, 12, 0),
            [new DateOnly(2026, 10, 28)] = LongDay(5),
            [new DateOnly(2026, 10, 29)] = null, // unreadable
            [new DateOnly(2026, 10, 30)] = DayRecord.Empty.WithAbsence(AbsenceKind.Vacation),
            [new DateOnly(2026, 11, 1)] = LongDay(1),
            [new DateOnly(2026, 11, 2)] = LongDay(60), // next week: never checked
        };
        List<DateOnly> loaded = [];
        DayRecord? Load(DateOnly d)
        {
            loaded.Add(d);
            return files.TryGetValue(d, out DayRecord? r) ? r : DayRecord.Empty;
        }

        IReadOnlyList<DateOnly> warned = WorkingTimeChecks.WeekWarnings(new DateOnly(2026, 10, 27), Load, WorkingTimeCheckOptions.All);

        Assert.Equal([new DateOnly(2026, 10, 26), new DateOnly(2026, 10, 28), new DateOnly(2026, 11, 1)], warned);
        Assert.DoesNotContain(new DateOnly(2026, 11, 2), loaded);
        Assert.Equal(loaded.Count, loaded.Distinct().Count());
        Assert.Equal(["Mon", "Wed", "Sun"], warned.Select(WorkingTimeChecks.ShortName));
    }

    // AC-5: only the shown day uses the live minute.
    [Fact]
    public void WeekWarnings_LiveMinute_OnlyForTheShownDay()
    {
        DayRecord open = new DayRecord([E(EntryKind.Arrival, 7, 0)]);
        DayRecord? Load(DateOnly d) => d.Day is 27 or 28 ? open : DayRecord.Empty;

        IReadOnlyList<DateOnly> warned =
            WorkingTimeChecks.WeekWarnings(new DateOnly(2026, 10, 27), Load, WorkOnly, new TimeOnly(18, 0));

        Assert.Equal([new DateOnly(2026, 10, 27)], warned);
    }

    // AC-6: every check off gives no week warning.
    [Fact]
    public void WeekWarnings_AllOff_Nothing()
    {
        Assert.Empty(WorkingTimeChecks.WeekWarnings(new DateOnly(2026, 10, 27), _ => LongDay(60), WorkingTimeCheckOptions.None));
    }

    // Edges of the calendar never throw.
    [Fact]
    public void WeekWarnings_AtTheEdgesOfTheCalendar_DoesNotThrow()
    {
        Assert.Single(WorkingTimeChecks.WeekWarnings(DateOnly.MinValue, d => d == DateOnly.MinValue ? LongDay(5) : DayRecord.Empty, WorkingTimeCheckOptions.All));
        Assert.Single(WorkingTimeChecks.WeekWarnings(DateOnly.MaxValue, d => d == DateOnly.MaxValue ? LongDay(5) : DayRecord.Empty, WorkingTimeCheckOptions.All));
    }
}
