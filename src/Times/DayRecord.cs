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
/// One day's bookings: every entry, in ascending time order. Entries with the same time keep their
/// booking order, except that an arrival or break end always comes before a checkpoint at the same
/// time: every checkpoint booked before the last such opener of its time sits directly after that
/// opener, in booking order. The rule holds for every record, so also after <see cref="Add"/>,
/// <see cref="ChangeTime"/>, <see cref="RemoveAt"/> and <see cref="FromJson"/>. Saved
/// as <c>{"entries":[{"kind":"arrival","time":"08:15"},…]}</c>, with times as invariant <c>HH:mm</c>.
/// A checkpoint also has a <c>"name"</c>, if it belongs to a project a <c>"workItem"</c>, and, once it
/// is booked, <c>"booked":true</c> (left out while unbooked, so older files load as unbooked). The
/// legacy single-value format <c>{"arrival":"08:15","leaving":"17:30"}</c> still loads; it is
/// converted to entries and written in the new format on the next save.
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

    private readonly DayEntry[] entries;

    public DayRecord()
        : this([])
    {
    }

    public DayRecord(IEnumerable<DayEntry> entries)
    {
        this.entries = Order(entries);
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

    public bool IsEmpty => entries.Length == 0;

    /// <summary>The work time a day starts with, before any span is subtracted.</summary>
    public static TimeSpan DailyTarget { get; } = TimeSpan.FromHours(8);

    /// <summary>
    /// Returns the work time left at each entry's time, parallel to <see cref="Entries"/>. Walking the
    /// entries in time order, the day is <em>off</em>, <em>working</em> or <em>on break</em>; work
    /// time accrues between one entry and the next only while working. An arrival starts working when
    /// off, a break start pauses work while working, a break end resumes it while on break, and a
    /// leaving stops working or an open break; every other case, and every checkpoint, changes
    /// nothing. The value for an entry is <see cref="DailyTarget"/> minus the work accrued up to its
    /// time. Nothing accrues after the last entry. Goes negative past the target.
    /// </summary>
    public IReadOnlyList<TimeSpan> TimeLeft()
    {
        TimeSpan[] result = new TimeSpan[entries.Length];
        TimeSpan left = DailyTarget;
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
    /// <see cref="TimeLeft"/>, so <see cref="DailyTarget"/> minus the last time-left value.
    /// <see cref="TimeSpan.Zero"/> for an empty day. Nothing accrues after the last entry.
    /// </summary>
    public TimeSpan WorkTime() => Total(WorkState.Working);

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
    /// already at that time (see <see cref="DayRecord"/>).
    /// </summary>
    public DayRecord Add(DayEntry entry) => new(entries.Append(entry));

    /// <summary>
    /// Returns a new record without entry <paramref name="number"/> (1-based, the numbering of
    /// <see cref="Entries"/>). Throws <see cref="ArgumentOutOfRangeException"/> if there is no such
    /// entry.
    /// </summary>
    public DayRecord RemoveAt(int number)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(number, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(number, entries.Length);
        return new DayRecord(entries.Where((_, i) => i != number - 1));
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
        return new DayRecord(changed);
    }

    public bool Equals(DayRecord? other) =>
        other is not null && entries.SequenceEqual(other.entries);

    public override int GetHashCode()
    {
        HashCode hash = new HashCode();
        foreach (DayEntry entry in entries)
        {
            hash.Add(entry);
        }

        return hash.ToHashCode();
    }

    public string ToJson()
    {
        using MemoryStream stream = new MemoryStream();
        using (Utf8JsonWriter writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
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
        TimeOnly? arrival = null;
        TimeOnly? leaving = null;
        bool hasLegacy = false;
        HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                throw new JsonException($"The day file has \"{property.Name}\" more than once.");
            }

            switch (property.Name)
            {
                case EntriesProperty:
                    entriesElement = property.Value;
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

        if (entriesElement is { } list)
        {
            if (hasLegacy)
            {
                throw new JsonException("The day file mixes entries with a legacy arrival or leaving.");
            }

            return new DayRecord(ReadEntries(list));
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

        return new DayRecord(legacy);
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
            switch (property.Name)
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
        string? text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
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
        string? text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        if (text is null
            || !TimeOnly.TryParseExact(text, TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out TimeOnly time))
        {
            throw new JsonException("A time in the day file is not HH:mm.");
        }

        return time;
    }
}
