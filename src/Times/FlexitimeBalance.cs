using System.Text.Json;

namespace Times;

/// <summary>
/// The running flexitime balance up to and including a shown day: the sum of
/// <see cref="DayRecord.WorkTime"/> minus the daily target over every day file dated on or before
/// that day that holds at least one arrival. An absence day counts too, with a delta of zero. Every such
/// day's balance corrections (<see cref="DayRecord.Corrections"/>) are added as well, and a day that holds
/// only corrections counts too. Day files that cannot be read are skipped and counted.
/// Computing it only reads: it never creates or changes a file or folder.
/// </summary>
internal sealed class FlexitimeBalance
{
    public FlexitimeBalance(TimeSpan total, int countedDays, int skippedFiles)
    {
        Total = total;
        CountedDays = countedDays;
        SkippedFiles = skippedFiles;
    }

    /// <summary>No day counted and nothing skipped.</summary>
    public static FlexitimeBalance None { get; } = new(TimeSpan.Zero, 0, 0);

    /// <summary>The sum of work time minus target over the counted days, plus their corrections.</summary>
    public TimeSpan Total { get; }

    /// <summary>The number of days that added to <see cref="Total"/>.</summary>
    public int CountedDays { get; }

    /// <summary>The number of day files on or before the shown day that could not be read.</summary>
    public int SkippedFiles { get; }

    /// <summary>
    /// Computes the balance from <paramref name="store"/>'s day files dated on or before
    /// <paramref name="shownDay"/>, with <paramref name="target"/> as every day's target. A missing
    /// folder, or one that cannot be listed, gives <see cref="None"/>. <paramref name="now"/> is the live
    /// minute when <paramref name="shownDay"/> is today, or <c>null</c>: then the shown day counts with
    /// <see cref="DayRecord.WorkTimeAt"/> instead of <see cref="DayRecord.WorkTime"/>. Earlier days never do.
    /// </summary>
    public static FlexitimeBalance Compute(DayStore store, DateOnly shownDay, TimeSpan target, TimeOnly? now = null)
    {
        IReadOnlyList<DateOnly> days;
        try
        {
            days = store.Days();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return None;
        }

        TimeSpan total = TimeSpan.Zero;
        int counted = 0;
        int skipped = 0;
        foreach (DateOnly day in days)
        {
            if (day > shownDay)
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

            bool counts = record.Corrections.Count > 0;
            total += record.CorrectionTotal;
            if (record.Absence is not null)
            {
                // An absence day meets the target exactly: it counts, with a delta of zero.
                counts = true;
            }
            else if (record.Entries.Any(e => e.Kind == EntryKind.Arrival))
            {
                TimeSpan worked = day == shownDay && now is { } live ? record.WorkTimeAt(live) : record.WorkTime();
                total += worked - target;
                counts = true;
            }

            if (counts)
            {
                counted++;
            }
        }

        return new FlexitimeBalance(total, counted, skipped);
    }
}
