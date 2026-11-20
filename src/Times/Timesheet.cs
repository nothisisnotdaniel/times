using System.Text.Json;

namespace Times;

/// <summary>One calendar day on a monthly timesheet.</summary>
internal sealed class TimesheetDay
{
    public TimesheetDay(
        DateOnly date,
        TimeOnly? arrival,
        TimeOnly? leaving,
        TimeSpan? breakTime,
        TimeSpan? workTime,
        AbsenceKind? absence,
        string? holiday,
        TimeSpan correctionTotal,
        IReadOnlyList<string> correctionReasons,
        bool unreadable)
    {
        Date = date;
        Arrival = arrival;
        Leaving = leaving;
        BreakTime = breakTime;
        WorkTime = workTime;
        Absence = absence;
        Holiday = holiday;
        CorrectionTotal = correctionTotal;
        CorrectionReasons = correctionReasons;
        Unreadable = unreadable;
    }

    public DateOnly Date { get; }

    /// <summary>The first <see cref="EntryKind.Arrival"/>, or null.</summary>
    public TimeOnly? Arrival { get; }

    /// <summary>The last <see cref="EntryKind.Leaving"/>, or null.</summary>
    public TimeOnly? Leaving { get; }

    /// <summary><see cref="DayRecord.BreakTime"/> for a day with entries, otherwise null.</summary>
    public TimeSpan? BreakTime { get; }

    /// <summary><see cref="DayRecord.WorkTime"/> for a day with entries, otherwise null.</summary>
    public TimeSpan? WorkTime { get; }

    public AbsenceKind? Absence { get; }

    /// <summary>The public holiday's name from the settings, or null.</summary>
    public string? Holiday { get; }

    /// <summary>The sum of the day's balance corrections (zero when there are none).</summary>
    public TimeSpan CorrectionTotal { get; }

    /// <summary>The reasons of the day's corrections, in their order.</summary>
    public IReadOnlyList<string> CorrectionReasons { get; }

    /// <summary>True if the day file is there but could not be read: the row shows nothing else and counts nothing.</summary>
    public bool Unreadable { get; }

    /// <summary>True if the day has no booking, absence or correction (no file, or an empty one).</summary>
    public bool IsEmpty => !Unreadable && Arrival is null && Leaving is null && WorkTime is null && Absence is null
        && CorrectionReasons.Count == 0;
}

/// <summary>
/// One month's hours per calendar day, with the month's work time and balance and the balance at its end.
/// Calculating it only reads: it never creates or changes a file or folder.
/// </summary>
internal sealed class Timesheet
{
    public Timesheet(
        int year,
        int month,
        TimeSpan target,
        IReadOnlyList<TimesheetDay> days,
        TimeSpan monthWork,
        TimeSpan monthBalance,
        FlexitimeBalance endBalance,
        int skippedFiles)
    {
        Year = year;
        Month = month;
        Target = target;
        Days = days;
        MonthWork = monthWork;
        MonthBalance = monthBalance;
        EndBalance = endBalance;
        SkippedFiles = skippedFiles;
    }

    public int Year { get; }

    public int Month { get; }

    /// <summary>The daily target every day was measured against.</summary>
    public TimeSpan Target { get; }

    /// <summary>One entry per calendar day of the month, in order.</summary>
    public IReadOnlyList<TimesheetDay> Days { get; }

    /// <summary>The sum of <see cref="DayRecord.WorkTime"/> over the month's readable days.</summary>
    public TimeSpan MonthWork { get; }

    /// <summary>The <see cref="FlexitimeBalance"/> rule over the month's days only.</summary>
    public TimeSpan MonthBalance { get; }

    /// <summary><see cref="FlexitimeBalance.Compute"/> up to the last day of the month.</summary>
    public FlexitimeBalance EndBalance { get; }

    /// <summary>The number of day files in the month that could not be read.</summary>
    public int SkippedFiles { get; }

    /// <summary>
    /// Builds the sheet for <paramref name="year"/>-<paramref name="month"/> from <paramref name="store"/>'s day files
    /// (<see cref="DayStore.Days"/>), with <paramref name="target"/> as every day's target and <paramref name="holiday"/>
    /// naming public holidays. An open day counts with <see cref="DayRecord.WorkTime"/> (no live minute). Throws
    /// <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> if the folder cannot be listed.
    /// </summary>
    public static Timesheet Calculate(DayStore store, int year, int month, TimeSpan target, Func<DateOnly, string?> holiday)
    {
        DateOnly first = new DateOnly(year, month, 1);
        DateOnly last = first.AddDays(DateTime.DaysInMonth(year, month) - 1);
        HashSet<DateOnly> listed = store.Days().Where(d => d >= first && d <= last).ToHashSet();

        List<TimesheetDay> days = [];
        TimeSpan work = TimeSpan.Zero;
        TimeSpan balance = TimeSpan.Zero;
        int skipped = 0;
        for (DateOnly day = first; day <= last; day = day.AddDays(1))
        {
            string? name = holiday(day);
            if (!listed.Contains(day))
            {
                days.Add(new TimesheetDay(day, null, null, null, null, null, name, TimeSpan.Zero, [], false));
                continue;
            }

            DayRecord record;
            try
            {
                record = store.Load(day);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException
                or InvalidOperationException or JsonException)
            {
                skipped++;
                days.Add(new TimesheetDay(day, null, null, null, null, null, name, TimeSpan.Zero, [], true));
                continue;
            }

            bool booked = record.Entries.Count > 0;
            TimeSpan? breakTime = booked ? record.BreakTime() : null;
            TimeSpan? workTime = booked ? record.WorkTime() : null;
            DayEntry? arrival = record.Entries.FirstOrDefault(e => e.Kind == EntryKind.Arrival);
            DayEntry? leaving = record.Entries.LastOrDefault(e => e.Kind == EntryKind.Leaving);
            work += record.WorkTime();

            // The FlexitimeBalance rule: corrections always add; an absence day has a delta of zero; a day with
            // an arrival adds its work time minus the target.
            balance += record.CorrectionTotal;
            if (record.Absence is null && arrival is not null)
            {
                balance += record.WorkTime() - target;
            }

            days.Add(new TimesheetDay(
                day,
                arrival?.Time,
                leaving?.Time,
                breakTime,
                workTime,
                record.Absence,
                name,
                record.CorrectionTotal,
                record.Corrections.Select(c => c.Reason).ToList(),
                false));
        }

        FlexitimeBalance end = FlexitimeBalance.Compute(store, last, target);
        return new Timesheet(year, month, target, days, work, balance, end, skipped);
    }
}
