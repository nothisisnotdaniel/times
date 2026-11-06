using System.Text.Json;

namespace Times;

/// <summary>
/// The vacation days marked in one calendar year: <see cref="Taken"/> (on or before today) and
/// <see cref="Planned"/> (after today). Only days whose absence is <see cref="AbsenceKind.Vacation"/>
/// count, each as one whole day. Computing it only reads: it never creates or changes a file or folder.
/// </summary>
internal sealed class VacationAllowance
{
    public VacationAllowance(int taken, int planned, int skippedFiles)
    {
        Taken = taken;
        Planned = planned;
        SkippedFiles = skippedFiles;
    }

    /// <summary>The vacation days in the year on or before today.</summary>
    public int Taken { get; }

    /// <summary>The vacation days in the year after today.</summary>
    public int Planned { get; }

    /// <summary>The number of day files in the year that could not be read.</summary>
    public int SkippedFiles { get; }

    /// <summary>
    /// Counts the vacation days in <paramref name="year"/>. Only the days <see cref="DayStore.Days"/> lists
    /// in that year are loaded. Sick days, stored public holidays, computed public holidays and booked days
    /// never count. Day files that cannot be read are skipped and counted. Throws <see cref="IOException"/>
    /// or <see cref="UnauthorizedAccessException"/> if the folder cannot be listed.
    /// </summary>
    public static VacationAllowance Calculate(DayStore store, int year, DateOnly today)
    {
        int taken = 0;
        int planned = 0;
        int skipped = 0;
        foreach (DateOnly day in store.Days())
        {
            if (day.Year != year)
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

            if (record.Absence != AbsenceKind.Vacation)
            {
                continue;
            }

            if (day <= today)
            {
                taken++;
            }
            else
            {
                planned++;
            }
        }

        return new VacationAllowance(taken, planned, skipped);
    }
}
