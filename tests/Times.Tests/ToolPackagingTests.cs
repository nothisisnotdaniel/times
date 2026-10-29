using System.Diagnostics;
using System.Xml.Linq;

namespace Times.Tests;

public sealed class ToolPackagingTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    // AC-1
    [Fact]
    public void ProjectFile_PacksAsToolNamedTm()
    {
        XElement project = LoadProject();

        Assert.Equal("true", SingleProperty(project, "PackAsTool"));
        Assert.Equal("tm", SingleProperty(project, "ToolCommandName"));
    }

    // AC-2
    [Fact]
    public void ProjectFile_VersionEqualsAppVersionAndHasNoPackageVersion()
    {
        XElement project = LoadProject();

        Assert.Empty(project.Descendants("PackageVersion"));
        Assert.Equal(AppVersion.Current, SingleProperty(project, "Version"));
    }

    // AC-5
    [Theory]
    [InlineData("dotnet pack")]
    [InlineData("dotnet tool install -g --add-source ./nupkg Times")]
    [InlineData("dotnet tool update -g --add-source ./nupkg Times")]
    [InlineData("dotnet tool uninstall -g Times")]
    [InlineData("`tm`")]
    [InlineData("tm --version")]
    [InlineData("built-in")]
    public void Readme_ExplainsToolInstall(string expected)
    {
        string readme = File.ReadAllText(Path.Combine(RepoRoot, "README.md"));

        Assert.Contains("## Install as a .NET tool", readme, StringComparison.Ordinal);
        Assert.Contains(expected, readme, StringComparison.Ordinal);
    }

    // AC-6
    [Theory]
    [InlineData("nupkg/x.nupkg")]
    [InlineData("nupkg/")]
    public void NupkgFolder_IsGitIgnored(string path)
    {
        ProcessStartInfo startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = RepoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("check-ignore");
        startInfo.ArgumentList.Add("-q");
        startInfo.ArgumentList.Add(path);

        using Process process = Process.Start(startInfo)!;
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.True(process.ExitCode == 0, "git check-ignore exit " + process.ExitCode + ": " + error);
    }

    private static XElement LoadProject()
    {
        return XDocument.Load(Path.Combine(RepoRoot, "src", "Times", "Times.csproj")).Root!;
    }

    private static string SingleProperty(XElement project, string name)
    {
        XElement element = Assert.Single(project.Elements("PropertyGroup").Elements(name));
        return element.Value.Trim();
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Times.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Times.slnx not found above " + AppContext.BaseDirectory);
    }
}
