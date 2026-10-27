using System.Globalization;

namespace Times;

/// <summary>
/// Knows where day files live: one JSON file per day, <c>&lt;folder&gt;/yyyy-MM-dd.json</c>.
/// Read-only for now. It never creates the folder or a file, so browsing leaves nothing behind.
/// </summary>
internal sealed class DayStore(string folder)
{
    public string Folder { get; } = folder;

    /// <summary>The file path for <paramref name="day"/>, formatted with the invariant culture.</summary>
    public string PathFor(DateOnly day) =>
        Path.Combine(Folder, day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".json");

    /// <summary>True if the day's file exists. A missing folder simply means no file.</summary>
    public bool Exists(DateOnly day) => File.Exists(PathFor(day));
}
