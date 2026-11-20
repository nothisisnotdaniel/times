using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Times;

/// <summary>
/// Today's state as one stable JSON document for other tools (<c>times status --json</c>, read by the tray app):
/// whether the user is out, working, on a break or absent, since when, the running checkpoint, and the work time
/// against the daily target. Property names and their order are fixed; <see cref="SchemaVersion"/> changes only on a
/// breaking change. Building it only reads: it never creates or changes a file or folder.
/// </summary>
internal static class StatusJson
{
    /// <summary>The version of the document's shape.</summary>
    public const int SchemaVersion = 1;

    /// <summary>
    /// Builds the document for <paramref name="day"/>'s record at <paramref name="now"/>, with a trailing <c>\n</c>.
    /// <c>since</c> is the time of <see cref="DayRecord.StateEntry"/> (null for an empty or absence day), and
    /// <c>checkpoint</c> is the last checkpoint that has no ending entry, only while working.
    /// </summary>
    public static string Build(DateOnly day, DayRecord record, TimeSpan target, TimeOnly now)
    {
        string state = State(record);
        using MemoryStream stream = new MemoryStream();
        JsonWriterOptions options = new JsonWriterOptions { Indented = true, NewLine = "\n" };
        using (Utf8JsonWriter writer = new Utf8JsonWriter(stream, options))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", SchemaVersion);
            writer.WriteString("date", day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            writer.WriteString("state", state);
            WriteTime(writer, "since", record.Absence is null ? record.StateEntry()?.Time : null);

            DayEntry? checkpoint = state == "working" ? OpenCheckpoint(record) : null;
            if (checkpoint is not null)
            {
                writer.WriteStartObject("checkpoint");
                writer.WriteString("name", checkpoint.Name);
                if (checkpoint.WorkItem is { } workItem)
                {
                    writer.WriteNumber("workItem", workItem);
                }
                else
                {
                    writer.WriteNull("workItem");
                }

                WriteTime(writer, "since", checkpoint.Time);
                writer.WriteEndObject();
            }
            else
            {
                writer.WriteNull("checkpoint");
            }

            writer.WriteNumber("workedMinutes", Minutes(record.WorkTimeAt(now)));
            writer.WriteNumber("targetMinutes", Minutes(target));
            writer.WriteNumber("leftMinutes", Minutes(record.TimeLeftAt(target, now)));
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }

    private static string State(DayRecord record)
    {
        if (record.Absence is not null)
        {
            return "absent";
        }

        if (record.IsWorking)
        {
            return "working";
        }

        return record.IsOnBreak ? "break" : "out";
    }

    // The last checkpoint that nothing has ended yet (CheckpointSummary.EndOf), or null.
    private static DayEntry? OpenCheckpoint(DayRecord record)
    {
        IReadOnlyList<DayEntry> entries = record.Entries;
        for (int i = entries.Count - 1; i >= 0; i--)
        {
            if (entries[i].Kind == EntryKind.Checkpoint)
            {
                return CheckpointSummary.EndOf(entries, i) is null ? entries[i] : null;
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

    // Whole minutes, rounded toward zero; times are booked to the minute and now has its seconds dropped.
    private static long Minutes(TimeSpan time) => time.Ticks / TimeSpan.TicksPerMinute;
}
