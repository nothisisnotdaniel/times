using System.Text.Json;

namespace Times;

/// <summary>
/// One row of the project report: either a work item (every checkpoint booked on it over the range,
/// whatever its name) or, for checkpoints without a work item, one exact name.
/// </summary>
internal sealed class ReportRow
{
    public ReportRow(int? workItem, IReadOnlyList<string> names, TimeSpan total, bool open)
    {
        WorkItem = workItem;
        Names = names;
        Total = total;
        Open = open;
    }

    /// <summary>The row's work item, or <c>null</c> for a name row.</summary>
    public int? WorkItem { get; }

    /// <summary>The distinct checkpoint names in the row, in first-booked order over the range (exactly one for a name row).</summary>
    public IReadOnlyList<string> Names { get; }

    /// <summary>The sum of the time taken by the row's checkpoints over the range.</summary>
    public TimeSpan Total { get; }

    /// <summary>True if any of the row's checkpoints has no ending entry on a day that is not today.</summary>
    public bool Open { get; }

    /// <summary>
    /// The row's title: the quicklist title of its work item from <paramref name="projects"/>
    /// (<c>null</c>: none), otherwise its names joined with <c>", "</c>. Shared by the report table and the CSV export.
    /// </summary>
    public string Title(ProjectList? projects)
    {
        string? title = WorkItem is { } item ? projects?.Projects.FirstOrDefault(p => p.WorkItem == item)?.Title : null;
        return title ?? string.Join(", ", Names);
    }
}

/// <summary>
/// The checkpoint time per work item (and per name for checkpoints without one) over an inclusive
/// date range, summed from the day files with <see cref="CheckpointSummary"/>. Computing it only
/// reads: it never creates or changes a file or folder.
/// </summary>
internal sealed class ProjectReport
{
    public ProjectReport(IReadOnlyList<ReportRow> rows, int skippedFiles)
    {
        Rows = rows;
        SkippedFiles = skippedFiles;
        Total = rows.Aggregate(TimeSpan.Zero, (sum, row) => sum + row.Total);
    }

    /// <summary>The work-item rows in ascending work item order, then the name rows in ordinal name order.</summary>
    public IReadOnlyList<ReportRow> Rows { get; }

    /// <summary>The sum of every row's time.</summary>
    public TimeSpan Total { get; }

    /// <summary>The number of day files in the range that could not be read.</summary>
    public int SkippedFiles { get; }

    /// <summary>
    /// Sums the checkpoints of every day file from <paramref name="from"/> to <paramref name="to"/>
    /// (both inclusive). Only the days <see cref="DayStore.Days"/> lists are loaded, so the size of
    /// the range never matters. A day equal to <paramref name="today"/> counts an open checkpoint up
    /// to <paramref name="now"/>. Day files that cannot be read are skipped and counted. Throws
    /// <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> if the folder cannot be listed.
    /// </summary>
    /// <param name="now">The current local minute, seconds already dropped.</param>
    public static ProjectReport Calculate(DayStore store, DateOnly from, DateOnly to, DateOnly today, TimeOnly now)
    {
        Dictionary<int, Accumulator> byWorkItem = [];
        Dictionary<string, Accumulator> byName = new Dictionary<string, Accumulator>(StringComparer.Ordinal);
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

            foreach (CheckpointGroup group in CheckpointSummary.Calculate(record, day == today, now))
            {
                Accumulator? row;
                if (group.WorkItem is { } workItem)
                {
                    if (!byWorkItem.TryGetValue(workItem, out row))
                    {
                        row = new Accumulator();
                        byWorkItem.Add(workItem, row);
                    }
                }
                else if (!byName.TryGetValue(group.Names[0], out row))
                {
                    row = new Accumulator();
                    byName.Add(group.Names[0], row);
                }

                foreach (string name in group.Names)
                {
                    if (!row.Names.Contains(name, StringComparer.Ordinal))
                    {
                        row.Names.Add(name);
                    }
                }

                row.Total += group.Total;
                row.Open |= group.Open;
            }
        }

        List<ReportRow> rows = [];
        foreach (KeyValuePair<int, Accumulator> pair in byWorkItem.OrderBy(p => p.Key))
        {
            rows.Add(new ReportRow(pair.Key, pair.Value.Names, pair.Value.Total, pair.Value.Open));
        }

        foreach (KeyValuePair<string, Accumulator> pair in byName.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            rows.Add(new ReportRow(null, pair.Value.Names, pair.Value.Total, pair.Value.Open));
        }

        return new ProjectReport(rows, skipped);
    }

    private sealed class Accumulator
    {
        public List<string> Names { get; } = [];

        public TimeSpan Total { get; set; }

        public bool Open { get; set; }
    }
}
