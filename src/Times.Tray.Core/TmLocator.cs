namespace Times.Tray;

/// <summary>
/// Finds the installed <c>tm</c> one-shot the tray runs. Only an absolute path to an existing regular file counts:
/// a relative path (which would resolve against the tray's working folder) or a directory is skipped.
/// </summary>
internal static class TmLocator
{
    /// <summary>The environment variable that names the <c>tm</c> to run, checked first.</summary>
    public const string VariableName = "TIMES_TM";

    /// <summary>Finds <c>tm</c> from the real environment, profile folder and OS.</summary>
    public static string? Find() =>
        Find(Environment.GetEnvironmentVariable, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            OperatingSystem.IsWindows());

    /// <summary>
    /// Returns the first of: <see cref="VariableName"/>, <c>&lt;userProfile&gt;/.dotnet/tools/tm</c>, and each absolute
    /// <c>PATH</c> entry joined with <c>tm</c> (<c>tm.exe</c> on Windows), that is an absolute path to an existing
    /// regular file; <c>null</c> if none is.
    /// </summary>
    /// <param name="environment">Reads an environment variable, <c>null</c> when unset.</param>
    /// <param name="userProfile">The user's home folder, or empty when unknown.</param>
    /// <param name="windows">True on Windows: <c>tm.exe</c> and <c>;</c> between <c>PATH</c> entries.</param>
    public static string? Find(Func<string, string?> environment, string userProfile, bool windows)
    {
        string fileName = windows ? "tm.exe" : "tm";
        if (Usable(environment(VariableName)) is { } fromVariable)
        {
            return fromVariable;
        }

        if (!string.IsNullOrEmpty(userProfile) && Path.IsPathFullyQualified(userProfile)
            && Usable(Path.Combine(userProfile, ".dotnet", "tools", fileName)) is { } tool)
        {
            return tool;
        }

        string? path = environment("PATH");
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        foreach (string entry in path.Split(windows ? ';' : ':', StringSplitOptions.RemoveEmptyEntries))
        {
            if (Path.IsPathFullyQualified(entry) && Usable(Path.Combine(entry, fileName)) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    // The path if it is absolute and names an existing regular file (a link to one counts), otherwise null.
    private static string? Usable(string? path)
    {
        if (string.IsNullOrEmpty(path) || !Path.IsPathFullyQualified(path))
        {
            return null;
        }

        try
        {
            FileInfo info = new FileInfo(path);
            if (!info.Exists)
            {
                return null;
            }

            FileSystemInfo? target = info.LinkTarget is null ? info : info.ResolveLinkTarget(returnFinalTarget: true);
            return target is FileInfo { Exists: true } file && (file.Attributes & FileAttributes.Directory) == 0
                ? path
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
