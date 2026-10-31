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
    {
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
    }

    public EntryKind Kind { get; }

    public TimeOnly Time { get; }

    /// <summary>The checkpoint's name, or <c>null</c> for every other kind.</summary>
    public string? Name { get; }

    /// <summary>The Azure DevOps work item a checkpoint belongs to, or <c>null</c> if none.</summary>
    public int? WorkItem { get; }

    /// <summary>The same entry, kind, name and work item at <paramref name="time"/>.</summary>
    public DayEntry At(TimeOnly time) => new(Kind, time, Name, WorkItem);
}

/// <summary>
/// One day's bookings: every entry, in ascending time order (ties keep their booking order). Saved
/// as <c>{"entries":[{"kind":"arrival","time":"08:15"},…]}</c>, with times as invariant <c>HH:mm</c>.
/// A checkpoint also has a <c>"name"</c> and, if it belongs to a project, a <c>"workItem"</c>. The legacy single-value format <c>{"arrival":"08:15","leaving":"17:30"}</c>
/// still loads; it is converted to entries and written in the new format on the next save.
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

    private readonly DayEntry[] entries;

    public DayRecord()
        : this([])
    {
    }

    public DayRecord(IEnumerable<DayEntry> entries)
    {
        // OrderBy is stable, so entries with the same time keep the order they were given in.
        this.entries = entries.OrderBy(e => e.Time).ToArray();
    }

    public static DayRecord Empty { get; } = new();

    /// <summary>The entries in ascending time order. Entry number <c>n</c> is <c>Entries[n - 1]</c>.</summary>
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
        WorkState state = WorkState.Off;
        for (int i = 0; i < entries.Length; i++)
        {
            DayEntry entry = entries[i];
            if (state == WorkState.Working)
            {
                // Entries are sorted, so the previous entry is never after this one.
                left -= entry.Time - entries[i - 1].Time;
            }

            result[i] = left;
            state = Next(state, entry.Kind);
        }

        return result;
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
    /// with the same time.
    /// </summary>
    public DayRecord Add(DayEntry entry) => new(entries.Append(entry));

    /// <summary>
    /// Returns a new record without entry <paramref name="number"/> (1-based, the numbering of
    /// <see cref="Entries"/>). Throws <see cref="ArgumentOutOfRangeException"/> if there is no such entry.
    /// </summary>
    public DayRecord RemoveAt(int number)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(number, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(number, entries.Length);
        return new DayRecord(entries.Where((_, i) => i != number - 1));
    }

    /// <summary>
    /// Returns a new record where entry <paramref name="number"/> (1-based, the numbering of
    /// <see cref="Entries"/>) has <paramref name="time"/> and keeps its kind, name and work item. The result is in time
    /// order, with the changed entry after any other entries with the same time. Throws
    /// <see cref="ArgumentOutOfRangeException"/> if there is no such entry.
    /// </summary>
    public DayRecord ChangeTime(int number, TimeOnly time)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(number, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(number, entries.Length);
        return RemoveAt(number).Add(entries[number - 1].At(time));
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
            // Only checkpoints have a name and a work item; like other unknown properties, they are
            // ignored on every other kind.
            return new DayEntry(k, t);
        }

        if (nameElement is not { } n)
        {
            throw new JsonException("A checkpoint in the day file needs a name.");
        }

        int? workItem = workItemElement is { } w ? ReadWorkItem(w) : null;
        return new DayEntry(k, t, ReadName(n), workItem);
    }

    private static string ReadName(JsonElement value)
    {
        string? name = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        if (name is null || !ProjectList.IsValidTitle(name))
        {
            throw new JsonException("A checkpoint name in the day file is empty, too long or holds control characters.");
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
