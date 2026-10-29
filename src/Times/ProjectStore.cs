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
    /// creates nothing. Throws <see cref="InvalidDataException"/> if the file cannot be read as a project list.
    /// </summary>
    public ProjectList Load()
    {
        EnsureValidFolder();
        FileInfo file = new FileInfo(FilePath);
        if (!file.Exists)
        {
            return ProjectList.Empty;
        }

        if (file.Length > MaxFileBytes)
        {
            throw new InvalidDataException($"{FileName} is too large.");
        }

        try
        {
            return ProjectList.FromJson(File.ReadAllText(file.FullName, Encoding.UTF8));
        }
        catch (JsonException)
        {
            throw new InvalidDataException($"{FileName} is not valid.");
        }
    }

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
            throw new InvalidOperationException("The data folder is not an absolute path.");
        }
    }
}
