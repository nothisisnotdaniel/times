using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Times.Tests;

public sealed class ToolPackagingTests
{
    private const string NugetOrg = "https://api.nuget.org/v3/index.json";
    private const string LocalFolder = "./nupkg";
    private const string SemVerGrep = "grep -Eq '^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)(-[0-9A-Za-z-]+(\\.[0-9A-Za-z-]+)*)?$'";

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

    // AC-1 (075, replaces 072 "ReleaseWorkflow_TriggersOnDevelopPushVersionTagPushAndClosedPrIntoMain"), AC-3 (075)
    [Fact]
    public void ReleaseWorkflow_TriggersOnDevelopPushVersionTagPushAndClosedPrIntoMain_IgnoringBacklogOnlyPushes()
    {
        string workflow = ReadWorkflow();

        string on = TopLevelBlock(workflow, "on");
        List<string> keys = ChildLines(on).Select(line => line.Trim()).ToList();
        Assert.Equal(
            ["push:", "branches:", "- \"develop\"", "tags:", "- \"v*\"", "paths-ignore:", "- \"backlog.md\"", "pull_request:", "types:", "- \"closed\"", "branches:", "- \"main\""],
            keys);
        Assert.DoesNotContain("pull_request_target", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("workflow_dispatch", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain(workflow.Split('\n'), line => line.Trim().StartsWith("paths:", StringComparison.Ordinal));
    }

    // AC-2 (075): only push ignores paths, and only the root backlog.md; a merged release PR always runs.
    [Fact]
    public void ReleaseWorkflow_PathsIgnoreOnlyUnderPushAndOnlyRootBacklog()
    {
        string workflow = ReadWorkflow();
        List<string> on = ChildLines(TopLevelBlock(workflow, "on")).Select(line => line.Trim()).ToList();
        int pr = on.IndexOf("pull_request:");
        Assert.True(pr > 0, "the on block has no pull_request:");
        List<string> push = on[..pr];
        List<string> pullRequest = on[pr..];

        Assert.Contains("paths-ignore:", push);
        Assert.DoesNotContain("paths-ignore:", pullRequest);
        Assert.Equal(["pull_request:", "types:", "- \"closed\"", "branches:", "- \"main\""], pullRequest);

        Assert.Single(workflow.Split('\n'), line => line.Trim().StartsWith("paths-ignore:", StringComparison.Ordinal));
        int ignore = push.IndexOf("paths-ignore:");
        List<string> entries = push[(ignore + 1)..].TakeWhile(line => line.StartsWith("- ", StringComparison.Ordinal)).ToList();
        string entry = Assert.Single(entries);
        Assert.Equal("- \"backlog.md\"", entry);
        string path = entry[2..].Trim('"');
        Assert.DoesNotContain(path, c => c == '*' || c == '?' || c == '[' || c == '!' || c == '/' || c == '\\');
    }

    // AC-4 (075)
    [Fact]
    public void ReleaseWorkflow_HeaderCommentMentionsBacklogOnlyPushes()
    {
        string workflow = ReadWorkflow();
        int name = workflow.IndexOf("\nname: Release\n", StringComparison.Ordinal);
        Assert.True(name >= 0, "workflow has no 'name: Release' line");

        Assert.Contains("backlog.md", workflow[..name], StringComparison.Ordinal);
    }

    // AC-1 (072): the job runs for every push, and for a pull_request only when merged from develop in this repo.
    [Fact]
    public void ReleaseWorkflow_JobIfSkipsUnmergedForeignAndForkPullRequests()
    {
        string jobs = TopLevelBlock(ReadWorkflow(), "jobs");
        string ifLine = Assert.Single(jobs.Split('\n'), line => line.StartsWith("    if:", StringComparison.Ordinal));

        Assert.Equal(
            "    if: github.event_name == 'push' || (github.event.pull_request.merged == true && github.event.pull_request.head.ref == 'develop' && github.event.pull_request.head.repo.full_name == github.repository)",
            ifLine);
        int runsOn = jobs.IndexOf("    runs-on:", StringComparison.Ordinal);
        int steps = jobs.IndexOf("    steps:", StringComparison.Ordinal);
        Assert.True(jobs.IndexOf(ifLine, StringComparison.Ordinal) < steps && runsOn < steps, "the if: must be on the job, not a step");
    }

    // AC-1 (072): a release PR checks out its merge commit; pushes keep the default (the expression is empty).
    [Fact]
    public void ReleaseWorkflow_CheckoutUsesMergeCommitForPullRequests()
    {
        string[] step = CheckoutStep();

        Assert.Contains(step, line => line.Trim() == "ref: ${{ github.event.pull_request.merge_commit_sha }}");
    }

    // AC-1 (071): full history, so the tags and origin/main are there.
    [Fact]
    public void ReleaseWorkflow_CheckoutFetchesFullHistory()
    {
        string[] step = CheckoutStep();

        Assert.Contains(step, line => line.Trim() == "fetch-depth: 0");
        Assert.Contains(step, line => line.Trim() == "persist-credentials: false");
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
        int install = smoke.IndexOf("dotnet tool install --global --configfile \"$RUNNER_TEMP/smoke-nuget.config\" --add-source \"$REPO/nupkg\" --version \"$VERSION\" Times", StringComparison.Ordinal);
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
        Assert.Contains("gh release create \"$TAG\" \"nupkg/Times.$VERSION.nupkg\" --title \"Times $VERSION\" --generate-notes --target \"$TARGET\" $PRERELEASE", all, StringComparison.Ordinal);
        Assert.Contains("--prerelease", all, StringComparison.Ordinal);
        Assert.Contains("GH_TOKEN: ${{ github.token }}", ReadWorkflow(), StringComparison.Ordinal);
    }

    // AC-3 (070), security review round 1: the smoke install sees only the local folder, never nuget.org.
    [Fact]
    public void ReleaseWorkflow_SmokeInstallUsesIsolatedNugetConfig()
    {
        string smoke = Assert.Single(RunScripts(ReadWorkflow()), script => script.Contains("tm --version", StringComparison.Ordinal));
        string[] lines = smoke.Split('\n').Select(line => line.Trim()).ToArray();

        int cd = Array.IndexOf(lines, "cd \"$RUNNER_TEMP\"");
        int write = Array.IndexOf(lines, "cat > smoke-nuget.config <<XML");
        int end = Array.IndexOf(lines, "XML");
        int install = Array.FindIndex(lines, line => line.StartsWith("dotnet tool install", StringComparison.Ordinal));
        Assert.True(cd >= 0 && cd < write, "the config must be written in $RUNNER_TEMP");
        Assert.True(write < end && end < install, "the config must be written before the install");

        string[] config = lines[(write + 1)..end];
        Assert.Contains("<clear />", config);
        Assert.Equal(["<add key=\"local\" value=\"$REPO/nupkg\" />"], config.Where(line => line.StartsWith("<add", StringComparison.Ordinal)).ToArray());
        Assert.True(Array.IndexOf(config, "<clear />") < Array.FindIndex(config, line => line.StartsWith("<add", StringComparison.Ordinal)));
        Assert.Contains("--configfile \"$RUNNER_TEMP/smoke-nuget.config\"", lines[install], StringComparison.Ordinal);
    }

    // Security review round 1: the checkout does not leave the write token in .git/config.
    [Fact]
    public void ReleaseWorkflow_CheckoutDoesNotPersistCredentials()
    {
        string[] step = CheckoutStep();

        Assert.Contains(step, line => line.Trim() == "persist-credentials: false");
    }

    // Security review round 1: the tag is checked against SemVer where it enters, before msbuild.
    [Fact]
    public void ReleaseWorkflow_ChecksTagIsSemVerBeforeExportingIt()
    {
        string script = Assert.Single(RunScripts(ReadWorkflow()), s => s.Contains("GITHUB_REF_NAME#v", StringComparison.Ordinal));

        int check = script.IndexOf(SemVerGrep, StringComparison.Ordinal);
        int export = script.IndexOf(">> \"$GITHUB_ENV\"", StringComparison.Ordinal);
        Assert.True(check >= 0, "no SemVer check on the tag");
        Assert.True(check < export, "the tag must be checked before VERSION is exported");
        Assert.Contains("exit 1", script[check..export], StringComparison.Ordinal);
        Assert.Single(Regex.Matches(script, Regex.Escape(SemVerGrep)));
        Assert.Equal(script.LastIndexOf(SemVerGrep, StringComparison.Ordinal), check);
        Assert.Contains("echo \"VERSION=$VERSION\" >> \"$GITHUB_ENV\"", script[check..], StringComparison.Ordinal);
        Assert.Contains("echo \"TAG=$TAG\" >> \"$GITHUB_ENV\"", script[check..], StringComparison.Ordinal);
        Assert.Contains("echo \"TARGET=$TARGET\" >> \"$GITHUB_ENV\"", script[check..], StringComparison.Ordinal);
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

    // AC-2 (071)
    [Theory]
    [InlineData("v0.1.0-beta\n", "12", "0.1.1-develop.12", true)]
    [InlineData("v0.1.0\n", "12", "0.1.1-develop.12", true)]
    [InlineData("v0.1.0-beta\nv0.1.0\nv0.1.1-develop.5\n", "12", "0.1.1-develop.12", true)]
    [InlineData("v0.2.0\nv0.10.0\nv0.9.3\n", "12", "0.10.1-develop.12", true)]
    [InlineData("v1.2\nvx\n", "12", "0.0.1-develop.12", true)]
    [InlineData("", "12", "0.0.1-develop.12", true)]
    [InlineData("v0.1.0\n", "0", "", false)]
    [InlineData("v0.1.0\n", "abc", "", false)]
    [InlineData("v0.1.0\n", "12;x", "", false)]
    [InlineData("v0.1.0\n", "", "", false)]
    public async Task DevelopVersionScript_PrintsNextPatchDevelopVersion(string tags, string build, string expected, bool succeeds)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        ProcessStartInfo startInfo = new ProcessStartInfo("bash")
        {
            WorkingDirectory = RepoRoot,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(Path.Combine(".github", "scripts", "develop-version.sh"));
        startInfo.ArgumentList.Add(build);

        using Process process = Process.Start(startInfo)!;
        try
        {
            Stream input = process.StandardInput.BaseStream;
            await input.WriteAsync(System.Text.Encoding.UTF8.GetBytes(tags));
            input.Close();
        }
        catch (IOException)
        {
            // The script refuses a bad build number before it reads stdin, so the pipe can already be closed.
        }
        Task<string> error = process.StandardError.ReadToEndAsync();
        string output = await process.StandardOutput.ReadToEndAsync();
        await error;
        await process.WaitForExitAsync();

        Assert.Equal(expected, output.Trim());
        if (succeeds)
        {
            Assert.True(process.ExitCode == 0, "develop-version.sh exit " + process.ExitCode + ": " + await error);
        }
        else
        {
            Assert.NotEqual(0, process.ExitCode);
        }
    }

    // AC-3 (071), AC-5 (072): pull_request first, then develop push, then tag push; one SemVer check after all three.
    [Fact]
    public void ReleaseWorkflow_DevelopPushComputesVersionFromTags()
    {
        string script = VersionScript();

        int pr = script.IndexOf("if [ \"$GITHUB_EVENT_NAME\" = \"pull_request\" ]; then", StringComparison.Ordinal);
        int prTarget = script.IndexOf("TARGET=\"$MERGE_SHA\"", StringComparison.Ordinal);
        int branch = script.IndexOf("elif [ \"$GITHUB_REF_TYPE\" = \"branch\" ]; then", StringComparison.Ordinal);
        int compute = script.IndexOf("VERSION=\"$(git tag --list 'v*' | sh .github/scripts/develop-version.sh \"$GITHUB_RUN_NUMBER\")\"", StringComparison.Ordinal);
        int tag = script.IndexOf("TAG=\"v$VERSION\"", branch, StringComparison.Ordinal);
        int developTarget = script.IndexOf("TARGET=\"$GITHUB_SHA\"", branch, StringComparison.Ordinal);
        int otherwise = LineIndex(script, "else", branch);
        int fromRef = script.IndexOf("VERSION=\"${GITHUB_REF_NAME#v}\"", StringComparison.Ordinal);
        int refTag = script.IndexOf("TAG=\"$GITHUB_REF_NAME\"", StringComparison.Ordinal);
        int refTarget = script.IndexOf("TARGET=\"$GITHUB_SHA\"", refTag, StringComparison.Ordinal);
        int fi = LineIndex(script, "fi", refTarget);
        int check = script.IndexOf(SemVerGrep, StringComparison.Ordinal);
        Assert.True(pr >= 0, "no GITHUB_EVENT_NAME pull_request test");
        Assert.True(pr < prTarget && prTarget < branch, "the pull_request branch must come first and set TARGET");
        Assert.True(branch < compute && compute < tag && tag < developTarget && developTarget < otherwise, "develop branch must compute VERSION, TAG and TARGET");
        Assert.True(otherwise < fromRef && fromRef < refTag && refTag < refTarget && refTarget < fi, "tag push must take VERSION and TAG from the ref and TARGET from GITHUB_SHA");
        Assert.True(fi < check, "the SemVer check must come after all three branches");
        Assert.True(File.Exists(Path.Combine(RepoRoot, ".github", "scripts", "develop-version.sh")));
    }

    // AC-4 (072): PR fields come only from the step env:, are re-checked, and all refusals run before dotnet test.
    [Fact]
    public void ReleaseWorkflow_PullRequestBranchRechecksPrAndMergeCommit()
    {
        string workflow = ReadWorkflow();
        string[] lines = workflow.Split('\n');
        int versionStep = Array.FindIndex(lines, line => line.Trim() == "- name: Version");
        int run = Array.FindIndex(lines, versionStep, line => line.Trim() == "run: |");
        List<string> env = lines[(versionStep + 1)..run]
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToList();
        Assert.Equal(
            [
                "env:",
                "PR_TITLE: ${{ github.event.pull_request.title }}",
                "PR_MERGED: ${{ github.event.pull_request.merged }}",
                "PR_HEAD_REF: ${{ github.event.pull_request.head.ref }}",
                "PR_HEAD_REPO: ${{ github.event.pull_request.head.repo.full_name }}",
                "MERGE_SHA: ${{ github.event.pull_request.merge_commit_sha }}",
            ],
            env);

        string script = VersionScript();
        int pr = script.IndexOf("if [ \"$GITHUB_EVENT_NAME\" = \"pull_request\" ]; then", StringComparison.Ordinal);
        string[] checks =
        [
            "if [ \"$PR_MERGED\" != \"true\" ]; then",
            "if [ \"$PR_HEAD_REF\" != \"develop\" ]; then",
            "if [ \"$PR_HEAD_REPO\" != \"$GITHUB_REPOSITORY\" ]; then",
            "'' | *[!0-9a-f]*)",
            "if [ \"${#MERGE_SHA}\" -ne 40 ]; then",
            "if [ \"$(git rev-parse HEAD)\" != \"$MERGE_SHA\" ]; then",
            "if ! git merge-base --is-ancestor \"$MERGE_SHA\" origin/main; then",
            "if ! VERSION=\"$(git tag --list 'v*' | sh .github/scripts/release-pr-version.sh \"$PR_TITLE\")\"; then",
        ];
        int at = pr;
        foreach (string check in checks)
        {
            int next = script.IndexOf(check, at, StringComparison.Ordinal);
            Assert.True(next > at, "'" + check + "' missing or out of order");
            int end = script.IndexOf("elif [", next, StringComparison.Ordinal);
            string after = script[next..script.IndexOf("fi\n", next, StringComparison.Ordinal)];
            if (check.EndsWith(')'))
            {
                after = script[next..script.IndexOf(";;", next, StringComparison.Ordinal)];
            }

            Assert.True(end > next, "'" + check + "' must be in the pull_request branch");
            Assert.Contains(">&2", after, StringComparison.Ordinal);
            Assert.Contains("exit 1", after, StringComparison.Ordinal);
            at = next;
        }

        int tag = script.IndexOf("TAG=\"v$VERSION\"", at, StringComparison.Ordinal);
        int target = script.IndexOf("TARGET=\"$MERGE_SHA\"", at, StringComparison.Ordinal);
        Assert.True(at < tag && tag < target, "TAG and TARGET must be set after the version");

        string branch = script[pr..script.IndexOf("elif [", pr, StringComparison.Ordinal)];
        Assert.DoesNotContain("eval", branch, StringComparison.Ordinal);
        Assert.DoesNotContain("sh -c", branch, StringComparison.Ordinal);
        Assert.DoesNotContain("github.event", branch, StringComparison.Ordinal);

        string all = string.Join("\n", RunScripts(workflow));
        Assert.True(all.IndexOf("release-pr-version.sh", StringComparison.Ordinal) < all.IndexOf("dotnet test", StringComparison.Ordinal), "the PR checks must run before dotnet test");
        Assert.True(File.Exists(Path.Combine(RepoRoot, ".github", "scripts", "release-pr-version.sh")));
    }

    // AC-2, AC-3 (072)
    [Theory]
    [InlineData("release v1.2.3", "", "1.2.3", true)]
    [InlineData("release v0.1.0", "", "0.1.0", true)]
    [InlineData("release v1.2.3-beta.1", "", "1.2.3-beta.1", true)]
    [InlineData("release v1.2.3", "v1.2.30\nv1.2.3-beta.1\n", "1.2.3", true)]
    [InlineData("release v1.2.3", "v1.2.30\nv1.2.3-beta.1", "1.2.3", true)]
    [InlineData("release v1.2.3", "v1.2.2\nv1.2.3\n", "", false)]
    [InlineData("release v1.2.3", "v1.2.2\nv1.2.3", "", false)]
    [InlineData("Release v1.2.3", "", "", false)]
    [InlineData("release 1.2.3", "", "", false)]
    [InlineData(" release v1.2.3", "", "", false)]
    [InlineData("release v1.2.3 ", "", "", false)]
    [InlineData("release  v1.2.3", "", "", false)]
    [InlineData("release v1.2", "", "", false)]
    [InlineData("release v01.2.3", "", "", false)]
    [InlineData("release v1.2.3.4", "", "", false)]
    [InlineData("release v1.2.3+abc", "", "", false)]
    [InlineData("release v1.2.3-develop.4", "", "", false)]
    [InlineData("release v1.2.3;x", "", "", false)]
    [InlineData("release v1.2.3\nrelease v1.2.4", "", "", false)]
    [InlineData("release v1.2.3\n", "", "", false)]
    [InlineData("release v1.2.3\r", "", "", false)]
    [InlineData("release v$(id)", "", "", false)]
    [InlineData("", "", "", false)]
    public async Task ReleasePrVersionScript_AcceptsOnlyExactReleaseTitleForNewTag(string title, string tags, string expected, bool succeeds)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        ProcessStartInfo startInfo = new ProcessStartInfo("bash")
        {
            WorkingDirectory = RepoRoot,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(Path.Combine(".github", "scripts", "release-pr-version.sh"));
        startInfo.ArgumentList.Add(title);

        using Process process = Process.Start(startInfo)!;
        try
        {
            Stream input = process.StandardInput.BaseStream;
            await input.WriteAsync(System.Text.Encoding.UTF8.GetBytes(tags));
            input.Close();
        }
        catch (IOException)
        {
            // The script refuses a bad title before it reads stdin, so the pipe can already be closed.
        }
        Task<string> error = process.StandardError.ReadToEndAsync();
        string output = await process.StandardOutput.ReadToEndAsync();
        await error;
        await process.WaitForExitAsync();

        if (succeeds)
        {
            Assert.Equal(expected + "\n", output);
            Assert.True(process.ExitCode == 0, "release-pr-version.sh exit " + process.ExitCode + ": " + await error);
        }
        else
        {
            Assert.Equal(string.Empty, output);
            Assert.NotEqual(0, process.ExitCode);
            Assert.NotEqual(string.Empty, await error);
        }
    }

    // AC-2 (072): the title is only compared, never evaluated.
    [Fact]
    public void ReleasePrVersionScript_NeverEvaluatesTheTitle()
    {
        string source = File.ReadAllText(Path.Combine(RepoRoot, ".github", "scripts", "release-pr-version.sh")).ReplaceLineEndings("\n");
        string code = string.Join("\n", source.Split('\n').Where(line => !line.TrimStart().StartsWith('#')));

        Assert.StartsWith("#!/bin/sh\n", source, StringComparison.Ordinal);
        Assert.DoesNotContain("eval", code, StringComparison.Ordinal);
        Assert.DoesNotContain("sh -c", code, StringComparison.Ordinal);
        Assert.Contains("title=\"${1-}\"", code, StringComparison.Ordinal);
        Assert.Contains("LC_ALL=C\nexport LC_ALL\n", code, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"[^""{]\$title", code);
        Assert.DoesNotMatch(@"[^""]\$version", code.Replace("v$version", "", StringComparison.Ordinal));
    }

    // AC-4 (071), AC-5 (072)
    [Fact]
    public void ReleaseWorkflow_TagPushRefusesDevelopTagsAndTagsNotOnMain()
    {
        List<string> scripts = RunScripts(ReadWorkflow());
        string all = string.Join("\n", scripts);
        string script = VersionScript();

        int otherwise = LineIndex(script, "else", 0);
        int develop = script.IndexOf("*-develop*)", StringComparison.Ordinal);
        int main = script.IndexOf("if ! git merge-base --is-ancestor \"$GITHUB_SHA\" origin/main; then", StringComparison.Ordinal);
        int fromRef = script.IndexOf("VERSION=\"${GITHUB_REF_NAME#v}\"", StringComparison.Ordinal);
        Assert.True(develop > otherwise, "no -develop refusal in the tag branch");
        Assert.True(main > develop, "no main-ancestor check after the -develop refusal");
        Assert.True(fromRef > main, "both checks must run before VERSION is taken from the tag");
        Assert.Contains(">&2", script[develop..main], StringComparison.Ordinal);
        Assert.Contains("exit 1", script[develop..main], StringComparison.Ordinal);
        Assert.Contains(">&2", script[main..fromRef], StringComparison.Ordinal);
        Assert.Contains("exit 1", script[main..fromRef], StringComparison.Ordinal);

        int test = all.IndexOf("dotnet test", StringComparison.Ordinal);
        Assert.True(all.IndexOf("--is-ancestor", StringComparison.Ordinal) < test, "the main check must run before dotnet test");
        Assert.True(all.IndexOf("*-develop*)", StringComparison.Ordinal) < test, "the -develop check must run before dotnet test");
    }

    // AC-6 (071), AC-6 (072): the release lands on $TARGET (the merge commit for a release PR).
    [Fact]
    public void ReleaseWorkflow_MarksVersionsWithHyphenAsPrerelease()
    {
        string release = Assert.Single(RunScripts(ReadWorkflow()), script => script.Contains("gh release create", StringComparison.Ordinal));

        Assert.Contains("case \"$VERSION\" in", release, StringComparison.Ordinal);
        Assert.Contains("*-*) PRERELEASE=\"--prerelease\" ;;", release, StringComparison.Ordinal);
        Assert.Contains("--target \"$TARGET\" $PRERELEASE", release, StringComparison.Ordinal);
        Assert.DoesNotContain("$GITHUB_SHA", release, StringComparison.Ordinal);
    }

    // AC-7 (071)
    [Fact]
    public void Readme_ReleasingDescribesDevelopBuildsThenReleaseFromMain()
    {
        string section = ReadmeSection("## Releasing");

        int develop = section.IndexOf("-develop.<run>", StringComparison.Ordinal);
        Assert.True(develop >= 0, "README '## Releasing' does not describe develop builds");
        string[] commands =
        [
            "git checkout main",
            "git merge --no-ff develop",
            "git tag v1.2.3",
            "git push origin main",
            "git push origin v1.2.3",
        ];
        int at = section.IndexOf("```sh", StringComparison.Ordinal);
        Assert.True(at > develop, "the release commands must come after the develop builds");
        foreach (string command in commands)
        {
            int next = section.IndexOf("\n" + command + "\n", at, StringComparison.Ordinal);
            Assert.True(next > at, "'" + command + "' missing or out of order");
            at = next;
        }

        string flat = Regex.Replace(section, @"\s+", " ");
        Assert.Contains("Every push to `develop`", flat, StringComparison.Ordinal);
        Assert.Contains("prerelease", flat, StringComparison.Ordinal);
        Assert.Contains("A release tag whose commit is not on `main` fails", flat, StringComparison.Ordinal);
        Assert.Contains("hand-pushed `-develop` tag", flat, StringComparison.Ordinal);
    }

    // AC-7 (072): after develop builds, releasing by a merged develop -> main PR; tag push stays the alternative.
    [Fact]
    public void Readme_ReleasingDescribesReleasePullRequestBeforeTagAlternative()
    {
        string section = ReadmeSection("## Releasing");
        string flat = Regex.Replace(section, @"\s+", " ");

        int develop = section.IndexOf("-develop.<run>", StringComparison.Ordinal);
        int pr = section.IndexOf("\ngh pr create --base main --head develop --title \"release v1.2.3\"\n", StringComparison.Ordinal);
        int tag = section.IndexOf("\ngit tag v1.2.3\n", StringComparison.Ordinal);
        Assert.True(develop >= 0 && pr > develop, "the release PR command must come after the develop builds");
        Assert.True(tag > pr, "the tag push must stay documented, after the release PR");
        Assert.Contains("Open a pull request from `develop` into `main` titled `release v1.2.3` and merge it", flat, StringComparison.Ordinal);
        Assert.Contains("The title must be exactly `release vX.Y.Z`", flat, StringComparison.Ordinal);
        Assert.Contains("A title whose tag already exists, or with a `-develop` version, fails", flat, StringComparison.Ordinal);
        Assert.Contains("closed without merging, or one from another branch or a fork, releases nothing", flat, StringComparison.Ordinal);
        Assert.Contains("on the merge commit", flat, StringComparison.Ordinal);
        Assert.Contains("As an alternative", flat, StringComparison.Ordinal);
    }

    // AC-7 (071)
    [Fact]
    public void ClaudeMd_ReleaseWorkflowNoteMentionsDevelopBuildsAndMainOnly()
    {
        string claude = File.ReadAllText(Path.Combine(RepoRoot, "CLAUDE.md")).ReplaceLineEndings("\n");
        int start = claude.IndexOf("`.github/workflows/release.yml`:", StringComparison.Ordinal);
        Assert.True(start >= 0, "CLAUDE.md has no release.yml layout note");
        int end = claude.IndexOf("\n  - ", start, StringComparison.Ordinal);
        string note = end < 0 ? claude[start..] : claude[start..end];

        Assert.Contains("`develop`", note, StringComparison.Ordinal);
        Assert.Contains("-develop.<run number>", note, StringComparison.Ordinal);
        Assert.Contains("`main`", note, StringComparison.Ordinal);
    }

    // AC-8 (072)
    [Fact]
    public void ClaudeMd_ReleaseWorkflowNoteMentionsReleasePullRequestOnMergeCommit()
    {
        string claude = File.ReadAllText(Path.Combine(RepoRoot, "CLAUDE.md")).ReplaceLineEndings("\n");
        int start = claude.IndexOf("`.github/workflows/release.yml`:", StringComparison.Ordinal);
        Assert.True(start >= 0, "CLAUDE.md has no release.yml layout note");
        int end = claude.IndexOf("\n  - ", start, StringComparison.Ordinal);
        string note = Regex.Replace(end < 0 ? claude[start..] : claude[start..end], @"\s+", " ");

        Assert.Contains("a merged `develop` -> `main` pull request titled `release vX.Y.Z`", note, StringComparison.Ordinal);
        Assert.Contains("made on the merge commit", note, StringComparison.Ordinal);
    }

    // AC-5 (075)
    [Fact]
    public void ClaudeMd_ReleaseWorkflowNoteMentionsBacklogOnlyPushes()
    {
        string claude = File.ReadAllText(Path.Combine(RepoRoot, "CLAUDE.md")).ReplaceLineEndings("\n");
        int start = claude.IndexOf("`.github/workflows/release.yml`:", StringComparison.Ordinal);
        Assert.True(start >= 0, "CLAUDE.md has no release.yml layout note");
        int end = claude.IndexOf("\n  - ", start, StringComparison.Ordinal);
        string note = Regex.Replace(end < 0 ? claude[start..] : claude[start..end], @"\s+", " ");

        Assert.Contains("backlog.md", note, StringComparison.Ordinal);
        Assert.Contains("paths-ignore", note, StringComparison.Ordinal);
    }

    // AC-6 (075)
    [Fact]
    public void Readme_ReleasingSaysBacklogOnlyPushesMakeNoPrerelease()
    {
        string flat = Regex.Replace(ReadmeSection("## Releasing"), @"\s+", " ");

        int develop = flat.IndexOf("Every push to `develop`", StringComparison.Ordinal);
        int backlog = flat.IndexOf("backlog.md", Math.Max(develop, 0), StringComparison.Ordinal);
        int pr = flat.IndexOf("gh pr create", StringComparison.Ordinal);
        Assert.True(develop >= 0, "README '## Releasing' does not say 'Every push to `develop`'");
        Assert.True(backlog > develop, "README '## Releasing' does not mention backlog.md after the develop builds");
        Assert.True(pr > backlog, "backlog.md must be mentioned before the gh pr create command");
    }

    private static string ReadmeSection(string heading)
    {
        string readme = "\n" + File.ReadAllText(Path.Combine(RepoRoot, "README.md")).ReplaceLineEndings("\n");
        int start = readme.IndexOf("\n" + heading + "\n", StringComparison.Ordinal);
        Assert.True(start >= 0, "README has no '" + heading + "' section");

        int end = readme.IndexOf("\n## ", start + 1, StringComparison.Ordinal);
        return end < 0 ? readme[start..] : readme[start..end];
    }

    /// <summary>The index of the first line at or after <paramref name="start"/> whose trimmed text is <paramref name="text"/>, or -1.</summary>
    private static int LineIndex(string script, string text, int start)
    {
        Match match = Regex.Match(script[start..], @"(?m)^[ \t]*" + Regex.Escape(text) + @"[ \t]*$");
        return match.Success ? start + match.Index : -1;
    }

    private static string VersionScript()
    {
        return Assert.Single(RunScripts(ReadWorkflow()), script => script.Contains("GITHUB_REF_TYPE", StringComparison.Ordinal));
    }

    private static string[] CheckoutStep()
    {
        string[] lines = ReadWorkflow().Split('\n');

        int checkout = Array.FindIndex(lines, line => line.Trim() == "uses: actions/checkout@v4");
        Assert.True(checkout >= 0, "workflow has no actions/checkout step");
        int next = Array.FindIndex(lines, checkout + 1, line => line.TrimStart().StartsWith("- ", StringComparison.Ordinal));
        return lines[(checkout + 1)..(next < 0 ? lines.Length : next)];
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
