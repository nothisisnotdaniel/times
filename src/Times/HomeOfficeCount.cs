using System.Text.Json;

namespace Times;

/// <summary>
/// Counts the home-office days in a date range, for the home-office allowance. Only days whose workplace is
/// <see cref="Workplace.Home"/> count; office, unmarked and absence days never do. Counting only reads: it never
/// creates or changes a file or folder.
/// </summary>
internal static class HomeOfficeCount
{
    /// <summary>
    /// The number of day files <see cref="DayStore.Days"/> lists from <paramref name="from"/> to <paramref name="to"/>
    /// (inclusive) that are marked <see cref="Workplace.Home"/>. Day files that cannot be read are skipped. Throws
    /// <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> if the folder cannot be listed.
    /// </summary>
    public static int Calculate(DayStore store, DateOnly from, DateOnly to)
    {
        int count = 0;
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
                continue;
            }

            if (record.Workplace == Workplace.Home)
            {
                count++;
            }
        }

        return count;
    }
}
