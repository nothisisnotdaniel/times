using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Times;

/// <summary>
/// Reads day files written by the old time tracking tool (format: <c>import-samples/README.md</c>):
/// <c>{"Recordings":[{"Time":"07:22:00","Type":0,"Description":null,"Booked":null,"UsedTime":null},…]}</c>.
/// <see cref="Scan"/> lists a folder's candidates, <see cref="Read"/> takes a candidate's bytes in and
/// <see cref="Parse"/> checks them as strictly as typed entries. Nothing here writes or shows anything.
/// </summary>
internal static partial class OldDataImport
{
    private const string RecordingsProperty = "Recordings";
    private const string TimeProperty = "Time";
    private const string TypeProperty = "Type";
    private const string DescriptionProperty = "Description";
    private const string BookedProperty = "Booked";
    private const string UsedTimeProperty = "UsedTime";
    private const string TimeFormat = "HH:mm:ss";
    private const int CheckpointType = 4;

    // Fixed reasons, so nothing from a foreign file reaches the output through them.
    internal const string NotJson = "not valid UTF-8 JSON";
    internal const string BrokenCharacters = "holds a broken character";
    internal const string NoRecordings = "has no Recordings list";
    internal const string EmptyRecordings = "Recordings is empty";
    internal const string TooLargeDay = "would make a day file larger than 64 KiB";

    private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    private static readonly Dictionary<int, EntryKind> Kinds = new Dictionary<int, EntryKind>
    {
        [0] = EntryKind.Arrival,
        [1] = EntryKind.Leaving,
        [2] = EntryKind.BreakStart,
        [3] = EntryKind.BreakEnd,
        [CheckpointType] = EntryKind.Checkpoint,
    };

    /// <summary>
    /// Lists the top-level entries of <paramref name="folder"/> (never recursive, never opens a file): the
    /// candidates whose names pass <see cref="DayStore.TryParseName"/>, by date, and every other name (folders
    /// included), in ordinal order. Throws <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/>
    /// if the folder cannot be listed.
    /// </summary>
    public static ImportScan Scan(string folder)
    {
        List<ImportCandidate> candidates = [];
        List<string> skipped = [];
        foreach (string path in Directory.EnumerateFileSystemEntries(folder))
        {
            string name = Path.GetFileName(path);
            if (DayStore.TryParseName(name, out DateOnly day))
            {
                candidates.Add(new ImportCandidate(day, name, path));
            }
            else
            {
                skipped.Add(name);
            }
        }

        candidates.Sort((a, b) => a.Day.CompareTo(b.Day));
        skipped.Sort(StringComparer.Ordinal);
        return new ImportScan(candidates, skipped);
    }

    /// <summary>
    /// The candidate's bytes through <see cref="CappedFile"/> (regular file only, at most
    /// <see cref="DayStore.MaxFileBytes"/>), or a fixed reason why it cannot be read.
    /// </summary>
    public static ImportRead Read(string path)
    {
        try
        {
            byte[]? bytes = CappedFile.Read(path, "The file", DayStore.MaxFileBytes, "too large");
            return bytes is null ? ImportRead.Failed("is gone") : ImportRead.Of(bytes);
        }
        catch (InvalidDataException ex)
        {
            return ImportRead.Failed(ex.Message == "too large" ? "is larger than 64 KiB" : "is not a regular file");
        }
        catch (FileNotFoundException)
        {
            return ImportRead.Failed("is gone");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ImportRead.Failed("cannot be read");
        }
    }

    /// <summary>
    /// Checks an old day file for <paramref name="day"/> and turns it into a <see cref="DayRecord"/>, with a
    /// warning for every checkpoint whose <c>UsedTime</c> does not match its entries. Any problem gives one
    /// fixed error reason (naming the 1-based entry number where there is one) and no record.
    /// </summary>
    public static ImportParse Parse(DateOnly day, byte[] bytes)
    {
        try
        {
            StrictUtf8.GetCharCount(bytes);
        }
        catch (DecoderFallbackException)
        {
            return ImportParse.Failed(NotJson);
        }

        // A leading byte order mark is valid UTF-8; JsonDocument does not take it.
        ReadOnlyMemory<byte> json = bytes.AsSpan().StartsWith(Utf8Bom) ? bytes.AsMemory(Utf8Bom.Length) : bytes;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowDuplicateProperties = false });
        }
        catch (JsonException)
        {
            return ImportParse.Failed(NotJson);
        }
        catch (InvalidOperationException)
        {
            // The duplicate-property check decodes property names; an escaped lone surrogate fails there.
            return ImportParse.Failed(BrokenCharacters);
        }

        using (document)
        {
            if (HasBrokenStrings(document.RootElement))
            {
                return ImportParse.Failed(BrokenCharacters);
            }

            return ParseRoot(day, document.RootElement);
        }
    }

    private static ImportParse ParseRoot(DateOnly day, JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty(RecordingsProperty, out JsonElement recordings)
            || recordings.ValueKind != JsonValueKind.Array)
        {
            return ImportParse.Failed(NoRecordings);
        }

        if (recordings.GetArrayLength() == 0)
        {
            return ImportParse.Failed(EmptyRecordings);
        }

        List<DayEntry> entries = [];
        Dictionary<DayEntry, JsonElement> usedTimes = new Dictionary<DayEntry, JsonElement>(ReferenceEqualityComparer.Instance);
        int number = 0;
        foreach (JsonElement item in recordings.EnumerateArray())
        {
            number++;
            if (ParseEntry(item, number, out DayEntry? entry, out JsonElement? usedTime) is { } error)
            {
                return ImportParse.Failed(error);
            }

            entries.Add(entry!);
            if (usedTime is { } used)
            {
                usedTimes[entry!] = used;
            }
        }

        DayRecord record = new DayRecord(entries);
        if (Encoding.UTF8.GetByteCount(record.ToJson()) > DayStore.MaxFileBytes)
        {
            return ImportParse.Failed(TooLargeDay);
        }

        return ImportParse.Of(record, Warnings(day, record, usedTimes));
    }

    // One entry, or the fixed reason it is refused.
    private static string? ParseEntry(JsonElement item, int number, out DayEntry? entry, out JsonElement? usedTime)
    {
        entry = null;
        usedTime = null;
        string at = "entry " + number.ToString(CultureInfo.InvariantCulture);
        if (item.ValueKind != JsonValueKind.Object)
        {
            return $"{at} is not an object";
        }

        if (!item.TryGetProperty(TimeProperty, out JsonElement timeElement)
            || ReadTime(timeElement) is not { } time)
        {
            return $"{at} has no Time as HH:mm:ss";
        }

        if (time.Second != 0)
        {
            return $"{at} has a Time with seconds";
        }

        if (!item.TryGetProperty(TypeProperty, out JsonElement typeElement)
            || typeElement.ValueKind != JsonValueKind.Number
            || !typeElement.TryGetInt32(out int type)
            || !Kinds.TryGetValue(type, out EntryKind kind))
        {
            return $"{at} has no Type from 0 to 4";
        }

        bool checkpoint = kind == EntryKind.Checkpoint;
        bool hasDescription = item.TryGetProperty(DescriptionProperty, out JsonElement description)
            && description.ValueKind != JsonValueKind.Null;
        string? name = null;
        if (checkpoint)
        {
            name = hasDescription && description.ValueKind == JsonValueKind.String ? description.GetString()!.Trim() : null;
            if (name is null || !ProjectList.IsValidTitle(name))
            {
                return $"{at} is a checkpoint without a valid Description";
            }
        }
        else if (hasDescription)
        {
            return $"{at} has a Description but is not a checkpoint";
        }

        bool booked = false;
        if (item.TryGetProperty(BookedProperty, out JsonElement bookedElement))
        {
            if (bookedElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null))
            {
                return $"{at} has a Booked that is not true, false or null";
            }

            booked = bookedElement.ValueKind == JsonValueKind.True;
            if (booked && !checkpoint)
            {
                return $"{at} is booked but is not a checkpoint";
            }
        }

        if (checkpoint && item.TryGetProperty(UsedTimeProperty, out JsonElement used) && used.ValueKind != JsonValueKind.Null)
        {
            usedTime = used;
        }

        entry = new DayEntry(kind, time, name, null, booked);
        return null;
    }

    // Exactly HH:mm:ss with ASCII digits, invariant; anything else is null.
    private static TimeOnly? ReadTime(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } text || !TimeText().IsMatch(text)
            || !TimeOnly.TryParseExact(text, TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out TimeOnly time))
        {
            return null;
        }

        return time;
    }

    // UsedTime is checked against the gap to the next entry in the record's order, never imported.
    private static List<ImportWarning> Warnings(DateOnly day, DayRecord record, Dictionary<DayEntry, JsonElement> usedTimes)
    {
        List<ImportWarning> warnings = [];
        IReadOnlyList<DayEntry> entries = record.Entries;
        for (int i = 0; i + 1 < entries.Count; i++)
        {
            DayEntry entry = entries[i];
            if (!usedTimes.TryGetValue(entry, out JsonElement used))
            {
                continue;
            }

            TimeSpan gap = entries[i + 1].Time - entry.Time;
            TimeSpan? stated = ReadDuration(used);
            if (stated != gap)
            {
                warnings.Add(new ImportWarning(day, entry.Time, entry.Name!, stated, gap));
            }
        }

        return warnings;
    }

    private static TimeSpan? ReadDuration(JsonElement value) =>
        ReadTime(value) is { } time ? time.ToTimeSpan() : null;

    // True if any property name or string in the tree cannot be turned into a string (an escaped lone surrogate).
    private static bool HasBrokenStrings(JsonElement element)
    {
        try
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.String:
                    _ = element.GetString();
                    return false;
                case JsonValueKind.Array:
                    foreach (JsonElement item in element.EnumerateArray())
                    {
                        if (HasBrokenStrings(item))
                        {
                            return true;
                        }
                    }

                    return false;
                case JsonValueKind.Object:
                    foreach (JsonProperty property in element.EnumerateObject())
                    {
                        _ = property.Name;
                        if (HasBrokenStrings(property.Value))
                        {
                            return true;
                        }
                    }

                    return false;
                default:
                    return false;
            }
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    /// <summary><c>h:mm</c> for a duration, invariant.</summary>
    internal static string Duration(TimeSpan value) =>
        ((int)value.TotalHours).ToString(CultureInfo.InvariantCulture) + ":" + value.Minutes.ToString("00", CultureInfo.InvariantCulture);

    [GeneratedRegex(@"^[0-9]{2}:[0-9]{2}:[0-9]{2}\z", RegexOptions.CultureInvariant)]
    private static partial Regex TimeText();
}

/// <summary>A file in the import folder whose name is a day file's name.</summary>
internal sealed record ImportCandidate
{
    public ImportCandidate(DateOnly day, string name, string path)
    {
        Day = day;
        Name = name;
        Path = path;
    }

    public DateOnly Day { get; }

    public string Name { get; }

    public string Path { get; }
}

/// <summary>What <see cref="OldDataImport.Scan"/> found: the candidates by date, and every other name in ordinal order.</summary>
internal sealed record ImportScan
{
    public ImportScan(IReadOnlyList<ImportCandidate> candidates, IReadOnlyList<string> skipped)
    {
        Candidates = candidates;
        Skipped = skipped;
    }

    public IReadOnlyList<ImportCandidate> Candidates { get; }

    public IReadOnlyList<string> Skipped { get; }
}

/// <summary>A candidate's bytes, or the fixed reason they could not be read.</summary>
internal sealed record ImportRead
{
    private ImportRead(byte[]? bytes, string? error)
    {
        Bytes = bytes;
        Error = error;
    }

    public byte[]? Bytes { get; }

    public string? Error { get; }

    public static ImportRead Of(byte[] bytes) => new(bytes, null);

    public static ImportRead Failed(string error) => new(null, error);
}

/// <summary>A parsed old day file: the record and its warnings, or one fixed error reason.</summary>
internal sealed record ImportParse
{
    private ImportParse(DayRecord? record, IReadOnlyList<ImportWarning> warnings, string? error)
    {
        Record = record;
        Warnings = warnings;
        Error = error;
    }

    public DayRecord? Record { get; }

    public IReadOnlyList<ImportWarning> Warnings { get; }

    public string? Error { get; }

    public static ImportParse Of(DayRecord record, IReadOnlyList<ImportWarning> warnings) => new(record, warnings, null);

    public static ImportParse Failed(string error) => new(null, [], error);
}

/// <summary>
/// A checkpoint whose <c>UsedTime</c> is not <c>HH:mm:ss</c> (<see cref="Stated"/> is <c>null</c>) or differs
/// from the <see cref="Actual"/> gap to the next entry.
/// </summary>
internal sealed record ImportWarning
{
    public ImportWarning(DateOnly day, TimeOnly time, string title, TimeSpan? stated, TimeSpan actual)
    {
        Day = day;
        Time = time;
        Title = title;
        Stated = stated;
        Actual = actual;
    }

    public DateOnly Day { get; }

    public TimeOnly Time { get; }

    public string Title { get; }

    public TimeSpan? Stated { get; }

    public TimeSpan Actual { get; }
}
