namespace Times;

/// <summary>
/// What the tracker remembers about a <c>Call</c> it booked: the day, the entries right after the booking (so
/// any later change counts as "the user booked something since") and the checkpoint that was open before.
/// </summary>
internal sealed class TeamsCall
{
    public TeamsCall(DateOnly date, IReadOnlyList<DayEntry> entriesAfter, DayEntry? previous)
    {
        Date = date;
        EntriesAfter = entriesAfter;
        Previous = previous;
    }

    public DateOnly Date { get; }

    public IReadOnlyList<DayEntry> EntriesAfter { get; }

    /// <summary>The checkpoint that was open when the call started (name and work item), or <c>null</c>.</summary>
    public DayEntry? Previous { get; }
}

/// <summary>
/// The pure rules for booking a Teams call: when a meeting starts, a <c>Call</c> checkpoint; when it ends, the
/// checkpoint that was open before. Never reads or writes a file.
/// </summary>
internal static class TeamsCallBooking
{
    /// <summary>The name of the checkpoint a call books (protocol 2.0.0 sends no meeting title).</summary>
    public const string CallName = "Call";

    /// <summary>
    /// The last checkpoint of <paramref name="day"/> that nothing ended yet (<see cref="CheckpointSummary.EndOf"/>
    /// is null), or <c>null</c>.
    /// </summary>
    public static DayEntry? OpenCheckpoint(DayRecord day)
    {
        IReadOnlyList<DayEntry> entries = day.Entries;
        for (int i = entries.Count - 1; i >= 0; i--)
        {
            if (entries[i].Kind == EntryKind.Checkpoint)
            {
                return CheckpointSummary.EndOf(entries, i) is null ? entries[i] : null;
            }
        }

        return null;
    }

    /// <summary>
    /// A meeting started at <paramref name="now"/> on <paramref name="date"/>: the day with a <c>Call</c> checkpoint
    /// (no work item) added, and what to remember, or <c>null</c> (nothing booked) on an absence day or while not
    /// working.
    /// </summary>
    public static (DayRecord Updated, TeamsCall Call)? Start(DateOnly date, DayRecord day, TimeOnly now)
    {
        if (day.Absence is not null || !day.IsWorking)
        {
            return null;
        }

        DayEntry? previous = OpenCheckpoint(day);
        DayRecord updated = day.Add(new DayEntry(EntryKind.Checkpoint, now, CallName, null));
        return (updated, new TeamsCall(date, updated.Entries.ToArray(), previous));
    }

    /// <summary>
    /// A meeting ended at <paramref name="now"/> on <paramref name="date"/>: the day with the remembered checkpoint
    /// booked again (same name and work item), or <c>null</c> (nothing booked) when no call is remembered, it was
    /// on another day, nothing was open before it, the day's entries changed since the call was booked (so its open
    /// checkpoint is no longer that call), it is an absence day, or the user is not working.
    /// </summary>
    public static DayRecord? End(DateOnly date, DayRecord day, TimeOnly now, TeamsCall? call)
    {
        if (call is null || call.Previous is not { } previous || call.Date != date
            || day.Absence is not null || !day.IsWorking || !day.Entries.SequenceEqual(call.EntriesAfter))
        {
            return null;
        }

        DayEntry? open = OpenCheckpoint(day);
        if (open is null || open.Name != CallName || open.WorkItem is not null)
        {
            return null;
        }

        return day.Add(new DayEntry(EntryKind.Checkpoint, now, previous.Name, previous.WorkItem));
    }
}
