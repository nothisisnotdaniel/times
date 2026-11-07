using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit.Sdk;

namespace Times.Tests;

public sealed class ToolPackagingTests
{
    private const string NugetOrg = "https://api.nuget.org/v3/index.json";
    private const string LocalFolder = "./nupkg";
    private const string SemVerGrep = "LC_ALL=C grep -Eq '^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)(-[0-9A-Za-z-]+(\\.[0-9A-Za-z-]+)*)?$'";

    private const string CheckoutAction = "actions/checkout@11d5960a326750d5838078e36cf38b85af677262";
    private const string SetupDotnetAction = "actions/setup-dotnet@67a3573c9a986a3f9c594539f4ab511d57bb3ce9";
    private const string JobIf = "github.event_name == 'push' || (github.event.pull_request.merged == true && github.event.pull_request.head.ref == 'develop' && github.event.pull_request.head.repo.full_name == github.repository)";

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

    // AC-5 (090: the user part keeps the tm, SDK and PATH notes; building moved to "## Install your own build")
    [Theory]
    [InlineData("dotnet tool uninstall -g Times")]
    [InlineData("`tm`")]
    [InlineData("tm --version")]
    [InlineData("built-in")]
    [InlineData("It needs the [.NET 10 SDK]")]
    [InlineData("is on your `PATH`")]
    public void Readme_ExplainsToolInstall(string expected)
    {
        string section = Regex.Replace(ReadmeSection("## Install as a .NET tool"), @"\s+", " ");

        Assert.Contains(expected, section, StringComparison.Ordinal);
    }

    // AC-6 (090, replaces 055/AC-6 "Readme_EveryAddSourceLinePinsVersion")
    [Fact]
    public void Readme_HasNoAddSource_AndEveryConfigfileLinePinsVersion()
    {
        string[] lines = File.ReadAllLines(Path.Combine(RepoRoot, "README.md"));

        Assert.DoesNotContain(lines, line => line.Contains("--add-source", StringComparison.Ordinal));
        List<string> configfile = lines.Where(line => line.Contains("--configfile", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(configfile);
        Assert.All(configfile, line => Assert.Contains("--version", line, StringComparison.Ordinal));
    }

    // AC-1 (090, replaces 077 "Readme_HasInstallFromReleaseSection_InsideInstallSection")
    [Fact]
    public void Readme_InstallSectionHasReleaseThenUpdateThenUninstall()
    {
        List<string> headings = ReadmeHeadings(ReadmeSection("## Install as a .NET tool"))
            .Where(h => h.StartsWith("### ", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(["### Install from a GitHub release", "### Update", "### Uninstall"], headings);
    }

    // AC-3 (090)
    [Fact]
    public void Readme_ReleaseInstallHasLocalOnlyNugetConfig()
    {
        string section = ReadmeSubsection("### Install from a GitHub release");

        List<string> xml = FencedBlocks(section, "xml");
        string config = Assert.Single(xml);
        Assert.Contains("<clear />", config, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(config, "<add "));
        Assert.Contains("value=\".\"", config, StringComparison.Ordinal);
        Assert.DoesNotContain("nuget.org", config, StringComparison.Ordinal);
        Assert.DoesNotContain("http", config, StringComparison.Ordinal);
    }

    // AC-3 (090): the README's config really is a well-formed config with one "." source after <clear />.
    [Fact]
    public void Readme_ReleaseInstallNugetConfigParses()
    {
        string config = Assert.Single(FencedBlocks(ReadmeSubsection("### Install from a GitHub release"), "xml"));

        XElement sources = Assert.Single(XElement.Parse(config.Trim()).Elements("packageSources"));
        List<XElement> children = sources.Elements().ToList();
        Assert.Equal(["clear", "add"], children.Select(e => e.Name.LocalName).ToList());
        Assert.Equal(".", (string?)children[1].Attribute("value"));
    }

    // AC-4 (090)
    [Theory]
    [InlineData("dotnet tool install --global --configfile <folder>/nuget.config --version <version> Times")]
    [InlineData("dotnet tool install --global --configfile ~/Downloads/times/nuget.config --version 1.2.3 Times")]
    [InlineData("nuget.org")]
    [InlineData("tm --version")]
    [InlineData("dotnet nuget locals global-packages --clear")]
    public void Readme_ReleaseInstallMentions(string expected)
    {
        Assert.Contains(expected, ReadmeSubsection("### Install from a GitHub release"), StringComparison.Ordinal);
    }

    // AC-5 (090)
    [Fact]
    public void Readme_UpdateUsesConfigfileAndPinsVersion()
    {
        Assert.Contains(
            "dotnet tool update --global --configfile <folder>/nuget.config --version <new version> Times",
            ReadmeSubsection("### Update"),
            StringComparison.Ordinal);
    }

    // AC-7 (090)
    [Theory]
    [InlineData("dotnet pack")]
    [InlineData("nupkg/")]
    [InlineData("./nupkg")]
    [InlineData("repo root")]
    [InlineData("packageSourceMapping")]
    [InlineData("Times.csproj")]
    public void Readme_UserPartHasNoBuildMaterial(string forbidden)
    {
        string readme = File.ReadAllText(Path.Combine(RepoRoot, "README.md")).ReplaceLineEndings("\n");
        int user = readme.IndexOf("\n# Using Times\n", StringComparison.Ordinal);
        int developer = readme.IndexOf("\n# Building Times\n", StringComparison.Ordinal);
        Assert.True(user >= 0 && developer > user, "README needs '# Using Times' before '# Building Times'");

        Assert.DoesNotContain(forbidden, readme[user..developer], StringComparison.Ordinal);
    }

    // AC-8 (090)
    [Theory]
    [InlineData("dotnet pack src/Times -c Release")]
    [InlineData("dotnet tool install -g Times")]
    [InlineData("dotnet tool update -g Times")]
    [InlineData("raise `<Version>`")]
    [InlineData("from the repo root (where `nuget.config` lives)")]
    [InlineData("maps the `Times` package to `./nupkg` only, so a `Times` package on nuget.org is never used")]
    [InlineData("fails instead of falling back")]
    [InlineData("(#uninstall)")]
    public void Readme_InstallYourOwnBuildMentions(string expected)
    {
        string section = Regex.Replace(ReadmeSection("## Install your own build"), @"\s+", " ");

        Assert.Contains(expected, section, StringComparison.Ordinal);
    }

    // AC-9 (090)
    [Fact]
    public void Readme_EveryInPageLinkMatchesAHeading()
    {
        string readme = File.ReadAllText(Path.Combine(RepoRoot, "README.md"));
        HashSet<string> slugs = ReadmeHeadings(readme)
            .Select(h => GitHubSlug(h.TrimStart('#').Trim()))
            .ToHashSet(StringComparer.Ordinal);

        List<string> anchors = Regex.Matches(readme, @"\]\(#([^)]*)\)").Select(m => m.Groups[1].Value).ToList();
        Assert.NotEmpty(anchors);
        Assert.All(anchors, anchor => Assert.Contains(anchor, slugs));
    }

    // AC-9 (090)
    [Theory]
    [InlineData("Install as a .NET tool", "install-as-a-net-tool")]
    [InlineData("Install from a GitHub release", "install-from-a-github-release")]
    [InlineData("Using Times", "using-times")]
    public void GitHubSlug_FollowsGitHubRule(string heading, string slug)
    {
        Assert.Equal(slug, GitHubSlug(heading));
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

    // AC-1, AC-6 (089; replaces the line-based checks from 070, 071, 072, 075 and 081 on triggers,
    // permissions, the job if:, the checkout, the action pins, the Version env: and the prune step).
    [Fact]
    public void ReleaseWorkflow_HasExpectedStructure()
    {
        AssertWorkflowStructure(ReadWorkflow());
    }

    // AC-1 (089): the pinned uses: lines name their version in a trailing comment, in the source too.
    [Fact]
    public void ReleaseWorkflow_PinsActionsToCommitShasWithVersionComments()
    {
        List<YamlScalar> uses = Steps(ReadWorkflow())
            .Where(step => step.ContainsKey("uses"))
            .Select(step => Assert.IsType<YamlScalar>(step["uses"]))
            .ToList();
        string[] lines = ReadWorkflow().Split('\n');

        Assert.Equal([CheckoutAction, SetupDotnetAction], uses.Select(scalar => scalar.Value));
        Assert.Equal(["v4.4.0", "v4.3.1"], uses.Select(scalar => scalar.Comment));
        Assert.EndsWith(" # v4.4.0", lines[uses[0].Line - 1], StringComparison.Ordinal);
        Assert.EndsWith(" # v4.3.1", lines[uses[1].Line - 1], StringComparison.Ordinal);
        Assert.All(uses, scalar => Assert.DoesNotMatch(@"@(v[0-9]+|main|master|develop)$", scalar.Value));
    }

    // AC-7 (089): the structure check reads only the parsed model, so reformatting the YAML keeps it passing.
    [Theory]
    [InlineData("develop")]
    [InlineData("'develop'")]
    public void ReleaseWorkflow_StructureCheckPassesOnReformattedCopy(string develop)
    {
        string reformatted = ReformattedWorkflow(ReadWorkflow(), develop);

        Assert.NotEqual(ReadWorkflow(), reformatted);
        Assert.Contains("\n            -   " + develop + "\n", reformatted, StringComparison.Ordinal);
        AssertWorkflowStructure(reformatted);
    }

    // AC-7 (089): the structure check fails on an unpinned action, a wider token and an extra trigger.
    [Theory]
    [InlineData("unpinned checkout")]
    [InlineData("write-all")]
    [InlineData("workflow_dispatch")]
    public void ReleaseWorkflow_StructureCheckFailsOnUnsafeCopy(string change)
    {
        string workflow = ReadWorkflow();
        string copy = change switch
        {
            "unpinned checkout" => ReplaceOnce(workflow, CheckoutAction + " # v4.4.0", "actions/checkout@v4"),
            "write-all" => ReplaceOnce(workflow, "permissions:\n  contents: write\n", "permissions: write-all\n"),
            _ => ReplaceOnce(workflow, "\non:\n", "\non:\n  workflow_dispatch:\n"),
        };

        WorkflowYaml.Parse(copy);
        Assert.ThrowsAny<XunitException>(() => AssertWorkflowStructure(copy));
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

        Assert.Equal(7, scripts.Count);
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
        Assert.Contains("gh release create \"$TAG\" \"nupkg/Times.$VERSION.nupkg\" \"nupkg/Times.$VERSION.nupkg.sig\" --title \"Times $VERSION\" --generate-notes --target \"$TARGET\" $PRERELEASE", all, StringComparison.Ordinal);
        Assert.Contains("--prerelease", all, StringComparison.Ordinal);
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

    // Security review round 1: the tag is checked against SemVer where it enters, before msbuild.
    // AC-2 (089): the check runs under LC_ALL=C and is the only grep in the Version step.
    [Fact]
    public void ReleaseWorkflow_ChecksTagIsSemVerBeforeExportingIt()
    {
        string script = VersionScript();

        int check = script.IndexOf(SemVerGrep, StringComparison.Ordinal);
        int export = script.IndexOf(">> \"$GITHUB_ENV\"", StringComparison.Ordinal);
        Assert.True(check >= 0, "no SemVer check on the tag");
        Assert.True(check < export, "the tag must be checked before VERSION is exported");
        Assert.Contains("exit 1", script[check..export], StringComparison.Ordinal);
        Assert.Single(Regex.Matches(script, Regex.Escape(SemVerGrep)));
        Assert.Single(Regex.Matches(script, @"\bgrep\b"));
        Assert.Contains("| " + SemVerGrep + "; then", script, StringComparison.Ordinal);
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

    // AC-6 (065), AC-2 (077, replaces "## Shortcuts" with "## Commands")
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
        int commands = headings.IndexOf("## Commands");
        Assert.True(install >= 0, "README has no '## Install as a .NET tool' section");
        Assert.True(install < commands, "'## Install as a .NET tool' must come before '## Commands'");
        Assert.Equal(["## Commands", "## Keys", "## Mouse"], headings.GetRange(commands, 3));

        int sdk = readme.IndexOf("It needs the [.NET 10 SDK]", StringComparison.Ordinal);
        Assert.True(sdk >= 0, "README has no '.NET 10 SDK' paragraph");
        Assert.True(sdk < readme.IndexOf("\n## Commands\n", StringComparison.Ordinal));
    }

    // AC-1 (077), AC-1 and AC-2 (090: "## Install your own build" in the developer part)
    [Fact]
    public void Readme_HasUserPartThenDeveloperPart()
    {
        string readme = File.ReadAllText(Path.Combine(RepoRoot, "README.md")).ReplaceLineEndings("\n");
        List<string> headings = [];
        bool inFence = false;
        foreach (string line in readme.Split('\n'))
        {
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
            }
            else if (!inFence && (line.StartsWith("# ", StringComparison.Ordinal) || line.StartsWith("## ", StringComparison.Ordinal)))
            {
                headings.Add(line);
            }
        }

        Assert.Equal(["# Times", "# Using Times", "# Building Times"], headings.Where(h => h.StartsWith("# ", StringComparison.Ordinal)).ToList());
        int user = headings.IndexOf("# Using Times");
        int developer = headings.IndexOf("# Building Times");
        Assert.Equal(
            ["## Install as a .NET tool", "## Commands", "## Keys", "## Mouse", "## The day view", "## Your data", "## License"],
            headings.GetRange(user + 1, developer - user - 1));
        Assert.Equal(
            ["## Prerequisites", "## Build and test", "## Run from source", "## Install your own build", "## Project layout", "## Releasing"],
            headings.GetRange(developer + 1, headings.Count - developer - 1));
    }

    // AC-8 (077)
    [Theory]
    [InlineData("## Your data", "TIMES_DATA_DIR")]
    [InlineData("## Your data", "projects.json")]
    [InlineData("## Your data", "~/.config/Times/")]
    [InlineData("## Run from source", "dotnet run --project src/Times")]
    [InlineData("## Build and test", "dotnet build")]
    [InlineData("## Build and test", "dotnet test")]
    public void Readme_SectionMentions(string heading, string expected)
    {
        Assert.Contains(expected, ReadmeSection(heading), StringComparison.Ordinal);
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
    // AC-3 (089): a non-ASCII prerelease is ignored even in a UTF-8 locale.
    [InlineData("v0.1.0\nv9.0.0-b\u00e9t\u00e0\n", "12", "0.1.1-develop.12", true, "en_US.UTF-8")]
    // AC-4 (089): tags with a MAJOR, MINOR or PATCH of more than 9 digits are ignored.
    [InlineData("v0.1.0\nv1000000000.0.0\n", "12", "0.1.1-develop.12", true)]
    [InlineData("v0.0.9223372036854775807\n", "12", "0.0.1-develop.12", true)]
    [InlineData("v99999999999999999999.0.0\nv0.2.0\n", "12", "0.2.1-develop.12", true)]
    [InlineData("v999999999.999999999.999999999\n", "12", "999999999.999999999.1000000000-develop.12", true)]
    [InlineData("v0.1000000000.0\nv0.0.1000000000\nv0.0.3\n", "12", "0.0.4-develop.12", true, "en_US.UTF-8")]
    public async Task DevelopVersionScript_PrintsNextPatchDevelopVersion(string tags, string build, string expected, bool succeeds, string locale = "")
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
        if (locale.Length > 0)
        {
            startInfo.Environment["LC_ALL"] = locale;
            startInfo.Environment["LANG"] = locale;
        }

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

    // AC-3 (089): the script runs in the C locale before its first grep.
    [Fact]
    public void DevelopVersionScript_SetsCLocaleBeforeFirstGrep()
    {
        string source = File.ReadAllText(Path.Combine(RepoRoot, ".github", "scripts", "develop-version.sh")).ReplaceLineEndings("\n");
        string code = string.Join("\n", source.Split('\n').Where(line => !line.TrimStart().StartsWith('#')));

        int locale = code.IndexOf("\nLC_ALL=C\nexport LC_ALL\n", StringComparison.Ordinal);
        int grep = code.IndexOf("grep", StringComparison.Ordinal);
        Assert.True(locale >= 0, "develop-version.sh must set and export LC_ALL=C on lines of their own");
        Assert.True(grep > locale, "LC_ALL=C must be set before the first grep");
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

    // AC-4 (072): PR fields come only from the step env: (checked in AssertWorkflowStructure), are re-checked,
    // and all refusals run before dotnet test.
    [Fact]
    public void ReleaseWorkflow_PullRequestBranchRechecksPrAndMergeCommit()
    {
        string workflow = ReadWorkflow();
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

    // AC-1, AC-2, AC-3 (081)
    [Theory]
    [InlineData("1.2.3", "v1.2.3-develop.5\nv1.2.2-develop.1\nv0.9.10-develop.3\nv1.2.4-develop.9\nv1.3.0-develop.2\nv2.0.0-develop.1\n", "v1.2.3-develop.5\nv1.2.2-develop.1\nv0.9.10-develop.3", true)]
    [InlineData("1.2.3", "v1.2.2-develop.1", "v1.2.2-develop.1", true)]
    [InlineData("1.2.3", "v1.2.2\nv1.2.3\nv1.0.0-beta.1\nv1.2.2-develop\nv1.2.2-develop.0\nv1.2.2-develop.01\nv1.2.2-develop.1.x\nv01.2.2-develop.1\nx v1.2.2-develop.1\nv1.2.2-develop.1;rm\n\n \n", "", true)]
    [InlineData("0.10.0", "v0.9.3-develop.1\nv0.10.1-develop.1\n", "v0.9.3-develop.1", true)]
    [InlineData("1.2.3", "v1.2.3-develop.5\r\n", "", true)]
    [InlineData("1.2.3", "", "", true)]
    [InlineData("", "v1.2.2-develop.1\n", "", false)]
    [InlineData("1.2", "v1.2.2-develop.1\n", "", false)]
    [InlineData("1.2.3-beta.1", "v1.2.2-develop.1\n", "", false)]
    [InlineData("1.2.3-develop.4", "v1.2.2-develop.1\n", "", false)]
    [InlineData("v1.2.3", "v1.2.2-develop.1\n", "", false)]
    [InlineData("01.2.3", "v1.2.2-develop.1\n", "", false)]
    [InlineData("1.2.3;x", "v1.2.2-develop.1\n", "", false)]
    [InlineData("1.2.3\n", "v1.2.2-develop.1\n", "", false)]
    public async Task OldPrereleasesScript_PrintsOnlyOlderDevelopTags(string release, string tags, string expected, bool succeeds)
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
        startInfo.ArgumentList.Add(Path.Combine(".github", "scripts", "old-prereleases.sh"));
        startInfo.ArgumentList.Add(release);

        using Process process = Process.Start(startInfo)!;
        try
        {
            Stream input = process.StandardInput.BaseStream;
            await input.WriteAsync(System.Text.Encoding.UTF8.GetBytes(tags));
            input.Close();
        }
        catch (IOException)
        {
            // The script refuses a bad argument before it reads stdin, so the pipe can already be closed.
        }
        Task<string> error = process.StandardError.ReadToEndAsync();
        string output = await process.StandardOutput.ReadToEndAsync();
        await error;
        await process.WaitForExitAsync();

        Assert.Equal(expected, output.TrimEnd('\n'));
        if (succeeds)
        {
            Assert.True(process.ExitCode == 0, "old-prereleases.sh exit " + process.ExitCode + ": " + await error);
        }
        else
        {
            Assert.NotEqual(0, process.ExitCode);
        }
    }

    // AC-4 (081): tag names are only compared, never evaluated.
    [Fact]
    public void OldPrereleasesScript_NeverEvaluatesTags()
    {
        string source = File.ReadAllText(Path.Combine(RepoRoot, ".github", "scripts", "old-prereleases.sh")).ReplaceLineEndings("\n");
        string code = string.Join("\n", source.Split('\n').Where(line => !line.TrimStart().StartsWith('#')));

        Assert.StartsWith("#!/bin/sh\n", source, StringComparison.Ordinal);
        Assert.DoesNotContain("eval", source, StringComparison.Ordinal);
        Assert.DoesNotContain("sh -c", code, StringComparison.Ordinal);
        Assert.Contains("while IFS= read -r tag", code, StringComparison.Ordinal);
        Assert.Contains("LC_ALL=C\nexport LC_ALL\n", code, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"[^""{]\$tag", code);
    }

    // AC-6 (081)
    [Fact]
    public void ReleaseWorkflow_PruneScriptSkipsPrereleasesAndDeletesOnlyFilteredTags()
    {
        string script = Assert.Single(RunScripts(ReadWorkflow()), s => s.Contains("old-prereleases.sh", StringComparison.Ordinal));

        int skip = script.IndexOf("case \"$VERSION\" in", StringComparison.Ordinal);
        int hyphen = script.IndexOf("*-*)", Math.Max(skip, 0), StringComparison.Ordinal);
        int exit = script.IndexOf("exit 0", Math.Max(hyphen, 0), StringComparison.Ordinal);
        int esac = LineIndex(script, "esac", Math.Max(exit, 0));
        int firstGh = script.IndexOf("gh ", StringComparison.Ordinal);
        int list = script.IndexOf("gh release list --limit 1000 --json tagName,isPrerelease --jq '.[] | select(.isPrerelease) | .tagName' |", StringComparison.Ordinal);
        int filter = script.IndexOf("sh .github/scripts/old-prereleases.sh \"$VERSION\" |", StringComparison.Ordinal);
        int loop = script.IndexOf("while IFS= read -r tag; do", StringComparison.Ordinal);
        int delete = script.IndexOf("gh release delete \"$tag\" --cleanup-tag --yes || echo", StringComparison.Ordinal);
        int done = LineIndex(script, "done", Math.Max(delete, 0));

        Assert.True(skip >= 0 && skip < hyphen && hyphen < exit && exit < esac, "the script must exit 0 for a $VERSION containing '-'");
        Assert.True(esac < firstGh, "the prerelease skip must come before any gh call");
        int pipefail = script.IndexOf("set -o pipefail", StringComparison.Ordinal);
        Assert.True(pipefail >= 0 && pipefail < list, "a failed listing must fail the step (set -o pipefail before the gh release list pipe)");
        Assert.True(firstGh == list, "the first gh call must be the prerelease listing");
        Assert.True(list < filter && filter < loop && loop < delete && delete < done, "list | filter | while read loop with a delete that may fail");
        Assert.Single(Regex.Matches(script, @"gh release delete"));
    }

    // AC-8 (081)
    [Fact]
    public void Readme_ReleasingDescribesPruningOldPrereleases()
    {
        string flat = Regex.Replace(ReadmeSection("## Releasing"), @"\s+", " ");

        Assert.Contains("deletes older `-develop` prereleases and their tags", flat, StringComparison.Ordinal);
        Assert.Contains("keeps proper releases", flat, StringComparison.Ordinal);
        Assert.Contains("higher prereleases", flat, StringComparison.Ordinal);
        Assert.Contains("a failed deletion only shows a warning and does not fail the release", flat, StringComparison.Ordinal);
    }

    // AC-8 (081)
    [Fact]
    public void ClaudeMd_ReleaseWorkflowNoteMentionsPruning()
    {
        string claude = File.ReadAllText(Path.Combine(RepoRoot, "CLAUDE.md")).ReplaceLineEndings("\n");
        int start = claude.IndexOf("`.github/workflows/release.yml`:", StringComparison.Ordinal);
        Assert.True(start >= 0, "CLAUDE.md has no release.yml layout note");
        int end = claude.IndexOf("\n  - ", start, StringComparison.Ordinal);
        string note = Regex.Replace(end < 0 ? claude[start..] : claude[start..end], @"\s+", " ");

        Assert.Contains("deletes older `-develop` prereleases with their tags", note, StringComparison.Ordinal);
        Assert.Contains("old-prereleases.sh", note, StringComparison.Ordinal);
    }

    /// <summary>The <c>#</c> to <c>###</c> heading lines of <paramref name="text"/> outside fenced code (fences may be indented).</summary>
    private static List<string> ReadmeHeadings(string text)
    {
        List<string> headings = [];
        bool inFence = false;
        foreach (string line in text.ReplaceLineEndings("\n").Split('\n'))
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
            }
            else if (!inFence && Regex.IsMatch(line, "^#{1,3} "))
            {
                headings.Add(line);
            }
        }

        return headings;
    }

    /// <summary>A <c>###</c> section: from its heading to the next <c>#</c>, <c>##</c> or <c>###</c> heading outside fenced code.</summary>
    private static string ReadmeSubsection(string heading)
    {
        string[] lines = File.ReadAllText(Path.Combine(RepoRoot, "README.md")).ReplaceLineEndings("\n").Split('\n');
        int start = Array.IndexOf(lines, heading);
        Assert.True(start >= 0, "README has no '" + heading + "' section");

        bool inFence = false;
        int end = start + 1;
        for (; end < lines.Length; end++)
        {
            if (lines[end].TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
            }
            else if (!inFence && Regex.IsMatch(lines[end], "^#{1,3} "))
            {
                break;
            }
        }

        return string.Join("\n", lines[start..end]);
    }

    /// <summary>The bodies of the fenced blocks in <paramref name="text"/> whose info string is <paramref name="language"/>.</summary>
    private static List<string> FencedBlocks(string text, string language)
    {
        List<string> blocks = [];
        List<string>? current = null;
        bool inFence = false;
        foreach (string line in text.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                if (inFence && current != null)
                {
                    blocks.Add(string.Join("\n", current));
                }

                current = !inFence && trimmed == "```" + language ? [] : null;
                inFence = !inFence;
            }
            else if (current != null)
            {
                current.Add(line);
            }
        }

        return blocks;
    }

    /// <summary>GitHub's heading anchor: lowercase, only letters, digits, spaces and <c>-</c> kept, spaces turned into <c>-</c>.</summary>
    private static string GitHubSlug(string heading)
    {
        string kept = new string(heading.ToLowerInvariant().Where(c => char.IsLetterOrDigit(c) || c == ' ' || c == '-').ToArray());
        return kept.Replace(' ', '-');
    }

    // (077, replaces the end at the next "\n## " only): a section also ends at a "\n# " heading.
    private static string ReadmeSection(string heading)
    {
        string readme = "\n" + File.ReadAllText(Path.Combine(RepoRoot, "README.md")).ReplaceLineEndings("\n");
        int start = readme.IndexOf("\n" + heading + "\n", StringComparison.Ordinal);
        Assert.True(start >= 0, "README has no '" + heading + "' section");

        int end = NextHeading(readme, start + 1);
        return end < 0 ? readme[start..] : readme[start..end];
    }

    /// <summary>The index of the next <c>\n## </c> or <c>\n# </c> at or after <paramref name="from"/>, or -1.</summary>
    private static int NextHeading(string readme, int from)
    {
        int section = readme.IndexOf("\n## ", from, StringComparison.Ordinal);
        int part = readme.IndexOf("\n# ", from, StringComparison.Ordinal);
        if (section < 0 || part < 0)
        {
            return Math.Max(section, part);
        }

        return Math.Min(section, part);
    }

    /// <summary>The index of the first line at or after <paramref name="start"/> whose trimmed text is <paramref name="text"/>, or -1.</summary>
    private static int LineIndex(string script, string text, int start)
    {
        Match match = Regex.Match(script[start..], @"(?m)^[ \t]*" + Regex.Escape(text) + @"[ \t]*$");
        return match.Success ? start + match.Index : -1;
    }

    private static string VersionScript()
    {
        return RunScript(Step(ReadWorkflow(), "Version"));
    }

    // (077, replaces the end at the next "\n## " only): the section also ends at a "\n# " heading.
    private static string ReadmeKeysSection()
    {
        string readme = File.ReadAllText(Path.Combine(RepoRoot, "README.md")).ReplaceLineEndings("\n");
        int start = readme.IndexOf("## Keys\n", StringComparison.Ordinal);
        Assert.True(start >= 0, "README has no '## Keys' section");

        int end = NextHeading(readme, start);
        return end < 0 ? readme[start..] : readme[start..end];
    }

    private static string ReadWorkflow()
    {
        return File.ReadAllText(Path.Combine(RepoRoot, ".github", "workflows", "release.yml")).ReplaceLineEndings("\n");
    }

    /// <summary>
    /// AC-6 (089): every structural check on the release workflow, on the parsed model only (no raw lines
    /// or indents): triggers, permissions, the job if:, step names and order, the pinned actions, the
    /// checkout with:, the Version env: and the release and prune steps' settings. Throws on a mismatch.
    /// </summary>
    private static void AssertWorkflowStructure(string yaml)
    {
        YamlMapping root = Map(WorkflowYaml.Parse(yaml));
        // Exact key sets: no workflow- or job-level env:, defaults:, container: or services: next to the write token.
        Assert.Equal(["jobs", "name", "on", "permissions"], Sorted(root.Keys));

        YamlMapping on = Map(root["on"]);
        Assert.Equal(["pull_request", "push"], Sorted(on.Keys));
        YamlMapping push = Map(on["push"]);
        Assert.Equal(["branches", "paths-ignore", "tags"], Sorted(push.Keys));
        Assert.Equal(["develop"], Strings(push["branches"]));
        Assert.Equal(["v*"], Strings(push["tags"]));
        Assert.Equal(["backlog.md"], Strings(push["paths-ignore"]));
        YamlMapping pullRequest = Map(on["pull_request"]);
        Assert.Equal(["branches", "types"], Sorted(pullRequest.Keys));
        Assert.Equal(["closed"], Strings(pullRequest["types"]));
        Assert.Equal(["main"], Strings(pullRequest["branches"]));

        Assert.Equal(["contents=write"], Pairs(root["permissions"]));

        YamlMapping jobs = Map(root["jobs"]);
        Assert.Equal(["release"], jobs.Keys);
        YamlMapping job = Map(jobs["release"]);
        Assert.Equal(["if", "runs-on", "steps"], Sorted(job.Keys));
        Assert.Equal(JobIf, Regex.Replace(Scalar(job["if"]), @"\s+", " ").Trim());
        Assert.Equal("ubuntu-latest", Scalar(job["runs-on"]));

        List<YamlMapping> steps = Steps(root);
        Assert.Equal(
            ["Check out", "Set up .NET", "Version", "Test", "Pack", "Smoke check the packed tool", "Sign", "Create GitHub release", "Prune old prereleases"],
            steps.Select(step => Scalar(step["name"])));

        List<YamlScalar> uses = steps.Where(step => step.ContainsKey("uses")).Select(step => Assert.IsType<YamlScalar>(step["uses"])).ToList();
        Assert.Equal([CheckoutAction, SetupDotnetAction], uses.Select(scalar => scalar.Value));
        Assert.Equal(["v4.4.0", "v4.3.1"], uses.Select(scalar => scalar.Comment));
        Assert.All(uses, scalar => Assert.Matches("@[0-9a-f]{40}$", scalar.Value));
        Assert.Equal(CheckoutAction, Scalar(steps[0]["uses"]));
        Assert.Equal(SetupDotnetAction, Scalar(steps[1]["uses"]));

        Assert.Equal(
            ["fetch-depth=0", "persist-credentials=false", "ref=${{ github.event.pull_request.merge_commit_sha }}"],
            Pairs(steps[0]["with"]));
        Assert.Equal(["dotnet-version=10.0.x"], Pairs(steps[1]["with"]));

        YamlMapping version = steps[2];
        Assert.Equal(
            [
                "MERGE_SHA=${{ github.event.pull_request.merge_commit_sha }}",
                "PR_HEAD_REF=${{ github.event.pull_request.head.ref }}",
                "PR_HEAD_REPO=${{ github.event.pull_request.head.repo.full_name }}",
                "PR_MERGED=${{ github.event.pull_request.merged }}",
                "PR_TITLE=${{ github.event.pull_request.title }}",
            ],
            Pairs(version["env"]));
        Assert.Equal(["env", "name", "run"], Sorted(version.Keys));

        YamlMapping sign = steps[6];
        Assert.Equal(["env", "name", "run"], Sorted(sign.Keys));
        Assert.Equal(["TIMES_SIGNING_KEY=${{ secrets.TIMES_SIGNING_KEY }}"], Pairs(sign["env"]));
        Assert.False(sign.ContainsKey("continue-on-error"), "a failed signature must fail the run");
        Assert.Equal(["GH_TOKEN=${{ github.token }}"], Pairs(steps[7]["env"]));
        Assert.False(steps[7].ContainsKey("continue-on-error"), "a failed release must fail the run");
        YamlMapping prune = steps[8];
        Assert.Equal("true", Scalar(prune["continue-on-error"]));
        Assert.Equal(["GH_TOKEN=${{ github.token }}"], Pairs(prune["env"]));
        Assert.All(steps, step => Assert.True(step.ContainsKey("uses") != step.ContainsKey("run"), "each step has either uses: or run:"));
    }

    /// <summary>AC-7 (089): the real workflow with 4-space indents, other quoting, extra comments and blank lines, and reordered step keys.</summary>
    private static string ReformattedWorkflow(string workflow, string develop)
    {
        string text = workflow;
        text = ReplaceOnce(text, "      - \"develop\"\n", "      - " + develop + "\n");
        text = ReplaceOnce(text, "      - \"main\"\n", "      - main\n");
        text = ReplaceOnce(text, "dotnet-version: \"10.0.x\"\n", "dotnet-version: '10.0.x'   # any 10.0 patch\n");
        text = ReplaceOnce(
            text,
            "      - name: Check out\n        uses: " + CheckoutAction + " # v4.4.0\n",
            "      - uses: " + CheckoutAction + "    # v4.4.0\n        # name after uses\n        name: \"Check out\"\n");
        text = ReplaceOnce(
            text,
            "        continue-on-error: true\n        env:\n          GH_TOKEN: ${{ github.token }}\n",
            "        env:\n\n          GH_TOKEN: ${{ github.token }}\n        continue-on-error: 'true'\n");
        text = ReplaceOnce(text, "    if: github.event_name == 'push' || (", "    if: >\n      github.event_name == 'push' ||\n        (");
        text = ReplaceOnce(text, "\non:\n", "\non: # triggers\n\n  # every push\n");
        text = ReplaceOnce(text, "permissions:\n  contents: write\n", "permissions:   # token scope\n\n  contents: write # nothing else\n");
        text = ReplaceOnce(text, "      - name: Test\n", "\n      # an extra comment\n\n      - name: Test\n");

        return string.Join('\n', text.Split('\n').Select(line =>
        {
            int indent = line.Length - line.TrimStart(' ').Length;
            string rest = line[indent..];
            if (rest.StartsWith("- ", StringComparison.Ordinal))
            {
                rest = "-   " + rest[2..];
            }

            return new string(' ', indent * 2) + rest;
        }));
    }

    private static string ReplaceOnce(string text, string old, string replacement)
    {
        Assert.Single(Regex.Matches(text, Regex.Escape(old)));
        return text.Replace(old, replacement, StringComparison.Ordinal);
    }

    private static YamlMapping Map(YamlNode node) => Assert.IsType<YamlMapping>(node);

    private static string Scalar(YamlNode node) => Assert.IsType<YamlScalar>(node).Value;

    private static List<string> Strings(YamlNode node)
    {
        return Assert.IsType<YamlSequence>(node).Items.Select(Scalar).ToList();
    }

    private static List<string> Sorted(IEnumerable<string> keys) => keys.Order(StringComparer.Ordinal).ToList();

    /// <summary>A mapping of scalars as sorted <c>key=value</c> strings, so key order does not matter.</summary>
    private static List<string> Pairs(YamlNode node)
    {
        return Sorted(Map(node).Entries.Select(entry => entry.Key + "=" + Scalar(entry.Value)));
    }

    private static List<YamlMapping> Steps(string yaml) => Steps(WorkflowYaml.Parse(yaml));

    private static List<YamlMapping> Steps(YamlNode root)
    {
        YamlMapping job = Map(Map(Map(root)["jobs"])["release"]);
        return Assert.IsType<YamlSequence>(job["steps"]).Items.Select(Map).ToList();
    }

    private static YamlMapping Step(string yaml, string name)
    {
        return Assert.Single(Steps(yaml), step => Scalar(step["name"]) == name);
    }

    private static string RunScript(YamlMapping step) => Scalar(step["run"]);

    /// <summary>The body of every step's <c>run:</c>, in step order, read from the parsed model.</summary>
    private static List<string> RunScripts(string yaml)
    {
        return Steps(yaml).Where(step => step.ContainsKey("run")).Select(RunScript).ToList();
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

    // AC-12 (093): the Sign step sits between the smoke check and the release, and its script refuses an
    // empty secret, keeps the key file private and deletes it, signs, then checks with the committed public key.
    [Fact]
    public void SignStep_SignsThenVerifiesWithTheCommittedKey()
    {
        List<string> names = Steps(ReadWorkflow()).Select(step => Scalar(step["name"])).ToList();
        Assert.Equal(names.IndexOf("Smoke check the packed tool") + 1, names.IndexOf("Sign"));
        Assert.Equal(names.IndexOf("Sign") + 1, names.IndexOf("Create GitHub release"));

        string script = RunScript(Step(ReadWorkflow(), "Sign"));
        Assert.DoesNotContain("${{", script, StringComparison.Ordinal);
        string[] order =
        [
            "if [ -z \"$TIMES_SIGNING_KEY\" ]; then",
            "exit 1",
            "KEY=\"$RUNNER_TEMP/",
            "trap 'rm -f \"$KEY\"' EXIT",
            "umask 077",
            "printf '%s\\n' \"$TIMES_SIGNING_KEY\" > \"$KEY\"",
            "openssl dgst -sha256 -sign \"$KEY\" -out \"$PACKAGE.sig\" \"$PACKAGE\"",
            "openssl dgst -sha256 -verify src/Times/update-key.pem -signature \"$PACKAGE.sig\" \"$PACKAGE\"",
            "exit 1",
        ];
        int at = 0;
        foreach (string part in order)
        {
            int found = script.IndexOf(part, at, StringComparison.Ordinal);
            Assert.True(found >= 0, "missing or out of order: " + part);
            at = found + part.Length;
        }

        Assert.DoesNotContain("echo \"$TIMES_SIGNING_KEY", script, StringComparison.Ordinal);
        Assert.DoesNotContain("set -x", script, StringComparison.Ordinal);
    }

    // AC-12 (093): the Sign script fails on an empty secret, and with a test key (never the real one) it makes
    // a .sig that the committed-key check and Times's own verifier both accept, and leaves no key file behind.
    [Theory]
    [InlineData(false, false, 1)]
    [InlineData(true, false, 0)]
    [InlineData(true, true, 1)]
    public async Task SignStep_RunsWithATestKey(bool secret, bool otherPublicKey, int expectedExit)
    {
        if (OperatingSystem.IsWindows() || !OnPath("openssl"))
        {
            return;
        }

        using TempFolder work = new TempFolder();
        using TestSigner signer = new TestSigner();
        using TestSigner other = new TestSigner();
        string runnerTemp = Directory.CreateDirectory(Path.Combine(work.Path, "runner-temp")).FullName;
        Directory.CreateDirectory(Path.Combine(work.Path, "nupkg"));
        Directory.CreateDirectory(Path.Combine(work.Path, "src", "Times"));
        byte[] package = TestSigner.Package("Times", "1.2.0");
        string packagePath = Path.Combine(work.Path, "nupkg", "Times.1.2.0.nupkg");
        File.WriteAllBytes(packagePath, package);
        File.WriteAllText(Path.Combine(work.Path, "src", "Times", "update-key.pem"), (otherPublicKey ? other : signer).PublicPem);

        ProcessStartInfo startInfo = new ProcessStartInfo("bash")
        {
            WorkingDirectory = work.Path,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-e");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(RunScript(Step(ReadWorkflow(), "Sign")));
        startInfo.Environment["TIMES_SIGNING_KEY"] = secret ? signer.PrivatePem.TrimEnd('\n') : "";
        startInfo.Environment["RUNNER_TEMP"] = runnerTemp;
        startInfo.Environment["VERSION"] = "1.2.0";

        using Process process = Process.Start(startInfo)!;
        process.StandardInput.Close();
        Task<string> error = process.StandardError.ReadToEndAsync();
        await process.StandardOutput.ReadToEndAsync();
        await error;
        await process.WaitForExitAsync();

        Assert.Equal(expectedExit, process.ExitCode);
        Assert.Empty(Directory.GetFiles(runnerTemp));
        if (expectedExit == 0)
        {
            Assert.True(signer.Signature.Verify(package, File.ReadAllBytes(packagePath + ".sig")));
        }
    }

    private static bool OnPath(string tool) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Any(folder => folder.Length > 0 && File.Exists(Path.Combine(folder, tool)));
}
