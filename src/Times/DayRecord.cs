using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Times;

/// <summary>Whether an entry books an arrival or a leaving.</summary>
internal enum EntryKind
{
    Arrival,
    Leaving,
}

/// <summary>One booking on a day: an arrival or a leaving at a local wall-clock time.</summary>
internal sealed record DayEntry
{
    public DayEntry(EntryKind kind, TimeOnly time)
    {
        Kind = kind;
        Time = time;
    }

    public EntryKind Kind { get; }

    public TimeOnly Time { get; }
}

/// <summary>
/// One day's bookings: every arrival and leaving, in ascending time order (ties keep their booking
/// order). Saved as <c>{"entries":[{"kind":"arrival","time":"08:15"},…]}</c>, with times as
/// invariant <c>HH:mm</c>. The legacy single-value format <c>{"arrival":"08:15","leaving":"17:30"}</c>
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

    /// <summary>
    /// Returns a new record with <paramref name="entry"/> added in time order, after any entries
    /// with the same time.
    /// </summary>
    public DayRecord Add(DayEntry entry) => new(entries.Append(entry));

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
                writer.WriteString(KindProperty, entry.Kind == EntryKind.Arrival ? ArrivalName : LeavingName);
                writer.WriteString(TimeProperty, entry.Time.ToString(TimeFormat, CultureInfo.InvariantCulture));
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
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
                default:
                    break;
            }
        }

        if (kind is not { } k || time is not { } t)
        {
            throw new JsonException("An entry in the day file needs a kind and a time.");
        }

        return new DayEntry(k, t);
    }

    private static EntryKind ReadKind(JsonElement value)
    {
        string? text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        return text switch
        {
            ArrivalName => EntryKind.Arrival,
            LeavingName => EntryKind.Leaving,
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
