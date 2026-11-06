namespace Times.Tests;

public sealed class EndOfDayAlertsTests
{
    private static readonly DateOnly Day = new(2026, 10, 27);
    private static readonly TimeSpan Target = TimeSpan.FromHours(8);

    private static DayEntry At(EntryKind kind, int hour, int minute = 0) => new(kind, new TimeOnly(hour, minute));

    // Arrived at 8:00 and still working: work time at h:m is h:m - 8:00.
    private static DayRecord Working() => new DayRecord([At(EntryKind.Arrival, 8)]);

    private static TimeOnly T(int hour, int minute) => new(hour, minute);

    private static IReadOnlyList<EndOfDayAlert> Check(EndOfDayAlerts alerts, DayRecord day, TimeOnly now,
        bool targetOn = true, bool limitOn = true, DateOnly? on = null, TimeSpan? target = null) =>
        alerts.Check(on ?? Day, day, target ?? Target, now, targetOn, limitOn);

    // AC-1
    [Fact]
    public void Target_FiresAtAndAboveTheTarget_NotBelow()
    {
        Assert.Empty(Check(new EndOfDayAlerts(), Working(), T(15, 59)));
        Assert.Equal([EndOfDayAlert.Target], Check(new EndOfDayAlerts(), Working(), T(16, 0)));
        Assert.Equal([EndOfDayAlert.Target], Check(new EndOfDayAlerts(), Working(), T(16, 30)));
    }

    // AC-1
    [Fact]
    public void Limit_FiresAtAndAboveNineFortyFive_NotBelow()
    {
        Assert.Equal(new TimeSpan(9, 45, 0), EndOfDayAlerts.LimitWarning);
        Assert.Empty(Check(new EndOfDayAlerts(), Working(), T(17, 44), targetOn: false));
        Assert.Equal([EndOfDayAlert.Limit], Check(new EndOfDayAlerts(), Working(), T(17, 45), targetOn: false));
        Assert.Equal([EndOfDayAlert.Limit], Check(new EndOfDayAlerts(), Working(), T(18, 30), targetOn: false));
    }

    // AC-1: both reached on the same check fire together, target first.
    [Fact]
    public void BothReached_FireTogether_TargetFirst()
    {
        Assert.Equal([EndOfDayAlert.Target, EndOfDayAlert.Limit], Check(new EndOfDayAlerts(), Working(), T(18, 0)));
    }

    // AC-1
    [Fact]
    public void TargetZero_NeverFiresTarget_ButTheLimitStillFires()
    {
        EndOfDayAlerts alerts = new EndOfDayAlerts();

        Assert.Empty(Check(alerts, Working(), T(9, 0), target: TimeSpan.Zero));
        Assert.Equal([EndOfDayAlert.Limit], Check(alerts, Working(), T(18, 0), target: TimeSpan.Zero));
    }

    // AC-1: work time counts breaks out.
    [Fact]
    public void WorkTimeLeavesBreaksOut()
    {
        DayRecord day = new DayRecord([At(EntryKind.Arrival, 8), At(EntryKind.BreakStart, 12), At(EntryKind.BreakEnd, 12, 30)]);

        Assert.Empty(Check(new EndOfDayAlerts(), day, T(16, 29)));
        Assert.Equal([EndOfDayAlert.Target], Check(new EndOfDayAlerts(), day, T(16, 30)));
    }

    public static TheoryData<string> NotWorking => ["left", "break", "empty", "absence"];

    private static DayRecord NotWorkingDay(string kind) => kind switch
    {
        "left" => new DayRecord([At(EntryKind.Arrival, 6), At(EntryKind.Leaving, 17)]),
        "break" => new DayRecord([At(EntryKind.Arrival, 6), At(EntryKind.BreakStart, 17)]),
        "empty" => new DayRecord(),
        _ => new DayRecord([], AbsenceKind.Vacation),
    };

    // AC-1: a day that is not working fires nothing, even past both thresholds.
    [Theory]
    [MemberData(nameof(NotWorking))]
    public void NotWorking_FiresNothing(string kind)
    {
        Assert.Empty(Check(new EndOfDayAlerts(), NotWorkingDay(kind), T(19, 0)));
    }

    // AC-2
    [Fact]
    public void EachAlert_FiresOncePerDate()
    {
        EndOfDayAlerts alerts = new EndOfDayAlerts();

        Assert.Equal([EndOfDayAlert.Target], Check(alerts, Working(), T(16, 0)));
        Assert.Empty(Check(alerts, Working(), T(16, 1)));
        Assert.Equal([EndOfDayAlert.Limit], Check(alerts, Working(), T(17, 45)));
        Assert.Empty(Check(alerts, Working(), T(17, 46)));
        Assert.Empty(Check(alerts, Working(), T(19, 0)));
    }

    // AC-2: dropping below the threshold (a break booked later) and crossing it again fires nothing.
    [Fact]
    public void DroppingBelowAndCrossingAgain_FiresNothingMore()
    {
        EndOfDayAlerts alerts = new EndOfDayAlerts();
        Assert.Equal([EndOfDayAlert.Target], Check(alerts, Working(), T(16, 0)));

        DayRecord withBreak = new DayRecord([At(EntryKind.Arrival, 8), At(EntryKind.BreakStart, 12), At(EntryKind.BreakEnd, 13)]);
        Assert.Empty(Check(alerts, withBreak, T(16, 30)));
        Assert.Empty(Check(alerts, withBreak, T(17, 0)));
    }

    // AC-2: on a new date nothing counts as given.
    [Fact]
    public void NewDate_StartsWithNothingGiven()
    {
        EndOfDayAlerts alerts = new EndOfDayAlerts();
        Assert.Equal([EndOfDayAlert.Target, EndOfDayAlert.Limit], Check(alerts, Working(), T(18, 0)));

        DateOnly next = Day.AddDays(1);
        Assert.Equal([EndOfDayAlert.Target, EndOfDayAlert.Limit], Check(alerts, Working(), T(18, 0), on: next));
        Assert.Empty(Check(alerts, Working(), T(18, 1), on: next));
    }

    // AC-3
    [Fact]
    public void Prime_PastBothThresholds_LaterChecksFireNothing()
    {
        EndOfDayAlerts alerts = new EndOfDayAlerts();
        alerts.Prime(Day, Working(), Target, T(18, 0), true, true);

        Assert.Empty(Check(alerts, Working(), T(18, 1)));
        Assert.Empty(Check(alerts, Working(), T(20, 0)));
    }

    // AC-3: an alert not yet reached at the prime still fires later.
    [Fact]
    public void Prime_PastTheTargetOnly_TheLimitStillFiresLater()
    {
        EndOfDayAlerts alerts = new EndOfDayAlerts();
        alerts.Prime(Day, Working(), Target, T(16, 30), true, true);

        Assert.Empty(Check(alerts, Working(), T(16, 31)));
        Assert.Equal([EndOfDayAlert.Limit], Check(alerts, Working(), T(17, 45)));
    }

    // AC-3: before both thresholds the prime marks nothing.
    [Fact]
    public void Prime_BeforeBothThresholds_MarksNothing()
    {
        EndOfDayAlerts alerts = new EndOfDayAlerts();
        alerts.Prime(Day, Working(), Target, T(9, 0), true, true);

        Assert.Equal([EndOfDayAlert.Target], Check(alerts, Working(), T(16, 0)));
    }

    // AC-3: a day already left past the target counts as reached, so arriving again later rings nothing for it.
    [Fact]
    public void Prime_OnALeftDayPastTheTarget_MarksTheTarget()
    {
        EndOfDayAlerts alerts = new EndOfDayAlerts();
        DayRecord left = new DayRecord([At(EntryKind.Arrival, 8), At(EntryKind.Leaving, 16, 30)]);
        alerts.Prime(Day, left, Target, T(17, 0), true, true);

        DayRecord back = left.Add(At(EntryKind.Arrival, 17));
        Assert.Empty(Check(alerts, back, T(17, 5)));
        Assert.Equal([EndOfDayAlert.Limit], Check(alerts, back, T(18, 15)));
    }

    // AC-3: a prime on another date does not count for today.
    [Fact]
    public void Prime_OnAnotherDate_DoesNotCountForToday()
    {
        EndOfDayAlerts alerts = new EndOfDayAlerts();
        alerts.Prime(Day.AddDays(-1), Working(), Target, T(18, 0), true, true);

        Assert.Equal([EndOfDayAlert.Target, EndOfDayAlert.Limit], Check(alerts, Working(), T(18, 0)));
    }

    // AC-4
    [Fact]
    public void TargetOff_NeverFiresTarget_LimitOff_NeverFiresLimit()
    {
        Assert.Equal([EndOfDayAlert.Limit], Check(new EndOfDayAlerts(), Working(), T(18, 0), targetOn: false));
        Assert.Equal([EndOfDayAlert.Target], Check(new EndOfDayAlerts(), Working(), T(18, 0), limitOn: false));
        Assert.Empty(Check(new EndOfDayAlerts(), Working(), T(18, 0), targetOn: false, limitOn: false));
    }

    // AC-4: a check while off does not count as given; switched on again, it fires on the next check.
    [Fact]
    public void Off_ThenOnAgain_WhileOverTheThreshold_FiresOnTheNextCheck()
    {
        EndOfDayAlerts alerts = new EndOfDayAlerts();

        Assert.Empty(Check(alerts, Working(), T(18, 0), targetOn: false, limitOn: false));
        Assert.Empty(Check(alerts, Working(), T(18, 1), targetOn: false, limitOn: false));
        Assert.Equal([EndOfDayAlert.Target, EndOfDayAlert.Limit], Check(alerts, Working(), T(18, 2)));
        Assert.Empty(Check(alerts, Working(), T(18, 3)));
    }
}
