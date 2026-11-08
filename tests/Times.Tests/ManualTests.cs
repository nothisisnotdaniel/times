using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Spectre.Console.Testing;

namespace Times.Tests;

/// <summary>
/// Checks that <c>MANUAL.md</c> documents every registered command and setting, with the sections users need.
/// The checker functions are also fed synthetic texts, so the guard is proven to fail when something is missing.
/// </summary>
public sealed class ManualTests : IDisposable
{
    private static readonly string[] Sections =
    [
        "Getting started", "The screen", "One-shot commands", "Commands", "Settings", "Features", "Keyboard shortcuts",
        "Mouse and paste", "Data folder", "Files", "Troubleshooting",
    ];

    private readonly TempFolder data = new();

    public void Dispose() => data.Dispose();

    // AC-1
    [Fact]
    public void Manual_HasEverySectionInOrder()
    {
        string manual = ReadRepoFile("MANUAL.md");

        List<string> headings = Lines(manual)
            .Where(line => line.StartsWith("## ", StringComparison.Ordinal))
            .Select(line => line[3..].Trim())
            .ToList();

        Assert.Equal(Sections, headings);
    }

    // AC-2
    [Fact]
    public void Manual_DocumentsEveryRegisteredCommand()
    {
        List<Command> commands = RegisteredCommands();
        Assert.Contains(commands, command => command.Name == "help");
        Assert.Contains(commands, command => command.Name == "quit");

        string? problem = CommandsProblem(ReadRepoFile("MANUAL.md"), commands);

        Assert.True(problem is null, problem);
    }

    // AC-3
    [Fact]
    public void Manual_HasNoStaleCommandHeading()
    {
        string? problem = StaleCommandsProblem(ReadRepoFile("MANUAL.md"), RegisteredCommands());

        Assert.True(problem is null, problem);
    }

    // AC-4
    [Fact]
    public void Manual_DocumentsEveryRegisteredSetting()
    {
        IReadOnlyList<Setting> settings = SettingsRegistry.CreateDefault().Settings;
        Assert.NotEmpty(settings);

        string? problem = SettingsProblem(ReadRepoFile("MANUAL.md"), settings);

        Assert.True(problem is null, problem);
    }

    // AC-5
    [Fact]
    public void Manual_HasNoStaleSettingHeading()
    {
        string? problem = StaleSettingsProblem(ReadRepoFile("MANUAL.md"), SettingsRegistry.CreateDefault().Settings);

        Assert.True(problem is null, problem);
    }

    // AC-6: a complete synthetic manual passes, so each failure below comes from the one change made to it.
    [Fact]
    public void Checker_CompleteSyntheticManual_Passes()
    {
        string manual = SyntheticManual(RegisteredCommands(), SettingsRegistry.CreateDefault().Settings);

        Assert.Null(CommandsProblem(manual, RegisteredCommands()));
        Assert.Null(StaleCommandsProblem(manual, RegisteredCommands()));
        Assert.Null(SettingsProblem(manual, SettingsRegistry.CreateDefault().Settings));
        Assert.Null(StaleSettingsProblem(manual, SettingsRegistry.CreateDefault().Settings));
    }

    // AC-6
    [Fact]
    public void Checker_CommandHeadingRemoved_NamesTheCommand()
    {
        List<Command> commands = RegisteredCommands();
        string manual = SyntheticManual(commands, SettingsRegistry.CreateDefault().Settings)
            .Replace("### `summary` (`s`)\n", string.Empty, StringComparison.Ordinal);

        string? problem = CommandsProblem(manual, commands);

        Assert.NotNull(problem);
        Assert.Contains("summary (s)", problem, StringComparison.Ordinal);
        Assert.DoesNotContain("arrive (a)", problem, StringComparison.Ordinal);
    }

    // AC-6
    [Fact]
    public void Checker_ExampleBlockRemoved_NamesTheCommand()
    {
        List<Command> commands = RegisteredCommands();
        string manual = SyntheticManual(commands, SettingsRegistry.CreateDefault().Settings)
            .Replace("```text\nvacation\nva\n```\n", string.Empty, StringComparison.Ordinal);

        string? problem = CommandsProblem(manual, commands);

        Assert.NotNull(problem);
        Assert.Contains("vacation (va)", problem, StringComparison.Ordinal);
    }

    // AC-6: an example line that starts with another word is incomplete too.
    [Fact]
    public void Checker_ExampleLineWithOtherCommand_NamesTheCommand()
    {
        List<Command> commands = RegisteredCommands();
        string manual = SyntheticManual(commands, SettingsRegistry.CreateDefault().Settings)
            .Replace("```text\nprev\np\n```\n", "```text\nprev\nnext\n```\n", StringComparison.Ordinal);

        string? problem = CommandsProblem(manual, commands);

        Assert.NotNull(problem);
        Assert.Contains("prev (p)", problem, StringComparison.Ordinal);
    }

    // AC-6: a heading for something not registered is reported by name.
    [Fact]
    public void Checker_StaleHeadings_AreNamed()
    {
        string manual = SyntheticManual(RegisteredCommands(), SettingsRegistry.CreateDefault().Settings)
            .Replace("## Settings\n", "### `jump` (`j`)\n\nArguments: none.\n\n## Settings\n", StringComparison.Ordinal)
            .Replace("## Features\n", "### `dark-mode`\n\nValues: `on` or `off`.\n\n## Features\n", StringComparison.Ordinal);

        Assert.Contains("jump", StaleCommandsProblem(manual, RegisteredCommands()), StringComparison.Ordinal);
        Assert.Contains("dark-mode", StaleSettingsProblem(manual, SettingsRegistry.CreateDefault().Settings), StringComparison.Ordinal);
    }

    // AC-6
    [Fact]
    public void Checker_SettingHeadingRemoved_NamesTheSetting()
    {
        IReadOnlyList<Setting> settings = SettingsRegistry.CreateDefault().Settings;
        string manual = SyntheticManual(RegisteredCommands(), settings)
            .Replace("### `check-rest`\n", string.Empty, StringComparison.Ordinal);

        string? problem = SettingsProblem(manual, settings);

        Assert.NotNull(problem);
        Assert.Contains("check-rest", problem, StringComparison.Ordinal);
        Assert.DoesNotContain("daily-target", problem, StringComparison.Ordinal);
    }

    // AC-6
    [Theory]
    [InlineData("daily-target", "Default: `480`.", "Default: `420`.")]
    [InlineData("alert-target", "Default: `on`.", "Default: `off`.")]
    [InlineData("update-mode", "Default: `notify only`.", "Default: `install on quit`.")]
    public void Checker_WrongDefault_NamesTheSetting(string name, string right, string wrong)
    {
        IReadOnlyList<Setting> settings = SettingsRegistry.CreateDefault().Settings;
        string complete = SyntheticManual(RegisteredCommands(), settings);
        string heading = "### `" + name + "`\n";
        int at = complete.IndexOf(heading, StringComparison.Ordinal);
        int defaultAt = complete.IndexOf(right, at, StringComparison.Ordinal);
        string manual = complete[..defaultAt] + wrong + complete[(defaultAt + right.Length)..];

        string? problem = SettingsProblem(manual, settings);

        Assert.NotNull(problem);
        Assert.Contains(name, problem, StringComparison.Ordinal);
    }

    // AC-7
    [Fact]
    public void Manual_KeyboardShortcutsNameEveryKey()
    {
        string section = string.Join("\n", Section(ReadRepoFile("MANUAL.md"), "Keyboard shortcuts") ?? []);

        foreach (string key in new[] { "Enter", "Esc", "Tab", "Backspace", "↑", "↓", "PgUp", "PgDn", "Shift+PgUp", "Ctrl+↑", "Ctrl+↓", "Space", "Ctrl+C" })
        {
            Assert.Contains(key, section, StringComparison.Ordinal);
        }

        Assert.Contains("settings panel", section, StringComparison.Ordinal);
    }

    // AC-8
    [Fact]
    public void Manual_DataFolderAndFilesNameEverything()
    {
        string manual = ReadRepoFile("MANUAL.md");
        string folder = string.Join("\n", Section(manual, "Data folder") ?? []);
        string files = string.Join("\n", Section(manual, "Files") ?? []);

        foreach (string text in new[] { "~/.config/Times/", "%APPDATA%\\Times\\", "TIMES_DATA_DIR", "`2`", "`3`", "`4`" })
        {
            Assert.Contains(text, folder, StringComparison.Ordinal);
        }

        foreach (string file in new[] { "yyyy-MM-dd.json", "projects.json", "settings.json", "times.lock", "update.log", "update-pending.json" })
        {
            Assert.Contains(file, files, StringComparison.Ordinal);
        }
    }

    // AC-9
    [Theory]
    [InlineData(@"\.cs\b")]
    [InlineData("dotnet build")]
    [InlineData("dotnet test")]
    [InlineData(@"\bSettingsRegistry\b")]
    [InlineData(@"\bCommandRegistry\b")]
    [InlineData(@"\bDayStore\b")]
    [InlineData(@"\bCappedFile\b")]
    [InlineData(@"\bApp\.\w")]
    [InlineData(@"\bIAnsiConsole\b")]
    public void Manual_HasNoDeveloperTerms(string pattern)
    {
        Match match = Regex.Match(ReadRepoFile("MANUAL.md"), pattern);

        Assert.False(match.Success, "MANUAL.md holds developer term '" + match.Value + "'");
    }

    // AC-9: the check counts whole tokens only.
    [Fact]
    public void DeveloperTerms_DoNotMatchCsvOrSentenceEnds()
    {
        Assert.DoesNotMatch(@"\.cs\b", "Saves times-report.csv for you.");
        Assert.DoesNotMatch(@"\bApp\.\w", "Ask in WhatsApp. Then go.");
        Assert.Matches(@"\.cs\b", "See App.cs for details.");
    }

    // AC-10
    [Fact]
    public void Readme_LinksToManualBeforeBuildingSection()
    {
        string readme = ReadRepoFile("README.md");
        int building = readme.IndexOf("\n# Building Times", StringComparison.Ordinal);
        Match link = Regex.Match(readme, @"\[[^\]]+\]\(MANUAL\.md\)");

        Assert.True(building > 0, "README has no '# Building Times' heading.");
        Assert.True(link.Success, "README has no Markdown link to MANUAL.md.");
        Assert.True(link.Index < building, "The link to MANUAL.md comes after '# Building Times'.");
    }

    /// <summary>The commands <see cref="App"/> registers, read from a registry it was built with; the loop is never run.</summary>
    private List<Command> RegisteredCommands()
    {
        CommandRegistry registry = CommandRegistry.CreateDefault();
        _ = new App(new TestConsole(), FixedTimeProvider.At20261027(), new DayStore(data.Path), registry);
        return registry.Commands.ToList();
    }

    private static string ReadRepoFile(string name)
    {
        DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Times.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.True(directory is not null, "Times.slnx not found above " + AppContext.BaseDirectory);
        return File.ReadAllText(Path.Combine(directory.FullName, name));
    }

    private static string[] Lines(string text) => text.ReplaceLineEndings("\n").Split('\n');

    /// <summary>
    /// The lines under the <c>## <paramref name="heading"/></c> line up to the next <c># </c> or <c>## </c> heading
    /// outside fenced code, or <c>null</c> if the text has no such heading.
    /// </summary>
    internal static List<string>? Section(string text, string heading)
    {
        string[] lines = Lines(text);
        int start = Array.IndexOf(lines, "## " + heading);
        if (start < 0)
        {
            return null;
        }

        List<string> section = [];
        bool inFence = false;
        for (int i = start + 1; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
            }
            else if (!inFence && (line.StartsWith("# ", StringComparison.Ordinal) || line.StartsWith("## ", StringComparison.Ordinal)))
            {
                break;
            }

            section.Add(line);
        }

        return section;
    }

    /// <summary>
    /// The <c>###</c> subsections of <paramref name="section"/> (headings outside fenced code), each as its heading
    /// line and the lines below it up to the next <c>###</c>, in order.
    /// </summary>
    internal static List<(string Heading, List<string> Body)> Subsections(List<string> section)
    {
        List<(string Heading, List<string> Body)> result = [];
        bool inFence = false;
        foreach (string line in section)
        {
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
            }
            else if (!inFence && line.StartsWith("### ", StringComparison.Ordinal))
            {
                result.Add((line, []));
                continue;
            }

            if (result.Count > 0)
            {
                result[^1].Body.Add(line);
            }
        }

        return result;
    }

    /// <summary>The non-empty lines inside the fenced code blocks of <paramref name="body"/>, per block.</summary>
    private static List<List<string>> FencedBlocks(List<string> body)
    {
        List<List<string>> blocks = [];
        bool inFence = false;
        foreach (string line in body)
        {
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
                if (inFence)
                {
                    blocks.Add([]);
                }

                continue;
            }

            if (inFence && line.Trim().Length > 0)
            {
                blocks[^1].Add(line);
            }
        }

        return blocks;
    }

    /// <summary>The text after <paramref name="prefix"/> on the first body line that starts with it, or <c>null</c>.</summary>
    private static string? LineAfter(List<string> body, string prefix)
    {
        string? line = body.FirstOrDefault(l => l.StartsWith(prefix, StringComparison.Ordinal));
        return line?[prefix.Length..].Trim();
    }

    /// <summary>The first backticked word of a <c>###</c> heading, or the whole heading text if it has none.</summary>
    private static string HeadingName(string heading)
    {
        Match match = Regex.Match(heading, "`([^`]*)`");
        return match.Success ? match.Groups[1].Value : heading[4..].Trim();
    }

    /// <summary>
    /// What is missing or incomplete in <c>## Commands</c> for <paramref name="commands"/>, or <c>null</c>: each command
    /// needs exactly one heading <c>### `name` (`alias`)</c>, a non-empty <c>Arguments:</c> line and at least one fenced
    /// block, every non-empty line of which starts with the name or alias.
    /// </summary>
    internal static string? CommandsProblem(string manual, IEnumerable<Command> commands)
    {
        List<string>? section = Section(manual, "Commands");
        if (section is null)
        {
            return "MANUAL.md has no '## Commands' section.";
        }

        List<(string Heading, List<string> Body)> subsections = Subsections(section);
        List<string> problems = [];
        foreach (Command command in commands)
        {
            string heading = "### `" + command.Name + "` (`" + command.Alias + "`)";
            List<(string Heading, List<string> Body)> found = subsections
                .Where(s => string.Equals(s.Heading.TrimEnd(), heading, StringComparison.Ordinal))
                .ToList();
            string label = command.Name + " (" + command.Alias + ")";
            if (found.Count != 1)
            {
                problems.Add(label + (found.Count == 0 ? ": no heading" : ": more than one heading"));
                continue;
            }

            List<string> body = found[0].Body;
            if (string.IsNullOrWhiteSpace(LineAfter(body, "Arguments:")))
            {
                problems.Add(label + ": no Arguments line");
            }

            List<List<string>> blocks = FencedBlocks(body);
            if (blocks.Count == 0 || blocks.All(block => block.Count == 0))
            {
                problems.Add(label + ": no examples");
            }
            else if (blocks.SelectMany(block => block).Any(line => !StartsWithCommand(line, command)))
            {
                problems.Add(label + ": an example does not start with the name or alias");
            }
        }

        return problems.Count == 0 ? null : "MANUAL.md '## Commands' is missing or incomplete: " + string.Join("; ", problems);
    }

    private static bool StartsWithCommand(string line, Command command)
    {
        string first = line.TrimStart().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries)[0];
        return string.Equals(first, command.Name, StringComparison.Ordinal) || string.Equals(first, command.Alias, StringComparison.Ordinal);
    }

    /// <summary>The <c>###</c> headings in <c>## Commands</c> whose name is not a registered command, or <c>null</c>.</summary>
    internal static string? StaleCommandsProblem(string manual, IEnumerable<Command> commands)
    {
        HashSet<string> names = commands.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        List<string> stale = Subsections(Section(manual, "Commands") ?? [])
            .Select(s => HeadingName(s.Heading))
            .Where(name => !names.Contains(name))
            .ToList();
        return stale.Count == 0 ? null : "MANUAL.md '## Commands' has headings for unregistered commands: " + string.Join(", ", stale);
    }

    /// <summary>
    /// What is missing or incomplete in <c>## Settings</c> for <paramref name="settings"/>, or <c>null</c>: each setting
    /// needs exactly one heading <c>### `name`</c>, a <c>Values:</c> line naming its values, a <c>Default:</c> line with its
    /// default in backticks, and at least one other non-empty line.
    /// </summary>
    internal static string? SettingsProblem(string manual, IEnumerable<Setting> settings)
    {
        List<string>? section = Section(manual, "Settings");
        if (section is null)
        {
            return "MANUAL.md has no '## Settings' section.";
        }

        List<(string Heading, List<string> Body)> subsections = Subsections(section);
        List<string> problems = [];
        foreach (Setting setting in settings)
        {
            string heading = "### `" + setting.Name + "`";
            List<(string Heading, List<string> Body)> found = subsections
                .Where(s => string.Equals(s.Heading.TrimEnd(), heading, StringComparison.Ordinal))
                .ToList();
            if (found.Count != 1)
            {
                problems.Add(setting.Name + (found.Count == 0 ? ": no heading" : ": more than one heading"));
                continue;
            }

            List<string> body = found[0].Body;
            string? values = LineAfter(body, "Values:");
            if (values is null || !ExpectedValues(setting).All(v => values.Contains("`" + v + "`", StringComparison.Ordinal)))
            {
                problems.Add(setting.Name + ": Values line missing or incomplete");
            }

            string? defaultLine = LineAfter(body, "Default:");
            if (defaultLine is null || !DefaultMatches(setting, defaultLine))
            {
                problems.Add(setting.Name + ": Default line missing or wrong");
            }

            bool hasEffect = body.Any(line => line.Trim().Length > 0
                && !line.StartsWith("Values:", StringComparison.Ordinal)
                && !line.StartsWith("Default:", StringComparison.Ordinal));
            if (!hasEffect)
            {
                problems.Add(setting.Name + ": no description of the effect");
            }
        }

        return problems.Count == 0 ? null : "MANUAL.md '## Settings' is missing or incomplete: " + string.Join("; ", problems);
    }

    private static IEnumerable<string> ExpectedValues(Setting setting) => setting.Kind switch
    {
        SettingKind.OnOff => ["on", "off"],
        SettingKind.Number => [setting.Min.ToString(CultureInfo.InvariantCulture), setting.Max.ToString(CultureInfo.InvariantCulture)],
        _ => setting.Options,
    };

    private static string DefaultText(Setting setting) => setting.Default switch
    {
        bool on => on ? "on" : "off",
        int number => number.ToString(CultureInfo.InvariantCulture),
        _ => (string)setting.Default,
    };

    private static bool DefaultMatches(Setting setting, string line)
    {
        if (!line.Contains("`" + DefaultText(setting) + "`", StringComparison.Ordinal))
        {
            return false;
        }

        // An on/off default must not name the other value too.
        return setting.Kind != SettingKind.OnOff
            || !line.Contains("`" + ((bool)setting.Default ? "off" : "on") + "`", StringComparison.Ordinal);
    }

    /// <summary>The <c>###</c> headings in <c>## Settings</c> whose name is not a registered setting, or <c>null</c>.</summary>
    internal static string? StaleSettingsProblem(string manual, IEnumerable<Setting> settings)
    {
        HashSet<string> names = settings.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        List<string> stale = Subsections(Section(manual, "Settings") ?? [])
            .Select(s => HeadingName(s.Heading))
            .Where(name => !names.Contains(name))
            .ToList();
        return stale.Count == 0 ? null : "MANUAL.md '## Settings' has headings for unregistered settings: " + string.Join(", ", stale);
    }

    /// <summary>A complete manual for <paramref name="commands"/> and <paramref name="settings"/>, in the shape the checker expects.</summary>
    private static string SyntheticManual(IEnumerable<Command> commands, IEnumerable<Setting> settings)
    {
        StringBuilder text = new StringBuilder("# Manual\n\n## Getting started\n\nHello.\n\n## Commands\n\nIntro.\n\n");
        foreach (Command command in commands)
        {
            text.Append("### `").Append(command.Name).Append("` (`").Append(command.Alias).Append("`)\n\n")
                .Append("Arguments: none.\n\n").Append(command.Description).Append("\n\n")
                .Append("```text\n").Append(command.Name).Append('\n').Append(command.Alias).Append("\n```\n\n");
        }

        text.Append("## Settings\n\nIntro.\n\n");
        foreach (Setting setting in settings)
        {
            text.Append("### `").Append(setting.Name).Append("`\n\n")
                .Append("Values: ").Append(string.Join(", ", ExpectedValues(setting).Select(v => "`" + v + "`"))).Append(".\n\n")
                .Append("Default: `").Append(DefaultText(setting)).Append("`.\n\n")
                .Append(setting.Description).Append(".\n\n");
        }

        text.Append("## Features\n\nAll of them.\n");
        return text.ToString();
    }
}
