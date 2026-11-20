namespace Times;

/// <summary>
/// Keeps the <see cref="DayRecord"/>s one <see cref="DayStore"/> has loaded, so a redraw does not read and parse
/// every day file again. An entry is used only while the day file's final link target has the same length and
/// <see cref="FileSystemInfo.LastWriteTimeUtc"/> as when it was loaded and the store has not saved that day since
/// (<see cref="DayStore.SaveCount"/>). A load that fails is not kept, so an unreadable file is tried every time.
/// An outside edit that keeps both the length and the last-write time is not seen until the app restarts.
/// </summary>
internal sealed class DayRecordCache
{
    private readonly Dictionary<DateOnly, Entry> entries = [];
    private int loads;

    public DayRecordCache(DayStore store)
    {
        Store = store;
    }

    /// <summary>The store whose day files this cache keeps.</summary>
    public DayStore Store { get; }

    /// <summary>How many times this cache has called <see cref="DayStore.Load"/>.</summary>
    internal int Loads => Volatile.Read(ref loads);

    /// <summary>
    /// The record for <paramref name="day"/>: the kept one while its file is unchanged, otherwise a fresh
    /// <see cref="DayStore.Load"/> (which throws as it does). A missing file is loaded every time and never kept.
    /// </summary>
    public DayRecord Load(DateOnly day)
    {
        // Taken before the load, so a change while loading makes the kept entry stale on the next call.
        FileStamp? stamp = StampOf(day);
        if (stamp is not null)
        {
            lock (entries)
            {
                if (entries.TryGetValue(day, out Entry? kept) && kept.Stamp == stamp)
                {
                    return kept.Record;
                }
            }
        }

        lock (entries)
        {
            entries.Remove(day);
        }

        Interlocked.Increment(ref loads);
        DayRecord record = Store.Load(day);
        if (stamp is not null)
        {
            lock (entries)
            {
                entries[day] = new Entry(record, stamp);
            }
        }

        return record;
    }

    // Length, last-write time and save count of the day's final link target, or null when there is no
    // regular-looking file to compare (missing, a directory, a link that cannot be resolved).
    private FileStamp? StampOf(DateOnly day)
    {
        int saves = Store.SaveCount(day);
        try
        {
            FileInfo file = new FileInfo(Store.PathFor(day));
            FileSystemInfo target = file.ResolveLinkTarget(returnFinalTarget: true) ?? file;
            if (target is not FileInfo info || !info.Exists)
            {
                return null;
            }

            return new FileStamp(info.Length, info.LastWriteTimeUtc, saves);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private sealed record FileStamp
    {
        public FileStamp(long length, DateTime lastWriteUtc, int saves)
        {
            Length = length;
            LastWriteUtc = lastWriteUtc;
            Saves = saves;
        }

        public long Length { get; }

        public DateTime LastWriteUtc { get; }

        public int Saves { get; }
    }

    private sealed class Entry
    {
        public Entry(DayRecord record, FileStamp stamp)
        {
            Record = record;
            Stamp = stamp;
        }

        public DayRecord Record { get; }

        public FileStamp Stamp { get; }
    }
}
