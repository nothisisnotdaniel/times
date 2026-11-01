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

    // AC-1 (063)
    [Fact]
    public void Readme_HasKeysSectionBeforeMouse()
    {
        string readme = "\n" + File.ReadAllText(Path.Combine(RepoRoot, "README.md")).ReplaceLineEndings("\n");

        int keys = readme.IndexOf("\n## Keys\n", StringComparison.Ordinal);
        int mouse = readme.IndexOf("\n## Mouse\n", StringComparison.Ordinal);

        Assert.True(keys >= 0, "README has no '## Keys' line");
        Assert.True(keys < mouse, "'## Keys' must come before '## Mouse'");
    }

    // AC-2 (063)
    [Fact]
    public void Readme_KeysSectionListsScrollKeys()
    {
        string section = ReadmeKeysSection();

        Assert.Contains("PgUp", section, StringComparison.Ordinal);
        Assert.Contains("PgDn", section, StringComparison.Ordinal);
        Assert.Contains("Shift+PgUp", section, StringComparison.Ordinal);
        Assert.Contains("Shift+PgDn", section, StringComparison.Ordinal);
        Assert.Contains("Ctrl+↑", section, StringComparison.Ordinal);
        Assert.Contains("Ctrl+↓", section, StringComparison.Ordinal);
        Assert.Contains("scroll", section, StringComparison.OrdinalIgnoreCase);
    }

    // AC-3 (063)
    [Fact]
    public void Readme_KeysSectionSaysArrowsWalkHistory()
    {
        string section = ReadmeKeysSection();

        Assert.Contains("↑", section, StringComparison.Ordinal);
        Assert.Contains("↓", section, StringComparison.Ordinal);
        Assert.Contains("history", section, StringComparison.Ordinal);
    }

    // AC-4 (063)
    [Fact]
    public void Readme_KeysSectionExplainsMacOsTerminal()
    {
        string section = ReadmeKeysSection();

        string? paragraph = section
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(part => part.Contains("macOS Terminal", StringComparison.Ordinal));

        Assert.NotNull(paragraph);
        Assert.Contains("Shift", paragraph, StringComparison.Ordinal);
        Assert.Contains("Ctrl", paragraph, StringComparison.Ordinal);
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

    // AC-6 (065)
    [Fact]
    public void Readme_HasInstallBeforeUsageSections()
    {
        string readme = "\n" + File.ReadAllText(Path.Combine(RepoRoot, "README.md")).ReplaceLineEndings("\n");
        List<string> headings = [];
        int index = readme.IndexOf("\n## ", StringComparison.Ordinal);
        while (index >= 0)
        {
            int end = readme.IndexOf('\n', index + 1);
            headings.Add(readme[(index + 1)..end]);
            index = readme.IndexOf("\n## ", end, StringComparison.Ordinal);
        }

        int install = headings.IndexOf("## Install as a .NET tool");
        int shortcuts = headings.IndexOf("## Shortcuts");
        Assert.True(install >= 0, "README has no '## Install as a .NET tool' section");
        Assert.True(install < shortcuts, "'## Install as a .NET tool' must come before '## Shortcuts'");
        Assert.Equal(["## Shortcuts", "## Keys", "## Mouse"], headings.GetRange(shortcuts, 3));

        int sdk = readme.IndexOf("It needs the [.NET 10 SDK]", StringComparison.Ordinal);
        Assert.True(sdk >= 0, "README has no '.NET 10 SDK' paragraph");
        Assert.True(sdk < readme.IndexOf("\n## Shortcuts\n", StringComparison.Ordinal));
    }

    // AC-1 (065)
    [Fact]
    public void LineEditorSource_HasNoEscapeByte_AndNoLongDocLines()
    {
        string[] lines = File.ReadAllLines(Path.Combine(RepoRoot, "src", "Times", "LineEditor.cs"));

        Assert.DoesNotContain(lines, line => line.Contains('\u001b'));
        Assert.DoesNotContain(lines, line => line.TrimStart().StartsWith("///", StringComparison.Ordinal) && line.Length > 100);
    }

    // AC-2, AC-3 (065)
    [Fact]
    public void MouseInputSource_ParsesOnce_AndHasNoX10()
    {
        string source = File.ReadAllText(Path.Combine(RepoRoot, "src", "Times", "MouseInput.cs"));

        Assert.DoesNotContain("int.Parse(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("X10", source, StringComparison.Ordinal);
        Assert.DoesNotContain("keys[1] == 'M'", source, StringComparison.Ordinal);
    }

    private static string ReadmeKeysSection()
    {
        string readme = File.ReadAllText(Path.Combine(RepoRoot, "README.md")).ReplaceLineEndings("\n");
        int start = readme.IndexOf("## Keys\n", StringComparison.Ordinal);
        Assert.True(start >= 0, "README has no '## Keys' section");

        int end = readme.IndexOf("\n## ", start, StringComparison.Ordinal);
        return end < 0 ? readme[start..] : readme[start..end];
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
