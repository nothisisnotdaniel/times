using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Times;

/// <summary>What an entry books.</summary>
internal enum EntryKind
{
    Arrival,
    Leaving,
    BreakStart,
    BreakEnd,
    Checkpoint,
}

/// <summary>Why a day is off work. An absence day counts as exactly meeting the daily target.</summary>
internal enum AbsenceKind
{
    Vacation,
    Sick,
    PublicHoliday,
}

/// <summary>
/// One booking on a day at a local wall-clock time. Only a <see cref="EntryKind.Checkpoint"/> has a
/// <see cref="Name"/> (always valid, see <see cref="ProjectList.IsValidTitle"/>) and optionally the
/// Azure DevOps <see cref="WorkItem"/> it belongs to; every other kind has neither.
/// </summary>
internal sealed record DayEntry
{
    public DayEntry(EntryKind kind, TimeOnly time)
        : this(kind, time, null, null)
    {
    }

    /// <summary>
    /// Throws <see cref="ArgumentException"/> if a checkpoint has no valid name or a work item that is
    /// not positive, or another kind has a name or a work item.
    /// </summary>
    public DayEntry(EntryKind kind, TimeOnly time, string? name, int? workItem)
        : this(kind, time, name, workItem, false)
    {
    }

    /// <summary>
    /// Like <see cref="DayEntry(EntryKind, TimeOnly, string?, int?)"/>, and also throws
    /// <see cref="ArgumentException"/> if <paramref name="booked"/> is true on another kind than a
    /// checkpoint.
    /// </summary>
    public DayEntry(EntryKind kind, TimeOnly time, string? name, int? workItem, bool booked)
    {
        if (booked && kind != EntryKind.Checkpoint)
        {
            throw new ArgumentException("Only a checkpoint can be booked.", nameof(booked));
        }

        if (kind == EntryKind.Checkpoint)
        {
            if (name is null || !ProjectList.IsValidTitle(name))
            {
                throw new ArgumentException("A checkpoint needs a valid name.", nameof(name));
            }

            if (workItem is < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(workItem), "A work item must be positive.");
            }
        }
        else if (name is not null || workItem is not null)
        {
            throw new ArgumentException("Only a checkpoint has a name and a work item.", nameof(name));
        }

        Kind = kind;
        Time = time;
        Name = name;
        WorkItem = workItem;
        Booked = booked;
    }

    public EntryKind Kind { get; }

    public TimeOnly Time { get; }

    /// <summary>The checkpoint's name, or <c>null</c> for every other kind.</summary>
    public string? Name { get; }

    /// <summary>The Azure DevOps work item a checkpoint belongs to, or <c>null</c> if none.</summary>
    public int? WorkItem { get; }

    /// <summary>
    /// Whether a checkpoint is already entered in Azure DevOps; always <c>false</c> for every other kind.
    /// </summary>
    public bool Booked { get; }

    /// <summary>The same entry, kind, name, work item and booked flag at <paramref name="time"/>.</summary>
    public DayEntry At(TimeOnly time) => new(Kind, time, Name, WorkItem, Booked);

    /// <summary>The same entry with <paramref name="booked"/> as its booked flag.</summary>
    public DayEntry WithBooked(bool booked) => new(Kind, Time, Name, WorkItem, booked);
}

/// <summary>
/// A manual adjustment to the flexitime balance, booked on a day: signed whole <see cref="Minutes"/> (never zero, at
/// most <see cref="MaxMinutes"/> either way) and a <see cref="Reason"/> that passes <see cref="ProjectList.IsValidTitle"/>.
/// It is not an entry: it has no time of day and does not change the day's work time.
/// </summary>
internal sealed record BalanceCorrection
{
    /// <summary>The largest amount either way: 999:59.</summary>
    public const int MaxMinutes = 59_999;

    /// <summary>
    /// Throws <see cref="ArgumentException"/> if <paramref name="minutes"/> is zero or beyond
    /// <see cref="MaxMinutes"/> either way, or <paramref name="reason"/> is not a valid title.
    /// </summary>
    public BalanceCorrection(int minutes, string reason)
    {
        if (minutes == 0 || minutes > MaxMinutes || minutes < -MaxMinutes)
        {
            throw new ArgumentOutOfRangeException(nameof(minutes), "A correction is 1 to 59999 minutes either way.");
        }

        if (reason is null || !ProjectList.IsValidTitle(reason))
        {
            throw new ArgumentException("A correction needs a valid reason.", nameof(reason));
        }

        Minutes = minutes;
        Reason = reason;
    }

    /// <summary>The signed amount in whole minutes.</summary>
    public int Minutes { get; }

    /// <summary>Why the balance was corrected (user text: escape it before rendering).</summary>
    public string Reason { get; }

    /// <summary>The amount as a <see cref="TimeSpan"/>.</summary>
    public TimeSpan Amount => TimeSpan.FromMinutes(Minutes);

    /// <summary>
    /// Parses a typed amount: a required <c>+</c> or <c>-</c>, 1 to 3 ASCII digits of hours, <c>:</c>, and exactly
    /// 2 ASCII digits of minutes from 00 to 59, not zero. No culture, whitespace or other forms. Returns false otherwise.
    /// </summary>
    public static bool TryParseAmount(string text, out int minutes)
    {
        minutes = 0;
        if (text.Length < 5 || text.Length > 7 || (text[0] != '+' && text[0] != '-'))
        {
            return false;
        }

        int colon = text.Length - 3;
        if (text[colon] != ':')
        {
            return false;
        }

        int hours = 0;
        for (int i = 1; i < colon; i++)
        {
            if (!char.IsAsciiDigit(text[i]))
            {
                return false;
            }

            hours = (hours * 10) + (text[i] - '0');
        }

        if (!char.IsAsciiDigit(text[colon + 1]) || !char.IsAsciiDigit(text[colon + 2]))
        {
            return false;
        }

        int mins = ((text[colon + 1] - '0') * 10) + (text[colon + 2] - '0');
        if (mins > 59)
        {
            return false;
        }

        int total = (hours * 60) + mins;
        if (total == 0)
        {
            return false;
        }

        minutes = text[0] == '-' ? -total : total;
        return true;
    }
}

/// <summary>
/// One day's bookings: every entry, in ascending time order. Entries with the same time keep their
/// booking order, except that an arrival or break end always comes before a checkpoint at the same
/// time: every checkpoint booked before the last such opener of its time sits directly after that
/// opener, in booking order. The rule holds for every record, so also after <see cref="Add"/>,
/// <see cref="ChangeTime"/>, <see cref="RemoveAt"/> and <see cref="FromJson"/>. Saved
/// as <c>{"entries":[{"kind":"arrival","time":"08:15"},…]}</c>, with times as invariant <c>HH:mm</c>.
/// A checkpoint also has a <c>"name"</c>, if it belongs to a project a <c>"workItem"</c>, and, once it
/// is booked, <c>"booked":true</c> (left out while unbooked, so older files load as unbooked). The
/// legacy single-value format <c>{"arrival":"08:15","leaving":"17:30"}</c> still loads; it is
/// converted to entries and written in the new format on the next save. A day can instead be marked
/// as an absence (<see cref="Absence"/>), saved as a top-level <c>"absence"</c> (<c>"vacation"</c>,
/// <c>"sick"</c> or <c>"publicHoliday"</c>) before <c>"entries"</c>; it is left out when there is none.
/// A record holds an absence or entries, never both. Either way it may also hold balance corrections
/// (<see cref="Corrections"/>, in booking order), saved as a top-level
/// <c>"corrections":[{"minutes":150,"reason":"Starting balance"}]</c> after <c>"entries"</c> and left out when empty.
/// </summary>
internal sealed record DayRecord
{
    private const string TimeFormat = "HH:mm";
    private const string EntriesProperty = "entries";
    private const string KindProperty = "kind";
    private const string TimeProperty = "time";
    private const string ArrivalName = "arrival";
    private const string LeavingName = "leaving";
    private const string BreakStartName = "breakStart";
    private const string BreakEndName = "breakEnd";
    private const string CheckpointName = "checkpoint";
    private const string NameProperty = "name";
    private const string WorkItemProperty = "workItem";
    private const string BookedProperty = "booked";
    private const string AbsenceProperty = "absence";
    private const string VacationName = "vacation";
    private const string SickName = "sick";
    private const string PublicHolidayName = "publicHoliday";
    private const string CorrectionsProperty = "corrections";
    private const string MinutesProperty = "minutes";
    private const string ReasonProperty = "reason";

    private readonly DayEntry[] entries;
    private readonly BalanceCorrection[] corrections;

    public DayRecord()
        : this([])
    {
    }

    public DayRecord(IEnumerable<DayEntry> entries)
        : this(entries, null)
    {
    }

    /// <summary>
    /// A record with <paramref name="entries"/> and <paramref name="absence"/>. Throws
    /// <see cref="ArgumentException"/> if it would hold both an absence and entries.
    /// </summary>
    public DayRecord(IEnumerable<DayEntry> entries, AbsenceKind? absence)
        : this(entries, absence, [])
    {
    }

    /// <summary>
    /// A record with <paramref name="entries"/>, <paramref name="absence"/> and <paramref name="corrections"/> (kept
    /// in the given order). Throws <see cref="ArgumentException"/> if it would hold both an absence and entries.
    /// </summary>
    public DayRecord(IEnumerable<DayEntry> entries, AbsenceKind? absence, IEnumerable<BalanceCorrection> corrections)
    {
        DayEntry[] ordered = Order(entries);
        if (absence is not null && ordered.Length > 0)
        {
            throw new ArgumentException("A day holds an absence or entries, never both.", nameof(absence));
        }

        this.entries = ordered;
        this.corrections = corrections.ToArray();
        Absence = absence;
    }

    // Sorts by time, stable. Then, within each group with the same time, every checkpoint before the
    // group's last opener (arrival or break end) moves to directly after it, keeping its relative
    // order; nothing else moves.
    private static DayEntry[] Order(IEnumerable<DayEntry> entries)
    {
        DayEntry[] sorted = entries.OrderBy(e => e.Time).ToArray();
        List<DayEntry> result = new List<DayEntry>(sorted.Length);
        int start = 0;
        while (start < sorted.Length)
        {
            int end = SameTimeGroupEnd(sorted, start);
            AppendGroup(sorted, start, end, result);
            start = end;
        }

        return result.ToArray();
    }

    // The index after the last entry that has the same time as sorted[start].
    private static int SameTimeGroupEnd(DayEntry[] sorted, int start)
    {
        int end = start;
        while (end < sorted.Length && sorted[end].Time == sorted[start].Time)
        {
            end++;
        }

        return end;
    }

    // The index of the group's last arrival or break end, or -1 if it has none.
    private static int LastOpener(DayEntry[] sorted, int start, int end)
    {
        int lastOpener = -1;
        for (int i = start; i < end; i++)
        {
            if (IsOpener(sorted[i].Kind))
            {
                lastOpener = i;
            }
        }

        return lastOpener;
    }

    // Adds sorted[start..end] to result, with the checkpoints before the last opener moved after it.
    private static void AppendGroup(DayEntry[] sorted, int start, int end, List<DayEntry> result)
    {
        int lastOpener = LastOpener(sorted, start, end);
        List<DayEntry> moved = [];
        for (int i = start; i < end; i++)
        {
            DayEntry entry = sorted[i];
            if (i < lastOpener && entry.Kind == EntryKind.Checkpoint)
            {
                moved.Add(entry);
                continue;
            }

            result.Add(entry);
            if (i == lastOpener)
            {
                result.AddRange(moved);
            }
        }
    }

    private static bool IsOpener(EntryKind kind) => kind is EntryKind.Arrival or EntryKind.BreakEnd;

    public static DayRecord Empty { get; } = new();

    /// <summary>
    /// The entries in ascending time order. Entry number <c>n</c> is <c>Entries[n - 1]</c>.
    /// </summary>
    public IReadOnlyList<DayEntry> Entries => entries;

    /// <summary>True if the day has no entries, no absence and no corrections.</summary>
    public bool IsEmpty => entries.Length == 0 && Absence is null && corrections.Length == 0;

    /// <summary>The balance corrections in booking order. Correction number <c>n</c> is <c>Corrections[n - 1]</c>.</summary>
    public IReadOnlyList<BalanceCorrection> Corrections => corrections;

    /// <summary>The sum of every correction's amount.</summary>
    public TimeSpan CorrectionTotal => TimeSpan.FromMinutes(corrections.Sum(c => (long)c.Minutes));

    /// <summary>Returns the same day with <paramref name="correction"/> added after the other corrections.</summary>
    public DayRecord AddCorrection(BalanceCorrection correction) =>
        new(entries, Absence, corrections.Append(correction));

    /// <summary>
    /// Returns the same day without correction <paramref name="number"/> (1-based, the numbering of
    /// <see cref="Corrections"/>). Throws <see cref="ArgumentOutOfRangeException"/> if there is no such correction.
    /// </summary>
    public DayRecord RemoveCorrection(int number)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(number, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(number, corrections.Length);
        return new DayRecord(entries, Absence, corrections.Where((_, i) => i != number - 1));
    }

    /// <summary>The day's absence, or <c>null</c> if it is not marked as one.</summary>
    public AbsenceKind? Absence { get; }

    /// <summary>
    /// Returns the same day with <paramref name="absence"/> as its absence (<c>null</c> clears it). Throws
    /// <see cref="ArgumentException"/> if an absence is set on a day that has entries.
    /// </summary>
    public DayRecord WithAbsence(AbsenceKind? absence) => new(entries, absence, corrections);

    /// <summary>
    /// Returns the work time left at each entry's time, parallel to <see cref="Entries"/>. Walking the
    /// entries in time order, the day is <em>off</em>, <em>working</em> or <em>on break</em>; work
    /// time accrues between one entry and the next only while working. An arrival starts working when
    /// off, a break start pauses work while working, a break end resumes it while on break, and a
    /// leaving stops working or an open break; every other case, and every checkpoint, changes
    /// nothing. The value for an entry is <paramref name="target"/> (the daily work target) minus the work accrued up to its
    /// time. Nothing accrues after the last entry. Goes negative past the target.
    /// </summary>
    public IReadOnlyList<TimeSpan> TimeLeft(TimeSpan target)
    {
        TimeSpan[] result = new TimeSpan[entries.Length];
        TimeSpan left = target;
        int i = 0;
        foreach (Gap gap in Walk())
        {
            if (gap.State == WorkState.Working)
            {
                left -= gap.Length;
            }

            result[i++] = left;
        }

        return result;
    }

    /// <summary>
    /// Returns the total work time: the time accrued while working in the walk of
    /// <see cref="TimeLeft"/>, so the target minus the last time-left value.
    /// <see cref="TimeSpan.Zero"/> for an empty day. Nothing accrues after the last entry.
    /// </summary>
    public TimeSpan WorkTime() => Total(WorkState.Working);

    /// <summary>
    /// True when the walk of <see cref="TimeLeft"/> ends in the working state: the user arrived (or ended
    /// a break) and has neither started a break nor left since. False for an empty or absence day.
    /// </summary>
    public bool IsWorking => entries.Length > 0 && FinalState() == WorkState.Working;

    /// <summary>
    /// Returns the work time accrued up to <paramref name="now"/>: <see cref="WorkTime"/> plus the time since
    /// the last entry when the day <see cref="IsWorking"/> and <paramref name="now"/> is later than that entry.
    /// In every other case (on break, off, empty or absence day, <paramref name="now"/> at or before the last
    /// entry) it equals <see cref="WorkTime"/>.
    /// </summary>
    public TimeSpan WorkTimeAt(TimeOnly now)
    {
        TimeSpan work = WorkTime();
        if (!IsWorking)
        {
            return work;
        }

        TimeOnly last = entries[^1].Time;
        return now > last ? work + (now - last) : work;
    }

    /// <summary>The work time left at <paramref name="now"/>: <paramref name="target"/> minus <see cref="WorkTimeAt"/>.</summary>
    public TimeSpan TimeLeftAt(TimeSpan target, TimeOnly now) => target - WorkTimeAt(now);

    // The state the day is in after its last entry.
    private WorkState FinalState()
    {
        WorkState state = WorkState.Off;
        foreach (DayEntry entry in entries)
        {
            state = Next(state, entry.Kind);
        }

        return state;
    }

    /// <summary>
    /// Returns the total break time: the time between consecutive entries while on break in the walk
    /// of <see cref="TimeLeft"/> (a break start to the next break end, or to the leaving that closes
    /// an open break), plus every lunch gap. A lunch gap runs from a leaving that ends a span (moves
    /// the day from working or on break to off) to the next arrival that starts one (moves it from
    /// off to working); entries in between do not split it and add nothing on top of it. Other time
    /// while off, before the first span or after the last leaving, is not break time. Nothing accrues
    /// after the last entry, so a gap with no arrival after it yet counts nothing.
    /// </summary>
    public TimeSpan BreakTime()
    {
        TimeSpan total = TimeSpan.Zero;
        TimeSpan lunchGap = TimeSpan.Zero;
        bool afterSpan = false;
        int i = 0;
        foreach (Gap gap in Walk())
        {
            if (gap.State == WorkState.OnBreak)
            {
                total += gap.Length;
            }
            else if (gap.State == WorkState.Off && afterSpan)
            {
                lunchGap += gap.Length;
            }

            // Walk is parallel to entries: gap i is the time before entries[i], so this is the state
            // entries[i] moves the day into.
            WorkState next = Next(gap.State, entries[i++].Kind);
            if (gap.State == WorkState.Off && next == WorkState.Working)
            {
                total += lunchGap;
            }

            if (gap.State != WorkState.Off && next == WorkState.Off)
            {
                afterSpan = true;
            }

            if (next != WorkState.Off)
            {
                lunchGap = TimeSpan.Zero;
            }
        }

        return total;
    }

    /// <summary>
    /// Returns the break time required for <paramref name="worked"/>: <c>0:00</c> up to and including
    /// 6 hours, <c>0:30</c> for more than 6 up to and including 9 hours, and <c>0:45</c> for more
    /// than 9 hours.
    /// </summary>
    public static TimeSpan RequiredBreak(TimeSpan worked)
    {
        if (worked > TimeSpan.FromHours(9))
        {
            return TimeSpan.FromMinutes(45);
        }

        if (worked > TimeSpan.FromHours(6))
        {
            return TimeSpan.FromMinutes(30);
        }

        return TimeSpan.Zero;
    }

    /// <summary>
    /// Returns the 1-based numbers, ascending, of every break start that is never ended: its next
    /// entry among break starts, break ends and leavings is not a break end (another break start, a
    /// leaving, or none at all). Arrivals and checkpoints are skipped. Uses the entry order of
    /// <see cref="Entries"/>, so same-time entries count in that order. Empty when there is none.
    /// </summary>
    public IReadOnlyList<int> UnendedBreaks()
    {
        List<int> numbers = [];
        for (int i = 0; i < entries.Length; i++)
        {
            if (entries[i].Kind != EntryKind.BreakStart)
            {
                continue;
            }

            bool ended = false;
            for (int j = i + 1; j < entries.Length; j++)
            {
                EntryKind kind = entries[j].Kind;
                if (kind is EntryKind.BreakStart or EntryKind.BreakEnd or EntryKind.Leaving)
                {
                    ended = kind == EntryKind.BreakEnd;
                    break;
                }
            }

            if (!ended)
            {
                numbers.Add(i + 1);
            }
        }

        return numbers;
    }

    /// <summary>
    /// Returns the 1-based numbers, ascending, of every break start or break end booked while no span
    /// is open. Walking <see cref="Entries"/> in order, a span opens at an arrival and closes at the
    /// next leaving; an arrival inside an open span and a leaving outside one change nothing, and a
    /// span with no leaving stays open to the end of the day. Checkpoints are ignored. Empty when
    /// there is none.
    /// </summary>
    public IReadOnlyList<int> BreaksOutsideSpans()
    {
        List<int> numbers = [];
        bool open = false;
        for (int i = 0; i < entries.Length; i++)
        {
            switch (entries[i].Kind)
            {
                case EntryKind.Arrival:
                    open = true;
                    break;
                case EntryKind.Leaving:
                    open = false;
                    break;
                case EntryKind.BreakStart or EntryKind.BreakEnd:
                    if (!open)
                    {
                        numbers.Add(i + 1);
                    }

                    break;
            }
        }

        return numbers;
    }

    private TimeSpan Total(WorkState state)
    {
        TimeSpan total = TimeSpan.Zero;
        foreach (Gap gap in Walk())
        {
            if (gap.State == state)
            {
                total += gap.Length;
            }
        }

        return total;
    }

    // Parallel to entries: the time since the previous entry (zero for the first) and the state the
    // day was in during that time. The one place that walks the transition table.
    private IEnumerable<Gap> Walk()
    {
        WorkState state = WorkState.Off;
        for (int i = 0; i < entries.Length; i++)
        {
            DayEntry entry = entries[i];

            // Entries are sorted, so the previous entry is never after this one.
            TimeSpan length = i == 0 ? TimeSpan.Zero : entry.Time - entries[i - 1].Time;
            yield return new Gap(state, length);
            state = Next(state, entry.Kind);
        }
    }

    private readonly record struct Gap
    {
        public Gap(WorkState state, TimeSpan length)
        {
            State = state;
            Length = length;
        }

        public WorkState State { get; }

        public TimeSpan Length { get; }
    }

    private enum WorkState
    {
        Off,
        Working,
        OnBreak,
    }

    private static WorkState Next(WorkState state, EntryKind kind) => (state, kind) switch
    {
        (WorkState.Off, EntryKind.Arrival) => WorkState.Working,
        (WorkState.Working, EntryKind.BreakStart) => WorkState.OnBreak,
        (WorkState.OnBreak, EntryKind.BreakEnd) => WorkState.Working,
        (WorkState.Working or WorkState.OnBreak, EntryKind.Leaving) => WorkState.Off,
        _ => state,
    };

    /// <summary>
    /// Returns a new record with <paramref name="entry"/> added in time order, after any entries
    /// with the same time, except that an added arrival or break end goes before any checkpoints
    /// already at that time (see <see cref="DayRecord"/>). Throws <see cref="ArgumentException"/> on an
    /// absence day.
    /// </summary>
    public DayRecord Add(DayEntry entry) => new(entries.Append(entry), Absence, corrections);

    /// <summary>
    /// Returns a new record without entry <paramref name="number"/> (1-based, the numbering of
    /// <see cref="Entries"/>). Throws <see cref="ArgumentOutOfRangeException"/> if there is no such
    /// entry.
    /// </summary>
    public DayRecord RemoveAt(int number)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(number, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(number, entries.Length);
        return new DayRecord(entries.Where((_, i) => i != number - 1), Absence, corrections);
    }

    /// <summary>
    /// Returns a new record where entry <paramref name="number"/> (1-based, the numbering of
    /// <see cref="Entries"/>) has <paramref name="time"/> and keeps its kind, name and work item. The
    /// result is in time order, with the changed entry after any other entries with the same time,
    /// except that a changed arrival or break end goes before any checkpoints at that time (see
    /// <see cref="DayRecord"/>). Throws <see cref="ArgumentOutOfRangeException"/> if there is no such
    /// entry.
    /// </summary>
    public DayRecord ChangeTime(int number, TimeOnly time)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(number, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(number, entries.Length);
        return RemoveAt(number).Add(entries[number - 1].At(time));
    }

    /// <summary>
    /// Returns a new record where entry <paramref name="number"/> (1-based, the numbering of
    /// <see cref="Entries"/>) has <paramref name="booked"/> as its booked flag; every entry keeps its
    /// place. Throws <see cref="ArgumentOutOfRangeException"/> if there is no such entry and
    /// <see cref="ArgumentException"/> if it is not a checkpoint.
    /// </summary>
    public DayRecord SetBooked(int number, bool booked)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(number, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(number, entries.Length);
        DayEntry entry = entries[number - 1];
        if (entry.Kind != EntryKind.Checkpoint)
        {
            throw new ArgumentException("Only a checkpoint can be booked.", nameof(number));
        }

        DayEntry[] changed = (DayEntry[])entries.Clone();
        changed[number - 1] = entry.WithBooked(booked);
        return new DayRecord(changed, Absence, corrections);
    }

    public bool Equals(DayRecord? other) =>
        other is not null && Absence == other.Absence && entries.SequenceEqual(other.entries)
        && corrections.SequenceEqual(other.corrections);

    public override int GetHashCode()
    {
        HashCode hash = new HashCode();
        hash.Add(Absence);
        foreach (DayEntry entry in entries)
        {
            hash.Add(entry);
        }

        foreach (BalanceCorrection correction in corrections)
        {
            hash.Add(correction);
        }

        return hash.ToHashCode();
    }

    public string ToJson()
    {
        using MemoryStream stream = new MemoryStream();
        using (Utf8JsonWriter writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            if (Absence is { } absence)
            {
                writer.WriteString(AbsenceProperty, AbsenceName(absence));
            }

            writer.WriteStartArray(EntriesProperty);
            foreach (DayEntry entry in entries)
            {
                writer.WriteStartObject();
                writer.WriteString(KindProperty, KindName(entry.Kind));
                writer.WriteString(TimeProperty, entry.Time.ToString(TimeFormat, CultureInfo.InvariantCulture));
                if (entry.Name is { } name)
                {
                    writer.WriteString(NameProperty, name);
                }

                if (entry.WorkItem is { } workItem)
                {
                    writer.WriteNumber(WorkItemProperty, workItem);
                }

                if (entry.Booked)
                {
                    writer.WriteBoolean(BookedProperty, true);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            if (corrections.Length > 0)
            {
                writer.WriteStartArray(CorrectionsProperty);
                foreach (BalanceCorrection correction in corrections)
                {
                    writer.WriteStartObject();
                    writer.WriteNumber(MinutesProperty, correction.Minutes);
                    writer.WriteString(ReasonProperty, correction.Reason);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string KindName(EntryKind kind) => kind switch
    {
        EntryKind.Arrival => ArrivalName,
        EntryKind.Leaving => LeavingName,
        EntryKind.BreakStart => BreakStartName,
        EntryKind.BreakEnd => BreakEndName,
        EntryKind.Checkpoint => CheckpointName,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static string AbsenceName(AbsenceKind absence) => absence switch
    {
        AbsenceKind.Vacation => VacationName,
        AbsenceKind.Sick => SickName,
        AbsenceKind.PublicHoliday => PublicHolidayName,
        _ => throw new ArgumentOutOfRangeException(nameof(absence)),
    };

    private static AbsenceKind ReadAbsence(JsonElement value)
    {
        string? text = ReadStringOrNull(value, "The day file has an unknown absence.");
        return text switch
        {
            VacationName => AbsenceKind.Vacation,
            SickName => AbsenceKind.Sick,
            PublicHolidayName => AbsenceKind.PublicHoliday,
            _ => throw new JsonException("The day file has an unknown absence."),
        };
    }

    /// <summary>
    /// Reads a day file's contents, in the new or the legacy format. Throws
    /// <see cref="JsonException"/> if they are not a valid day; never returns a partial day.
    /// </summary>
    public static DayRecord FromJson(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("The day file holds no day.");
        }

        JsonElement? entriesElement = null;
        AbsenceKind? absence = null;
        List<BalanceCorrection> corrections = [];
        TimeOnly? arrival = null;
        TimeOnly? leaving = null;
        bool hasLegacy = false;
        HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in root.EnumerateObject())
        {
            string propertyName = ReadPropertyName(property);
            if (!seen.Add(propertyName))
            {
                throw new JsonException($"The day file has \"{propertyName}\" more than once.");
            }

            switch (propertyName)
            {
                case EntriesProperty:
                    entriesElement = property.Value;
                    break;
                case AbsenceProperty:
                    absence = ReadAbsence(property.Value);
                    break;
                case CorrectionsProperty:
                    corrections = ReadCorrections(property.Value);
                    break;
                case ArrivalName:
                    hasLegacy = true;
                    arrival = ReadOptionalTime(property.Value);
                    break;
                case LeavingName:
                    hasLegacy = true;
                    leaving = ReadOptionalTime(property.Value);
                    break;
                default:
                    // Unknown properties are ignored, as before.
                    break;
            }
        }

        if (absence is { } kind)
        {
            if (hasLegacy)
            {
                throw new JsonException("The day file mixes an absence with a legacy arrival or leaving.");
            }

            if (entriesElement is { } absenceList && ReadEntries(absenceList).Count > 0)
            {
                throw new JsonException("The day file mixes an absence with entries.");
            }

            return new DayRecord([], kind, corrections);
        }

        if (entriesElement is { } list)
        {
            if (hasLegacy)
            {
                throw new JsonException("The day file mixes entries with a legacy arrival or leaving.");
            }

            return new DayRecord(ReadEntries(list), null, corrections);
        }

        List<DayEntry> legacy = [];
        if (arrival is { } a)
        {
            legacy.Add(new DayEntry(EntryKind.Arrival, a));
        }

        if (leaving is { } l)
        {
            legacy.Add(new DayEntry(EntryKind.Leaving, l));
        }

        return new DayRecord(legacy, null, corrections);
    }

    private static List<BalanceCorrection> ReadCorrections(JsonElement list)
    {
        if (list.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("The day file's corrections are not a list.");
        }

        List<BalanceCorrection> result = [];
        foreach (JsonElement item in list.EnumerateArray())
        {
            result.Add(ReadCorrection(item));
        }

        return result;
    }

    // Strict: exactly "minutes" (a non-zero whole number within the limit) and "reason" (a valid title), nothing else.
    private static BalanceCorrection ReadCorrection(JsonElement item)
    {
        const string Broken = "A correction in the day file is not a valid amount and reason.";
        if (item.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException(Broken);
        }

        int? minutes = null;
        string? reason = null;
        foreach (JsonProperty property in item.EnumerateObject())
        {
            switch (ReadPropertyName(property))
            {
                case MinutesProperty:
                    if (minutes is not null
                        || property.Value.ValueKind != JsonValueKind.Number
                        || !property.Value.TryGetInt32(out int value)
                        || value == 0 || value > BalanceCorrection.MaxMinutes || value < -BalanceCorrection.MaxMinutes)
                    {
                        throw new JsonException(Broken);
                    }

                    minutes = value;
                    break;
                case ReasonProperty:
                    if (reason is not null)
                    {
                        throw new JsonException(Broken);
                    }

                    reason = ReadStringOrNull(property.Value, Broken);
                    if (reason is null || !ProjectList.IsValidTitle(reason))
                    {
                        throw new JsonException(Broken);
                    }

                    break;
                default:
                    throw new JsonException(Broken);
            }
        }

        if (minutes is not { } m || reason is null)
        {
            throw new JsonException(Broken);
        }

        return new BalanceCorrection(m, reason);
    }

    private static List<DayEntry> ReadEntries(JsonElement list)
    {
        if (list.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("The day file's entries are not a list.");
        }

        List<DayEntry> result = [];
        foreach (JsonElement item in list.EnumerateArray())
        {
            result.Add(ReadEntry(item));
        }

        return result;
    }

    private static DayEntry ReadEntry(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("An entry in the day file is not an object.");
        }

        EntryKind? kind = null;
        TimeOnly? time = null;
        JsonElement? nameElement = null;
        JsonElement? workItemElement = null;
        JsonElement? bookedElement = null;
        foreach (JsonProperty property in item.EnumerateObject())
        {
            switch (ReadPropertyName(property))
            {
                case KindProperty:
                    if (kind is not null)
                    {
                        throw new JsonException("An entry in the day file has more than one kind.");
                    }

                    kind = ReadKind(property.Value);
                    break;
                case TimeProperty:
                    if (time is not null)
                    {
                        throw new JsonException("An entry in the day file has more than one time.");
                    }

                    time = ReadTime(property.Value);
                    break;
                case NameProperty:
                    if (nameElement is not null)
                    {
                        throw new JsonException("An entry in the day file has more than one name.");
                    }

                    nameElement = property.Value;
                    break;
                case WorkItemProperty:
                    if (workItemElement is not null)
                    {
                        throw new JsonException("An entry in the day file has more than one work item.");
                    }

                    workItemElement = property.Value;
                    break;
                case BookedProperty:
                    if (bookedElement is not null)
                    {
                        throw new JsonException("An entry in the day file has more than one booked flag.");
                    }

                    bookedElement = property.Value;
                    break;
                default:
                    break;
            }
        }

        if (kind is not { } k || time is not { } t)
        {
            throw new JsonException("An entry in the day file needs a kind and a time.");
        }

        if (k != EntryKind.Checkpoint)
        {
            // Only checkpoints have a name, a work item and a booked flag; like other unknown properties, they are
            // ignored on every other kind.
            return new DayEntry(k, t);
        }

        if (nameElement is not { } n)
        {
            throw new JsonException("A checkpoint in the day file needs a name.");
        }

        int? workItem = workItemElement is { } w ? ReadWorkItem(w) : null;
        bool booked = bookedElement is { } b && ReadBooked(b);
        return new DayEntry(k, t, ReadName(n), workItem, booked);
    }

    private static bool ReadBooked(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw new JsonException("A booked flag in the day file is not true or false."),
    };

    private static string ReadName(JsonElement value)
    {
        string? name;
        try
        {
            name = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }
        catch (InvalidOperationException ex)
        {
            // An escaped lone surrogate such as \ud800 cannot be turned into a string.
            throw new JsonException("A checkpoint name in the day file is empty, too long or holds control, formatting or broken characters.", ex);
        }

        if (name is null || !ProjectList.IsValidTitle(name))
        {
            throw new JsonException("A checkpoint name in the day file is empty, too long or holds control, formatting or broken characters.");
        }

        return name;
    }

    private static int ReadWorkItem(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int workItem) || workItem < 1)
        {
            throw new JsonException("A work item in the day file is not a positive whole number.");
        }

        return workItem;
    }

    private static EntryKind ReadKind(JsonElement value)
    {
        string? text = ReadStringOrNull(value, "An entry in the day file has an unknown kind.");
        return text switch
        {
            ArrivalName => EntryKind.Arrival,
            LeavingName => EntryKind.Leaving,
            BreakStartName => EntryKind.BreakStart,
            BreakEndName => EntryKind.BreakEnd,
            CheckpointName => EntryKind.Checkpoint,
            _ => throw new JsonException("An entry in the day file has an unknown kind."),
        };
    }

    // Legacy values could be null (not booked), as the old reader allowed.
    private static TimeOnly? ReadOptionalTime(JsonElement value) =>
        value.ValueKind == JsonValueKind.Null ? null : ReadTime(value);

    private static TimeOnly ReadTime(JsonElement value)
    {
        string? text = ReadStringOrNull(value, "A time in the day file is not HH:mm.");
        if (text is null
            || !TimeOnly.TryParseExact(text, TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out TimeOnly time))
        {
            throw new JsonException("A time in the day file is not HH:mm.");
        }

        return time;
    }

    /// <summary>
    /// The property's name. An escaped lone surrogate such as <c>\ud800</c> cannot be turned into a
    /// string; that becomes a <see cref="JsonException"/>, never an <see cref="InvalidOperationException"/>.
    /// </summary>
    private static string ReadPropertyName(JsonProperty property)
    {
        try
        {
            return property.Name;
        }
        catch (InvalidOperationException ex)
        {
            throw new JsonException("A property name in the day file holds broken characters.", ex);
        }
    }

    /// <summary>
    /// The value as a string, or <c>null</c> if it is not a string. A string that cannot be read
    /// (such as an escaped lone surrogate) throws <see cref="JsonException"/> with <paramref name="message"/>.
    /// </summary>
    private static string? ReadStringOrNull(JsonElement value, string message)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        try
        {
            return value.GetString();
        }
        catch (InvalidOperationException ex)
        {
            throw new JsonException(message, ex);
        }
    }
}
