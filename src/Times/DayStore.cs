using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Times;

/// <summary>
/// Reads and writes day files: one JSON file per day, <c>&lt;folder&gt;/yyyy-MM-dd.json</c>.
/// Reading never creates the folder or a file, so browsing leaves nothing behind. Only
/// <see cref="Save"/> writes, and only into an absolute folder.
/// </summary>
internal sealed class DayStore
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
    /// </summary>
    public DayRecord Load(DateOnly day)
    {
        EnsureValidFolder();
        FileInfo file = new FileInfo(PathFor(day));
        if (!file.Exists)
        {
            return DayRecord.Empty;
        }

        if (file.Length > MaxFileBytes)
        {
            throw new InvalidDataException($"The file for {Name(day)} is too large to be a day file.");
        }

        try
        {
            return DayRecord.FromJson(File.ReadAllText(file.FullName, Encoding.UTF8));
        }
        catch (JsonException)
        {
            throw new InvalidDataException($"The file for {Name(day)} is not a valid day file.");
        }
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
            throw new InvalidOperationException("The data folder is not an absolute path.");
        }
    }

    private static string Name(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
