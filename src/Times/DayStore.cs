using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Times;

/// <summary>
/// Reads and writes day files: one JSON file per day, <c>&lt;folder&gt;/yyyy-MM-dd.json</c>.
/// Reading never creates the folder or a file, so browsing leaves nothing behind. Only
/// <see cref="Save"/> writes, and only into an absolute folder.
/// </summary>
internal sealed partial class DayStore
{
    /// <summary>Day files are tiny. Anything bigger is not ours and is not read.</summary>
    internal const long MaxFileBytes = 64 * 1024;

    public DayStore(string folder)
    {
        Folder = folder;
    }

    public string Folder { get; }

    /// <summary>
    /// True only for a non-empty, non-whitespace, fully qualified path that does not start or end
    /// with whitespace, so nothing is ever written relative to the working directory or into a
    /// folder whose name has invisible padding. Whitespace inside the path is fine.
    /// </summary>
    public static bool IsValidFolder(string folder) =>
        !string.IsNullOrWhiteSpace(folder) && !HasEdgeWhitespace(folder) && Path.IsPathFullyQualified(folder);

    /// <summary>True if <paramref name="folder"/> is non-empty and starts or ends with a whitespace character (<see cref="char.IsWhiteSpace(char)"/>).</summary>
    internal static bool HasEdgeWhitespace(string folder) =>
        folder.Length > 0 && (char.IsWhiteSpace(folder[0]) || char.IsWhiteSpace(folder[^1]));

    /// <summary>The file path for <paramref name="day"/>, formatted with the invariant culture.</summary>
    public string PathFor(DateOnly day) =>
        Path.Combine(Folder, day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".json");

    /// <summary>True if the day's file exists. A missing folder simply means no file.</summary>
    public bool Exists(DateOnly day) => File.Exists(PathFor(day));

    /// <summary>
    /// Loads <paramref name="day"/>. A missing file or folder gives <see cref="DayRecord.Empty"/> and
    /// creates nothing. Throws <see cref="InvalidDataException"/> if the file cannot be read as a day.
    /// The file's final link target must be a regular file (read through <see cref="CappedFile"/>, so
    /// a FIFO or a device never blocks), and at most <see cref="MaxFileBytes"/> bytes are taken in,
    /// whatever its reported size.
    /// </summary>
    public DayRecord Load(DateOnly day)
    {
        EnsureValidFolder();
        string name = Name(day);
        byte[]? bytes = CappedFile.Read(
            PathFor(day),
            $"The file for {name}",
            MaxFileBytes,
            $"The file for {name} is too large to be a day file.");
        if (bytes is null)
        {
            return DayRecord.Empty;
        }

        try
        {
            using StreamReader reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8);
            return DayRecord.FromJson(reader.ReadToEnd());
        }
        catch (JsonException)
        {
            throw new InvalidDataException($"The file for {name} is not a valid day file.");
        }
    }

    /// <summary>
    /// Lists the days that have an entry in the folder, in ascending order. Only names that are exactly
    /// <c>yyyy-MM-dd.json</c> (ASCII digits, lower-case extension) and parse as a date with the invariant
    /// culture count; every other name is ignored. An entry is listed whatever it is (a directory or FIFO
    /// with a day's name too), so <see cref="Load"/> decides whether it can be read. Reads only the
    /// folder listing: never opens a file and never creates the folder. A missing or invalid folder lists
    /// nothing. Throws <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> if the
    /// folder cannot be listed.
    /// </summary>
    public IReadOnlyList<DateOnly> Days()
    {
        if (!IsValidFolder(Folder) || !Directory.Exists(Folder))
        {
            return [];
        }

        List<DateOnly> days = [];
        foreach (string path in Directory.EnumerateFileSystemEntries(Folder))
        {
            if (TryParseName(Path.GetFileName(path), out DateOnly day))
            {
                days.Add(day);
            }
        }

        days.Sort();
        return days;
    }

    /// <summary>True if <paramref name="fileName"/> is exactly a day file's name, <c>yyyy-MM-dd.json</c>, for a real date.</summary>
    internal static bool TryParseName(string fileName, out DateOnly day)
    {
        day = default;
        return DayFileName().IsMatch(fileName)
            && DateOnly.TryParseExact(fileName[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day);
    }

    /// <summary>
    /// Saves <paramref name="record"/> for <paramref name="day"/>. Creates the folder if it is
    /// missing, writes a temp file next to the target and moves it over the target. Throws
    /// <see cref="InvalidOperationException"/> and writes nothing if <see cref="Folder"/> is not valid.
    /// </summary>
    public void Save(DateOnly day, DayRecord record)
    {
        EnsureValidFolder();
        string target = PathFor(day);
        Directory.CreateDirectory(Folder);
        string temp = Path.Combine(Folder, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temp, record.ToJson(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temp, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    private void EnsureValidFolder()
    {
        if (!IsValidFolder(Folder))
        {
            throw new InvalidOperationException("The data folder is not valid.");
        }
    }

    private static string Name(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    // \z, not $: a trailing line break is not part of a name.
    [GeneratedRegex(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}\.json\z", RegexOptions.CultureInvariant)]
    private static partial Regex DayFileName();
}
