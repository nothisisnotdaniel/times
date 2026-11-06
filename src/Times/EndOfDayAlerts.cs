namespace Times;

/// <summary>An alert the end-of-day check can give.</summary>
internal enum EndOfDayAlert
{
    /// <summary>Today's work time reached the daily target.</summary>
    Target,

    /// <summary>Today's work time reached <see cref="EndOfDayAlerts.LimitWarning"/>, 15 minutes before 10 hours.</summary>
    Limit,
}

/// <summary>
/// Decides when the end-of-day alerts fire, and remembers which were already given for which date, so each
/// fires at most once per date in a session. Pure logic: no clock, no files, no output.
/// </summary>
internal sealed class EndOfDayAlerts
{
    /// <summary>The work time at which <see cref="EndOfDayAlert.Limit"/> fires: 10 hours minus 15 minutes.</summary>
    public static readonly TimeSpan LimitWarning = new TimeSpan(9, 45, 0);

    private readonly HashSet<EndOfDayAlert> given = [];
    private DateOnly? date;

    /// <summary>
    /// Marks every alert already reached on <paramref name="day"/> at <paramref name="now"/> as given, without
    /// firing it, so starting Times past a threshold rings nothing for it. Reached means the work time is at or
    /// past the threshold, whether or not the day is still working and whether or not the alert is switched on.
    /// </summary>
    public void Prime(DateOnly on, DayRecord day, TimeSpan target, TimeOnly now, bool targetOn, bool limitOn)
    {
        MoveTo(on);
        TimeSpan work = day.WorkTimeAt(now);
        if (target > TimeSpan.Zero && work >= target)
        {
            given.Add(EndOfDayAlert.Target);
        }

        if (work >= LimitWarning)
        {
            given.Add(EndOfDayAlert.Limit);
        }
    }

    /// <summary>
    /// The alerts that fire now, target first: each one that is switched on, not given yet for
    /// <paramref name="on"/>, and reached while <paramref name="day"/> is working. They count as given from
    /// then on. An alert that is switched off is not marked, so it can still fire once it is switched on again.
    /// </summary>
    public IReadOnlyList<EndOfDayAlert> Check(DateOnly on, DayRecord day, TimeSpan target, TimeOnly now, bool targetOn, bool limitOn)
    {
        MoveTo(on);
        List<EndOfDayAlert> fired = [];
        if (!day.IsWorking)
        {
            return fired;
        }

        TimeSpan work = day.WorkTimeAt(now);
        if (targetOn && target > TimeSpan.Zero && work >= target && given.Add(EndOfDayAlert.Target))
        {
            fired.Add(EndOfDayAlert.Target);
        }

        if (limitOn && work >= LimitWarning && given.Add(EndOfDayAlert.Limit))
        {
            fired.Add(EndOfDayAlert.Limit);
        }

        return fired;
    }

    // A new date starts with nothing given.
    private void MoveTo(DateOnly on)
    {
        if (date != on)
        {
            date = on;
            given.Clear();
        }
    }
}
