namespace Times.Tests;

// Job 306, AC-10: the tray uses no NuGet package at all; the shell talks to each OS's own tray API.
public sealed class TrayProjectTests
{
    private static string RepoRoot()
    {
        DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Times.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.True(directory is not null, "Times.slnx not found above " + AppContext.BaseDirectory);
        return directory.FullName;
    }

    [Fact]
    public void TrayCore_ReferencesNoPackage()
    {
        string project = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Times.Tray.Core", "Times.Tray.Core.csproj"));

        Assert.DoesNotContain("PackageReference", project, StringComparison.Ordinal);
    }

    [Fact]
    public void TrayShell_ReferencesNoPackage_AndBothProjectsAreInTheSolution()
    {
        string root = RepoRoot();
        string shell = File.ReadAllText(Path.Combine(root, "src", "Times.Tray", "Times.Tray.csproj"));
        string solution = File.ReadAllText(Path.Combine(root, "Times.slnx"));
        string tests = File.ReadAllText(Path.Combine(root, "tests", "Times.Tests", "Times.Tests.csproj"));

        Assert.DoesNotContain("PackageReference", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("Avalonia", shell, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("src/Times.Tray.Core/Times.Tray.Core.csproj", solution, StringComparison.Ordinal);
        Assert.Contains("src/Times.Tray/Times.Tray.csproj", solution, StringComparison.Ordinal);
        Assert.DoesNotContain("Times.Tray.csproj", tests, StringComparison.Ordinal);
    }

    [Fact]
    public void TrayShell_UsesTheOsTrayApis_NoAvaloniaInItsCode()
    {
        string folder = Path.Combine(RepoRoot(), "src", "Times.Tray");
        string mac = File.ReadAllText(Path.Combine(folder, "MacOS", "MacTray.cs"));
        string windows = File.ReadAllText(Path.Combine(folder, "Windows", "WindowsTray.cs"));
        string[] sources = Directory.GetFiles(folder, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .ToArray();

        Assert.Contains("NSStatusBar", mac, StringComparison.Ordinal);
        Assert.Contains("NotifyIcon", windows, StringComparison.Ordinal);
        Assert.All(sources, path => Assert.DoesNotContain("Avalonia", File.ReadAllText(path), StringComparison.Ordinal));
    }
}
