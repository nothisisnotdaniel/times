using System.Globalization;
using System.Text.Json;

namespace Times.Tray;

/// <summary>The day's state as <c>tm status --json</c> reports it.</summary>
internal enum TrayState
{
    Out,
    Working,
    Break,
    Absent,
}

/// <summary>The checkpoint running while the user works.</summary>
internal sealed class TrayCheckpoint
{
    public TrayCheckpoint(string name, int? workItem, TimeOnly since)
    {
        Name = name;
        WorkItem = workItem;
        Since = since;
    }

    /// <summary>The name as <c>tm</c> gave it: untrusted, cleaned only when shown (<see cref="TrayText"/>).</summary>
    public string Name { get; }

    public int? WorkItem { get; }

    public TimeOnly Since { get; }
}

/// <summary>
/// One parsed <c>tm status --json</c> document (<c>schemaVersion</c> 1). <see cref="Parse"/> is strict: every
/// property must be there once with its type, so the tray never shows half-understood data.
/// </summary>
internal sealed class TrayStatus
{
    /// <summary>The one error for any document the tray cannot use; the raw text is never shown.</summary>
    public const string NotUnderstood = "Times answered in a way the tray does not understand.";

    /// <summary>The <c>schemaVersion</c> the tray reads.</summary>
    public const int SchemaVersion = 1;

    public TrayStatus(DateOnly date, TrayState state, TimeOnly? since, TrayCheckpoint? checkpoint, int workedMinutes,
        int targetMinutes, int leftMinutes, TimeOnly? arrival = null, string? holiday = null, TimeOnly? reminder = null)
    {
        Date = date;
        State = state;
        Since = since;
        Checkpoint = checkpoint;
        WorkedMinutes = workedMinutes;
        TargetMinutes = targetMinutes;
        LeftMinutes = leftMinutes;
        Arrival = arrival;
        Holiday = holiday;
        Reminder = reminder;
    }

    public DateOnly Date { get; }

    public TrayState State { get; }

    /// <summary>The time of the entry that started <see cref="State"/>, or <c>null</c>.</summary>
    public TimeOnly? Since { get; }

    /// <summary>The running checkpoint, only while <see cref="TrayState.Working"/>.</summary>
    public TrayCheckpoint? Checkpoint { get; }

    public int WorkedMinutes { get; }

    public int TargetMinutes { get; }

    /// <summary>Target minus worked minutes; negative past the target.</summary>
    public int LeftMinutes { get; }

    /// <summary>The day's first arrival, or <c>null</c> (also when an older <c>tm</c> leaves it out).</summary>
    public TimeOnly? Arrival { get; }

    /// <summary>Today's public holiday name as <c>tm</c> gave it (untrusted, never shown), or <c>null</c>.</summary>
    public string? Holiday { get; }

    /// <summary>From when on the check-in reminder is due; <c>null</c> when it is off or <c>tm</c> leaves it out.</summary>
    public TimeOnly? Reminder { get; }

    private static readonly string[] Required =
        ["schemaVersion", "date", "state", "since", "checkpoint", "workedMinutes", "targetMinutes", "leftMinutes"];

    /// <summary>
    /// Parses <paramref name="text"/>. Returns <c>null</c> for bad JSON, another <c>schemaVersion</c>, a
    /// missing, repeated or mistyped property, an unknown <c>state</c>, or a checkpoint outside <c>working</c>; the
    /// caller then shows <see cref="NotUnderstood"/>. The later, optional properties <c>arrival</c>, <c>holiday</c> and
    /// <c>reminder</c> may be missing (null, so the reminder is off), but a present one must be null or of its type
    /// (<c>HH:mm</c>, a string, <c>HH:mm</c>). Other properties are ignored.
    /// </summary>
    public static TrayStatus? Parse(string text)
    {
        try
        {
            JsonDocumentOptions options = new JsonDocumentOptions { MaxDepth = 4, AllowDuplicateProperties = false };
            using JsonDocument document = JsonDocument.Parse(text, options);
            return Read(document.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static TrayStatus? Read(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (string name in Required)
        {
            if (!root.TryGetProperty(name, out _))
            {
                return null;
            }
        }

        if (Int(root.GetProperty("schemaVersion")) != SchemaVersion)
        {
            return null;
        }

        JsonElement dateElement = root.GetProperty("date");
        if (dateElement.ValueKind != JsonValueKind.String
            || !DateOnly.TryParseExact(Text(dateElement), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out DateOnly date))
        {
            return null;
        }

        TrayState? state = ReadState(root.GetProperty("state"));
        if (state is null)
        {
            return null;
        }

        JsonElement sinceElement = root.GetProperty("since");
        TimeOnly? since = null;
        if (sinceElement.ValueKind != JsonValueKind.Null)
        {
            since = Time(sinceElement);
            if (since is null)
            {
                return null;
            }
        }

        JsonElement checkpointElement = root.GetProperty("checkpoint");
        TrayCheckpoint? checkpoint = null;
        if (checkpointElement.ValueKind != JsonValueKind.Null)
        {
            checkpoint = state == TrayState.Working ? ReadCheckpoint(checkpointElement) : null;
            if (checkpoint is null)
            {
                return null;
            }
        }

        int? worked = Int(root.GetProperty("workedMinutes"));
        int? target = Int(root.GetProperty("targetMinutes"));
        int? left = Int(root.GetProperty("leftMinutes"));
        if (worked is null || target is null || left is null)
        {
            return null;
        }

        if (!OptionalTime(root, "arrival", out TimeOnly? arrival) || !OptionalTime(root, "reminder", out TimeOnly? reminder))
        {
            return null;
        }

        string? holiday = null;
        if (root.TryGetProperty("holiday", out JsonElement holidayElement) && holidayElement.ValueKind != JsonValueKind.Null)
        {
            if (holidayElement.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            holiday = Text(holidayElement);
            if (holiday is null)
            {
                return null;
            }
        }

        return new TrayStatus(date, state.Value, since, checkpoint, worked.Value, target.Value, left.Value, arrival, holiday,
            reminder);
    }

    // Reads an optional HH:mm property: missing or null gives null; false when it is there with another value.
    private static bool OptionalTime(JsonElement root, string name, out TimeOnly? time)
    {
        time = null;
        if (!root.TryGetProperty(name, out JsonElement element) || element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        time = Time(element);
        return time is not null;
    }

    private static TrayCheckpoint? ReadCheckpoint(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty("name", out JsonElement name) || name.ValueKind != JsonValueKind.String
            || !element.TryGetProperty("workItem", out JsonElement workItemElement)
            || !element.TryGetProperty("since", out JsonElement sinceElement))
        {
            return null;
        }

        int? workItem = null;
        if (workItemElement.ValueKind != JsonValueKind.Null)
        {
            workItem = Int(workItemElement);
            if (workItem is null)
            {
                return null;
            }
        }

        string? text = Text(name);
        return Time(sinceElement) is { } since && !string.IsNullOrEmpty(text)
            ? new TrayCheckpoint(text, workItem, since)
            : null;
    }

    // A string element's text, or null when it cannot be decoded (a lone surrogate escape such as \ud800).
    private static string? Text(JsonElement element)
    {
        try
        {
            return element.GetString();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static TrayState? ReadState(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return Text(element) switch
        {
            "out" => TrayState.Out,
            "working" => TrayState.Working,
            "break" => TrayState.Break,
            "absent" => TrayState.Absent,
            _ => null,
        };
    }

    private static TimeOnly? Time(JsonElement element) =>
        element.ValueKind == JsonValueKind.String
        && TimeOnly.TryParseExact(Text(element), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None,
            out TimeOnly time)
            ? time
            : null;

    private static int? Int(JsonElement element) =>
        element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out int value) ? value : null;
}
