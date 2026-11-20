using System.Globalization;
using System.Text.Json;

namespace Times;

/// <summary>One appointment from Context's <c>current --json</c>: its start, all-day flag and title.</summary>
internal sealed class CalendarAppointment
{
    public CalendarAppointment(DateTimeOffset start, bool allDay, string? title)
    {
        Start = start;
        AllDay = allDay;
        Title = title;
    }

    /// <summary>The start, as Context gave it (wall clock in its own offset).</summary>
    public DateTimeOffset Start { get; }

    public bool AllDay { get; }

    /// <summary>The title as Context gave it (outside text, unchecked), or <c>null</c>.</summary>
    public string? Title { get; }
}

/// <summary>What <see cref="CalendarMeetings.Offer"/> made of one appointment.</summary>
internal enum MeetingStatus
{
    /// <summary>Offered for booking, with a number.</summary>
    Offered,

    /// <summary>Today's file already holds this checkpoint (same time, name and work item); no number.</summary>
    AlreadyBooked,

    /// <summary>The title is missing or fails the checks typed names pass; never shown, only the fixed reason.</summary>
    NotOffered,
}

/// <summary>One row of the meetings list, in start order.</summary>
internal sealed class MeetingRow
{
    public MeetingRow(MeetingStatus status, int? number, TimeOnly time, string? name, Project? project, string? reason)
    {
        Status = status;
        Number = number;
        Time = time;
        Name = name;
        Project = project;
        Reason = reason;
    }

    public MeetingStatus Status { get; }

    /// <summary>The 1-based number the user types to book it; only for <see cref="MeetingStatus.Offered"/>.</summary>
    public int? Number { get; }

    /// <summary>The start's wall-clock minute (seconds dropped).</summary>
    public TimeOnly Time { get; }

    /// <summary>The checkpoint name (valid, see <see cref="ProjectList.IsValidTitle"/>); <c>null</c> when not offered.</summary>
    public string? Name { get; }

    /// <summary>The quicklist project a <c>#&lt;work item&gt;</c> token picked, or <c>null</c>.</summary>
    public Project? Project { get; }

    /// <summary>The fixed reason a meeting is not offered (never holds the title), or <c>null</c>.</summary>
    public string? Reason { get; }

    /// <summary>The checkpoint this row books.</summary>
    public DayEntry ToEntry() => new DayEntry(EntryKind.Checkpoint, Time, Name, Project?.WorkItem);
}

/// <summary>
/// Turns Context's <c>current --json</c> calendar into checkpoints offered for today. Pure: no output, clock or files.
/// </summary>
internal static class CalendarMeetings
{
    /// <summary>The start format Context writes.</summary>
    public const string StartFormat = "yyyy-MM-ddTHH:mm:sszzz";

    /// <summary>The reason for a null or blank title.</summary>
    public const string NoTitle = "it has no title.";

    /// <summary>The reason for a title longer than <see cref="ProjectList.MaxTitleLength"/>.</summary>
    public static readonly string TooLong =
        $"its title is longer than {ProjectList.MaxTitleLength.ToString(CultureInfo.InvariantCulture)} characters.";

    /// <summary>The reason for a title with control characters.</summary>
    public const string ControlCharacters = "its title contains control characters.";

    /// <summary>The reason for a title with invisible or broken characters.</summary>
    public const string InvisibleCharacters = "its title contains invisible formatting or broken characters.";

    /// <summary>
    /// The appointments in Context's order, or <c>null</c> when <paramref name="json"/> is not the contract: not JSON,
    /// a root that is not an object, no <c>appointments</c> array, an item that is not an object, a <c>start</c> that is
    /// missing or not a <see cref="StartFormat"/> string, an <c>allDay</c> that is missing or not a bool, or a
    /// <c>title</c> that is neither a string nor null. A missing <c>title</c> is null; other properties are ignored.
    /// </summary>
    public static IReadOnlyList<CalendarAppointment>? Parse(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("appointments", out JsonElement appointments)
                || appointments.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            List<CalendarAppointment> list = [];
            foreach (JsonElement item in appointments.EnumerateArray())
            {
                if (ParseItem(item) is not { } appointment)
                {
                    return null;
                }

                list.Add(appointment);
            }

            return list;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private static CalendarAppointment? ParseItem(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object
            || !item.TryGetProperty("start", out JsonElement startElement)
            || startElement.ValueKind != JsonValueKind.String
            || !DateTimeOffset.TryParseExact(startElement.GetString(), StartFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out DateTimeOffset start))
        {
            return null;
        }

        if (!item.TryGetProperty("allDay", out JsonElement allDay)
            || allDay.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return null;
        }

        string? title = null;
        if (item.TryGetProperty("title", out JsonElement titleElement) && titleElement.ValueKind != JsonValueKind.Null)
        {
            if (titleElement.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            title = titleElement.GetString();
        }

        return new CalendarAppointment(start, allDay.ValueKind == JsonValueKind.True, title);
    }

    /// <summary>
    /// The rows for <paramref name="today"/>: every appointment that is not all-day and starts on that date (wall clock
    /// in its own offset), sorted by start (ties keep Context's order). A title that fails the typed-name checks is
    /// <see cref="MeetingStatus.NotOffered"/>; one that matches a checkpoint already in <paramref name="day"/> (same
    /// time, name and work item) is <see cref="MeetingStatus.AlreadyBooked"/>; the rest are numbered 1..n. A
    /// <c>#&lt;work item&gt;</c> token picks a project from <paramref name="projects"/> (<c>null</c>: none is picked).
    /// </summary>
    public static IReadOnlyList<MeetingRow> Offer(IReadOnlyList<CalendarAppointment> appointments, DateOnly today,
        ProjectList? projects, DayRecord day)
    {
        List<MeetingRow> rows = [];
        int number = 0;
        IEnumerable<CalendarAppointment> todays = appointments
            .Where(a => !a.AllDay && DateOnly.FromDateTime(a.Start.DateTime) == today)
            .OrderBy(a => a.Start.DateTime.TimeOfDay);
        foreach (CalendarAppointment appointment in todays)
        {
            TimeOnly at = new TimeOnly(appointment.Start.Hour, appointment.Start.Minute);
            string? title = appointment.Title;
            if (TitleProblem(title) is { } reason)
            {
                rows.Add(new MeetingRow(MeetingStatus.NotOffered, null, at, null, null, reason));
                continue;
            }

            (string name, Project? project) = NameAndProject(title!, projects);
            if (!ProjectList.IsValidTitle(name))
            {
                rows.Add(new MeetingRow(MeetingStatus.NotOffered, null, at, null, null, InvisibleCharacters));
                continue;
            }

            int? workItem = project?.WorkItem;
            bool booked = day.Entries.Any(e => e.Kind == EntryKind.Checkpoint && e.Time == at
                && string.Equals(e.Name, name, StringComparison.Ordinal) && e.WorkItem == workItem);
            if (booked)
            {
                rows.Add(new MeetingRow(MeetingStatus.AlreadyBooked, null, at, name, project, null));
                continue;
            }

            number++;
            rows.Add(new MeetingRow(MeetingStatus.Offered, number, at, name, project, null));
        }

        return rows;
    }

    // The fixed reason a raw title cannot be a checkpoint name, or null when it passes ProjectList.IsValidTitle.
    private static string? TitleProblem(string? title)
    {
        if (title is null || string.IsNullOrWhiteSpace(title))
        {
            return NoTitle;
        }

        if (title.Length > ProjectList.MaxTitleLength)
        {
            return TooLong;
        }

        if (ProjectList.HasControlCharacters(title))
        {
            return ControlCharacters;
        }

        if (ProjectList.HasInvisibleOrBrokenCharacters(title))
        {
            return InvisibleCharacters;
        }

        return null;
    }

    // The first whole space-separated #<1-9 ASCII digits> token that is a quicklist work item picks that project and is
    // removed from the name; if nothing is left, the project's title is the name. Otherwise the trimmed title is the name.
    private static (string Name, Project? Project) NameAndProject(string title, ProjectList? projects)
    {
        if (projects is not null)
        {
            string[] tokens = title.Split(' ');
            for (int i = 0; i < tokens.Length; i++)
            {
                if (WorkItemToken(tokens[i]) is not { } workItem)
                {
                    continue;
                }

                Project? project = projects.Projects.FirstOrDefault(p => p.WorkItem == workItem);
                if (project is null)
                {
                    continue;
                }

                string rest = string.Join(' ', tokens.Take(i).Concat(tokens.Skip(i + 1))).Trim();
                return (rest.Length == 0 ? project.Title : rest, project);
            }
        }

        return (title.Trim(), null);
    }

    // The work item a token #<1-9 ASCII digits> names, or null for any other token.
    private static int? WorkItemToken(string token)
    {
        if (token.Length < 2 || token.Length > 10 || token[0] != '#')
        {
            return null;
        }

        for (int i = 1; i < token.Length; i++)
        {
            if (!char.IsAsciiDigit(token[i]))
            {
                return null;
            }
        }

        return int.Parse(token.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture);
    }
}
