using System.Text;
using System.Text.Json;

namespace Times;

/// <summary>
/// Reads and writes the project quicklist, <c>&lt;folder&gt;/projects.json</c>, next to the day
/// files. Reading never creates the folder or the file. Only <see cref="Save"/> writes, and only
/// into an absolute folder.
/// </summary>
internal sealed class ProjectStore
{
    /// <summary>The file name inside the data folder. It cannot clash with a <c>yyyy-MM-dd.json</c> day file.</summary>
    public const string FileName = "projects.json";

    /// <summary>The project list is tiny. Anything bigger is not ours and is not read.</summary>
    internal const long MaxFileBytes = 64 * 1024;

    public ProjectStore(string folder)
    {
        Folder = folder;
    }

    public string Folder { get; }

    public string FilePath => Path.Combine(Folder, FileName);

    /// <summary>
    /// Loads the project list. A missing file or folder gives <see cref="ProjectList.Empty"/> and
    /// creates nothing. Throws <see cref="InvalidDataException"/> if the file cannot be read as a
    /// project list. The file's final link target must be a regular file, checked before it is
    /// opened (so a FIFO or a device never blocks), and at most <see cref="MaxFileBytes"/> bytes
    /// are taken in, whatever its reported size.
    /// </summary>
    public ProjectList Load()
    {
        EnsureValidFolder();
        byte[]? bytes = CappedFile.Read(FilePath, FileName, MaxFileBytes);
        if (bytes is null)
        {
            return ProjectList.Empty;
        }

        try
        {
            using StreamReader reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8);
            return ProjectList.FromJson(reader.ReadToEnd());
        }
        catch (JsonException)
        {
            throw new InvalidDataException($"{FileName} is not valid.");
        }
    }

    /// <summary>
    /// Reads from <paramref name="stream"/> until it ends or <paramref name="limit"/> bytes have
    /// arrived, whichever comes first. Never asks the stream for more than <paramref name="limit"/> bytes.
    /// </summary>
    internal static byte[] ReadAtMost(Stream stream, long limit) => CappedFile.ReadAtMost(stream, limit);

    /// <summary>
    /// Saves <paramref name="list"/>. Creates the folder if it is missing, writes a temp file next to
    /// the target and moves it over the target, UTF-8 without BOM. Throws
    /// <see cref="InvalidOperationException"/> and writes nothing if <see cref="Folder"/> is not valid.
    /// </summary>
    public void Save(ProjectList list)
    {
        EnsureValidFolder();
        string target = FilePath;
        Directory.CreateDirectory(Folder);
        string temp = Path.Combine(Folder, $".{FileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temp, list.ToJson(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
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
        if (!DayStore.IsValidFolder(Folder))
        {
            throw new InvalidOperationException("The data folder is not valid.");
        }
    }
}
