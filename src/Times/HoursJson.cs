using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Times;

/// <summary>
/// The hours over an inclusive date range as one stable JSON document for other tools
/// (<c>times report --from A --to B --json</c>): each readable day's arrival, leaving and breaks, and
/// the rows of <see cref="ProjectReport"/>. Property names and their order are fixed; <see cref="SchemaVersion"/>
/// changes only on a breaking change. Building it only reads: it never creates or changes a file or folder.
/// </summary>
internal static class HoursJson
{
    /// <summary>The version of the document's shape.</summary>
    public const int SchemaVersion = 1;

    /// <summary>
    /// Builds the document for <paramref name="from"/> to <paramref name="to"/> (both inclusive), with a
    /// trailing <c>\n</c>. Only the days <see cref="DayStore.Days"/> lists are loaded; a day file that cannot
    /// be read is left out and counted in <c>skippedFiles</c>. A day equal to <paramref name="today"/> counts
    /// its running work and checkpoint up to <paramref name="now"/>. Throws <see cref="IOException"/> or
    /// <see cref="UnauthorizedAccessException"/> if the folder cannot be listed.
    /// </summary>
    /// <param name="stepMinutes">The <c>report-rounding</c> step in minutes, 0 for exact times.</param>
    /// <param name="projects">The quicklist for the titles, or <c>null</c> (titles fall back to the names).</param>
    public static string Build(DayStore store, DateOnly from, DateOnly to, DateOnly today, TimeOnly now, int stepMinutes,
        ProjectList? projects)
    {
        List<(DateOnly Day, DayRecord Record)> days = [];
        foreach (DateOnly day in store.Days())
        {
            if (day < from || day > to)
            {
                continue;
            }

            try
            {
                days.Add((day, store.Load(day)));
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException
                or InvalidOperationException or JsonException)
            {
                // Counted through the report below, which skips the same files.
            }
        }

        ProjectReport report = ProjectReport.Calculate(store, from, to, today, now, stepMinutes);

        using MemoryStream stream = new MemoryStream();
        JsonWriterOptions options = new JsonWriterOptions { Indented = true, NewLine = "\n" };
        using (Utf8JsonWriter writer = new Utf8JsonWriter(stream, options))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", SchemaVersion);
            writer.WriteString("from", Date(from));
            writer.WriteString("to", Date(to));
            writer.WriteNumber("roundedToMinutes", stepMinutes);
            writer.WriteNumber("skippedFiles", report.SkippedFiles);

            writer.WriteStartArray("days");
            foreach ((DateOnly day, DayRecord record) in days)
            {
                WriteDay(writer, day, record, day == today, now);
            }

            writer.WriteEndArray();

            long totalMinutes = 0;
            writer.WriteStartArray("workItems");
            foreach (ReportRow row in report.Rows)
            {
                long minutes = Minutes(row.Total);
                totalMinutes += minutes;
                writer.WriteStartObject();
                if (row.WorkItem is { } workItem)
                {
                    writer.WriteNumber("workItem", workItem);
                }
                else
                {
                    writer.WriteNull("workItem");
                }

                writer.WriteString("title", row.Title(projects));
                writer.WriteNumber("minutes", minutes);
                writer.WriteBoolean("open", row.Open);
                writer.WriteStartArray("checkpoints");
                foreach (string name in row.Names)
                {
                    writer.WriteStringValue(name);
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteNumber("totalMinutes", totalMinutes);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }

    private static void WriteDay(Utf8JsonWriter writer, DateOnly day, DayRecord record, bool isToday, TimeOnly now)
    {
        IReadOnlyList<DayEntry> entries = record.Entries;
        writer.WriteStartObject();
        writer.WriteString("date", Date(day));
        if (record.Absence is { } absence)
        {
            writer.WriteString("absence", AbsenceName(absence));
        }
        else
        {
            writer.WriteNull("absence");
        }

        WriteTime(writer, "arrival", entries.FirstOrDefault(e => e.Kind == EntryKind.Arrival)?.Time);
        WriteTime(writer, "leaving", entries.LastOrDefault(e => e.Kind == EntryKind.Leaving)?.Time);

        writer.WriteStartArray("breaks");
        for (int i = 0; i < entries.Count; i++)
        {
            if (entries[i].Kind != EntryKind.BreakStart)
            {
                continue;
            }

            writer.WriteStartObject();
            WriteTime(writer, "start", entries[i].Time);
            WriteTime(writer, "end", BreakEnd(entries, i));
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteNumber("breakMinutes", Minutes(record.BreakTime()));
        writer.WriteNumber("workMinutes", Minutes(isToday ? record.WorkTimeAt(now) : record.WorkTime()));
        writer.WriteEndObject();
    }

    // The time of the break end or leaving that closes the break starting at entries[start], or null when the
    // next break start, break end or leaving is another break start (or there is none). Same rule as
    // DayRecord.UnendedBreaks, but a closing leaving counts as the end.
    private static TimeOnly? BreakEnd(IReadOnlyList<DayEntry> entries, int start)
    {
        for (int j = start + 1; j < entries.Count; j++)
        {
            EntryKind kind = entries[j].Kind;
            if (kind is EntryKind.BreakEnd or EntryKind.Leaving)
            {
                return entries[j].Time;
            }

            if (kind == EntryKind.BreakStart)
            {
                return null;
            }
        }

        return null;
    }

    private static void WriteTime(Utf8JsonWriter writer, string name, TimeOnly? time)
    {
        if (time is { } value)
        {
            writer.WriteString(name, value.ToString("HH:mm", CultureInfo.InvariantCulture));
        }
        else
        {
            writer.WriteNull(name);
        }
    }

    private static string AbsenceName(AbsenceKind absence) => absence switch
    {
        AbsenceKind.Vacation => "vacation",
        AbsenceKind.Sick => "sick",
        _ => "publicHoliday",
    };

    // Whole minutes; times are booked to the minute and now has its seconds dropped, so nothing is lost.
    private static long Minutes(TimeSpan time) => time.Ticks / TimeSpan.TicksPerMinute;

    private static string Date(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
