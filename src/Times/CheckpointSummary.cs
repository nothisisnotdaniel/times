namespace Times;

/// <summary>
/// One row of the checkpoint summary: either a work item (every checkpoint booked on it, whatever
/// its name) or, for checkpoints without a work item, one exact name.
/// </summary>
internal sealed class CheckpointGroup
{
    public CheckpointGroup(int? workItem, IReadOnlyList<string> names, TimeSpan total, bool open)
    {
        WorkItem = workItem;
        Names = names;
        Total = total;
        Open = open;
    }

    /// <summary>The group's work item, or <c>null</c> for a name group.</summary>
    public int? WorkItem { get; }

    /// <summary>The distinct checkpoint names in the group, in first-booked order (exactly one for a name group).</summary>
    public IReadOnlyList<string> Names { get; }

    /// <summary>The sum of the time taken by the group's checkpoints.</summary>
    public TimeSpan Total { get; }

    /// <summary>True if any of the group's checkpoints has no ending entry on a day that is not today.</summary>
    public bool Open { get; }
}

/// <summary>
/// Works out the time taken per work item (and per name for checkpoints without one) from a day's
/// checkpoints. Pure: it reads nothing and writes nothing.
/// </summary>
internal sealed class CheckpointSummary
{
    private CheckpointSummary()
    {
    }

    /// <summary>
    /// Returns the day's checkpoint groups in the order of their first checkpoint's time. A
    /// checkpoint runs from its time to the first later entry (in <see cref="DayRecord.Entries"/>
    /// order) that is a checkpoint, a break start or a leaving; arrivals and break ends do not end
    /// it. With no such entry, it runs to <paramref name="now"/> (never below zero) if
    /// <paramref name="isToday"/>, otherwise it counts nothing and its group is open.
    /// </summary>
    /// <param name="now">The current local minute, seconds already dropped. Used only on today.</param>
    public static IReadOnlyList<CheckpointGroup> Calculate(DayRecord day, bool isToday, TimeOnly now)
    {
        IReadOnlyList<DayEntry> entries = day.Entries;
        List<Accumulator> groups = [];
        Dictionary<int, Accumulator> byWorkItem = [];
        Dictionary<string, Accumulator> byName = new Dictionary<string, Accumulator>(StringComparer.Ordinal);

        for (int i = 0; i < entries.Count; i++)
        {
            DayEntry entry = entries[i];
            if (entry.Kind != EntryKind.Checkpoint)
            {
                continue;
            }

            string name = entry.Name!;
            Accumulator? group;
            if (entry.WorkItem is { } workItem)
            {
                if (!byWorkItem.TryGetValue(workItem, out group))
                {
                    group = new Accumulator(workItem);
                    byWorkItem.Add(workItem, group);
                    groups.Add(group);
                }
            }
            else if (!byName.TryGetValue(name, out group))
            {
                group = new Accumulator(null);
                byName.Add(name, group);
                groups.Add(group);
            }

            if (!group.Names.Contains(name, StringComparer.Ordinal))
            {
                group.Names.Add(name);
            }

            DayEntry? end = null;
            for (int j = i + 1; j < entries.Count; j++)
            {
                if (EndsCheckpoint(entries[j].Kind))
                {
                    end = entries[j];
                    break;
                }
            }

            if (end is not null)
            {
                // Entries are sorted, so the end is never before the checkpoint.
                group.Total += end.Time - entry.Time;
            }
            else if (isToday)
            {
                if (now > entry.Time)
                {
                    group.Total += now - entry.Time;
                }
            }
            else
            {
                group.Open = true;
            }
        }

        return groups.Select(g => new CheckpointGroup(g.WorkItem, g.Names, g.Total, g.Open)).ToList();
    }

    private static bool EndsCheckpoint(EntryKind kind) =>
        kind is EntryKind.Checkpoint or EntryKind.BreakStart or EntryKind.Leaving;

    private sealed class Accumulator
    {
        public Accumulator(int? workItem)
        {
            WorkItem = workItem;
        }

        public int? WorkItem { get; }

        public List<string> Names { get; } = [];

        public TimeSpan Total { get; set; }

        public bool Open { get; set; }
    }
}
