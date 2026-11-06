using System.Globalization;
using System.IO.Compression;

namespace Times;

/// <summary>The counts <see cref="DataBackup.Write"/> returns.</summary>
internal sealed class BackupCounts
{
    public BackupCounts(int dayFiles, bool projects, int skipped)
    {
        DayFiles = dayFiles;
        Projects = projects;
        Skipped = skipped;
    }

    /// <summary>Day files written into the zip.</summary>
    public int DayFiles { get; }

    /// <summary>True if <c>projects.json</c> was written into the zip.</summary>
    public bool Projects { get; }

    /// <summary>Files left out because they are not regular files, too large or unreadable.</summary>
    public int Skipped { get; }

    /// <summary>Every file written into the zip.</summary>
    public int Written => DayFiles + (Projects ? 1 : 0);
}

/// <summary>
/// Copies the day files and <c>projects.json</c> byte for byte into a zip, and decides whether a
/// target folder lies inside the data folder. Never parses JSON and never writes to the data folder.
/// </summary>
internal static class DataBackup
{
    /// <summary>The most symbolic links <see cref="Resolve"/> follows before it gives up, like the OS's own limit.</summary>
    private const int MaxLinks = 40;

    /// <summary><c>times-backup-yyyy-MM-dd-HHmm.zip</c> for <paramref name="localNow"/>, with invariant digits.</summary>
    public static string FileName(DateTime localNow) =>
        "times-backup-" + localNow.ToString("yyyy-MM-dd-HHmm", CultureInfo.InvariantCulture) + ".zip";

    /// <summary>
    /// True if <paramref name="folder"/> is <paramref name="dataFolder"/> or lies anywhere below it.
    /// Both are made full paths with every symbolic link resolved (a <c>..</c> after a link goes up
    /// from the link's target, as the OS does), then compared ignoring case and a trailing separator.
    /// A sibling that only shares a prefix (<c>/data-old</c> next to <c>/data</c>) is not inside.
    /// </summary>
    public static bool IsInside(string folder, string dataFolder)
    {
        string target = TrimEnd(Resolve(folder));
        string data = TrimEnd(Resolve(dataFolder));
        if (string.Equals(target, data, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string prefix = EndsWithSeparator(data) ? data : data + Path.DirectorySeparatorChar;
        return target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || (Path.AltDirectorySeparatorChar != Path.DirectorySeparatorChar
                && target.StartsWith(data + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Writes a zip to <paramref name="target"/> with one entry per day file <see cref="DayStore.Days"/>
    /// lists, then <c>projects.json</c> if it exists, at the zip root in ordinal name order. Each file is
    /// read through <see cref="CappedFile"/> with <see cref="DayStore.MaxFileBytes"/>; a file that is not
    /// regular, too large or cannot be read is left out and counted. Throws <see cref="IOException"/> or
    /// <see cref="UnauthorizedAccessException"/> if the data folder cannot be listed.
    /// </summary>
    public static BackupCounts Write(DayStore store, Stream target)
    {
        List<string> names = [];
        foreach (DateOnly day in store.Days())
        {
            names.Add(Path.GetFileName(store.PathFor(day)));
        }

        if (DayStore.IsValidFolder(store.Folder))
        {
            names.Add(ProjectStore.FileName);
        }

        names.Sort(StringComparer.Ordinal);

        int dayFiles = 0;
        bool projects = false;
        int skipped = 0;
        using ZipArchive zip = new ZipArchive(target, ZipArchiveMode.Create, leaveOpen: true);
        foreach (string name in names)
        {
            string path = Path.Combine(store.Folder, name);
            byte[]? bytes;
            try
            {
                bytes = CappedFile.Read(path, name, DayStore.MaxFileBytes);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                skipped++;
                continue;
            }

            if (bytes is null)
            {
                // Nothing there (no projects.json, or a day file deleted since the listing).
                continue;
            }

            ZipArchiveEntry entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            SetLastWriteTime(entry, path);
            using (Stream stream = entry.Open())
            {
                stream.Write(bytes);
            }

            if (name == ProjectStore.FileName)
            {
                projects = true;
            }
            else
            {
                dayFiles++;
            }
        }

        return new BackupCounts(dayFiles, projects, skipped);
    }

    // Keeps the file's own time on the entry where the zip format can hold it (1980 to 2107).
    private static void SetLastWriteTime(ZipArchiveEntry entry, string path)
    {
        try
        {
            DateTime written = File.GetLastWriteTime(path);
            if (written.Year >= 1980 && written.Year <= 2107)
            {
                entry.LastWriteTime = new DateTimeOffset(written);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
        }
    }

    /// <summary>
    /// The full path of <paramref name="path"/> with every symbolic link along it resolved. A part that
    /// does not exist is kept as typed. On Windows the path is normalised first, as Win32 does before it
    /// opens anything; elsewhere a <c>..</c> goes up from where the previous part really is.
    /// </summary>
    internal static string Resolve(string path)
    {
        string start = OperatingSystem.IsWindows() ? Path.GetFullPath(path) : path;
        string root = Path.GetPathRoot(start) ?? string.Empty;
        if (root.Length == 0)
        {
            start = Path.GetFullPath(start);
            root = Path.GetPathRoot(start) ?? string.Empty;
        }

        Stack<string> pending = new Stack<string>(Split(start[root.Length..]).Reverse());
        List<string> resolved = [];
        int links = 0;
        while (pending.Count > 0)
        {
            string part = pending.Pop();
            if (part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                if (resolved.Count > 0)
                {
                    resolved.RemoveAt(resolved.Count - 1);
                }

                continue;
            }

            string current = Join(root, resolved, part);
            string? linkTarget = LinkTarget(current);
            if (linkTarget is null || links >= MaxLinks)
            {
                resolved.Add(part);
                continue;
            }

            links++;
            if (Path.IsPathRooted(linkTarget))
            {
                string linkRoot = Path.GetPathRoot(linkTarget) ?? string.Empty;
                if (linkRoot.Length > 0)
                {
                    root = linkRoot;
                }

                resolved.Clear();
                linkTarget = linkTarget[linkRoot.Length..];
            }

            foreach (string next in Split(linkTarget).Reverse())
            {
                pending.Push(next);
            }
        }

        return Join(root, resolved, null);
    }

    private static string? LinkTarget(string path)
    {
        try
        {
            return new FileInfo(path).LinkTarget;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static IEnumerable<string> Split(string path) =>
        path.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);

    private static string Join(string root, List<string> parts, string? last)
    {
        string joined = root;
        foreach (string part in parts)
        {
            joined = Path.Combine(joined, part);
        }

        return last is null ? joined : Path.Combine(joined, last);
    }

    private static bool EndsWithSeparator(string path) =>
        path.Length > 0 && (path[^1] == Path.DirectorySeparatorChar || path[^1] == Path.AltDirectorySeparatorChar);

    // Drops trailing separators, but never the root's own.
    private static string TrimEnd(string path)
    {
        string root = Path.GetPathRoot(path) ?? string.Empty;
        string trimmed = path;
        while (trimmed.Length > root.Length && EndsWithSeparator(trimmed))
        {
            trimmed = trimmed[..^1];
        }

        return trimmed;
    }
}
