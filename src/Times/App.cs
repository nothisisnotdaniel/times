using System.Globalization;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Times;

/// <summary>
/// The interactive main screen. Each iteration clears the screen, draws the full-terminal layout
/// (quicklist panel and day view on top, the hint rows (up to 3, wrapped), a 3-line input box at the bottom), reads one line and dispatches it through the
/// <see cref="CommandRegistry"/>. All output goes through <see cref="IAnsiConsole"/> and all clock
/// reads through <see cref="TimeProvider"/>, so both can be faked in tests. Day files live in the
/// injected <see cref="DayStore"/>, so tests use a temp folder.
/// </summary>
internal sealed class App
{
    private const int InputHeight = 3;

    /// <summary>The most rows a hint wraps onto; a longer hint ends its last row in a grey <c>…</c>.</summary>
    private const int MaxHintRows = 3;

    // While a hint takes more than one row, the top area keeps at least this many rows.
    private const int MinTopRowsForWrap = 4;
    private const string Prompt = "> ";
    private const string Placeholder = "Type help for commands";
    private const string RetypeHiddenCharacters = "Retype it without the hidden characters.";

    private static readonly string PasteCutHint =
        $"[red]Paste cut to {LineEditor.MaxPasteLength.ToString(CultureInfo.InvariantCulture)} characters; " +
        "the rest was dropped.[/]";

    /// <summary>What to do when <c>projects.json</c> cannot be read, shown in the quicklist panel and in every command hint about it.</summary>
    public const string UnreadableProjectsNextStep = "Fix or delete projects.json in the data folder (help shows where).";

    private readonly IAnsiConsole console;
    private readonly TimeProvider time;
    private readonly CommandRegistry registry;
    private readonly DayView dayView;
    private readonly bool folderFromEnvironment;
    private readonly ProjectStore projectStore;
    private readonly MouseReporting? mouse;
    private readonly SettingsRegistry settings;
    private readonly SettingsStore settingsStore;
    private readonly UpdateService? updates;

    // The start-up update check (started once in Run), whether its hint was shown, and whether its
    // staged package was taken over. The package staged for quit, from that check or from update.
    private Task<UpdateOutcome>? startupCheck;
    private bool startupHintShown;
    private bool startupStagedTaken;
    private StagedUpdate? staged;

    // Set by the settings command; the loop then runs the settings panel until Esc.
    private bool openSettings;
    private string framePlaceholder = Placeholder;

    // The last drawn frame, one entry per screen row, so rows the command popup covered can be restored.
    // A scroll replaces it, so the popup restores the scrolled rows.
    private List<SegmentLine> frame = [];

    // The top area's rendered content lines (before fitting), and the line the window starts at.
    // The offset goes back to 0 on every full redraw.
    private List<SegmentLine> topLines = [];
    private int offset;
    private IRenderable frameHint = Text.Empty;

    // The hint rows last laid out, and for which hint and terminal size, so they are rendered once per hint.
    private IRenderable? hintRowsFor;
    private int hintRowsWidth = -1;
    private int hintRowsHeight = -1;
    private List<SegmentLine> hintRows = [];

    /// <summary>Lines one mouse-wheel notch moves the top area.</summary>
    public const int WheelLines = 3;

    /// <param name="folderFromEnvironment">True if the store's folder came from <see cref="DataFolder.VariableName"/>.</param>
    /// <param name="mouse">Turned on while the loop runs and off on every way out of it, or <c>null</c> for none.</param>
    /// <param name="settings">The settings the <c>settings</c> panel lists, or <c>null</c> for <see cref="SettingsRegistry.CreateDefault"/>.</param>
    /// <param name="updates">Checks for and installs updates, or <c>null</c> for none (no network at all).</param>
    public App(IAnsiConsole console, TimeProvider time, DayStore store, CommandRegistry? registry = null,
        bool folderFromEnvironment = false, MouseReporting? mouse = null, SettingsRegistry? settings = null,
        UpdateService? updates = null)
    {
        this.updates = updates;
        this.settings = settings ?? SettingsRegistry.CreateDefault();
        settingsStore = new SettingsStore(store.Folder);
        this.mouse = mouse;
        this.console = console;
        this.time = time;
        this.folderFromEnvironment = folderFromEnvironment;
        Store = store;
        projectStore = new ProjectStore(store.Folder);
        dayView = new DayView(time);
        this.registry = registry ?? CommandRegistry.CreateDefault();
        RegisterNavigation();
        RegisterBookings();
        RegisterProjects();
        this.registry.Register("settings", "se", "Change your settings", NoArguments("settings", () =>
        {
            openSettings = true;
            return CommandResult.None;
        }));
        this.registry.Register("update", "up", "Check for a newer Times now and install it when you quit", NoArguments("update", Update));
    }

    // Runs the check and download now, with the settings as they are, and stages the result for quit
    // in every mode. All its hints are grey (or steelblue): an update problem is never an input error.
    private CommandResult Update()
    {
        if (updates is null)
        {
            return new CommandResult(hint: new Markup("[grey]Update check skipped: this build cannot update itself.[/]"));
        }

        TakeStartupStaged();
        UpdateOutcome outcome = updates
            .CheckAsync(UpdateMode(), UpdateChannel(), Store.Folder, command: true, CancellationToken.None)
            .GetAwaiter().GetResult();
        if (outcome.Staged is not null)
        {
            if (staged is not null && staged.Folder != outcome.Staged.Folder)
            {
                UpdateDownloader.DeleteFolder(staged.Folder);
            }

            staged = outcome.Staged;
            // A package the start-up check stages later is not taken over: this one is newer or the same.
            startupStagedTaken = true;
        }

        return outcome.Hint is null ? CommandResult.None : new CommandResult(hint: new Markup(outcome.Hint));
    }

    // The update-mode setting, or notify only when it is missing, unreadable or not registered.
    private string UpdateMode() =>
        ChoiceSetting(SettingsRegistry.UpdateModeName, SettingsRegistry.UpdateNotifyOnly);

    // The update-channel setting, or stable when it is missing, unreadable or not registered.
    private string UpdateChannel() =>
        ChoiceSetting(SettingsRegistry.UpdateChannelName, UpdateChecker.Stable);

    private string ChoiceSetting(string name, string fallback)
    {
        if (settings.TryGet(name, out Setting setting) && setting.Kind == SettingKind.Choice
            && LoadSettings(out SettingValues values) is null)
        {
            return values.Choice(name);
        }

        return fallback;
    }

    // Takes over the package the start-up check staged, once it is done (never waits for it).
    private void TakeStartupStaged()
    {
        if (startupStagedTaken || startupCheck is not { IsCompletedSuccessfully: true })
        {
            return;
        }

        startupStagedTaken = true;
        if (startupCheck.Result.Staged is { } fromStartup)
        {
            staged ??= fromStartup;
        }
    }

    // The start-up check's hint, once it is done, at the first full redraw that has no other hint.
    private IRenderable? StartupHint(IRenderable? hint)
    {
        if (hint is not null || startupHintShown || startupCheck is not { IsCompletedSuccessfully: true })
        {
            return hint;
        }

        startupHintShown = true;
        return startupCheck.Result.Hint is { } markup ? new Markup(markup) : null;
    }

    // At quit with a staged package: write the pending file, start the helper once and return. Only
    // install and restart prints a line, after the screen was cleared.
    private void InstallStaged()
    {
        TakeStartupStaged();
        if (updates?.Install is null || staged is null)
        {
            return;
        }

        string? failed = updates.Applier.Apply(staged, updates.Install, Store.Folder, Environment.ProcessId, time.GetUtcNow());
        string version = Markup.Escape(staged.Version.ToString());
        if (failed is not null)
        {
            console.MarkupLine($"[red]Update to {version} could not start: {Markup.Escape(failed)}[/]");
            return;
        }

        if (UpdateMode() == SettingsRegistry.UpdateInstallAndRestart)
        {
            console.MarkupLine($"[grey]Times {version} is installing. Start tm again in a few seconds.[/]");
        }
    }

    /// <summary>Where the day files live. Booking features read and write through it.</summary>
    public DayStore Store { get; }

    /// <summary>Writes <c>Times &lt;version&gt;</c> as plain text, so the version is never parsed as markup.</summary>
    internal static void WriteVersion(IAnsiConsole console, string version)
    {
        console.WriteLine($"Times {version}");
    }

    private void RegisterNavigation()
    {
        registry.Register("prev", "p", "Go to the previous day", NoArguments("prev", () =>
            dayView.Previous() ? CommandResult.None : Hint("There is no day before this one.")));
        registry.Register("next", "n", "Go to the next day", NoArguments("next", () =>
            dayView.Next() ? CommandResult.None : Hint("There is no day after this one.")));
        registry.Register("today", "t", "Go back to today", NoArguments("today", () =>
        {
            dayView.Today();
            return CommandResult.None;
        }));
    }

    private void RegisterBookings()
    {
        registry.Register("arrive", "a", $"Book your arrival: now, or at an optional {TimeInput.Format}", argument =>
            Book("arrive", argument, EntryKind.Arrival));
        registry.Register("leave", "l", $"Book your leaving: now, or at an optional {TimeInput.Format}", argument =>
            Book("leave", argument, EntryKind.Leaving));
        registry.Register("break", "b", $"Book the start of a break: now, or at an optional {TimeInput.Format}", argument =>
            Book("break", argument, EntryKind.BreakStart));
        registry.Register("breakend", "be", $"Book the end of a break: now, or at an optional {TimeInput.Format}", argument =>
            Book("breakend", argument, EntryKind.BreakEnd));
        registry.Register("checkpoint", "cp", $"Book a checkpoint: [{TimeInput.Format}] [#project] <name>", Checkpoint);
        registry.Register("remove", "r", "Remove an entry from this day by its number", Remove);
        registry.Register("change", "c", $"Change the time of an entry on this day: <number> <{TimeInput.Format}>", Change);
        registry.Register("booked", "bk", "Mark a checkpoint on this day as booked in Azure DevOps by its number", argument =>
            SetBooked("booked", argument, true));
        registry.Register("unbooked", "ub", "Mark a checkpoint on this day as not yet booked by its number", argument =>
            SetBooked("unbooked", argument, false));
        registry.Register("summary", "s", "Show the time taken per work item from this day's checkpoints", NoArguments("summary", Summary));
        registry.Register("absence", "ab", "Mark this day as vacation, sick or holiday, or clear it", Absence);
        registry.Register("report", "rp", "Show the hours per project over a date range: <from> <to>", Report);
    }

    // Marks the day being viewed as an absence (vacation, sick, holiday) or clears it. Nothing is written
    // if the argument is not one of the four words, the day's file cannot be read, the day has entries,
    // or (for clear) the day has no absence.
    private CommandResult Absence(string argument)
    {
        const string Name = "absence";
        const string Usage = "Use [bold]absence vacation|sick|holiday|clear[/].";
        string word = argument.Trim();
        if (word.Length == 0)
        {
            return Hint($"{Name}: say which absence. {Usage}");
        }

        // ASCII only, so no culture or Unicode case folding (such as a long s) can turn a word into one of ours.
        string key = word.All(char.IsAscii) ? word.ToLowerInvariant() : string.Empty;
        AbsenceKind? wanted;
        bool clear = false;
        switch (key)
        {
            case "vacation":
                wanted = AbsenceKind.Vacation;
                break;
            case "sick":
                wanted = AbsenceKind.Sick;
                break;
            case "holiday":
                wanted = AbsenceKind.PublicHoliday;
                break;
            case "clear":
                wanted = null;
                clear = true;
                break;
            default:
                return Hint($"{Name}: [bold]{Markup.Escape(word)}[/] is not an absence. {Usage}");
        }

        DateOnly day = dayView.Current;
        DayRecord current;
        try
        {
            current = Store.Load(day);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return Hint($"{Name}: {Markup.Escape(ex.Message)} Nothing was changed.");
        }

        if (clear && current.Absence is null)
        {
            return Hint($"{Name}: This day has no absence.");
        }

        if (!clear && current.Entries.Count > 0)
        {
            return Hint($"{Name}: this day has entries. Remove them first with [bold]remove <number>[/].");
        }

        try
        {
            Store.Save(day, current.WithAbsence(wanted));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Hint($"{Name}: could not save the day. {Markup.Escape(ex.Message)}");
        }

        return CommandResult.None;
    }

    // Shows the time taken per work item (and per name without one) on the day being viewed, as a
    // table, or a grey note if the day has no checkpoints. Read-only: never calls Store.Save.
    private CommandResult Summary()
    {
        DateOnly day = dayView.Current;
        DayRecord record;
        try
        {
            record = Store.Load(day);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return Hint($"summary: {Markup.Escape(ex.Message)}");
        }

        // "Today" is read from the clock each time the command runs.
        bool isToday = day == DateOnly.FromDateTime(time.GetLocalNow().DateTime);
        IReadOnlyList<CheckpointGroup> groups = CheckpointSummary.Calculate(record, isToday, Now());
        if (groups.Count == 0)
        {
            return new CommandResult(new Markup("[grey]No checkpoints on this day.[/]"));
        }

        return new CommandResult(SummaryView(day, groups));
    }

    /// <summary>
    /// The summary table for <paramref name="groups"/> (at least one), captioned with the day heading,
    /// and, if any group is open, one grey footnote below it explaining <c>open</c>.
    /// </summary>
    internal static IRenderable SummaryView(DateOnly day, IReadOnlyList<CheckpointGroup> groups)
    {
        Table table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .AddColumn("[grey]Work item[/]")
            .AddColumn("[grey]Name[/]")
            .AddColumn("[grey]Time[/]")
            .Caption(Markup.Escape(DayView.Heading(day)), new Style(Color.Grey));

        foreach (CheckpointGroup group in groups)
        {
            string workItem = group.WorkItem is { } w ? "#" + w.ToString(CultureInfo.InvariantCulture) : string.Empty;
            string names = string.Join(", ", group.Names.Select(Markup.Escape));
            string open = group.Open ? " [grey]open[/]" : string.Empty;
            table.AddRow(
                new Text(workItem),
                new Markup(names),
                new Markup($"[steelblue1]{DayView.FormatTimeLeft(group.Total)}[/]{open}"));
        }

        if (groups.Any(g => g.Open))
        {
            return new Rows(table, new Markup($"[grey]{OpenFootnote}[/]"));
        }

        return table;
    }

    // Explains the "open" marker below the summary table; a fixed literal, so no escaping is needed.
    internal const string OpenFootnote = "open: this checkpoint has no end on this day and counts no time.";

    // Shows the checkpoint time per work item (and per name without one) over an inclusive date range,
    // as one table with a total row, or a grey note if the range holds no checkpoints. Read-only.
    private CommandResult Report(string argument)
    {
        const string Name = "report";
        const string Usage = "Use [bold]report <from> <to>[/] with dates as [bold]yyyy-MM-dd[/].";
        string[] parts = argument.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
        {
            return Hint($"{Name}: say the first and the last day. {Usage}");
        }

        DateOnly[] dates = new DateOnly[2];
        for (int i = 0; i < 2; i++)
        {
            if (!DateOnly.TryParseExact(parts[i], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out dates[i]))
            {
                return Hint($"{Name}: [bold]{Markup.Escape(parts[i])}[/] is not a date. {Usage}");
            }
        }

        DateOnly from = dates[0];
        DateOnly to = dates[1];
        if (from > to)
        {
            return Hint($"{Name}: the first day {ReportDate(from)} is after the last day {ReportDate(to)}. {Usage}");
        }

        ProjectReport report;
        try
        {
            report = ProjectReport.Calculate(Store, from, to, DateOnly.FromDateTime(time.GetLocalNow().DateTime), Now());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Hint($"{Name}: the data folder cannot be listed: {Markup.Escape(ex.Message)}");
        }

        // Titles are optional: without a readable quicklist every row falls back to its names.
        ProjectList? projects;
        try
        {
            projects = projectStore.Load();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            projects = null;
        }

        return new CommandResult(ReportView(from, to, report, projects), hideQuicklist: true);
    }

    private static string ReportDate(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>
    /// The report table captioned with the range, with a <c>Total</c> row, or a grey note if
    /// <paramref name="report"/> has no rows; then the open footnote and the yellow skipped line when
    /// they apply. Titles come from <paramref name="projects"/> (<c>null</c>: none), otherwise the names.
    /// </summary>
    internal static IRenderable ReportView(DateOnly from, DateOnly to, ProjectReport report, ProjectList? projects)
    {
        List<IRenderable> parts = [];
        if (report.Rows.Count == 0)
        {
            parts.Add(new Markup($"[grey]No checkpoints between {ReportDate(from)} and {ReportDate(to)}.[/]"));
        }
        else
        {
            Table table = new Table()
                .Border(TableBorder.Rounded)
                .BorderColor(Color.Grey)
                .AddColumn("[grey]Work item[/]")
                .AddColumn("[grey]Title[/]")
                .AddColumn("[grey]Time[/]")
                .AddColumn("[grey]Hours[/]")
                .Caption($"{ReportDate(from)} – {ReportDate(to)}", new Style(Color.Grey));

            foreach (ReportRow row in report.Rows)
            {
                string workItem = row.WorkItem is { } w ? "#" + w.ToString(CultureInfo.InvariantCulture) : string.Empty;
                string? title = row.WorkItem is { } item ? projects?.Projects.FirstOrDefault(p => p.WorkItem == item)?.Title : null;
                string titleText = title ?? string.Join(", ", row.Names);
                string open = row.Open ? " [grey]open[/]" : string.Empty;
                table.AddRow(
                    new Text(workItem),
                    new Markup(Markup.Escape(titleText)),
                    new Markup($"[steelblue1]{DayView.FormatTimeLeft(row.Total)}[/]{open}"),
                    new Markup($"[steelblue1]{FormatHours(row.Total)}[/]"));
            }

            table.AddRow(
                new Markup("[bold]Total[/]"),
                Text.Empty,
                new Markup($"[bold steelblue1]{DayView.FormatTimeLeft(report.Total)}[/]"),
                new Markup($"[bold steelblue1]{FormatHours(report.Total)}[/]"));
            parts.Add(table);
            if (report.Rows.Any(r => r.Open))
            {
                parts.Add(new Markup($"[grey]{OpenFootnote}[/]"));
            }
        }

        if (report.SkippedFiles > 0)
        {
            string files = report.SkippedFiles == 1 ? "day file" : "day files";
            parts.Add(new Markup($"[yellow]! {report.SkippedFiles.ToString(CultureInfo.InvariantCulture)} {files} skipped[/]"));
        }

        return parts.Count == 1 ? parts[0] : new Rows(parts);
    }

    /// <summary>Whole minutes as decimal hours with two decimals and the invariant culture, e.g. <c>7.50</c>.</summary>
    internal static string FormatHours(TimeSpan value)
    {
        long minutes = value.Ticks / TimeSpan.TicksPerMinute;
        return (minutes / 60m).ToString("0.00", CultureInfo.InvariantCulture);
    }

    private void RegisterProjects()
    {
        registry.Register("addproject", "ap", "Add a project to the quicklist: <work item> <title>", AddProject);
        registry.Register("projects", "pr", "List the projects on the quicklist", NoArguments("projects", ListProjects));
    }

    // Adds a project to the end of the quicklist. Nothing is written if the work item or title is
    // invalid, the work item is already listed, or projects.json cannot be read.
    private CommandResult AddProject(string argument)
    {
        const string Name = "addproject";
        const string Usage = "Use [bold]addproject <work item> <title>[/].";
        const string TitleMissing = "the title is missing.";
        if (argument.Length == 0)
        {
            return Hint($"{Name}: say the work item number and the title. {Usage}");
        }

        int split = argument.IndexOfAny([' ', '\t']);
        string number = split < 0 ? argument : argument[..split];
        string title = split < 0 ? string.Empty : argument[(split + 1)..].Trim();
        if (!int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out int workItem) || workItem < 1)
        {
            return Hint($"{Name}: [bold]{Markup.Escape(number)}[/] is not a work item number. {Usage}");
        }

        if (title.Length == 0)
        {
            return Hint($"{Name}: {TitleMissing} {Usage}");
        }

        if (title.Length > ProjectList.MaxTitleLength)
        {
            return Hint($"{Name}: the title is longer than {ProjectList.MaxTitleLength.ToString(CultureInfo.InvariantCulture)} characters.");
        }

        if (ProjectList.HasControlCharacters(title))
        {
            return Hint($"{Name}: the title must not contain control characters. {RetypeHiddenCharacters}");
        }

        if (ProjectList.HasInvisibleOrBrokenCharacters(title))
        {
            return Hint($"{Name}: the title must not contain invisible formatting or broken characters. {RetypeHiddenCharacters}");
        }

        ProjectList current;
        try
        {
            current = projectStore.Load();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return Hint($"{Name}: {UnreadableProjects(ex)} Nothing was added. {UnreadableProjectsNextStep}");
        }

        if (current.NumberOf(workItem) is { } existing)
        {
            return Hint($"{Name}: work item #{workItem.ToString(CultureInfo.InvariantCulture)} is already project " +
                $"[bold]{existing.ToString(CultureInfo.InvariantCulture)}[/].");
        }

        Project project = new Project(workItem, title);
        ProjectList updated = current.Add(project);
        try
        {
            projectStore.Save(updated);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Hint($"{Name}: could not save the project list. {Markup.Escape(ex.Message)}");
        }

        return new CommandResult(new Markup(
            $"Added project [steelblue1]{updated.Projects.Count.ToString(CultureInfo.InvariantCulture)}[/]: " +
            $"#{workItem.ToString(CultureInfo.InvariantCulture)} {Markup.Escape(title)}"));
    }

    // Shows the quicklist as a table, or a grey note if it is empty. Never writes anything.
    private CommandResult ListProjects()
    {
        ProjectList list;
        try
        {
            list = projectStore.Load();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return Hint($"projects: {UnreadableProjects(ex)} {UnreadableProjectsNextStep}");
        }

        if (list.IsEmpty)
        {
            return new CommandResult(new Markup("[grey]No projects yet. Add one with addproject <work item> <title>.[/]"), hideQuicklist: true);
        }

        Table table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .AddColumn("[grey]#[/]")
            .AddColumn("[grey]Work item[/]")
            .AddColumn("[grey]Title[/]");

        for (int i = 0; i < list.Projects.Count; i++)
        {
            Project project = list.Projects[i];
            table.AddRow(
                new Markup($"[steelblue1]{(i + 1).ToString(CultureInfo.InvariantCulture)}[/]"),
                new Text("#" + project.WorkItem.ToString(CultureInfo.InvariantCulture)),
                new Text(project.Title));
        }

        return new CommandResult(table, hideQuicklist: true);
    }

    private static string UnreadableProjects(Exception ex) =>
        $"the project list cannot be read: {Markup.Escape(ex.Message)}";

    // Removes entry <number> from the day being viewed. Nothing is written if the number is not a
    // plain number, there is no such entry, or the day's file cannot be read.
    private CommandResult Remove(string argument)
    {
        const string Name = "remove";
        const string Usage = "Use [bold]remove <number>[/].";
        if (argument.Length == 0)
        {
            return Hint($"{Name}: say which entry to remove. {Usage}");
        }

        if (!int.TryParse(argument, NumberStyles.None, CultureInfo.InvariantCulture, out int number))
        {
            return Hint($"{Name}: [bold]{Markup.Escape(argument)}[/] is not an entry number. {Usage}");
        }

        DateOnly day = dayView.Current;
        DayRecord current;
        try
        {
            current = Store.Load(day);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return Hint($"{Name}: {Markup.Escape(ex.Message)} Nothing was removed.");
        }

        if (number < 1 || number > current.Entries.Count)
        {
            return Hint($"{Name}: there is no entry [bold]{number.ToString(CultureInfo.InvariantCulture)}[/].");
        }

        try
        {
            Store.Save(day, current.RemoveAt(number));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Hint($"{Name}: could not save the day. {Markup.Escape(ex.Message)}");
        }

        return CommandResult.None;
    }

    // Marks checkpoint <number> on the day being viewed as booked or unbooked. Nothing is written if
    // the number is not a plain number, there is no such entry, it is not a checkpoint, the day's
    // file cannot be read, or the checkpoint already has that state (so a repeat is a no-op).
    private CommandResult SetBooked(string name, string argument, bool booked)
    {
        string usage = $"Use [bold]{name} <number>[/].";
        if (argument.Length == 0)
        {
            return Hint($"{name}: say which checkpoint to mark. {usage}");
        }

        if (!int.TryParse(argument, NumberStyles.None, CultureInfo.InvariantCulture, out int number))
        {
            return Hint($"{name}: [bold]{Markup.Escape(argument)}[/] is not an entry number. {usage}");
        }

        DateOnly day = dayView.Current;
        DayRecord current;
        try
        {
            current = Store.Load(day);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return Hint($"{name}: {Markup.Escape(ex.Message)} Nothing was changed.");
        }

        string numberText = number.ToString(CultureInfo.InvariantCulture);
        if (number < 1 || number > current.Entries.Count)
        {
            return Hint($"{name}: there is no entry [bold]{numberText}[/].");
        }

        DayEntry entry = current.Entries[number - 1];
        if (entry.Kind != EntryKind.Checkpoint)
        {
            return Hint($"{name}: entry [bold]{numberText}[/] is not a checkpoint.");
        }

        if (entry.Booked == booked)
        {
            return CommandResult.None;
        }

        try
        {
            Store.Save(day, current.SetBooked(number, booked));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Hint($"{name}: could not save the day. {Markup.Escape(ex.Message)}");
        }

        return CommandResult.None;
    }

    // Changes the time of entry <number> on the day being viewed, keeping its kind. Nothing is
    // written if the argument is not a number and a time, there is no such entry, or the day's file
    // cannot be read.
    private CommandResult Change(string argument)
    {
        const string Name = "change";
        string usage = $"Use [bold]change <number> <{TimeInput.Format}>[/].";
        string[] parts = argument.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
        {
            return Hint($"{Name}: say which entry and its new time. {usage}");
        }

        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int number))
        {
            return Hint($"{Name}: [bold]{Markup.Escape(parts[0])}[/] is not an entry number. {usage}");
        }

        if (!TimeInput.TryParse(parts[1], out TimeOnly at))
        {
            return Hint($"{Name}: [bold]{Markup.Escape(parts[1])}[/] is not a time. " +
                $"Use [bold]{TimeInput.Format}[/], e.g. [bold]8:15[/].");
        }

        DateOnly day = dayView.Current;
        DayRecord current;
        try
        {
            current = Store.Load(day);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return Hint($"{Name}: {Markup.Escape(ex.Message)} Nothing was changed.");
        }

        if (number < 1 || number > current.Entries.Count)
        {
            return Hint($"{Name}: there is no entry [bold]{number.ToString(CultureInfo.InvariantCulture)}[/].");
        }

        try
        {
            Store.Save(day, current.ChangeTime(number, at));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Hint($"{Name}: could not save the day. {Markup.Escape(ex.Message)}");
        }

        return CommandResult.None;
    }

    // Adds an entry to the day being viewed, after any earlier ones. Nothing is written if the time
    // is invalid or the day's file cannot be read.
    private CommandResult Book(string name, string argument, EntryKind kind)
    {
        TimeOnly at;
        if (argument.Length == 0)
        {
            at = Now();
        }
        else if (!TimeInput.TryParse(argument, out at))
        {
            return NotATime(name, argument);
        }

        return Save(name, new DayEntry(kind, at));
    }

    // Books a named checkpoint: [H:mm] [#<project>] <name>. Only a token right after the optional time
    // that starts with '#' is a project number; it is looked up in the quicklist and the project's
    // work item is stored. Nothing is written if any part is invalid, and projects.json is read only
    // when a project number is given.
    private CommandResult Checkpoint(string argument)
    {
        const string Name = "checkpoint";
        string usage = $"Use [bold]{Markup.Escape($"checkpoint [{TimeInput.Format}] [#project] <name>")}[/], " +
            "where #project is the project's number on the quicklist.";

        string rest = argument;
        TimeOnly at = Now();
        (string first, string afterFirst) = SplitToken(rest);
        if (TimeInput.TryParse(first, out TimeOnly given))
        {
            at = given;
            rest = afterFirst;
        }
        else if (first.Contains(':') && first.All(c => c == ':' || char.IsAsciiDigit(c)))
        {
            return NotATime(Name, first);
        }

        int? projectNumber = null;
        (string token, string afterToken) = SplitToken(rest);
        if (token.StartsWith('#'))
        {
            rest = afterToken;
            string digits = token[1..];
            if (digits.Length == 0 || !digits.All(char.IsAsciiDigit))
            {
                return Hint($"{Name}: [bold]{Markup.Escape(token)}[/] is not a project number. {usage}");
            }

            // Too many digits for an int can never be a quicklist position: 0 stands for "no such project".
            projectNumber = int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) ? parsed : 0;
        }

        string checkpoint = rest.Trim();
        if (checkpoint.Length == 0)
        {
            return Hint($"{Name}: say the checkpoint's name. {usage}");
        }

        if (checkpoint.Length > ProjectList.MaxTitleLength)
        {
            return Hint($"{Name}: the name is longer than {ProjectList.MaxTitleLength.ToString(CultureInfo.InvariantCulture)} characters.");
        }

        if (ProjectList.HasControlCharacters(checkpoint))
        {
            return Hint($"{Name}: the name must not contain control characters. {RetypeHiddenCharacters}");
        }

        if (ProjectList.HasInvisibleOrBrokenCharacters(checkpoint))
        {
            return Hint($"{Name}: the name must not contain invisible formatting or broken characters. {RetypeHiddenCharacters}");
        }

        int? workItem = null;
        if (projectNumber is { } number)
        {
            ProjectList projects;
            try
            {
                projects = projectStore.Load();
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                return Hint($"{Name}: {UnreadableProjects(ex)} Nothing was booked. {UnreadableProjectsNextStep}");
            }

            if (number < 1 || number > projects.Projects.Count)
            {
                return Hint($"{Name}: there is no project [bold]{Markup.Escape(token)}[/] on the quicklist.");
            }

            workItem = projects.Projects[number - 1].WorkItem;
        }

        return Save(Name, new DayEntry(EntryKind.Checkpoint, at, checkpoint, workItem));
    }

    // Splits off the first space- or tab-separated token. The rest keeps its inner spacing.
    private static (string Token, string Remainder) SplitToken(string text)
    {
        string trimmed = text.TrimStart(' ', '\t');
        int split = trimmed.IndexOfAny([' ', '\t']);
        return split < 0 ? (trimmed, string.Empty) : (trimmed[..split], trimmed[(split + 1)..]);
    }

    // The current local minute, seconds dropped.
    private TimeOnly Now()
    {
        TimeOnly now = TimeOnly.FromDateTime(time.GetLocalNow().DateTime);
        return new TimeOnly(now.Hour, now.Minute);
    }

    private static CommandResult NotATime(string name, string text) =>
        Hint($"{name}: [bold]{Markup.Escape(text)}[/] is not a time. " +
            $"Use [bold]{TimeInput.Format}[/], e.g. [bold]8:15[/].");

    // Adds entry to the day being viewed. Nothing is written if the day's file cannot be read.
    private CommandResult Save(string name, DayEntry entry)
    {
        DateOnly day = dayView.Current;
        DayRecord current;
        try
        {
            current = Store.Load(day);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return Hint($"{name}: {Markup.Escape(ex.Message)} Nothing was booked.");
        }

        if (current.Absence is { } absence)
        {
            return Hint($"{name}: this day is marked as absence ({DayView.AbsenceLabel(absence)}). " +
                "Use [bold]absence clear[/] first. Nothing was booked.");
        }

        DayRecord updated = current.Add(entry);

        try
        {
            Store.Save(day, updated);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Hint($"{name}: could not save the day. {Markup.Escape(ex.Message)}");
        }

        return CommandResult.None;
    }

    private static Func<string, CommandResult> NoArguments(string name, Func<CommandResult> handler) =>
        argument => argument.Length == 0
            ? handler()
            : Hint($"[bold]{Markup.Escape(name)}[/] takes no arguments.");

    public int Run(string[] args)
    {
        // First of all: --version prints and exits without drawing or touching the data folder.
        if (args.Contains("--version", StringComparer.Ordinal))
        {
            WriteVersion(console, AppVersion.Current);
            return 0;
        }

        // Before anything is drawn, read or written: never fall back to the working directory.
        if (!DayStore.IsValidFolder(Store.Folder))
        {
            string reason;
            if (!string.IsNullOrWhiteSpace(Store.Folder) && DayStore.HasEdgeWhitespace(Store.Folder))
            {
                string shown = Markup.Escape(DataFolder.ForDisplay(Store.Folder));
                reason = folderFromEnvironment
                    ? $"the data folder [bold]\"{shown}\"[/] from {DataFolder.VariableName} starts or ends with whitespace"
                    : $"the data folder [bold]{shown}[/] starts or ends with whitespace";
            }
            else if (folderFromEnvironment)
            {
                reason = $"the data folder [bold]\"{Markup.Escape(DataFolder.ForDisplay(Store.Folder))}\"[/] from {DataFolder.VariableName} is not an absolute path";
            }
            else if (string.IsNullOrWhiteSpace(Store.Folder))
            {
                reason = "the app-data folder could not be found (the path is empty)";
            }
            else
            {
                reason = $"the data folder [bold]{Markup.Escape(DataFolder.ForDisplay(Store.Folder))}[/] is not an absolute path";
            }

            console.MarkupLine($"[red]Times cannot start: {reason}. Nothing was saved.[/]");
            return 2;
        }

        // The outcome of an update a previous run started, then the one start-up check, outside the loop.
        string? pending = UpdateApplier.ReadOutcome(Store.Folder, updates?.RunningVersion ?? AppVersion.Current);
        if (updates is not null)
        {
            startupCheck = updates.StartBackground(UpdateMode(), UpdateChannel(), Store.Folder);
        }

        // Mouse reporting is on only while the loop runs: off again on every exit, even an exception.
        mouse?.Enable();
        try
        {
            return Loop(pending is null ? null : new Markup(pending));
        }
        finally
        {
            mouse?.Disable();
        }
    }

    private int Loop(IRenderable? startHint)
    {
        LineEditor editor = new LineEditor(console, registry);
        IRenderable? message = null;
        IRenderable? hint = startHint;
        bool hideQuicklist = false;

        while (true)
        {
            Draw(message, hint, hideQuicklist);

            int height = console.Profile.Height;
            int width = console.Profile.Width;
            // Inside the panel: border + 1 padding on the left, then the prompt. The cursor starts
            // right after the prompt, on the placeholder's first character, not after it.
            int column = 3 + Prompt.Length;
            int line = height - 1;
            console.Cursor.SetPosition(column, line);

            // The popup ends on the last hint row (just above the input box) and grows upwards over the others.
            PopupArea popup = new PopupArea(height - InputHeight, width, RestoreRow);
            string? input = editor.ReadLine(column, line, width - column - 2, popup, Placeholder, Scroll, ShowPasteCut);
            if (input is null)
            {
                return 0;
            }

            CommandResult result = Dispatch(input);
            if (result.Quit)
            {
                console.Clear(home: true);
                InstallStaged();
                return 0;
            }

            if (openSettings)
            {
                openSettings = false;
                if (!RunSettings(editor))
                {
                    return 0;
                }
            }

            message = result.Message;
            hint = result.Hint;
            hideQuicklist = result.HideQuicklist && result.Message is not null;
        }
    }

    private CommandResult Dispatch(string input)
    {
        string text = input.Trim();
        if (text.Length == 0)
        {
            return CommandResult.None;
        }

        int split = text.IndexOfAny([' ', '\t']);
        string token = split < 0 ? text : text[..split];
        string argument = split < 0 ? string.Empty : text[(split + 1)..].Trim();

        // One leading '/' is still accepted (old habit); only one, so "//help" stays unknown.
        string name = token.StartsWith('/') ? token[1..] : token;
        if (name.Length == 0 || !registry.TryGet(name, out Command? command))
        {
            return Hint($"Unknown command [bold]{Markup.Escape(token)}[/]. Type [bold]help[/] to see all commands.");
        }

        try
        {
            // The data-folder footer belongs to the app, not to the shared registry, so it is passed in here.
            if (string.Equals(command.Name, "help", StringComparison.OrdinalIgnoreCase))
            {
                return new CommandResult(registry.RenderHelp(RenderHelpFooter()), hideQuicklist: true);
            }

            return command.Handler(argument);
        }
        catch (Exception ex)
        {
            return Hint($"{Markup.Escape(command.Name)} failed: {Markup.Escape(ex.Message)}");
        }
    }

    private static CommandResult Hint(string markup) => CommandResult.Error(markup);

    // The lines under the help table: the day view codes, the running version, then the data folder
    // (always last).
    private IRenderable RenderHelpFooter() =>
        new Rows(
            new Markup(DayViewCodes),
            new Markup($"[grey]Version:[/] {Markup.Escape(AppVersion.Current)}"),
            RenderDataFolder());

    private const string DayViewCodes =
        "[grey]Day view codes:[/] [steelblue1]AR[/] arrival · [steelblue1]LE[/] leaving · [steelblue1]BS[/] break start · "
        + "[steelblue1]BE[/] break end · [steelblue1]CP[/] checkpoint · [grey]✓[/] booked";

    // The line under the help table, so the user can always tell where the day files are.
    private Markup RenderDataFolder()
    {
        string source = folderFromEnvironment ? $" [grey](from {DataFolder.VariableName})[/]" : string.Empty;
        return new Markup($"[grey]Data folder:[/] {Markup.Escape(DataFolder.ForDisplay(Store.Folder))}{source}");
    }

    // While help or projects output is shown (hideQuicklist), the quicklist panel is left out so the
    // output fits; projects shows the full list anyway. It comes back with the next command.
    // The top area is rendered once here and starts unscrolled.
    private void Draw(IRenderable? message, IRenderable? hint, bool hideQuicklist)
    {
        List<IRenderable> parts = [];
        if (!hideQuicklist)
        {
            parts.Add(RenderQuicklist());
        }

        parts.Add(RenderDay());
        if (message is not null)
        {
            parts.Add(Text.Empty);
            parts.Add(message);
        }

        SetTop(new Rows(parts));
        TakeStartupStaged();
        frameHint = StartupHint(hint) ?? Text.Empty;
        offset = 0;
        DrawFull();
    }

    private void SetTop(IRenderable top) =>
        topLines = Segment.SplitLines(top.Render(RenderOptions.Create(console, console.Profile.Capabilities), console.Profile.Width));

    // Clears the screen and writes the whole layout for the current top lines, offset and hint.
    private void DrawFull()
    {
        frame = RenderFrame();
        console.Clear(home: true);
        console.Write(new SegmentsRenderable(frame.SelectMany(l => l.Append(Segment.LineBreak)).SkipLast(1)));
    }

    private int TopHeight => console.Profile.Height - HintHeight - InputHeight;

    private int HintHeight => HintRows().Count;

    // The current hint wrapped at the terminal width, one entry per hint row: at least 1 row, at most
    // MaxHintRows, and more than 1 only while the top area keeps MinTopRowsForWrap rows. A hint that
    // needs more rows than it gets ends its last shown row in a grey ….
    private List<SegmentLine> HintRows()
    {
        int width = console.Profile.Width;
        int height = console.Profile.Height;
        if (ReferenceEquals(hintRowsFor, frameHint) && hintRowsWidth == width && hintRowsHeight == height)
        {
            return hintRows;
        }

        List<SegmentLine> lines = Segment.SplitLines(frameHint.Render(RenderOptions.Create(console, console.Profile.Capabilities), width));
        int rows = Math.Min(Math.Min(lines.Count, MaxHintRows), Math.Max(1, height - InputHeight - MinTopRowsForWrap));
        rows = Math.Max(1, rows);
        List<SegmentLine> shown = lines.Take(rows).ToList();
        while (shown.Count < rows)
        {
            shown.Add(new SegmentLine());
        }

        if (lines.Count > rows)
        {
            shown[^1] = WithEllipsis(shown[^1], width);
        }

        hintRowsFor = frameHint;
        hintRowsWidth = width;
        hintRowsHeight = height;
        hintRows = shown;
        return shown;
    }

    // The line cut to leave room for one cell, without trailing whitespace, followed by a grey ….
    private static SegmentLine WithEllipsis(SegmentLine line, int width)
    {
        List<Segment> segments = Segment.Truncate(line, Math.Max(0, width - 1)).ToList();
        while (segments.Count > 0)
        {
            Segment last = segments[^1];
            string trimmed = last.Text.TrimEnd();
            if (trimmed.Length == last.Text.Length)
            {
                break;
            }

            segments.RemoveAt(segments.Count - 1);
            if (trimmed.Length > 0)
            {
                segments.Add(new Segment(trimmed, last.Style));
                break;
            }
        }

        SegmentLine result = new SegmentLine();
        result.AddRange(segments);
        result.Add(new Segment("\u2026", new Style(Color.Grey)));
        return result;
    }

    // Lays out the whole screen for the current top lines, offset and hint, one entry per row.
    private List<SegmentLine> RenderFrame()
    {
        Panel input = new Panel(new Markup($"[steelblue1]{Markup.Escape(Prompt)}[/][grey]{Markup.Escape(framePlaceholder)}[/]"))
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.SteelBlue1)
            .Expand();

        IRenderable content = new SegmentsRenderable(topLines.SelectMany(l => l.Append(Segment.LineBreak)).SkipLast(1));
        List<SegmentLine> hintLines = HintRows();
        IRenderable hint = new SegmentsRenderable(hintLines.SelectMany(l => l.Append(Segment.LineBreak)).SkipLast(1));
        Layout layout = new Layout("Root").SplitRows(
            new Layout("Day", TopArea.Fit(content, TopHeight, offset)),
            new Layout("Hint", hint).Size(hintLines.Count),
            new Layout("Input", input).Size(InputHeight));

        return Segment.SplitLines(layout.GetSegments(console));
    }

    // Moves the top area one page or one wheel notch (WheelLines) up (-1) or down (+1) and rewrites
    // only its rows in place. Returns false, writing nothing, if the offset stays the same (at the
    // top, at the bottom, or it fits).
    private bool Scroll(int direction, ScrollStep step)
    {
        int topHeight = TopHeight;
        int lines = step == ScrollStep.Wheel ? WheelLines : TopArea.PageSize(topHeight);
        int next = Math.Clamp(offset + (direction * lines), 0, TopArea.MaxOffset(topLines.Count, topHeight));
        if (next == offset)
        {
            return false;
        }

        offset = next;
        frame = RenderFrame();
        for (int row = 1; row <= topHeight; row++)
        {
            RestoreRow(row);
        }

        return true;
    }

    // The settings panel, modal in place of the quicklist and day view, until Esc (true) or the end of
    // the input (false). Opening and closing it reads settings.json but never writes it. Every change
    // reloads the file first and saves at once; a file that cannot be read is never overwritten.
    private bool RunSettings(LineEditor editor)
    {
        int selected = 0;
        string? unreadable = LoadSettings(out SettingValues values);
        offset = 0;
        ShowSettings(values, selected, unreadable, null, full: true);
        while (true)
        {
            ConsoleKeyInfo? read = editor.ReadKey();
            if (read is null)
            {
                return false;
            }

            ConsoleKeyInfo key = read.Value;
            if (key.Key == ConsoleKey.Escape)
            {
                return true;
            }

            int count = settings.Settings.Count;
            if (count == 0)
            {
                continue;
            }

            bool control = (key.Modifiers & ConsoleModifiers.Control) != 0;
            int moved = key.Key switch
            {
                ConsoleKey.UpArrow when !control => Math.Max(0, selected - 1),
                ConsoleKey.DownArrow when !control => Math.Min(count - 1, selected + 1),
                _ => selected,
            };

            if (moved != selected)
            {
                selected = moved;
                ShowSettings(values, selected, unreadable, null, full: false);
                continue;
            }

            if (key.Key is not (ConsoleKey.Enter or ConsoleKey.Spacebar) && key.KeyChar != ' ')
            {
                continue;
            }

            Setting setting = settings.Settings[selected];
            IRenderable? hint;
            if (setting.Kind == SettingKind.Number)
            {
                unreadable = LoadSettings(out values);
                if (unreadable is not null)
                {
                    hint = NotSaved();
                }
                else
                {
                    string? text = EditNumber(editor, setting, values, selected, out bool cancelled);
                    if (text is null)
                    {
                        return false;
                    }

                    if (cancelled)
                    {
                        hint = null;
                    }
                    else if (TryParseWhole(text, out int number) && setting.Accepts(number))
                    {
                        hint = ChangeSetting(setting, _ => number, ref values, ref unreadable);
                    }
                    else
                    {
                        hint = Hint($"settings: [bold]{Markup.Escape(setting.Name)}[/] takes a whole number from " +
                            $"{setting.Min.ToString(CultureInfo.InvariantCulture)} to {setting.Max.ToString(CultureInfo.InvariantCulture)}.").Hint;
                    }
                }
            }
            else
            {
                hint = ChangeSetting(setting, current => NextValue(setting, current.Get(setting.Name)), ref values, ref unreadable);
            }

            ShowSettings(values, selected, unreadable, hint, full: false);
        }
    }

    // Reloads settings.json, sets the setting to next(fresh values) and saves at once. Returns the
    // hint to show (null on success). A file that cannot be read or a failed save leaves values
    // as the file has them and writes nothing.
    private IRenderable? ChangeSetting(Setting setting, Func<SettingValues, object> next, ref SettingValues values, ref string? unreadable)
    {
        unreadable = LoadSettings(out values);
        if (unreadable is not null)
        {
            return NotSaved();
        }

        SettingValues updated = values.With(setting.Name, next(values));
        try
        {
            settingsStore.Save(updated);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return Hint($"settings: could not save the settings. {Markup.Escape(ex.Message)}").Hint;
        }

        values = updated;
        return null;
    }

    // The panel already shows why the file cannot be read, so the hint stays short enough to fit.
    private static IRenderable? NotSaved() =>
        Hint("settings: the settings cannot be read (see above). Nothing was saved.").Hint;

    // On/off flips; a choice moves to the next option, wrapping.
    private static object NextValue(Setting setting, object current) => setting.Kind switch
    {
        SettingKind.OnOff => !(bool)current,
        SettingKind.Choice => setting.Options[(IndexOf(setting.Options, (string)current) + 1) % setting.Options.Count],
        _ => current,
    };

    private static int IndexOf(IReadOnlyList<string> options, string option)
    {
        for (int i = 0; i < options.Count; i++)
        {
            if (string.Equals(options[i], option, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    // Reads a number for setting in the input box, with a grey placeholder naming the range. Returns
    // null if the input ended; cancelled is set when Esc ended the edit.
    private string? EditNumber(LineEditor editor, Setting setting, SettingValues values, int selected, out bool cancelled)
    {
        framePlaceholder = $"{setting.Name}: a whole number from {setting.Min.ToString(CultureInfo.InvariantCulture)} " +
            $"to {setting.Max.ToString(CultureInfo.InvariantCulture)} · Esc cancels";
        try
        {
            ShowSettings(values, selected, null, null, full: false);
            int width = console.Profile.Width;
            return editor.ReadValue(InputColumn, InputLine, width - InputColumn - 2, framePlaceholder, out cancelled, ShowPasteCut);
        }
        finally
        {
            framePlaceholder = Placeholder;
        }
    }

    // A whole number: optional leading '-', invariant ASCII digits, surrounding whitespace trimmed.
    internal static bool TryParseWhole(string text, out int number)
    {
        number = 0;
        string trimmed = text.Trim();
        int start = trimmed.StartsWith('-') ? 1 : 0;
        if (trimmed.Length == start || !trimmed[start..].All(char.IsAsciiDigit))
        {
            return false;
        }

        return int.TryParse(trimmed, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out number);
    }

    // Loads the settings. Returns null, or the reason the file cannot be read (values are then the defaults).
    private string? LoadSettings(out SettingValues values)
    {
        try
        {
            values = settingsStore.Load(settings);
            return null;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            values = SettingValues.Defaults(settings);
            return ex.Message;
        }
    }

    // Draws the settings panel in the top area, scrolled so the selected row is in view, and the hint.
    // full clears the screen first; otherwise every row is rewritten in place. The cursor ends in the input box.
    private void ShowSettings(SettingValues values, int selected, string? unreadable, IRenderable? hint, bool full)
    {
        // The hint first: its height sets the top area's rows, which the offset must keep the selected row in.
        frameHint = hint ?? Text.Empty;
        SetTop(SettingsPanel.Render(settings, values, selected, unreadable));
        offset = settings.Settings.Count == 0
            ? 0
            : TopArea.OffsetShowing(SettingsPanel.LineOf(selected, unreadable is not null), topLines.Count, TopHeight, offset);
        if (full)
        {
            DrawFull();
        }
        else
        {
            frame = RenderFrame();
            for (int row = 1; row <= frame.Count; row++)
            {
                RestoreRow(row);
            }
        }

        console.Cursor.SetPosition(InputColumn, InputLine);
    }

    // Inside the input panel: border + 1 padding on the left, then the prompt.
    private static int InputColumn => 3 + Prompt.Length;

    private int InputLine => console.Profile.Height - 1;

    // Reloads projects.json on every redraw, so a project added a moment ago shows up. Never writes.
    private IRenderable RenderQuicklist()
    {
        try
        {
            return QuicklistPanel.Render(projectStore.Load());
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return QuicklistPanel.RenderUnreadable(
                $"The project list cannot be read: {Markup.Escape(ex.Message)}",
                Markup.Escape(UnreadableProjectsNextStep));
        }
    }

    // The daily-target setting, read from settings.json on every redraw. A missing or broken file, or
    // a registry without the setting, gives the default (8:00). Reading never writes the file.
    private TimeSpan DailyTarget()
    {
        int minutes = SettingsRegistry.DailyTargetDefault;
        if (settings.TryGet(SettingsRegistry.DailyTargetName, out Setting setting) && setting.Kind == SettingKind.Number
            && LoadSettings(out SettingValues values) is null)
        {
            int value = values.Number(SettingsRegistry.DailyTargetName);
            if (value >= 0 && value <= SettingsRegistry.DailyTargetMax)
            {
                minutes = value;
            }
        }

        return TimeSpan.FromMinutes(minutes);
    }

    private IRenderable RenderDay()
    {
        try
        {
            DayRecord day = Store.Load(dayView.Current);
            TimeSpan target = DailyTarget();
            return dayView.Render(day, target, FlexitimeBalance.Compute(Store, dayView.Current, target));
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return dayView.RenderUnreadable(Markup.Escape(ex.Message));
        }
    }

    // Called by the line editor when a paste was cut: shows the hint on the hint rows in place, without
    // a clear. If the hint keeps the same number of rows, only those are rewritten; otherwise the top
    // area changes size too, so every row is rewritten except the input line, which holds the typed
    // text the editor redraws next. The next full redraw shows the hint it is given, as always.
    private void ShowPasteCut()
    {
        int before = HintHeight;
        frameHint = new Markup(PasteCutHint);
        int after = HintHeight;
        int bottom = console.Profile.Height - InputHeight;
        if (after == before)
        {
            frame = RenderFrame();
            for (int row = bottom - after + 1; row <= bottom; row++)
            {
                RestoreRow(row);
            }

            return;
        }

        offset = Math.Min(offset, TopArea.MaxOffset(topLines.Count, TopHeight));
        frame = RenderFrame();
        for (int row = 1; row <= frame.Count; row++)
        {
            if (row != InputLine)
            {
                RestoreRow(row);
            }
        }

        console.Cursor.SetPosition(InputColumn, InputLine);
    }

    // Redraws one screen row (1-based) with what the last frame had there.
    private void RestoreRow(int row)
    {
        if (row < 1 || row > frame.Count)
        {
            return;
        }

        console.Cursor.SetPosition(1, row);
        console.Write(new SegmentsRenderable(frame[row - 1]));
    }

    /// <summary>Writes already rendered segments as they are.</summary>
    private sealed class SegmentsRenderable : IRenderable
    {
        private readonly List<Segment> segments;

        public SegmentsRenderable(IEnumerable<Segment> segments)
        {
            this.segments = segments.ToList();
        }

        public Measurement Measure(RenderOptions options, int maxWidth) => new(maxWidth, maxWidth);

        public IEnumerable<Segment> Render(RenderOptions options, int maxWidth) => segments;
    }
}
