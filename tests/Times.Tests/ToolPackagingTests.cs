using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Times.Tests;

public sealed class ToolPackagingTests
{
    private const string NugetOrg = "https://api.nuget.org/v3/index.json";
    private const string LocalFolder = "./nupkg";

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
    [InlineData("dotnet tool install -g Times")]
    [InlineData("dotnet tool update -g Times")]
    [InlineData("dotnet tool uninstall -g Times")]
    [InlineData("`tm`")]
    [InlineData("tm --version")]
    [InlineData("built-in")]
    [InlineData("dotnet pack src/Times -c Release")]
    [InlineData("raise `<Version>`")]
    [InlineData("nuget.config")]
    [InlineData("packageSourceMapping")]
    [InlineData("from the repo root (where `nuget.config` lives)")]
    [InlineData("maps the `Times` package to `./nupkg` only, so a `Times` package on nuget.org is never used")]
    public void Readme_ExplainsToolInstall(string expected)
    {
        string readme = Regex.Replace(File.ReadAllText(Path.Combine(RepoRoot, "README.md")), @"\s+", " ");

        Assert.Contains("## Install as a .NET tool", readme, StringComparison.Ordinal);
        Assert.Contains(expected, readme, StringComparison.Ordinal);
    }

    // AC-4 (055)
    [Fact]
    public void Readme_HasNoAddSource()
    {
        string readme = File.ReadAllText(Path.Combine(RepoRoot, "README.md"));

        Assert.DoesNotContain("--add-source", readme, StringComparison.Ordinal);
    }

    // AC-1 (055)
    [Fact]
    public void NugetConfig_ClearsSourcesAndAddsNugetOrgAndLocalFolder()
    {
        XElement sources = Assert.Single(LoadNugetConfig().Elements("packageSources"));

        XElement first = sources.Elements().First();
        Assert.Equal("clear", first.Name.LocalName);
        List<string?> values = sources.Elements("add").Select(add => (string?)add.Attribute("value")).ToList();
        Assert.Equal(2, values.Count);
        Assert.Contains(NugetOrg, values);
        Assert.Contains(LocalFolder, values);
    }

    // AC-2 (055)
    [Fact]
    public void NugetConfig_MapsTimesToLocalFolderOnly()
    {
        XElement config = LoadNugetConfig();
        Dictionary<string, string> valueByKey = Assert.Single(config.Elements("packageSources"))
            .Elements("add")
            .ToDictionary(add => (string)add.Attribute("key")!, add => (string)add.Attribute("value")!);
        XElement mapping = Assert.Single(config.Elements("packageSourceMapping"));
        Dictionary<string, List<string>> patternsBySource = mapping.Elements("packageSource")
            .ToDictionary(
                source => valueByKey[(string)source.Attribute("key")!],
                source => source.Elements("package").Select(package => (string)package.Attribute("pattern")!).ToList());

        Assert.Equal(new[] { "Times" }, patternsBySource[LocalFolder]);
        Assert.Equal(new[] { "*" }, patternsBySource[NugetOrg]);
        foreach (KeyValuePair<string, List<string>> pair in patternsBySource)
        {
            if (pair.Key != LocalFolder && pair.Key != NugetOrg)
            {
                Assert.DoesNotContain(pair.Value, pattern => PatternMatchesTimes(pattern));
            }
        }
    }

    // AC-10 (040)
    [Theory]
    [InlineData("README.md")]
    [InlineData("CLAUDE.md")]
    public void Docs_NameCommandsWithoutSlash(string file)
    {
        string text = File.ReadAllText(Path.Combine(RepoRoot, file));

        Assert.DoesNotMatch(@"`/[a-z]", text);
        Assert.DoesNotContain("/help", text, StringComparison.Ordinal);
        Assert.DoesNotContain("/quit", text, StringComparison.Ordinal);
    }

    // AC-10 (040)
    [Fact]
    public void Readme_SaysALeadingSlashStillWorks()
    {
        string readme = File.ReadAllText(Path.Combine(RepoRoot, "README.md")).ReplaceLineEndings("\n");

        Assert.Contains("A leading `/` is still accepted", readme, StringComparison.Ordinal);
    }

    // AC-10 (038)
    [Theory]
    [InlineData("## Mouse")]
    [InlineData("wheel scrolls the area above the input box")]
    [InlineData("Shift-drag")]
    [InlineData("Option-drag")]
    [InlineData("normal mouse behaviour comes back when Times\nexits")]
    public void Readme_ExplainsMouse(string expected)
    {
        string readme = File.ReadAllText(Path.Combine(RepoRoot, "README.md")).ReplaceLineEndings("\n");

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

    private static bool PatternMatchesTimes(string pattern)
    {
        if (pattern.EndsWith('*'))
        {
            return "Times".StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(pattern, "Times", StringComparison.OrdinalIgnoreCase);
    }

    private static XElement LoadNugetConfig()
    {
        return XDocument.Load(Path.Combine(RepoRoot, "nuget.config")).Root!;
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
