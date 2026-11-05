namespace Times;

/// <summary>
/// How this copy of Times was installed, read from where its assembly lives. A dotnet tool sits under
/// <c>&lt;dir&gt;/.store/times/&lt;version&gt;/…</c>: when <c>&lt;dir&gt;</c> is the global tools folder,
/// <c>dotnet tool update</c> takes <c>-g</c>, otherwise <c>--tool-path &lt;dir&gt;</c>. Any other place
/// (<c>dotnet run</c>, tests) is not an installed tool, and nothing is staged.
/// </summary>
internal sealed class ToolInstall
{
    /// <summary>Use <see cref="Detect"/>; tests build one directly for another OS's paths.</summary>
    internal ToolInstall(bool global, string folder)
    {
        Global = global;
        Folder = folder;
    }

    /// <summary>True for a global tool (<c>-g</c>).</summary>
    public bool Global { get; }

    /// <summary>The tools folder that holds <c>.store</c>.</summary>
    public string Folder { get; }

    /// <summary>The install part of the <c>dotnet tool update</c> arguments.</summary>
    public IReadOnlyList<string> Arguments => Global ? ["-g"] : ["--tool-path", Folder];

    /// <summary>This copy's install, from <c>typeof(App).Assembly.Location</c>, or <c>null</c> if it is not an installed tool.</summary>
    public static ToolInstall? Current() =>
        Detect(typeof(App).Assembly.Location, GlobalToolsFolder(
            Environment.GetEnvironmentVariable("DOTNET_CLI_HOME"),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));

    /// <summary>
    /// The global tools folder: <c>&lt;DOTNET_CLI_HOME or the user profile&gt;/.dotnet/tools</c>, or
    /// <c>null</c> when neither is an absolute path.
    /// </summary>
    public static string? GlobalToolsFolder(string? cliHome, string? userProfile)
    {
        string? home = !string.IsNullOrEmpty(cliHome) && Path.IsPathFullyQualified(cliHome) ? cliHome
            : !string.IsNullOrEmpty(userProfile) && Path.IsPathFullyQualified(userProfile) ? userProfile
            : null;
        return home is null ? null : Path.Combine(home, ".dotnet", "tools");
    }

    /// <summary>
    /// The install that holds <paramref name="assemblyLocation"/>: the nearest folder above it named
    /// <c>times</c> whose parent is <c>.store</c> gives the tools folder (the parent of <c>.store</c>).
    /// <c>null</c> for an empty or relative location, or one outside any tool store.
    /// </summary>
    public static ToolInstall? Detect(string? assemblyLocation, string? globalToolsFolder)
    {
        if (string.IsNullOrEmpty(assemblyLocation) || !Path.IsPathFullyQualified(assemblyLocation))
        {
            return null;
        }

        DirectoryInfo? directory = new FileInfo(assemblyLocation).Directory;
        while (directory?.Parent is { } parent)
        {
            if (string.Equals(directory.Name, "times", StringComparison.OrdinalIgnoreCase)
                && string.Equals(parent.Name, ".store", StringComparison.OrdinalIgnoreCase)
                && parent.Parent is { } tools)
            {
                bool global = globalToolsFolder is not null && SamePath(tools.FullName, globalToolsFolder);
                return new ToolInstall(global, Trim(tools.FullName));
            }

            directory = parent;
        }

        return null;
    }

    private static bool SamePath(string left, string right)
    {
        StringComparison comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        return string.Equals(Trim(Path.GetFullPath(left)), Trim(Path.GetFullPath(right)), comparison);
    }

    private static string Trim(string path) => Path.TrimEndingDirectorySeparator(path);
}
