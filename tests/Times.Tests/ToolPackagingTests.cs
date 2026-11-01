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

    // AC-6 (replaces 055 AC-4 "Readme_HasNoAddSource")
    [Fact]
    public void Readme_EveryAddSourceLinePinsVersion()
    {
        string[] lines = File.ReadAllLines(Path.Combine(RepoRoot, "README.md"));

        List<string> addSource = lines.Where(line => line.Contains("--add-source", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(addSource);
        Assert.All(addSource, line => Assert.Contains("--version", line, StringComparison.Ordinal));
    }

    // AC-6
    [Fact]
    public void Readme_HasInstallFromReleaseSection_InsideInstallSection()
    {
        string readme = "\n" + File.ReadAllText(Path.Combine(RepoRoot, "README.md")).ReplaceLineEndings("\n");

        int install = readme.IndexOf("\n## Install as a .NET tool\n", StringComparison.Ordinal);
        int release = readme.IndexOf("\n### Install from a GitHub release\n", StringComparison.Ordinal);
        int shortcuts = readme.IndexOf("\n## Shortcuts\n", StringComparison.Ordinal);
        Assert.True(install >= 0, "README has no '## Install as a .NET tool' section");
        Assert.True(install < release, "'### Install from a GitHub release' must come after '## Install as a .NET tool'");
        Assert.True(release < shortcuts, "'### Install from a GitHub release' must come before '## Shortcuts'");

        string section = readme[release..shortcuts];
        Assert.Contains("dotnet tool install --global --add-source <folder> --version <version> Times", section, StringComparison.Ordinal);
    }

    // AC-6
    [Fact]
    public void Readme_HasReleasingSection_AfterMouse()
    {
        string readme = "\n" + File.ReadAllText(Path.Combine(RepoRoot, "README.md")).ReplaceLineEndings("\n");

        int mouse = readme.IndexOf("\n## Mouse\n", StringComparison.Ordinal);
        int releasing = readme.IndexOf("\n## Releasing\n", StringComparison.Ordinal);
        Assert.True(mouse >= 0, "README has no '## Mouse' section");
        Assert.True(releasing > mouse, "'## Releasing' must come after '## Mouse'");

        int next = readme.IndexOf("\n## ", releasing + 1, StringComparison.Ordinal);
        string section = next < 0 ? readme[releasing..] : readme[releasing..next];
        Assert.Contains("git tag v1.2.3", section, StringComparison.Ordinal);
        Assert.Contains("git push origin v1.2.3", section, StringComparison.Ordinal);
        Assert.Contains(".github/workflows/release.yml", section, StringComparison.Ordinal);
    }

    // AC-1 (070)
    [Fact]
    public void ReleaseWorkflow_TriggersOnlyOnVersionTagPush()
    {
        string workflow = ReadWorkflow();

        string on = TopLevelBlock(workflow, "on");
        List<string> keys = ChildLines(on).Select(line => line.Trim()).ToList();
        Assert.Equal(["push:", "tags:", "- \"v*\""], keys);
        Assert.DoesNotContain("branches", on, StringComparison.Ordinal);
        Assert.DoesNotContain("pull_request", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("workflow_dispatch", workflow, StringComparison.Ordinal);
    }

    // AC-1 (070)
    [Fact]
    public void ReleaseWorkflow_GrantsContentsWriteOnly()
    {
        string permissions = TopLevelBlock(ReadWorkflow(), "permissions");

        Assert.Equal(["contents: write"], ChildLines(permissions).Select(line => line.Trim()).ToList());
    }

    // AC-1 (070), security note: only GitHub-owned actions.
    [Fact]
    public void ReleaseWorkflow_UsesOnlyGitHubOwnedActions()
    {
        List<string> uses = ReadWorkflow().Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("uses:", StringComparison.Ordinal) || line.StartsWith("- uses:", StringComparison.Ordinal))
            .Select(line => line[(line.IndexOf("uses:", StringComparison.Ordinal) + 5)..].Trim())
            .ToList();

        Assert.NotEmpty(uses);
        Assert.All(uses, action => Assert.Matches(@"^actions/(checkout|setup-dotnet)@", action));
        Assert.Contains("dotnet-version: \"10.0.x\"", ReadWorkflow(), StringComparison.Ordinal);
    }

    // AC-2 (070)
    [Fact]
    public void ReleaseWorkflow_TestsBeforePack_AndPacksWithVersionFromTag()
    {
        string scripts = string.Join("\n", RunScripts(ReadWorkflow()));

        int strip = scripts.IndexOf("VERSION=\"${GITHUB_REF_NAME#v}\"", StringComparison.Ordinal);
        int test = scripts.IndexOf("dotnet test -c Release", StringComparison.Ordinal);
        int pack = scripts.IndexOf("dotnet pack src/Times -c Release -p:Version=\"$VERSION\"", StringComparison.Ordinal);
        Assert.True(strip >= 0, "no VERSION=\"${GITHUB_REF_NAME#v}\" in a run script");
        Assert.True(test > strip, "dotnet test must run after VERSION is set");
        Assert.True(pack > test, "dotnet pack must run after dotnet test");
        Assert.DoesNotContain(RunScripts(ReadWorkflow()), script => script.Contains("dotnet test", StringComparison.Ordinal) && script.Contains("-p:Version", StringComparison.Ordinal));
    }

    // AC-2 (070)
    [Fact]
    public void ReleaseWorkflow_RunScriptsHaveNoExpressionInterpolation()
    {
        List<string> scripts = RunScripts(ReadWorkflow());

        Assert.NotEmpty(scripts);
        Assert.All(scripts, script =>
        {
            Assert.DoesNotContain("${{ github.ref", script, StringComparison.Ordinal);
            Assert.DoesNotContain("${{ github.ref_name", script, StringComparison.Ordinal);
            Assert.DoesNotContain("${{", script, StringComparison.Ordinal);
        });
    }

    // AC-3 (070)
    [Fact]
    public void ReleaseWorkflow_SmokeChecksInstalledToolBeforeRelease()
    {
        List<string> scripts = RunScripts(ReadWorkflow());
        string all = string.Join("\n", scripts);

        string smoke = Assert.Single(scripts, script => script.Contains("tm --version", StringComparison.Ordinal));
        int temp = smoke.IndexOf("cd \"$RUNNER_TEMP\"", StringComparison.Ordinal);
        int install = smoke.IndexOf("dotnet tool install --global --add-source \"$REPO/nupkg\" --version \"$VERSION\" Times", StringComparison.Ordinal);
        Assert.True(temp >= 0, "the smoke check must cd to $RUNNER_TEMP");
        Assert.True(install > temp, "the tool must be installed from outside the repo");
        Assert.True(smoke.IndexOf("tm --version", StringComparison.Ordinal) > install);
        Assert.Contains("!= \"Times $VERSION\"", smoke, StringComparison.Ordinal);
        Assert.Contains("exit 1", smoke, StringComparison.Ordinal);

        int pack = all.IndexOf("dotnet pack", StringComparison.Ordinal);
        int check = all.IndexOf("tm --version", StringComparison.Ordinal);
        int release = all.IndexOf("gh release create", StringComparison.Ordinal);
        Assert.True(pack < check, "the smoke check must run after dotnet pack");
        Assert.True(check < release, "gh release create must run after the smoke check");
        Assert.Contains("gh release create \"$GITHUB_REF_NAME\" \"nupkg/Times.$VERSION.nupkg\" --title \"Times $VERSION\" --generate-notes", all, StringComparison.Ordinal);
        Assert.Contains("--prerelease", all, StringComparison.Ordinal);
        Assert.Contains("GH_TOKEN: ${{ github.token }}", ReadWorkflow(), StringComparison.Ordinal);
    }

    // AC-4 (070)
    [Theory]
    [InlineData("1.2.3", true)]
    [InlineData("0.1.0", true)]
    [InlineData("1.2.3-beta.1", true)]
    [InlineData("v1.2.3", false)]
    [InlineData("1.2", false)]
    [InlineData("1.2.3.4", false)]
    [InlineData("01.2.3", false)]
    [InlineData("1.2.3+abc", false)]
    [InlineData("abc", false)]
    [InlineData("", false)]
    public async Task ProjectFile_ValidateVersion_AcceptsOnlySemVer(string version, bool valid)
    {
        ProcessStartInfo startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = RepoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("msbuild");
        startInfo.ArgumentList.Add(Path.Combine("src", "Times", "Times.csproj"));
        startInfo.ArgumentList.Add("-t:ValidateVersion");
        startInfo.ArgumentList.Add("-p:Version=" + version);
        startInfo.ArgumentList.Add("-nologo");
        startInfo.ArgumentList.Add("-nodeReuse:false");

        using Process process = Process.Start(startInfo)!;
        Task<string> error = process.StandardError.ReadToEndAsync();
        string output = await process.StandardOutput.ReadToEndAsync() + await error;
        await process.WaitForExitAsync();

        if (valid)
        {
            Assert.True(process.ExitCode == 0, "ValidateVersion exit " + process.ExitCode + ": " + output);
        }
        else
        {
            Assert.True(process.ExitCode != 0, "ValidateVersion accepted '" + version + "'");
            Assert.Contains("Version '" + version + "' is not a valid SemVer version", output, StringComparison.Ordinal);
        }
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
        bool inFence = false;
        foreach (string line in readme.Split('\n'))
        {
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
            }
            else if (!inFence && line.StartsWith("## ", StringComparison.Ordinal))
            {
                headings.Add(line);
            }
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

    private static string ReadWorkflow()
    {
        return File.ReadAllText(Path.Combine(RepoRoot, ".github", "workflows", "release.yml")).ReplaceLineEndings("\n");
    }

    /// <summary>The lines of the top-level <c>key:</c> block, up to the next top-level line (comments and blank lines skipped).</summary>
    private static string TopLevelBlock(string yaml, string key)
    {
        string[] lines = yaml.Split('\n');
        int start = Array.FindIndex(lines, line => line == key + ":");
        Assert.True(start >= 0, "workflow has no top-level '" + key + ":'");

        List<string> block = [lines[start]];
        for (int i = start + 1; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.Trim().Length == 0 || line.TrimStart().StartsWith('#'))
            {
                continue;
            }

            if (!char.IsWhiteSpace(line[0]))
            {
                break;
            }

            block.Add(line);
        }

        return string.Join('\n', block);
    }

    private static IEnumerable<string> ChildLines(string block)
    {
        return block.Split('\n').Skip(1);
    }

    /// <summary>The body of every <c>run:</c> key: the inline value, or the indented lines of a <c>run: |</c> block.</summary>
    private static List<string> RunScripts(string yaml)
    {
        string[] lines = yaml.Split('\n');
        List<string> scripts = [];
        for (int i = 0; i < lines.Length; i++)
        {
            string trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                trimmed = trimmed[2..].TrimStart();
            }

            if (!trimmed.StartsWith("run:", StringComparison.Ordinal))
            {
                continue;
            }

            string value = trimmed[4..].Trim();
            if (value != "|" && value != ">")
            {
                scripts.Add(value);
                continue;
            }

            int indent = lines[i].Length - lines[i].TrimStart().Length;
            List<string> body = [];
            while (i + 1 < lines.Length && (lines[i + 1].Trim().Length == 0 || lines[i + 1].Length - lines[i + 1].TrimStart().Length > indent))
            {
                i++;
                body.Add(lines[i]);
            }

            scripts.Add(string.Join('\n', body));
        }

        return scripts;
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
