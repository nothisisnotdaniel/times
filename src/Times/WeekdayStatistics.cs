using System.Text.Json;

namespace Times;

/// <summary>
/// The averages over the counted days of one weekday, or over every counted day for the <c>All</c> row.
/// </summary>
internal sealed class WeekdayRow
{
    public WeekdayRow(DayOfWeek? weekday, int days, TimeOnly arrival, TimeOnly? leaving, TimeSpan? workTime)
    {
        Weekday = weekday;
        Days = days;
        Arrival = arrival;
        Leaving = leaving;
        WorkTime = workTime;
    }

    /// <summary>The row's weekday, or <c>null</c> for the <c>All</c> row.</summary>
    public DayOfWeek? Weekday { get; }

    /// <summary>The number of counted days (days with an arrival) in the row.</summary>
    public int Days { get; }

    /// <summary>The average first arrival, in whole minutes.</summary>
    public TimeOnly Arrival { get; }

    /// <summary>The average last leaving over the row's days that have one, or <c>null</c> if none has.</summary>
    public TimeOnly? Leaving { get; }

    /// <summary>The average work time over the row's days that have a leaving, or <c>null</c> if none has.</summary>
    public TimeSpan? WorkTime { get; }
}

/// <summary>
/// The average arrival, leaving and work time per weekday over an inclusive date range, from the day
/// files. A day counts if it holds an arrival. Computing it only reads: it never creates or changes a
/// file or folder.
/// </summary>
internal sealed class WeekdayStatistics
{
    public WeekdayStatistics(IReadOnlyList<WeekdayRow> rows, WeekdayRow? all, int skippedFiles)
    {
        Rows = rows;
        All = all;
        SkippedFiles = skippedFiles;
    }

    /// <summary>One row per weekday with at least one counted day, Monday first, Sunday last.</summary>
    public IReadOnlyList<WeekdayRow> Rows { get; }

    /// <summary>The row over every counted day in the range, or <c>null</c> if no day counted.</summary>
    public WeekdayRow? All { get; }

    /// <summary>The number of day files in the range that could not be read.</summary>
    public int SkippedFiles { get; }

    /// <summary>
    /// Averages every day file from <paramref name="from"/> to <paramref name="to"/> (both inclusive)
    /// that holds an arrival. Only the days <see cref="DayStore.Days"/> lists are loaded. A day's
    /// arrival is its first arrival, its leaving its last leaving, and only a day with a leaving has a
    /// leaving and a work time. Day files that cannot be read are skipped and counted. Throws
    /// <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> if the folder cannot be listed.
    /// </summary>
    public static WeekdayStatistics Calculate(DayStore store, DateOnly from, DateOnly to)
    {
        Dictionary<DayOfWeek, Accumulator> byWeekday = [];
        Accumulator all = new Accumulator();
        int skipped = 0;
        foreach (DateOnly day in store.Days())
        {
            if (day < from || day > to)
            {
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
                continue;
            }

            DayEntry? arrival = record.Entries.FirstOrDefault(e => e.Kind == EntryKind.Arrival);
            if (arrival is null)
            {
                continue;
            }

            DayEntry? leaving = record.Entries.LastOrDefault(e => e.Kind == EntryKind.Leaving);
            TimeSpan? work = leaving is null ? null : record.WorkTime();
            if (!byWeekday.TryGetValue(day.DayOfWeek, out Accumulator? row))
            {
                row = new Accumulator();
                byWeekday.Add(day.DayOfWeek, row);
            }

            row.Add(arrival.Time, leaving?.Time, work);
            all.Add(arrival.Time, leaving?.Time, work);
        }

        List<WeekdayRow> rows = [];
        foreach (KeyValuePair<DayOfWeek, Accumulator> pair in byWeekday.OrderBy(p => MondayFirst(p.Key)))
        {
            rows.Add(pair.Value.ToRow(pair.Key));
        }

        return new WeekdayStatistics(rows, all.Days == 0 ? null : all.ToRow(null), skipped);
    }

    /// <summary>
    /// The mean of <paramref name="sum"/> over <paramref name="count"/> (both non-negative, count above 0),
    /// rounded to the nearest whole number with half rounding up.
    /// </summary>
    internal static long RoundedMean(long sum, int count) => (2 * sum + count) / (2L * count);

    private static int MondayFirst(DayOfWeek day) => ((int)day + 6) % 7;

    private static int Minutes(TimeOnly time) => time.Hour * 60 + time.Minute;

    private sealed class Accumulator
    {
        private long arrivalMinutes;
        private long leavingMinutes;
        private long workTicks;
        private int closedDays;

        public int Days { get; private set; }

        public void Add(TimeOnly arrival, TimeOnly? leaving, TimeSpan? work)
        {
            Days++;
            arrivalMinutes += Minutes(arrival);
            if (leaving is { } left && work is { } worked)
            {
                closedDays++;
                leavingMinutes += Minutes(left);
                workTicks += worked.Ticks;
            }
        }

        public WeekdayRow ToRow(DayOfWeek? weekday)
        {
            TimeOnly arrival = TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(RoundedMean(arrivalMinutes, Days)));
            TimeOnly? leaving = closedDays == 0
                ? null
                : TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(RoundedMean(leavingMinutes, closedDays)));
            // Work times are summed exactly and only the mean is rounded to the minute (half up).
            TimeSpan? work = closedDays == 0
                ? null
                : TimeSpan.FromMinutes((2 * workTicks + closedDays * TimeSpan.TicksPerMinute) / (2L * closedDays * TimeSpan.TicksPerMinute));
            return new WeekdayRow(weekday, Days, arrival, leaving, work);
        }
    }
}
