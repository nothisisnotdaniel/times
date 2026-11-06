using System.Text.Json;

namespace Times;

/// <summary>One checkpoint a sync sends: its day, its 1-based entry number on that day, and its minutes.</summary>
internal sealed class SyncCheckpoint
{
    public SyncCheckpoint(DateOnly day, int number, int minutes)
    {
        Day = day;
        Number = number;
        Minutes = minutes;
    }

    public DateOnly Day { get; }

    /// <summary>The checkpoint's 1-based entry number on <see cref="Day"/>, as <c>booked</c> takes it.</summary>
    public int Number { get; }

    public int Minutes { get; }
}

/// <summary>The unbooked checkpoints of one work item that a sync sends together.</summary>
internal sealed class SyncItem
{
    public SyncItem(int workItem, IReadOnlyList<string> names, IReadOnlyList<SyncCheckpoint> checkpoints)
    {
        WorkItem = workItem;
        Names = names;
        Checkpoints = checkpoints;
        Minutes = checkpoints.Sum(c => c.Minutes);
        Hours = Math.Round(Minutes / 60.0, 2);
    }

    public int WorkItem { get; }

    /// <summary>The distinct checkpoint names, in first-booked order: the title when the quicklist has none.</summary>
    public IReadOnlyList<string> Names { get; }

    /// <summary>The checkpoints, in day order and then entry order.</summary>
    public IReadOnlyList<SyncCheckpoint> Checkpoints { get; }

    public int Minutes { get; }

    /// <summary>The hours to add to Completed Work: <c>Math.Round(Minutes / 60.0, 2)</c>.</summary>
    public double Hours { get; }

    /// <summary>The title: the quicklist title of the work item, otherwise the names joined with <c>", "</c>.</summary>
    public string Title(ProjectList? projects) =>
        projects?.Projects.FirstOrDefault(p => p.WorkItem == WorkItem)?.Title ?? string.Join(", ", Names);
}

/// <summary>What a sync over a range would send, per work item, and how many day files it could not read.</summary>
internal sealed class SyncPlan
{
    public SyncPlan(IReadOnlyList<SyncItem> items, int skippedFiles)
    {
        Items = items;
        SkippedFiles = skippedFiles;
    }

    /// <summary>The work items in ascending order.</summary>
    public IReadOnlyList<SyncItem> Items { get; }

    public int SkippedFiles { get; }
}

/// <summary>
/// Plans an Azure DevOps sync: the checkpoints not yet booked, grouped per work item. A checkpoint
/// counts only if it has a work item, is not booked, has an end entry (the rule of
/// <see cref="CheckpointSummary.EndOf"/>) and takes at least one minute; a running or open
/// checkpoint, one without a work item and one of 0 minutes are left out. Planning never writes.
/// </summary>
internal sealed class DevOpsSync
{
    private DevOpsSync()
    {
    }

    /// <summary>The plan for the given days. Pure: it reads nothing and writes nothing.</summary>
    public static SyncPlan Plan(IEnumerable<(DateOnly Day, DayRecord Record)> days, int skippedFiles)
    {
        SortedDictionary<int, (List<string> Names, List<SyncCheckpoint> Checkpoints)> byWorkItem = [];
        foreach ((DateOnly day, DayRecord record) in days.OrderBy(d => d.Day))
        {
            IReadOnlyList<DayEntry> entries = record.Entries;
            for (int i = 0; i < entries.Count; i++)
            {
                DayEntry entry = entries[i];
                if (entry.Kind != EntryKind.Checkpoint || entry.Booked || entry.WorkItem is not { } workItem)
                {
                    continue;
                }

                if (CheckpointSummary.EndOf(entries, i) is not { } end)
                {
                    continue;
                }

                int minutes = (int)((end.Time - entry.Time).Ticks / TimeSpan.TicksPerMinute);
                if (minutes <= 0)
                {
                    continue;
                }

                if (!byWorkItem.TryGetValue(workItem, out (List<string> Names, List<SyncCheckpoint> Checkpoints) group))
                {
                    group = ([], []);
                    byWorkItem.Add(workItem, group);
                }

                if (!group.Names.Contains(entry.Name!, StringComparer.Ordinal))
                {
                    group.Names.Add(entry.Name!);
                }

                group.Checkpoints.Add(new SyncCheckpoint(day, i + 1, minutes));
            }
        }

        List<SyncItem> items = byWorkItem
            .Select(pair => new SyncItem(pair.Key, pair.Value.Names, pair.Value.Checkpoints))
            .ToList();
        return new SyncPlan(items, skippedFiles);
    }

    /// <summary>
    /// The plan over the day files <see cref="DayStore.Days"/> lists from <paramref name="from"/> to
    /// <paramref name="to"/> (both inclusive). Unreadable day files are skipped and counted. Only
    /// reads. Throws <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> if the
    /// folder cannot be listed.
    /// </summary>
    public static SyncPlan Plan(DayStore store, DateOnly from, DateOnly to)
    {
        List<(DateOnly, DayRecord)> days = [];
        int skipped = 0;
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
                skipped++;
            }
        }

        return Plan(days, skipped);
    }
}
