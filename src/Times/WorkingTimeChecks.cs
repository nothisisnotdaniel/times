using System.Globalization;

namespace Times;

/// <summary>Which working-time checks are on. Each one is an <c>OnOff</c> setting (see <see cref="SettingsRegistry"/>).</summary>
internal sealed record WorkingTimeCheckOptions
{
    public WorkingTimeCheckOptions(bool breaks, bool maxWork, bool rest)
    {
        Breaks = breaks;
        MaxWork = maxWork;
        Rest = rest;
    }

    /// <summary>Every check on.</summary>
    public static WorkingTimeCheckOptions All { get; } = new(true, true, true);

    /// <summary>Every check off.</summary>
    public static WorkingTimeCheckOptions None { get; } = new(false, false, false);

    /// <summary>The break check: taken break shorter than <see cref="DayRecord.RequiredBreak"/>.</summary>
    public bool Breaks { get; }

    /// <summary>The work check: more than <see cref="WorkingTimeChecks.MaxWork"/> of work.</summary>
    public bool MaxWork { get; }

    /// <summary>The rest check: less than <see cref="WorkingTimeChecks.MinRest"/> since the previous day's last leaving.</summary>
    public bool Rest { get; }
}

/// <summary>
/// The working-time warnings of one day. Pure: it reads no file and no clock. Each value is null when its check is
/// off or does not fail.
/// </summary>
internal sealed class WorkingTimeChecks
{
    /// <summary>The most work a day may hold without a warning (10:00; more than this warns).</summary>
    public static readonly TimeSpan MaxWork = TimeSpan.FromHours(10);

    /// <summary>The least rest between two working days without a warning (11:00; less than this warns).</summary>
    public static readonly TimeSpan MinRest = TimeSpan.FromHours(11);

    public WorkingTimeChecks(TimeSpan? breakMissing, TimeSpan? overWorked, TimeSpan? shortRest)
    {
        BreakMissing = breakMissing;
        OverWorked = overWorked;
        ShortRest = shortRest;
    }

    /// <summary>No warning.</summary>
    public static WorkingTimeChecks None { get; } = new(null, null, null);

    /// <summary>The break still missing (required minus taken), or null.</summary>
    public TimeSpan? BreakMissing { get; }

    /// <summary>The day's work time when it is over <see cref="MaxWork"/>, or null.</summary>
    public TimeSpan? OverWorked { get; }

    /// <summary>The rest since the previous day's last leaving when it is under <see cref="MinRest"/>, or null.</summary>
    public TimeSpan? ShortRest { get; }

    /// <summary>True when at least one check failed.</summary>
    public bool HasWarning => BreakMissing is not null || OverWorked is not null || ShortRest is not null;

    /// <summary>
    /// Checks <paramref name="day"/>. An absence or empty day has no warning.
    /// Breaks: taken (<see cref="DayRecord.BreakTime"/>) under <see cref="DayRecord.RequiredBreak"/> of
    /// <see cref="DayRecord.WorkTime"/> (never live). Work: <see cref="DayRecord.WorkTimeAt"/> of
    /// <paramref name="now"/> when it is set (the live minute of today), else <see cref="DayRecord.WorkTime"/>,
    /// over <see cref="MaxWork"/>. Rest: when the day has an arrival and <paramref name="previous"/> (the previous
    /// calendar day, or null when missing or unreadable) is not an absence day and holds a leaving, the time from
    /// its last leaving to midnight plus the first arrival, under <see cref="MinRest"/>.
    /// </summary>
    public static WorkingTimeChecks Check(DayRecord day, DayRecord? previous, WorkingTimeCheckOptions options, TimeOnly? now = null)
    {
        if (day.Absence is not null || day.Entries.Count == 0)
        {
            return None;
        }

        TimeSpan? breakMissing = null;
        if (options.Breaks)
        {
            TimeSpan taken = day.BreakTime();
            TimeSpan required = DayRecord.RequiredBreak(day.WorkTime());
            if (taken < required)
            {
                breakMissing = required - taken;
            }
        }

        TimeSpan? overWorked = null;
        if (options.MaxWork)
        {
            TimeSpan worked = now is { } live ? day.WorkTimeAt(live) : day.WorkTime();
            if (worked > MaxWork)
            {
                overWorked = worked;
            }
        }

        TimeSpan? shortRest = null;
        if (options.Rest && Rest(day, previous) is { } rest && rest < MinRest)
        {
            shortRest = rest;
        }

        return new WorkingTimeChecks(breakMissing, overWorked, shortRest);
    }

    /// <summary>
    /// The rest before <paramref name="day"/>: from <paramref name="previous"/>'s last leaving to midnight plus
    /// the day's first arrival. Null when the day has no arrival, or the previous day is null, an absence day or
    /// has no leaving.
    /// </summary>
    internal static TimeSpan? Rest(DayRecord day, DayRecord? previous)
    {
        if (previous is null || previous.Absence is not null)
        {
            return null;
        }

        DayEntry? lastLeaving = previous.Entries.LastOrDefault(e => e.Kind == EntryKind.Leaving);
        DayEntry? firstArrival = day.Entries.FirstOrDefault(e => e.Kind == EntryKind.Arrival);
        if (lastLeaving is null || firstArrival is null)
        {
            return null;
        }

        return TimeSpan.FromDays(1) - lastLeaving.Time.ToTimeSpan() + firstArrival.Time.ToTimeSpan();
    }

    /// <summary>
    /// The days of the week (Monday to Sunday) holding <paramref name="shown"/> that have at least one warning,
    /// Monday first. <paramref name="load"/> gives a day's record, or null when it cannot be read; a missing day is
    /// <see cref="DayRecord.Empty"/>. Each day is checked against its own previous calendar day. Only
    /// <paramref name="shown"/> uses <paramref name="now"/>. Days outside the <see cref="DateOnly"/> range are skipped.
    /// </summary>
    public static IReadOnlyList<DateOnly> WeekWarnings(
        DateOnly shown, Func<DateOnly, DayRecord?> load, WorkingTimeCheckOptions options, TimeOnly? now = null)
    {
        List<DateOnly> warned = [];
        if (!options.Breaks && !options.MaxWork && !options.Rest)
        {
            return warned;
        }

        int sinceMonday = ((int)shown.DayOfWeek + 6) % 7;
        int first = shown.DayNumber - sinceMonday;
        Dictionary<int, DayRecord?> cache = [];
        DayRecord? Get(int dayNumber)
        {
            if (dayNumber < DateOnly.MinValue.DayNumber || dayNumber > DateOnly.MaxValue.DayNumber)
            {
                return null;
            }

            if (!cache.TryGetValue(dayNumber, out DayRecord? record))
            {
                record = load(DateOnly.FromDayNumber(dayNumber));
                cache[dayNumber] = record;
            }

            return record;
        }

        for (int number = first; number < first + 7; number++)
        {
            // Get gives null outside the DateOnly range, so those days are skipped here too.
            DayRecord? day = Get(number);
            if (day is null || day.Absence is not null || day.Entries.Count == 0)
            {
                continue;
            }

            TimeOnly? live = number == shown.DayNumber ? now : null;
            if (Check(day, Get(number - 1), options, live).HasWarning)
            {
                warned.Add(DateOnly.FromDayNumber(number));
            }
        }

        return warned;
    }

    /// <summary>The three-letter invariant English name of a day (<c>Mon</c>).</summary>
    public static string ShortName(DateOnly day) => day.ToString("ddd", CultureInfo.InvariantCulture);
}
