namespace Times.Tests;

// Job 306, AC-10: the tray's logic library stays free of packages; Avalonia lives only in the shell.
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
    public void TrayShell_PinsAvalonia_AndBothProjectsAreInTheSolution()
    {
        string root = RepoRoot();
        string shell = File.ReadAllText(Path.Combine(root, "src", "Times.Tray", "Times.Tray.csproj"));
        string solution = File.ReadAllText(Path.Combine(root, "Times.slnx"));
        string tests = File.ReadAllText(Path.Combine(root, "tests", "Times.Tests", "Times.Tests.csproj"));

        Assert.Matches(@"<PackageReference Include=""Avalonia"" Version=""\[\d+\.\d+\.\d+\]"" />", shell);
        Assert.Matches(@"<PackageReference Include=""Avalonia.Desktop"" Version=""\[\d+\.\d+\.\d+\]"" />", shell);
        Assert.Contains("src/Times.Tray.Core/Times.Tray.Core.csproj", solution, StringComparison.Ordinal);
        Assert.Contains("src/Times.Tray/Times.Tray.csproj", solution, StringComparison.Ordinal);
        Assert.DoesNotContain("Times.Tray.csproj", tests, StringComparison.Ordinal);
    }
}
