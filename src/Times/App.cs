using System.Globalization;
using System.Text;
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

    /// <summary>The narrowest terminal the layout is drawn for; below it only <see cref="TooSmallText"/> shows.</summary>
    public const int MinWidth = 20;

    /// <summary>The shortest terminal the layout is drawn for; below it only <see cref="TooSmallText"/> shows.</summary>
    public const int MinHeight = 8;

    /// <summary>The one grey line a window below <see cref="MinWidth"/> × <see cref="MinHeight"/> shows, cut to the width.</summary>
    public const string TooSmallText = "Window too small";
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
    private readonly ITokenStore tokenStore;
    private readonly Func<DevOpsCredential, IDevOpsClient>? devOpsClient;
    private readonly IBackgroundEvents? events;
    private readonly bool liveClock;
    private readonly bool watchResize;
    private readonly InstanceLock instanceLock;

    // While the settings panel is open: what it shows, so a resize can draw it again (null otherwise).
    private SettingsView? shownSettings;

    // The day the last redraw showed and the stamp of its file then (null: it could not be read). A command
    // that writes the shown day refuses when the file's stamp is no longer this one.
    private DateOnly? shownDay;
    private DayStamp? shownStamp;

    // Which end-of-day alerts were already given today; checked on every live clock tick.
    private readonly EndOfDayAlerts alerts = new EndOfDayAlerts();

    // What the last full redraw showed above the day view's hint rows, so a background event can
    // rebuild the same top area in place (the hint itself stays in frameHint).
    private IRenderable? shownMessage;
    private bool shownHideQuicklist;

    // Set by devops connect to the validated organisation; the loop then asks for the token.
    private string? connectOrganization;

    // Set by import when imported days differ from existing ones; the loop then asks about each of them.
    private ImportRun? pendingImport;

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
    /// <param name="tokenStore">Holds the Azure DevOps token, or <c>null</c> for <see cref="UnsupportedTokenStore"/> (no OS store at all).</param>
    /// <param name="devOpsClient">Makes the Azure DevOps client for a credential, or <c>null</c> for none (sync cannot send).</param>
    /// <param name="events">Background events applied between keys at the prompt, or <c>null</c> for none.</param>
    /// <param name="liveClock">True to run a <see cref="LiveClock"/> while the loop runs (only with <paramref name="events"/>).</param>
    /// <param name="instanceLock">Keeps a second Times off the data folder while the loop runs, or <c>null</c> for one on the store's folder.</param>
    /// <param name="watchResize">
    /// True to poll the terminal size while waiting for a key where no background events are applied (the
    /// settings panel and the value prompts), so a resize redraws there without a key press.
    /// </param>
    public App(IAnsiConsole console, TimeProvider time, DayStore store, CommandRegistry? registry = null,
        bool folderFromEnvironment = false, MouseReporting? mouse = null, SettingsRegistry? settings = null,
        UpdateService? updates = null, ITokenStore? tokenStore = null, Func<DevOpsCredential, IDevOpsClient>? devOpsClient = null,
        IBackgroundEvents? events = null, bool liveClock = false, InstanceLock? instanceLock = null, bool watchResize = false)
    {
        this.watchResize = watchResize;
        this.instanceLock = instanceLock ?? new InstanceLock(store.Folder);
        this.events = events;
        this.liveClock = liveClock;
        this.tokenStore = tokenStore ?? new UnsupportedTokenStore();
        this.devOpsClient = devOpsClient;
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
        RegisterDevOps();
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
        registry.Register("arrive", "a", $"Book your arrival: now, or at an optional {TimeInput.Format}, -10m or +5m", argument =>
            Book("arrive", argument, EntryKind.Arrival));
        registry.Register("leave", "l", $"Book your leaving: now, or at an optional {TimeInput.Format}, -10m or +5m", argument =>
            Book("leave", argument, EntryKind.Leaving));
        registry.Register("break", "b", $"Book the start of a break: now, or at an optional {TimeInput.Format}, -10m or +5m", argument =>
            Book("break", argument, EntryKind.BreakStart));
        registry.Register("breakend", "be", $"Book the end of a break: now, or at an optional {TimeInput.Format}, -10m or +5m", argument =>
            Book("breakend", argument, EntryKind.BreakEnd));
        registry.Register("checkpoint", "cp", $"Book a checkpoint: [{TimeInput.ShortForms}] [#project] <name>", Checkpoint);
        registry.Register("remove", "r", "Remove an entry from this day by its number", Remove);
        registry.Register("change", "c", $"Change the time of an entry on this day: <number> <{TimeInput.ShortForms}>", Change);
        registry.Register("booked", "bk", "Mark a checkpoint on this day as booked in Azure DevOps by its number", argument =>
            SetBooked("booked", argument, true));
        registry.Register("unbooked", "ub", "Mark a checkpoint on this day as not yet booked by its number", argument =>
            SetBooked("unbooked", argument, false));
        registry.Register("summary", "s", "Show the time taken per work item from this day's checkpoints", NoArguments("summary", Summary));
        registry.Register("absence", "ab", "Mark this day as vacation, sick or holiday, or clear it", Absence);
        registry.Register("correction", "co", "Correct the balance on this day: +H:mm|-H:mm <reason>, or remove <number>", Correction);
        registry.Register("report", "rp", "Show the hours per project over a date range: <from> <to>", Report);
        registry.Register("export", "ex", "Save the project report for a date range as CSV: <from> <to> <folder>", Export);
        registry.Register("find", "f", "Find days whose checkpoints or projects match: <text>", Find);
        registry.Register("statistics", "st", "Show average arrival, leaving and work time per weekday: <from> <to>", Statistics);
        registry.Register("backup", "bu", "Zip your day files and projects into a folder: <folder>", Backup);
        registry.Register("import", "im", "Import day files from the old tool: <folder>", Import);
        registry.Register("vacation", "va", "Show vacation days taken and left this year", NoArguments("vacation", Vacation));
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
        DayStamp stamp;
        try
        {
            (current, stamp) = LoadForWrite(day);
        }
        catch (DayFileChangedException)
        {
            return ChangedOnDisk(Name, day);
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
            Store.Save(day, current.WithAbsence(wanted), stamp);
        }
        catch (DayFileChangedException)
        {
            return ChangedOnDisk(Name, day);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Hint($"{Name}: could not save the day. {Markup.Escape(ex.Message)}");
        }

        return CommandResult.None;
    }

    private const string CorrectionUsage = "Use [bold]correction +H:mm|-H:mm <reason>[/] or [bold]correction remove <number>[/].";

    // Books a balance correction on the day being viewed (+H:mm|-H:mm <reason>), or removes one (remove <number>).
    // Works on any day, also one with entries or an absence. Nothing is written if any part is invalid, the day's
    // file cannot be read, or it changed on disk since it was shown.
    private CommandResult Correction(string argument)
    {
        const string Name = "correction";
        (string first, string rest) = SplitToken(argument);
        if (first.Length == 0)
        {
            return Hint($"{Name}: say the amount and the reason. {CorrectionUsage}");
        }

        if (string.Equals(first, "remove", StringComparison.Ordinal))
        {
            return RemoveCorrection(rest.Trim());
        }

        if (!BalanceCorrection.TryParseAmount(first, out int minutes))
        {
            return Hint($"{Name}: [bold]{Markup.Escape(first)}[/] is not an amount. {CorrectionUsage}");
        }

        string reason = rest.Trim();
        if (reason.Length == 0)
        {
            return Hint($"{Name}: say the reason. {CorrectionUsage}");
        }

        if (reason.Length > ProjectList.MaxTitleLength)
        {
            return Hint($"{Name}: the reason is longer than {ProjectList.MaxTitleLength.ToString(CultureInfo.InvariantCulture)} characters.");
        }

        if (ProjectList.HasControlCharacters(reason))
        {
            return Hint($"{Name}: the reason must not contain control characters. {RetypeHiddenCharacters}");
        }

        if (ProjectList.HasInvisibleOrBrokenCharacters(reason))
        {
            return Hint($"{Name}: the reason must not contain invisible formatting or broken characters. {RetypeHiddenCharacters}");
        }

        BalanceCorrection correction = new BalanceCorrection(minutes, reason);
        return ChangeCorrections(Name, "Nothing was booked.", current => (current.AddCorrection(correction), null));
    }

    // Removes correction <number> from the day being viewed; entries are never touched.
    private CommandResult RemoveCorrection(string text)
    {
        const string Name = "correction";
        if (text.Length == 0)
        {
            return Hint($"{Name}: say which correction to remove. {CorrectionUsage}");
        }

        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int number))
        {
            return Hint($"{Name}: [bold]{Markup.Escape(text)}[/] is not a correction number. {CorrectionUsage}");
        }

        return ChangeCorrections(Name, "Nothing was removed.", current =>
            number < 1 || number > current.Corrections.Count
                ? (null, Hint($"{Name}: there is no correction [bold]{number.ToString(CultureInfo.InvariantCulture)}[/]."))
                : (current.RemoveCorrection(number), null));
    }

    // Loads the shown day for writing, applies change (which gives the new record, or a hint to show instead) and
    // saves it with the stamp it was loaded with.
    private CommandResult ChangeCorrections(
        string name, string nothingDone, Func<DayRecord, (DayRecord? Updated, CommandResult? Refused)> change)
    {
        DateOnly day = dayView.Current;
        DayRecord current;
        DayStamp stamp;
        try
        {
            (current, stamp) = LoadForWrite(day);
        }
        catch (DayFileChangedException)
        {
            return ChangedOnDisk(name, day);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return Hint($"{name}: {Markup.Escape(ex.Message)} {nothingDone}");
        }

        (DayRecord? updated, CommandResult? refused) = change(current);
        if (updated is null)
        {
            return refused ?? CommandResult.None;
        }

        try
        {
            Store.Save(day, updated, stamp);
        }
        catch (DayFileChangedException)
        {
            return ChangedOnDisk(name, day);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Hint($"{name}: could not save the day. {Markup.Escape(ex.Message)}");
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

        if (ParseRange(Name, Usage, parts[0], parts[1], out DateOnly from, out DateOnly to) is { } hint)
        {
            return hint;
        }

        int step = ReportRounding();
        if (CalculateReport(Name, from, to, step, out ProjectReport report) is { } failed)
        {
            return failed;
        }

        return new CommandResult(ReportView(from, to, report, LoadProjectsOrNull(), step), hideQuicklist: true);
    }

    // Parses the two report dates; a red hint (prefixed with name) if either is not a date or from is after to.
    private static CommandResult? ParseRange(string name, string usage, string first, string last, out DateOnly from, out DateOnly to)
    {
        string[] parts = [first, last];
        DateOnly[] dates = new DateOnly[2];
        for (int i = 0; i < 2; i++)
        {
            if (!DateOnly.TryParseExact(parts[i], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out dates[i]))
            {
                from = default;
                to = default;
                return Hint($"{name}: [bold]{Markup.Escape(parts[i])}[/] is not a date. {usage}");
            }
        }

        from = dates[0];
        to = dates[1];
        if (from > to)
        {
            return Hint($"{name}: the first day {ReportDate(from)} is after the last day {ReportDate(to)}. {usage}");
        }

        return null;
    }

    // Calculates the report over the range; a red hint (prefixed with name) if the data folder cannot be listed.
    private CommandResult? CalculateReport(string name, DateOnly from, DateOnly to, int step, out ProjectReport report)
    {
        try
        {
            report = ProjectReport.Calculate(Store, from, to, DateOnly.FromDateTime(time.GetLocalNow().DateTime), Now(), step);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            report = new ProjectReport([], 0);
            return Hint($"{name}: the data folder cannot be listed: {Markup.Escape(ex.Message)}");
        }
    }

    // The report-rounding step in minutes, read from settings.json at each call. A missing or broken
    // file, a value that is not one of the options, or a registry without the setting gives 0 (off).
    // Reading never writes the file.
    private int ReportRounding() =>
        SettingsRegistry.ReportRoundingMinutes(ChoiceSetting(SettingsRegistry.ReportRoundingName, SettingsRegistry.ReportRoundingOff));

    // The suffix that says the times are rounded, or nothing for exact times. Fixed text, no escaping needed.
    internal static string RoundingSuffix(int step) =>
        step > 0 ? " · rounded to " + step.ToString(CultureInfo.InvariantCulture) + " min" : string.Empty;

    // Titles are optional: without a readable quicklist every row falls back to its names.
    private ProjectList? LoadProjectsOrNull()
    {
        try
        {
            return projectStore.Load();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // Writes the report over an inclusive date range as a CSV file into an existing folder the user
    // names. Never overwrites: the file is created exclusively, and a failed write leaves no file.
    // Reads the data folder only, and creates nothing but the one file in the target folder.
    private CommandResult Export(string argument)
    {
        const string Name = "export";
        const string Usage = "Use [bold]export <from> <to> <folder>[/] with dates as [bold]yyyy-MM-dd[/].";
        if (!SplitExportArgument(argument, out string first, out string last, out string typedFolder))
        {
            return Hint($"{Name}: say the first day, the last day and the folder. {Usage}");
        }

        if (ParseRange(Name, Usage, first, last, out DateOnly from, out DateOnly to) is { } hint)
        {
            return hint;
        }

        string folder = ExpandFolder(typedFolder, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        string shownFolder = Shown(folder);
        if (!Path.IsPathFullyQualified(folder))
        {
            return Hint($"{Name}: [bold]{shownFolder}[/] is not an absolute path.");
        }

        if (!Directory.Exists(folder))
        {
            return File.Exists(folder)
                ? Hint($"{Name}: [bold]{shownFolder}[/] is a file, not a folder.")
                : Hint($"{Name}: the folder [bold]{shownFolder}[/] does not exist. Nothing was created.");
        }

        int step = ReportRounding();
        if (CalculateReport(Name, from, to, step, out ProjectReport report) is { } failed)
        {
            return failed;
        }

        if (report.Rows.Count == 0)
        {
            List<IRenderable> empty = [new Markup($"[grey]No checkpoints between {ReportDate(from)} and {ReportDate(to)}. Nothing was exported.[/]")];
            AddSkipped(empty, report);
            return new CommandResult(empty.Count == 1 ? empty[0] : new Rows(empty));
        }

        string path = Path.GetFullPath(Path.Combine(folder, ReportCsv.FileName(from, to)));
        string shownPath = Shown(path);
        if (TargetExists(path))
        {
            return Hint($"{Name}: [bold]{shownPath}[/] already exists. Move or delete it first.");
        }

        byte[] bytes = ReportCsv.ToBytes(ReportCsv.Format(report, LoadProjectsOrNull()));
        bool created = false;
        try
        {
            // CreateNew is O_CREAT | O_EXCL: it never overwrites a file or follows a link at the name.
            using (FileStream stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true;
                stream.Write(bytes);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!created)
            {
                if (TargetExists(path))
                {
                    return Hint($"{Name}: [bold]{shownPath}[/] already exists. Move or delete it first.");
                }

                return Hint($"{Name}: could not write [bold]{shownPath}[/]. {Shown(ex.Message)}");
            }

            try
            {
                File.Delete(path);
            }
            catch (Exception deleteFailed) when (deleteFailed is IOException or UnauthorizedAccessException)
            {
                return Hint($"{Name}: could not write [bold]{shownPath}[/] and could not remove the partial file. {Shown(ex.Message)}");
            }

            return Hint($"{Name}: could not write [bold]{shownPath}[/]. {Shown(ex.Message)}");
        }

        string rows = report.Rows.Count == 1 ? "row" : "rows";
        List<IRenderable> parts =
        [
            new Markup($"Exported [steelblue1]{report.Rows.Count.ToString(CultureInfo.InvariantCulture)}[/] {rows} to [steelblue1]{shownPath}[/]{RoundingSuffix(step)}"),
        ];
        if (report.Rows.Any(r => r.Open))
        {
            parts.Add(new Markup($"[grey]{ExportOpenNote}[/]"));
        }

        AddSkipped(parts, report);
        return new CommandResult(parts.Count == 1 ? parts[0] : new Rows(parts));
    }

    // Zips every day file and projects.json into a new file in an existing folder outside the data
    // folder. Never overwrites: the file is created exclusively, and a failed write leaves no file.
    // Reads the data folder only, and creates nothing but the one file in the target folder.
    private CommandResult Backup(string argument)
    {
        const string Name = "backup";
        string typedFolder = argument.Trim();
        if (typedFolder.Length == 0)
        {
            return Hint($"{Name}: say the folder. Use [bold]backup <folder>[/].");
        }

        string folder = ExpandFolder(typedFolder, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        string shownFolder = Shown(folder);
        if (!Path.IsPathFullyQualified(folder))
        {
            return Hint($"{Name}: [bold]{shownFolder}[/] is not an absolute path.");
        }

        if (!Directory.Exists(folder))
        {
            return File.Exists(folder)
                ? Hint($"{Name}: [bold]{shownFolder}[/] is a file, not a folder.")
                : Hint($"{Name}: the folder [bold]{shownFolder}[/] does not exist. Nothing was created.");
        }

        if (DataBackup.IsInside(folder, Store.Folder))
        {
            return Hint($"{Name}: choose a folder outside the data folder.");
        }

        // The zip is built in memory first, so nothing is created when there is nothing to back up.
        BackupCounts counts;
        byte[] bytes;
        try
        {
            using MemoryStream buffer = new MemoryStream();
            counts = DataBackup.Write(Store, buffer);
            bytes = buffer.ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Hint($"{Name}: the data folder cannot be listed: {Shown(ex.Message)}");
        }

        if (counts.Written == 0)
        {
            List<IRenderable> empty = [new Markup("[grey]Nothing to back up. No file was written.[/]")];
            AddSkippedFiles(empty, counts.Skipped);
            return new CommandResult(empty.Count == 1 ? empty[0] : new Rows(empty));
        }

        // Not GetFullPath: the OS resolves a ".." the same way IsInside did.
        string path = Path.Combine(folder, DataBackup.FileName(time.GetLocalNow().DateTime));
        string shownPath = Shown(path);
        if (TargetExists(path))
        {
            return Hint($"{Name}: [bold]{shownPath}[/] already exists. Move or delete it first.");
        }

        bool created = false;
        try
        {
            // CreateNew is O_CREAT | O_EXCL: it never overwrites a file or follows a link at the name.
            using (FileStream stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true;
                stream.Write(bytes);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!created)
            {
                if (TargetExists(path))
                {
                    return Hint($"{Name}: [bold]{shownPath}[/] already exists. Move or delete it first.");
                }

                return Hint($"{Name}: could not write [bold]{shownPath}[/]. {Shown(ex.Message)}");
            }

            try
            {
                File.Delete(path);
            }
            catch (Exception deleteFailed) when (deleteFailed is IOException or UnauthorizedAccessException)
            {
                return Hint($"{Name}: could not write [bold]{shownPath}[/] and could not remove the partial file. {Shown(ex.Message)}");
            }

            return Hint($"{Name}: could not write [bold]{shownPath}[/]. {Shown(ex.Message)}");
        }

        string dayFiles = counts.DayFiles == 1 ? "day file" : "day files";
        string projects = counts.Projects ? " and projects.json" : string.Empty;
        List<IRenderable> parts =
        [
            new Markup($"Backed up [steelblue1]{counts.DayFiles.ToString(CultureInfo.InvariantCulture)}[/] {dayFiles}{projects} to [steelblue1]{shownPath}[/]"),
        ];
        AddSkippedFiles(parts, counts.Skipped);
        return new CommandResult(parts.Count == 1 ? parts[0] : new Rows(parts));
    }

    // Adds the yellow line when the backup left files out (day files or projects.json).
    private static void AddSkippedFiles(List<IRenderable> parts, int skipped)
    {
        if (skipped > 0)
        {
            string files = skipped == 1 ? "file" : "files";
            parts.Add(new Markup($"[yellow]! {skipped.ToString(CultureInfo.InvariantCulture)} {files} skipped[/]"));
        }
    }

    /// <summary>The placeholder in the input box while import asks whether to replace a day.</summary>
    internal const string ImportPlaceholder = "yes replaces · anything else keeps · Esc keeps the rest";

    /// <summary>The most skipped names import lists before <c>… and N more</c>.</summary>
    internal const int MaxSkippedNames = OldDataImport.MaxSkippedNames;

    /// <summary>
    /// The most yellow problem and warning lines import shows (problems first, then warnings) before <c>… and N more</c>.
    /// </summary>
    internal const int MaxImportLines = 20;

    // Reads every old-format day file at the top of a folder outside the data folder and writes each day
    // that has no file yet. A day already there with the same entries is left alone; one that differs is
    // asked about in the loop (AskImportConflicts). The source folder is only listed and read.
    private CommandResult Import(string argument)
    {
        const string Name = "import";
        string typedFolder = argument.Trim();
        if (typedFolder.Length == 0)
        {
            return Hint($"{Name}: say the folder. Use [bold]import <folder>[/].");
        }

        string folder = ExpandFolder(typedFolder, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        string shownFolder = Shown(folder);
        if (!Path.IsPathFullyQualified(folder))
        {
            return Hint($"{Name}: [bold]{shownFolder}[/] is not an absolute path.");
        }

        if (!Directory.Exists(folder))
        {
            return File.Exists(folder)
                ? Hint($"{Name}: [bold]{shownFolder}[/] is a file, not a folder.")
                : Hint($"{Name}: the folder [bold]{shownFolder}[/] does not exist.");
        }

        if (DataBackup.IsInside(folder, Store.Folder))
        {
            return Hint($"{Name}: choose a folder outside the data folder.");
        }

        ImportScan scan;
        try
        {
            scan = OldDataImport.Scan(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Hint($"{Name}: the folder [bold]{shownFolder}[/] cannot be listed: {Shown(ex.Message)}");
        }

        ImportRun run = new ImportRun(shownFolder, scan);
        foreach (ImportCandidate candidate in scan.Candidates)
        {
            ImportRead read = OldDataImport.Read(candidate.Path);
            ImportParse? parsed = read.Bytes is { } bytes ? OldDataImport.Parse(candidate.Day, bytes) : null;
            string? error = read.Error ?? parsed?.Error;
            if (error is not null || parsed?.Record is not { } record)
            {
                run.Problems.Add($"! {Shown(candidate.Name)}: {Markup.Escape(error ?? "cannot be read")}");
                continue;
            }

            run.Warnings.AddRange(parsed.Warnings.Select(ImportWarningLine));
            PlaceImported(run, candidate.Day, record);
        }

        if (run.Conflicts.Count > 0)
        {
            pendingImport = run;
            return CommandResult.None;
        }

        return ImportResult(run);
    }

    // Writes a day that has no file, counts one that is already there, and queues one that differs.
    private void PlaceImported(ImportRun run, DateOnly day, DayRecord record)
    {
        DayRecord existing;
        DayStamp stamp;
        try
        {
            (existing, stamp) = Store.LoadWithStamp(day);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            run.Kept++;
            run.Problems.Add($"! {ReportDate(day)}: existing day file cannot be read, kept");
            return;
        }

        if (stamp.IsMissing)
        {
            SaveImported(run, day, record, stamp, "a day file appeared meanwhile, not written");
            return;
        }

        if (existing.Equals(record))
        {
            run.AlreadyThere++;
            return;
        }

        run.Conflicts.Add(new ImportConflict(day, record, stamp));
    }

    // Saves over exactly the file the stamp was taken from (or none); a change on disk writes nothing.
    private void SaveImported(ImportRun run, DateOnly day, DayRecord record, DayStamp stamp, string changed)
    {
        try
        {
            Store.Save(day, record, stamp);
            run.Imported++;
        }
        catch (DayFileChangedException)
        {
            run.Problems.Add($"! {ReportDate(day)}: {changed}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            run.Problems.Add($"! {ReportDate(day)}: could not be written: {Shown(ex.Message)}");
        }
    }

    // Asks about each differing day in date order, in the input box. Only yes or y replaces; Esc keeps this
    // day and every later one. Returns null if the input ended.
    private CommandResult? AskImportConflicts(LineEditor editor, ImportRun run)
    {
        framePlaceholder = ImportPlaceholder;
        try
        {
            for (int i = 0; i < run.Conflicts.Count; i++)
            {
                ImportConflict conflict = run.Conflicts[i];
                string date = ReportDate(conflict.Day);
                Draw(null, new Markup($"[grey]{date} already has a different day. Replace it with the imported one? Type yes to replace.[/]"), false);
                PlaceCursor();
                int width = console.Profile.Width;
                string? typed = editor.ReadValue(InputColumn, InputLine, width - InputColumn - 2, framePlaceholder, out bool cancelled,
                    ShowPasteCut, resized: Resize, hidden: TooSmall);
                if (typed is null)
                {
                    return null;
                }

                if (cancelled)
                {
                    run.Kept += run.Conflicts.Count - i;
                    break;
                }

                string answer = typed.Trim();
                if (string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase))
                {
                    SaveImported(run, conflict.Day, conflict.Record, conflict.Stamp, "the day file changed on disk, not written");
                }
                else
                {
                    run.Kept++;
                }
            }

            return ImportResult(run);
        }
        finally
        {
            framePlaceholder = Placeholder;
        }
    }

    // The summary: the imported count, the grey counts, the yellow problems and warnings, then the skipped names.
    private static CommandResult ImportResult(ImportRun run)
    {
        List<IRenderable> parts = [];
        if (run.Scan.Candidates.Count == 0)
        {
            parts.Add(new Markup($"[grey]No day files to import in {run.ShownFolder}. Nothing was written.[/]"));
        }
        else
        {
            string days = run.Imported == 1 ? "day" : "days";
            parts.Add(new Markup($"Imported [steelblue1]{run.Imported.ToString(CultureInfo.InvariantCulture)}[/] {days} from {run.ShownFolder}"));
            parts.Add(new Markup($"[grey]{run.AlreadyThere.ToString(CultureInfo.InvariantCulture)} already there · " +
                $"{run.Kept.ToString(CultureInfo.InvariantCulture)} kept[/]"));
        }

        int lineCount = run.Problems.Count + run.Warnings.Count;
        foreach (string line in run.Problems.Concat(run.Warnings).Take(MaxImportLines))
        {
            parts.Add(new Markup($"[yellow]{line}[/]"));
        }

        if (lineCount > MaxImportLines)
        {
            parts.Add(new Markup($"[grey]… and {(lineCount - MaxImportLines).ToString(CultureInfo.InvariantCulture)} more[/]"));
        }

        IReadOnlyList<string> skipped = run.Scan.Skipped;
        int skippedCount = run.Scan.SkippedCount;
        if (skippedCount > 0)
        {
            string names = string.Join(", ", skipped.Take(MaxSkippedNames).Select(Shown));
            parts.Add(new Markup($"[grey]Skipped (not a day file): {names}[/]"));
            int shown = Math.Min(skipped.Count, MaxSkippedNames);
            if (skippedCount > shown)
            {
                parts.Add(new Markup($"[grey]… and {(skippedCount - shown).ToString(CultureInfo.InvariantCulture)} more[/]"));
            }
        }

        return new CommandResult(new Rows(parts), hideQuicklist: true);
    }

    // ! <date> <HH:mm> <title>: UsedTime <h:mm> but the entries give <h:mm> (or: is not a time).
    private static string ImportWarningLine(ImportWarning warning)
    {
        string at = $"! {ReportDate(warning.Day)} {warning.Time.ToString("HH:mm", CultureInfo.InvariantCulture)} {Shown(warning.Title)}";
        return warning.Stated is { } stated
            ? $"{at}: UsedTime {OldDataImport.Duration(stated)} but the entries give {OldDataImport.Duration(warning.Actual)}"
            : $"{at}: UsedTime is not a time";
    }

    // One import's state between the command and the questions in the loop. Lines are markup, already escaped.
    private sealed class ImportRun
    {
        public ImportRun(string shownFolder, ImportScan scan)
        {
            ShownFolder = shownFolder;
            Scan = scan;
        }

        public string ShownFolder { get; }

        public ImportScan Scan { get; }

        public int Imported { get; set; }

        public int AlreadyThere { get; set; }

        public int Kept { get; set; }

        public List<string> Problems { get; } = [];

        public List<string> Warnings { get; } = [];

        public List<ImportConflict> Conflicts { get; } = [];
    }

    private sealed record ImportConflict
    {
        public ImportConflict(DateOnly day, DayRecord record, DayStamp stamp)
        {
            Day = day;
            Record = record;
            Stamp = stamp;
        }

        public DateOnly Day { get; }

        public DayRecord Record { get; }

        public DayStamp Stamp { get; }
    }

    // The grey note after an export with an open row; a fixed literal, so no escaping is needed.
    internal const string ExportOpenNote = "A row has an open checkpoint: it has no end on its day and counts no time.";

    // Text for markup: without control or invisible characters, then escaped.
    private static string Shown(string text) => Markup.Escape(DataFolder.ForDisplay(text));

    // True if anything is at path, a symlink included, even one whose target is missing.
    private static bool TargetExists(string path)
    {
        if (Path.Exists(path))
        {
            return true;
        }

        try
        {
            return new FileInfo(path).LinkTarget is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>
    /// Splits the export argument into the two date words and the rest of the line (the folder,
    /// trimmed). False if a date or the folder is missing.
    /// </summary>
    internal static bool SplitExportArgument(string argument, out string first, out string last, out string folder)
    {
        first = string.Empty;
        last = string.Empty;
        folder = string.Empty;
        string rest = argument.TrimStart();
        for (int i = 0; i < 2; i++)
        {
            int end = 0;
            while (end < rest.Length && !char.IsWhiteSpace(rest[end]))
            {
                end++;
            }

            if (end == 0)
            {
                return false;
            }

            if (i == 0)
            {
                first = rest[..end];
            }
            else
            {
                last = rest[..end];
            }

            rest = rest[end..].TrimStart();
        }

        folder = rest.Trim();
        return folder.Length > 0;
    }

    /// <summary>
    /// <paramref name="typed"/> without one pair of surrounding double quotes, with a leading <c>~</c>
    /// (alone, or before <c>/</c> or the platform's separator) replaced by <paramref name="home"/>.
    /// Nothing else is changed; the result may still be relative.
    /// </summary>
    internal static string ExpandFolder(string typed, string home)
    {
        string folder = typed;
        if (folder.Length >= 2 && folder[0] == '"' && folder[^1] == '"')
        {
            folder = folder[1..^1];
        }

        if (folder == "~")
        {
            return home;
        }

        if (folder.Length >= 2 && folder[0] == '~' && (folder[1] == '/' || folder[1] == Path.DirectorySeparatorChar))
        {
            return Path.Combine(home, folder[2..]);
        }

        return folder;
    }

    private static string ReportDate(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>
    /// The report table captioned with the range, with a <c>Total</c> row, or a grey note if
    /// <paramref name="report"/> has no rows; then the open footnote and the yellow skipped line when
    /// they apply. Titles come from <paramref name="projects"/> (<c>null</c>: none), otherwise the names.
    /// A <paramref name="step"/> above 0 adds <c> · rounded to &lt;n&gt; min</c> to the caption.
    /// </summary>
    internal static IRenderable ReportView(DateOnly from, DateOnly to, ProjectReport report, ProjectList? projects, int step = 0)
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
                .AddColumn(new TableColumn("[grey]Hours[/]").RightAligned())
                .Caption($"{ReportDate(from)} – {ReportDate(to)}{RoundingSuffix(step)}", new Style(Color.Grey));

            foreach (ReportRow row in report.Rows)
            {
                string workItem = row.WorkItem is { } w ? "#" + w.ToString(CultureInfo.InvariantCulture) : string.Empty;
                string titleText = row.Title(projects);
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

        AddSkipped(parts, report);
        return parts.Count == 1 ? parts[0] : new Rows(parts);
    }

    // Shows every day's checkpoint groups whose names or quicklist title contain the text (ignoring case),
    // one row per day and group, or a grey note if none match. Read-only.
    private CommandResult Find(string argument)
    {
        const string Name = "find";
        string text = argument.Trim();
        if (text.Length == 0)
        {
            return Hint($"{Name}: say what to look for. Use [bold]find <text>[/].");
        }

        ProjectList? projects = LoadProjectsOrNull();
        CheckpointSearch search;
        try
        {
            search = CheckpointSearch.Calculate(Store, projects, text, DateOnly.FromDateTime(time.GetLocalNow().DateTime), Now());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Hint($"{Name}: the data folder cannot be listed: {Markup.Escape(ex.Message)}");
        }

        return new CommandResult(FindView(text, search, projects), hideQuicklist: true);
    }

    /// <summary>
    /// The find table (date, work item, title, time) captioned with the match count and the text, or a
    /// grey note if <paramref name="search"/> has no matches; then the open footnote and the yellow
    /// skipped line when they apply. Titles come from <paramref name="projects"/> (<c>null</c>: none),
    /// otherwise the names.
    /// </summary>
    internal static IRenderable FindView(string text, CheckpointSearch search, ProjectList? projects)
    {
        List<IRenderable> parts = [];
        string shown = Markup.Escape(text);
        if (search.Matches.Count == 0)
        {
            parts.Add(new Markup($"[grey]No checkpoints or projects match \"{shown}\".[/]"));
        }
        else
        {
            string count = search.Matches.Count.ToString(CultureInfo.InvariantCulture);
            string matches = search.Matches.Count == 1 ? "match" : "matches";
            Table table = new Table()
                .Border(TableBorder.Rounded)
                .BorderColor(Color.Grey)
                .AddColumn("[grey]Date[/]")
                .AddColumn("[grey]Work item[/]")
                .AddColumn("[grey]Title[/]")
                .AddColumn("[grey]Time[/]")
                .Caption($"{count} {matches} for \"{shown}\"", new Style(Color.Grey));

            foreach (SearchMatch match in search.Matches)
            {
                string workItem = match.WorkItem is { } w ? "#" + w.ToString(CultureInfo.InvariantCulture) : string.Empty;
                string open = match.Open ? " [grey]open[/]" : string.Empty;
                table.AddRow(
                    new Text(ReportDate(match.Date)),
                    new Text(workItem),
                    new Markup(Markup.Escape(match.Title(projects))),
                    new Markup($"[steelblue1]{DayView.FormatTimeLeft(match.Total)}[/]{open}"));
            }

            parts.Add(table);
            if (search.Matches.Any(m => m.Open))
            {
                parts.Add(new Markup($"[grey]{OpenFootnote}[/]"));
            }
        }

        AddSkipped(parts, search.SkippedFiles);
        return parts.Count == 1 ? parts[0] : new Rows(parts);
    }

    // Shows the average arrival, leaving and work time per weekday over an inclusive date range, with an
    // All days row, or a grey note if no day in the range holds an arrival. Read-only.
    private CommandResult Statistics(string argument)
    {
        const string Name = "statistics";
        const string Usage = "Use [bold]statistics <from> <to>[/] with dates as [bold]yyyy-MM-dd[/].";
        string[] parts = argument.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
        {
            return Hint($"{Name}: say the first and the last day. {Usage}");
        }

        if (ParseRange(Name, Usage, parts[0], parts[1], out DateOnly from, out DateOnly to) is { } hint)
        {
            return hint;
        }

        WeekdayStatistics stats;
        try
        {
            stats = WeekdayStatistics.Calculate(Store, from, to);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Hint($"{Name}: the data folder cannot be listed: {Markup.Escape(ex.Message)}");
        }

        return new CommandResult(StatisticsView(from, to, stats), hideQuicklist: true);
    }

    /// <summary>
    /// The weekday statistics table (weekday, days, arrival, leaving, work time) captioned with the range,
    /// with a bold <c>All days</c> row, or a grey note if no day counted; then the yellow skipped line
    /// when files were skipped. A row without a leaving shows a grey <c>–</c> for leaving and work time.
    /// </summary>
    internal static IRenderable StatisticsView(DateOnly from, DateOnly to, WeekdayStatistics stats)
    {
        List<IRenderable> parts = [];
        if (stats.All is not { } all)
        {
            parts.Add(new Markup($"[grey]No working days between {ReportDate(from)} and {ReportDate(to)}.[/]"));
        }
        else
        {
            Table table = new Table()
                .Border(TableBorder.Rounded)
                .BorderColor(Color.Grey)
                .AddColumn("[grey]Weekday[/]")
                .AddColumn(new TableColumn("[grey]Days[/]").RightAligned())
                .AddColumn(new TableColumn("[grey]Arrival[/]").RightAligned())
                .AddColumn(new TableColumn("[grey]Leaving[/]").RightAligned())
                .AddColumn(new TableColumn("[grey]Work time[/]").RightAligned())
                .Caption($"{ReportDate(from)} – {ReportDate(to)}", new Style(Color.Grey));

            foreach (WeekdayRow row in stats.Rows)
            {
                AddStatisticsRow(table, row.Weekday!.Value.ToString(), row, "steelblue1");
            }

            AddStatisticsRow(table, "[bold]All days[/]", all, "bold steelblue1");
            parts.Add(table);
        }

        AddSkipped(parts, stats.SkippedFiles);
        return parts.Count == 1 ? parts[0] : new Rows(parts);
    }

    // One statistics row; label is markup (a fixed weekday name or the bold All days label).
    private static void AddStatisticsRow(Table table, string label, WeekdayRow row, string style)
    {
        const string Missing = "[grey]–[/]";
        table.AddRow(
            new Markup(label),
            new Markup($"[{style}]{row.Days.ToString(CultureInfo.InvariantCulture)}[/]"),
            new Markup($"[{style}]{row.Arrival.ToString("H:mm", CultureInfo.InvariantCulture)}[/]"),
            new Markup(row.Leaving is { } leaving ? $"[{style}]{leaving.ToString("H:mm", CultureInfo.InvariantCulture)}[/]" : Missing),
            new Markup(row.WorkTime is { } work ? $"[{style}]{DayView.FormatTimeLeft(work)}[/]" : Missing));
    }

    // Adds the yellow skipped line when the report skipped unreadable day files.
    private static void AddSkipped(List<IRenderable> parts, ProjectReport report) => AddSkipped(parts, report.SkippedFiles);

    // Adds the yellow skipped line when skipped unreadable day files were counted.
    private static void AddSkipped(List<IRenderable> parts, int skippedFiles)
    {
        if (skippedFiles > 0)
        {
            string files = skippedFiles == 1 ? "day file" : "day files";
            parts.Add(new Markup($"[yellow]! {skippedFiles.ToString(CultureInfo.InvariantCulture)} {files} skipped[/]"));
        }
    }

    // Shows this calendar year's vacation allowance, the vacation days taken and planned, and what is left.
    // The year is today's (from the clock), never the shown day's. Read-only: writes no day file and no settings.json.
    private CommandResult Vacation()
    {
        DateOnly today = DateOnly.FromDateTime(time.GetLocalNow().DateTime);
        VacationAllowance allowance;
        try
        {
            allowance = VacationAllowance.Calculate(Store, today.Year, today);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Hint($"vacation: the data folder cannot be listed: {Markup.Escape(ex.Message)}");
        }

        return new CommandResult(VacationView(today.Year, VacationDays(), allowance), hideQuicklist: true);
    }

    /// <summary>
    /// The vacation table captioned <c>Vacation &lt;year&gt;</c> with the rows <c>Allowance</c>, <c>Taken</c>,
    /// <c>Planned</c> and <c>Left</c> (allowance minus taken minus planned, negative when over), then a yellow
    /// line when the allowance is exceeded and the yellow skipped line when files were skipped.
    /// </summary>
    internal static IRenderable VacationView(int year, int days, VacationAllowance allowance)
    {
        int left = days - allowance.Taken - allowance.Planned;
        Table table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .AddColumn("[grey]Vacation[/]")
            .AddColumn(new TableColumn("[grey]Days[/]").RightAligned())
            .Caption($"Vacation {year.ToString(CultureInfo.InvariantCulture)}", new Style(Color.Grey));
        AddVacationRow(table, "Allowance", days, "steelblue1");
        AddVacationRow(table, "Taken", allowance.Taken, "steelblue1");
        AddVacationRow(table, "Planned", allowance.Planned, "steelblue1");
        AddVacationRow(table, "[bold]Left[/]", left, left < 0 ? "bold yellow" : "bold steelblue1");

        List<IRenderable> parts = [table];
        if (left < 0)
        {
            int over = -left;
            string unit = over == 1 ? "day" : "days";
            parts.Add(new Markup($"[yellow]! {over.ToString(CultureInfo.InvariantCulture)} {unit} over your allowance[/]"));
        }

        AddSkippedFiles(parts, allowance.SkippedFiles);
        return parts.Count == 1 ? parts[0] : new Rows(parts);
    }

    // One vacation row; label is fixed markup, the value a whole number in invariant culture.
    private static void AddVacationRow(Table table, string label, int value, string style) =>
        table.AddRow(new Markup(label), new Markup($"[{style}]{value.ToString(CultureInfo.InvariantCulture)}[/]"));

    /// <summary>Whole minutes as decimal hours with two decimals and the invariant culture, e.g. <c>7.50</c>.</summary>
    internal static string FormatHours(TimeSpan value)
    {
        long minutes = value.Ticks / TimeSpan.TicksPerMinute;
        return (minutes / 60m).ToString("0.00", CultureInfo.InvariantCulture);
    }

    /// <summary>The red hint for <c>devops connect</c>, <c>devops disconnect</c> and <c>sync … send</c> without an OS credential store.</summary>
    internal const string DevOpsUnsupported =
        "Azure DevOps needs the macOS Keychain or Windows Credential Manager; not available on this system";

    /// <summary>The placeholder of the masked token prompt.</summary>
    internal const string TokenPlaceholder = "Personal access token · Esc cancels";

    private const string DevOpsUsage =
        "Use [bold]devops[/], [bold]devops connect <organization>[/], [bold]devops import[/] or [bold]devops disconnect[/].";

    private void RegisterDevOps()
    {
        registry.Register("devops", "dv", "Show or set the Azure DevOps link: [connect <org> | import | disconnect]", DevOps);
        registry.Register("sync", "sy", "Send unbooked hours to Azure DevOps: <from> <to> [send]", Sync);
    }

    // devops shows the connection, devops connect <org> asks for the token (in the loop, masked), and
    // devops disconnect removes it. The token is never shown and never taken as an argument.
    private CommandResult DevOps(string argument)
    {
        const string Name = "devops";
        (string sub, string rest) = SplitToken(argument);
        rest = rest.Trim();
        if (sub.Length == 0)
        {
            return DevOpsStatus();
        }

        if (string.Equals(sub, "connect", StringComparison.OrdinalIgnoreCase))
        {
            if (!tokenStore.IsSupported)
            {
                return Hint(DevOpsUnsupported);
            }

            // Before the masked prompt, so the user never types a token the store would refuse.
            if (tokenStore.WriteRefusal is { } refusal)
            {
                return Hint($"{Name}: {Markup.Escape(refusal)} Nothing was stored.");
            }

            if (rest.Length == 0)
            {
                return Hint($"{Name}: say the organization, as in dev.azure.com/<organization>. {DevOpsUsage}");
            }

            if (rest.Any(char.IsWhiteSpace))
            {
                // Never echoed: the extra word may be a pasted token.
                return Hint($"{Name}: connect takes only the organization; the token is asked for next. Nothing was stored.");
            }

            if (!DevOpsCredential.IsValidOrganization(rest))
            {
                return Hint($"{Name}: [bold]{Markup.Escape(rest)}[/] is not an organization name " +
                    "(letters, digits and hyphens, as in dev.azure.com/<organization>). Nothing was stored.");
            }

            connectOrganization = rest;
            return CommandResult.None;
        }

        if (string.Equals(sub, "disconnect", StringComparison.OrdinalIgnoreCase))
        {
            if (rest.Length > 0)
            {
                return Hint("[bold]devops disconnect[/] takes no arguments.");
            }

            if (!tokenStore.IsSupported)
            {
                return Hint(DevOpsUnsupported);
            }

            bool deleted;
            try
            {
                deleted = tokenStore.Delete();
            }
            catch (Exception ex) when (IsStoreFailure(ex))
            {
                return Hint($"{Name}: {StoreMessage(ex)} Nothing was removed.");
            }

            return new CommandResult(hint: new Markup(deleted ? "[grey]Disconnected[/]" : "[grey]Nothing to remove[/]"));
        }

        if (string.Equals(sub, "import", StringComparison.OrdinalIgnoreCase))
        {
            if (rest.Length > 0)
            {
                return Hint("[bold]devops import[/] takes no arguments.");
            }

            return ImportWorkItems();
        }

        return Hint($"{Name}: [bold]{Markup.Escape(sub)}[/] is not something devops can do. {DevOpsUsage}");
    }

    // devops import: reads the quicklist first, then the token once, fetches the open work items assigned
    // to the user and appends the new ones in one save. Network titles are untrusted: each is checked like
    // a typed title, and a skipped one is shown by its work item number only, never by its text.
    private CommandResult ImportWorkItems()
    {
        const string Name = "devops";
        const string NothingDone = "Nothing was imported.";
        if (!tokenStore.IsSupported)
        {
            return Hint(DevOpsUnsupported);
        }

        if (LoadProjectsFor(Name, NothingDone, out CommandResult? unreadable) is not { } current)
        {
            return unreadable!;
        }

        DevOpsCredential? credential;
        try
        {
            credential = ReadCredential();
        }
        catch (Exception ex) when (IsStoreFailure(ex))
        {
            return Hint($"{Name}: {StoreMessage(ex)} {NothingDone}");
        }

        if (credential is null)
        {
            return Hint($"{Name}: not connected to Azure DevOps. Use [bold]devops connect <organization>[/] first. {NothingDone}");
        }

        if (devOpsClient is null)
        {
            return Hint($"{Name}: this build cannot reach Azure DevOps. {NothingDone}");
        }

        IReadOnlyList<(int Id, string? Title)> fetched;
        IDevOpsClient client = devOpsClient(credential);
        try
        {
            fetched = client.GetAssignedWorkItemsAsync().GetAwaiter().GetResult();
        }
        catch (DevOpsException ex)
        {
            return Hint($"{Name}: {Markup.Escape(ex.Message)} {NothingDone}");
        }
        finally
        {
            (client as IDisposable)?.Dispose();
        }

        List<Project> added = [];
        List<int> skipped = [];
        int already = 0;
        HashSet<int> seen = [];
        foreach ((int id, string? title) in fetched.OrderBy(item => item.Id))
        {
            if (id < 1 || !seen.Add(id))
            {
                continue;
            }

            if (current.NumberOf(id) is not null)
            {
                already++;
                continue;
            }

            string? trimmed = title?.Trim();
            if (trimmed is null || !ProjectList.IsValidTitle(trimmed))
            {
                skipped.Add(id);
                continue;
            }

            added.Add(new Project(id, trimmed));
        }

        List<IRenderable> parts = [];
        if (added.Count > 0)
        {
            ProjectList updated = new ProjectList(current.Projects.Concat(added));
            if (Encoding.UTF8.GetByteCount(updated.ToJson()) > ProjectStore.MaxFileBytes)
            {
                return Hint($"{Name}: the quicklist would be too large (over " +
                    $"{(ProjectStore.MaxFileBytes / 1024).ToString(CultureInfo.InvariantCulture)} KiB) with " +
                    $"{added.Count.ToString(CultureInfo.InvariantCulture)} more {(added.Count == 1 ? "project" : "projects")}. " +
                    $"Remove some with [bold]removeproject[/]. {NothingDone}");
            }

            if (SaveProjectsFor(Name, updated) is { } saveHint)
            {
                return saveHint;
            }

            Table table = new Table()
                .Border(TableBorder.Rounded)
                .BorderColor(Color.Grey)
                .AddColumn("[grey]Work item[/]")
                .AddColumn("[grey]Title[/]");
            foreach (Project project in added)
            {
                table.AddRow(
                    new Text("#" + project.WorkItem.ToString(CultureInfo.InvariantCulture)),
                    new Markup(Markup.Escape(project.Title)));
            }

            parts.Add(table);
        }

        if (already > 0)
        {
            parts.Add(new Markup($"[grey]{already.ToString(CultureInfo.InvariantCulture)} already on the quicklist[/]"));
        }

        if (skipped.Count > 0)
        {
            parts.Add(new Markup($"[grey]{skipped.Count.ToString(CultureInfo.InvariantCulture)} skipped (title not valid): " +
                string.Join(", ", skipped.Select(id => "#" + id.ToString(CultureInfo.InvariantCulture))) + "[/]"));
        }

        string done = added.Count > 0
            ? $"[grey]Added {added.Count.ToString(CultureInfo.InvariantCulture)} " +
                $"{(added.Count == 1 ? "work item" : "work items")} to the quicklist.[/]"
            : "[grey]No new work items to add.[/]";
        return new CommandResult(parts.Count == 0 ? null : new Rows(parts), hint: new Markup(done));
    }

    // A devops (dv) line with any argument never enters the in-memory history: connect asks for the
    // token next, but a user may still type or paste it on the line (devops connect contoso <token>),
    // and a typo'd sub-command may hold it too. Parsed like Dispatch (one leading '/', name or alias).
    private bool IsDevOpsLineWithArgument(string line)
    {
        string text = line.Trim();
        int split = text.IndexOfAny([' ', '\t']);
        if (split < 0)
        {
            return false;
        }

        string token = text[..split];
        string name = token.StartsWith('/') ? token[1..] : token;
        return name.Length > 0 && registry.TryGet(name, out Command? command)
            && string.Equals(command.Name, "devops", StringComparison.OrdinalIgnoreCase);
    }

    // The organisation and "token stored", or "not connected"; never the token.
    private CommandResult DevOpsStatus()
    {
        if (!tokenStore.IsSupported)
        {
            return new CommandResult(hint: new Markup($"[grey]Azure DevOps: not connected. {DevOpsUnsupported}.[/]"));
        }

        DevOpsCredential? credential;
        try
        {
            credential = ReadCredential();
        }
        catch (Exception ex) when (IsStoreFailure(ex))
        {
            return Hint($"devops: {StoreMessage(ex)}");
        }

        return new CommandResult(hint: new Markup(credential is null
            ? "[grey]Azure DevOps: not connected. Use [bold]devops connect <organization>[/] to connect.[/]"
            : $"[grey]Azure DevOps: [steelblue1]{Markup.Escape(credential.Organization)}[/] · token stored[/]"));
    }

    // The stored credential, or null if none is stored or what is stored fails validation.
    private DevOpsCredential? ReadCredential() =>
        tokenStore.Read() is { } stored ? DevOpsCredential.TryCreate(stored.Organization, stored.Pat) : null;

    // A store failure: its own fixed message, or a missing OS library (also fixed, never a secret).
    private static bool IsStoreFailure(Exception ex) =>
        ex is TokenStoreException or DllNotFoundException or EntryPointNotFoundException;

    private static string StoreMessage(Exception ex) =>
        ex is TokenStoreException ? Markup.Escape(ex.Message) : "The credential store cannot be reached.";

    // Asks for the token in the input box, masked, after devops connect <organization>. Esc or an
    // invalid token stores nothing. Returns null if the input ended.
    private CommandResult? ReadToken(LineEditor editor, string organization)
    {
        framePlaceholder = TokenPlaceholder;
        try
        {
            Draw(null, new Markup($"[grey]Paste or type the personal access token for [steelblue1]{Markup.Escape(organization)}[/].[/]"), false);
            PlaceCursor();
            int width = console.Profile.Width;
            string? typed = editor.ReadValue(InputColumn, InputLine, width - InputColumn - 2, framePlaceholder, out bool cancelled,
                ShowPasteCut, masked: true, resized: Resize, hidden: TooSmall);
            if (typed is null)
            {
                return null;
            }

            if (cancelled)
            {
                return new CommandResult(hint: new Markup("[grey]Cancelled. Nothing was stored.[/]"));
            }

            string pat = typed.Trim();
            if (!DevOpsCredential.IsValidPat(pat))
            {
                return Hint($"devops: that is not a personal access token (1 to {DevOpsCredential.MaxPatLength} " +
                    "printable ASCII characters, no spaces). Nothing was stored.");
            }

            try
            {
                tokenStore.Write(organization, pat);
            }
            catch (Exception ex) when (IsStoreFailure(ex))
            {
                return Hint($"devops: {StoreMessage(ex)} Nothing was stored.");
            }

            return new CommandResult(hint: new Markup($"[grey]Connected to {Markup.Escape(organization)}[/]"));
        }
        finally
        {
            framePlaceholder = Placeholder;
        }
    }

    // sync <from> <to> previews what would be sent, read-only and offline. With send, it reads the
    // token once, then per work item reads Completed Work, adds the hours with the rev test and,
    // only after that succeeded, marks the checkpoints booked.
    private CommandResult Sync(string argument)
    {
        const string Name = "sync";
        const string Usage = "Use [bold]sync <from> <to>[/] to preview and [bold]sync <from> <to> send[/] to send, " +
            "with dates as [bold]yyyy-MM-dd[/].";
        string[] parts = argument.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is not (2 or 3))
        {
            return Hint($"{Name}: say the first and the last day. {Usage}");
        }

        bool send = parts.Length == 3;
        if (send && !string.Equals(parts[2], "send", StringComparison.Ordinal))
        {
            return Hint($"{Name}: [bold]{Markup.Escape(parts[2])}[/] is not [bold]send[/]. {Usage}");
        }

        if (ParseRange(Name, Usage, parts[0], parts[1], out DateOnly from, out DateOnly to) is { } hint)
        {
            return hint;
        }

        if (send && !tokenStore.IsSupported)
        {
            return Hint(DevOpsUnsupported);
        }

        SyncPlan plan;
        try
        {
            plan = DevOpsSync.Plan(Store, from, to);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Hint($"{Name}: the data folder cannot be listed: {Markup.Escape(ex.Message)}");
        }

        ProjectList? projects = LoadProjectsOrNull();
        if (!send || plan.Items.Count == 0)
        {
            return new CommandResult(SyncPreview(from, to, plan, projects), hideQuicklist: true);
        }

        DevOpsCredential? credential;
        try
        {
            credential = ReadCredential();
        }
        catch (Exception ex) when (IsStoreFailure(ex))
        {
            return Hint($"{Name}: {StoreMessage(ex)} Nothing was sent.");
        }

        if (credential is null)
        {
            return Hint($"{Name}: not connected to Azure DevOps. Use [bold]devops connect <organization>[/] first. Nothing was sent.");
        }

        if (devOpsClient is null)
        {
            return Hint($"{Name}: this build cannot reach Azure DevOps. Nothing was sent.");
        }

        IDevOpsClient client = devOpsClient(credential);
        try
        {
            return SendPlan(from, to, plan, projects, client);
        }
        finally
        {
            (client as IDisposable)?.Dispose();
        }
    }

    // Sends each work item in turn; a failure leaves its checkpoints unbooked and the rest still run.
    private CommandResult SendPlan(DateOnly from, DateOnly to, SyncPlan plan, ProjectList? projects, IDevOpsClient client)
    {
        Table table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .AddColumn("[grey]Work item[/]")
            .AddColumn("[grey]Title[/]")
            .AddColumn(new TableColumn("[grey]Hours[/]").RightAligned())
            .AddColumn("[grey]Completed Work[/]")
            .Caption($"{ReportDate(from)} – {ReportDate(to)}", new Style(Color.Grey));
        List<IRenderable> notSaved = [];
        int failed = 0;
        foreach (SyncItem item in plan.Items)
        {
            string result;
            try
            {
                (double? completed, int rev) = client.GetCompletedWorkAsync(item.WorkItem).GetAwaiter().GetResult();
                double old = completed ?? 0;
                double updated = Math.Round(old + item.Hours, 2);
                if (!double.IsFinite(updated))
                {
                    throw new DevOpsException(DevOpsException.Unexpected);
                }

                client.SetCompletedWorkAsync(item.WorkItem, rev, updated).GetAwaiter().GetResult();
                result = $"[steelblue1]sent[/] {FormatCompleted(old)} → {FormatCompleted(updated)}";
                notSaved.AddRange(MarkBooked(item));
            }
            catch (DevOpsException ex)
            {
                failed++;
                result = $"[red]failed: {Markup.Escape(ex.Message)}[/]";
            }

            table.AddRow(
                new Text("#" + item.WorkItem.ToString(CultureInfo.InvariantCulture)),
                new Markup(Markup.Escape(item.Title(projects))),
                new Markup($"[steelblue1]{FormatCompleted(item.Hours)}[/]"),
                new Markup(result));
        }

        List<IRenderable> parts = [table];
        parts.AddRange(notSaved);
        AddSkipped(parts, plan.SkippedFiles);

        int count = plan.Items.Count;
        string items = count == 1 ? "work item" : "work items";
        IRenderable hint = failed > 0 || notSaved.Count > 0
            ? new Markup($"[red]sync: {failed.ToString(CultureInfo.InvariantCulture)} of " +
                $"{count.ToString(CultureInfo.InvariantCulture)} {items} failed" +
                (notSaved.Count > 0 ? ", and some days could not be saved" : string.Empty) + "; see the list above.[/]")
            : new Markup($"[grey]Sent {count.ToString(CultureInfo.InvariantCulture)} {items} to Azure DevOps.[/]");
        return new CommandResult(new Rows(parts), hint: hint, hideQuicklist: true);
    }

    // Marks the item's checkpoints booked, one save per day. Each day is loaded again and only an entry
    // that is still the same unbooked checkpoint is marked. Returns a red line for every day that
    // could not be saved, naming the work item, the day and the booked commands to run by hand.
    private List<IRenderable> MarkBooked(SyncItem item)
    {
        List<IRenderable> lines = [];
        foreach (IGrouping<DateOnly, SyncCheckpoint> day in item.Checkpoints.GroupBy(c => c.Day))
        {
            try
            {
                (DayRecord record, DayStamp stamp) = Store.LoadWithStamp(day.Key);
                foreach (SyncCheckpoint checkpoint in day)
                {
                    if (checkpoint.Number > record.Entries.Count)
                    {
                        throw new InvalidDataException("The day changed.");
                    }

                    DayEntry entry = record.Entries[checkpoint.Number - 1];
                    if (entry.Kind != EntryKind.Checkpoint || entry.WorkItem != item.WorkItem || entry.Booked)
                    {
                        throw new InvalidDataException("The day changed.");
                    }

                    record = record.SetBooked(checkpoint.Number, true);
                }

                Store.Save(day.Key, record, stamp);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException
                or InvalidOperationException or ArgumentException)
            {
                string date = ReportDate(day.Key);
                string commands = string.Join(", ", day.Select(c => $"[bold]booked {c.Number.ToString(CultureInfo.InvariantCulture)}[/]"));
                lines.Add(new Markup($"[red]#{item.WorkItem.ToString(CultureInfo.InvariantCulture)}: the hours were sent, " +
                    $"but {date} could not be saved. Run {commands} on {date} by hand.[/]"));
            }
        }

        return lines;
    }

    /// <summary>
    /// What <c>sync</c> would send: one row per work item (ascending) with its title, time, hours and
    /// checkpoint count, and how to send it; or a grey note when there is nothing to send. Then the
    /// yellow skipped line when day files could not be read.
    /// </summary>
    internal static IRenderable SyncPreview(DateOnly from, DateOnly to, SyncPlan plan, ProjectList? projects)
    {
        List<IRenderable> parts = [];
        if (plan.Items.Count == 0)
        {
            parts.Add(new Markup($"[grey]Nothing to send: no unbooked, ended checkpoints with a work item between " +
                $"{ReportDate(from)} and {ReportDate(to)}.[/]"));
        }
        else
        {
            Table table = new Table()
                .Border(TableBorder.Rounded)
                .BorderColor(Color.Grey)
                .AddColumn("[grey]Work item[/]")
                .AddColumn("[grey]Title[/]")
                .AddColumn("[grey]Time[/]")
                .AddColumn(new TableColumn("[grey]Hours[/]").RightAligned())
                .AddColumn(new TableColumn("[grey]Checkpoints[/]").RightAligned())
                .Caption($"{ReportDate(from)} – {ReportDate(to)}", new Style(Color.Grey));
            foreach (SyncItem item in plan.Items)
            {
                table.AddRow(
                    new Text("#" + item.WorkItem.ToString(CultureInfo.InvariantCulture)),
                    new Markup(Markup.Escape(item.Title(projects))),
                    new Markup($"[steelblue1]{DayView.FormatTimeLeft(TimeSpan.FromMinutes(item.Minutes))}[/]"),
                    new Markup($"[steelblue1]{FormatCompleted(item.Hours)}[/]"),
                    new Text(item.Checkpoints.Count.ToString(CultureInfo.InvariantCulture)));
            }

            parts.Add(table);
            parts.Add(new Markup($"[grey]Type [bold]sync {ReportDate(from)} {ReportDate(to)} send[/] " +
                "to write these hours to Azure DevOps[/]"));
        }

        AddSkipped(parts, plan.SkippedFiles);
        return parts.Count == 1 ? parts[0] : new Rows(parts);
    }

    /// <summary>Hours as Azure DevOps holds them, invariant with two decimals, e.g. <c>3.50</c>.</summary>
    internal static string FormatCompleted(double hours) => hours.ToString("0.00", CultureInfo.InvariantCulture);

    private void RegisterProjects()
    {
        registry.Register("addproject", "ap", "Add a project to the quicklist: <work item> <title>", AddProject);
        registry.Register("projects", "pr", "List the projects on the quicklist", NoArguments("projects", ListProjects));
        registry.Register("renameproject", "rn", "Rename a project on the quicklist: <number> <title>", RenameProject);
        registry.Register("removeproject", "xp", "Remove a project from the quicklist by its number", RemoveProject);
        registry.Register("moveproject", "mp", "Move a project to another number on the quicklist: <number> <new number>", MoveProject);
    }

    // The hint for a project title that is missing or invalid, or null if the title is fine. Used by
    // addproject and renameproject, so both refuse the same titles with the same texts.
    private static CommandResult? ProjectTitleProblem(string name, string title, string usage)
    {
        if (title.Length == 0)
        {
            return Hint($"{name}: the title is missing. {usage}");
        }

        if (title.Length > ProjectList.MaxTitleLength)
        {
            return Hint($"{name}: the title is longer than {ProjectList.MaxTitleLength.ToString(CultureInfo.InvariantCulture)} characters.");
        }

        if (ProjectList.HasControlCharacters(title))
        {
            return Hint($"{name}: the title must not contain control characters. {RetypeHiddenCharacters}");
        }

        if (ProjectList.HasInvisibleOrBrokenCharacters(title))
        {
            return Hint($"{name}: the title must not contain invisible formatting or broken characters. {RetypeHiddenCharacters}");
        }

        return null;
    }

    // Parses a quicklist number: a plain positive integer (no sign, no spaces, no separators).
    private static bool TryParseProjectNumber(string text, out int number) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out number) && number >= 1;

    // Loads the quicklist for a command that changes it, or returns the hint to show if it cannot be read.
    private ProjectList? LoadProjectsFor(string name, string nothingDone, out CommandResult? hint)
    {
        try
        {
            hint = null;
            return projectStore.Load();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            hint = Hint($"{name}: {UnreadableProjects(ex)} {nothingDone} {UnreadableProjectsNextStep}");
            return null;
        }
    }

    // The hint for a project number that is not on the list.
    private static CommandResult NoSuchProject(string name, int number, ProjectList list, string usage) =>
        list.IsEmpty
            ? Hint($"{name}: the quicklist is empty, so there is no project {number.ToString(CultureInfo.InvariantCulture)}. {usage}")
            : Hint($"{name}: there is no project {number.ToString(CultureInfo.InvariantCulture)}; the quicklist has " +
                $"{list.Projects.Count.ToString(CultureInfo.InvariantCulture)}. {usage}");

    // Saves a changed quicklist, or returns the hint to show if it cannot be saved.
    private CommandResult? SaveProjectsFor(string name, ProjectList list)
    {
        try
        {
            projectStore.Save(list);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Hint($"{name}: could not save the project list. {Markup.Escape(ex.Message)}");
        }
    }

    // Gives project <number> a new title, keeping its work item number and position. Nothing is
    // written if the number or title is invalid or projects.json cannot be read.
    private CommandResult RenameProject(string argument)
    {
        const string Name = "renameproject";
        const string Usage = "Use [bold]renameproject <number> <title>[/].";
        if (argument.Length == 0)
        {
            return Hint($"{Name}: say the project number and the new title. {Usage}");
        }

        int split = argument.IndexOfAny([' ', '\t']);
        string numberText = split < 0 ? argument : argument[..split];
        string title = split < 0 ? string.Empty : argument[(split + 1)..].Trim();
        if (!TryParseProjectNumber(numberText, out int number))
        {
            return Hint($"{Name}: [bold]{Markup.Escape(numberText)}[/] is not a project number. {Usage}");
        }

        if (ProjectTitleProblem(Name, title, Usage) is { } titleHint)
        {
            return titleHint;
        }

        if (LoadProjectsFor(Name, "Nothing was renamed.", out CommandResult? unreadable) is not { } current)
        {
            return unreadable!;
        }

        if (number > current.Projects.Count)
        {
            return NoSuchProject(Name, number, current, Usage);
        }

        ProjectList updated = current.Rename(number, title);
        if (SaveProjectsFor(Name, updated) is { } saveHint)
        {
            return saveHint;
        }

        return new CommandResult(new Markup(
            $"Renamed project [steelblue1]{number.ToString(CultureInfo.InvariantCulture)}[/]: " +
            $"#{updated.Projects[number - 1].WorkItem.ToString(CultureInfo.InvariantCulture)} {Markup.Escape(title)}"));
    }

    // Drops project <number> from the quicklist; later projects move up one number. Day files are
    // never read or written: checkpoints store work item numbers, so booked times keep theirs.
    private CommandResult RemoveProject(string argument)
    {
        const string Name = "removeproject";
        const string Usage = "Use [bold]removeproject <number>[/]. It changes only the quicklist; booked times stay.";
        if (argument.Length == 0)
        {
            return Hint($"{Name}: say which project to remove. {Usage}");
        }

        if (!TryParseProjectNumber(argument, out int number))
        {
            return Hint($"{Name}: [bold]{Markup.Escape(argument)}[/] is not a project number. {Usage}");
        }

        if (LoadProjectsFor(Name, "Nothing was removed.", out CommandResult? unreadable) is not { } current)
        {
            return unreadable!;
        }

        if (number > current.Projects.Count)
        {
            return NoSuchProject(Name, number, current, Usage);
        }

        Project removed = current.Projects[number - 1];
        if (SaveProjectsFor(Name, current.Remove(number)) is { } saveHint)
        {
            return saveHint;
        }

        return new CommandResult(new Markup(
            $"Removed project [steelblue1]{number.ToString(CultureInfo.InvariantCulture)}[/]: " +
            $"#{removed.WorkItem.ToString(CultureInfo.InvariantCulture)} {Markup.Escape(removed.Title)}"));
    }

    // Moves project <number> to <new number>; the projects in between shift by one. Nothing is
    // written if either number is invalid, both are the same, or projects.json cannot be read.
    private CommandResult MoveProject(string argument)
    {
        const string Name = "moveproject";
        const string Usage = "Use [bold]moveproject <number> <new number>[/].";
        string[] parts = argument.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return Hint($"{Name}: say the project number and its new number. {Usage}");
        }

        if (!TryParseProjectNumber(parts[0], out int from))
        {
            return Hint($"{Name}: [bold]{Markup.Escape(parts[0])}[/] is not a project number. {Usage}");
        }

        if (parts.Length == 1)
        {
            return Hint($"{Name}: the new number is missing. {Usage}");
        }

        if (parts.Length > 2)
        {
            return Hint($"{Name}: say only the project number and its new number. {Usage}");
        }

        if (!TryParseProjectNumber(parts[1], out int to))
        {
            return Hint($"{Name}: [bold]{Markup.Escape(parts[1])}[/] is not a project number. {Usage}");
        }

        if (LoadProjectsFor(Name, "Nothing was moved.", out CommandResult? unreadable) is not { } current)
        {
            return unreadable!;
        }

        if (from > current.Projects.Count)
        {
            return NoSuchProject(Name, from, current, Usage);
        }

        if (to > current.Projects.Count)
        {
            return NoSuchProject(Name, to, current, Usage);
        }

        string workItem = current.Projects[from - 1].WorkItem.ToString(CultureInfo.InvariantCulture);
        if (from == to)
        {
            return new CommandResult(hint: new Markup(
                $"[grey]Project #{workItem} is already number {to.ToString(CultureInfo.InvariantCulture)}. Nothing was moved.[/]"));
        }

        if (SaveProjectsFor(Name, current.Move(from, to)) is { } saveHint)
        {
            return saveHint;
        }

        return new CommandResult(new Markup(
            $"Moved project #{workItem} to number [steelblue1]{to.ToString(CultureInfo.InvariantCulture)}[/]"));
    }

    // Adds a project to the end of the quicklist. Nothing is written if the work item or title is
    // invalid, the work item is already listed, or projects.json cannot be read.
    private CommandResult AddProject(string argument)
    {
        const string Name = "addproject";
        const string Usage = "Use [bold]addproject <work item> <title>[/].";
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

        if (ProjectTitleProblem(Name, title, Usage) is { } titleHint)
        {
            return titleHint;
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
        DayStamp stamp;
        try
        {
            (current, stamp) = LoadForWrite(day);
        }
        catch (DayFileChangedException)
        {
            return ChangedOnDisk(Name, day);
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
            Store.Save(day, current.RemoveAt(number), stamp);
        }
        catch (DayFileChangedException)
        {
            return ChangedOnDisk(Name, day);
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
        DayStamp stamp;
        try
        {
            (current, stamp) = LoadForWrite(day);
        }
        catch (DayFileChangedException)
        {
            return ChangedOnDisk(name, day);
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
            Store.Save(day, current.SetBooked(number, booked), stamp);
        }
        catch (DayFileChangedException)
        {
            return ChangedOnDisk(name, day);
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
        string usage = $"Use [bold]change <number> <{TimeInput.ShortForms}>[/].";
        string[] parts = argument.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
        {
            return Hint($"{Name}: say which entry and its new time. {usage}");
        }

        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int number))
        {
            return Hint($"{Name}: [bold]{Markup.Escape(parts[0])}[/] is not an entry number. {usage}");
        }

        if (!TryParseTime(Name, parts[1], out TimeOnly at, out CommandResult refused))
        {
            return refused;
        }

        DateOnly day = dayView.Current;
        DayRecord current;
        DayStamp stamp;
        try
        {
            (current, stamp) = LoadForWrite(day);
        }
        catch (DayFileChangedException)
        {
            return ChangedOnDisk(Name, day);
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
            Store.Save(day, current.ChangeTime(number, at), stamp);
        }
        catch (DayFileChangedException)
        {
            return ChangedOnDisk(Name, day);
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
        else if (!TryParseTime(name, argument, out at, out CommandResult refused))
        {
            return refused;
        }

        return Save(name, new DayEntry(kind, at));
    }

    // Books a named checkpoint: [H:mm|now|-<n>m|+<n>m] [#<project>] <name>. Only a token right after the optional time
    // that starts with '#' is a project number; it is looked up in the quicklist and the project's
    // work item is stored. Nothing is written if any part is invalid, and projects.json is read only
    // when a project number is given.
    private CommandResult Checkpoint(string argument)
    {
        const string Name = "checkpoint";
        string usage = $"Use [bold]{Markup.Escape($"checkpoint [{TimeInput.ShortForms}] [#project] <name>")}[/], " +
            "where #project is the project's number on the quicklist.";

        string rest = argument;
        TimeOnly at = Now();
        (string first, string afterFirst) = SplitToken(rest);
        TimeInputResult firstTime = TimeInput.Parse(first, at, out TimeOnly given);
        if (firstTime == TimeInputResult.Valid)
        {
            at = given;
            rest = afterFirst;
        }
        else if (firstTime == TimeInputResult.CrossesMidnight)
        {
            return CrossesMidnight(Name, first);
        }
        else if ((first.Contains(':') && first.All(c => c == ':' || char.IsAsciiDigit(c))) || TimeInput.LooksRelative(first))
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

    // A live clock tick, on the loop thread: the top area is redrawn only while the shown day is today. After
    // midnight the shown day stays as it was, so it no longer ticks.
    private bool ShowsToday() => dayView.Current == DateOnly.FromDateTime(time.GetLocalNow().DateTime);

    // A live clock tick, on the loop thread: checks the end-of-day alerts against today's file (not the shown
    // day) and, if any fires, rings once and shows its hint in place. Returns true if the top area must be
    // redrawn (today is shown) or a hint was shown (so the editor redraws the popup and the line after it).
    private bool Tick()
    {
        bool fired = CheckAlerts();
        return ShowsToday() || fired;
    }

    // Today's date and minute, from one clock read.
    private (DateOnly Date, TimeOnly Minute) Today()
    {
        DateTime local = time.GetLocalNow().DateTime;
        return (DateOnly.FromDateTime(local), new TimeOnly(local.Hour, local.Minute));
    }

    // Marks the end-of-day alerts already reached today as given, so a start past a threshold stays silent.
    // An unreadable day file primes nothing.
    private void PrimeAlerts()
    {
        (DateOnly today, TimeOnly now) = Today();
        if (TryLoadDay(today) is not { } day)
        {
            return;
        }

        AlertSettings(out TimeSpan target, out bool targetOn, out bool limitOn);
        alerts.Prime(today, day, target, now, targetOn, limitOn);
    }

    // Checks the end-of-day alerts for today; for any that fire, writes one bell and shows their hint in place.
    // An unreadable day file fires nothing and shows no error. Returns true if a hint was shown.
    private bool CheckAlerts()
    {
        (DateOnly today, TimeOnly now) = Today();
        if (TryLoadDay(today) is not { } day)
        {
            return false;
        }

        AlertSettings(out TimeSpan target, out bool targetOn, out bool limitOn);
        IReadOnlyList<EndOfDayAlert> fired = alerts.Check(today, day, target, now, targetOn, limitOn);
        if (fired.Count == 0)
        {
            return false;
        }

        List<string> parts = [];
        foreach (EndOfDayAlert alert in fired)
        {
            parts.Add(alert == EndOfDayAlert.Target
                ? $"[steelblue1]Daily target {DayView.FormatTimeLeft(target)} reached.[/]"
                : "[yellow]! 15 minutes left until 10 hours of work.[/]");
        }

        console.Write(new ControlCode("\a"));
        ShowHintInPlace(new Markup(string.Join(' ', parts)));
        return true;
    }

    // Loads a day file for the alerts, or null if it cannot be read. Never writes.
    private DayRecord? TryLoadDay(DateOnly date)
    {
        try
        {
            return Store.Load(date);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // The daily target and both alert settings, from one read of settings.json. A missing or broken file, or a
    // registry without an alert setting, leaves that alert on.
    private void AlertSettings(out TimeSpan target, out bool targetOn, out bool limitOn)
    {
        bool readable = LoadSettings(out SettingValues values) is null;
        target = DailyTarget(readable ? values : null);
        targetOn = !readable || IsOnOrMissing(values, SettingsRegistry.AlertTargetName);
        limitOn = !readable || IsOnOrMissing(values, SettingsRegistry.AlertLimitName);
    }

    // The three working-time checks from settings already read; null (a broken file) or a registry without a check
    // as an OnOff setting leaves that check on.
    private WorkingTimeCheckOptions CheckOptions(SettingValues? values) =>
        values is null
            ? WorkingTimeCheckOptions.All
            : new WorkingTimeCheckOptions(
                IsOnOrMissing(values, SettingsRegistry.CheckBreaksName),
                IsOnOrMissing(values, SettingsRegistry.CheckMaxWorkName),
                IsOnOrMissing(values, SettingsRegistry.CheckRestName));

    // Loads a day file for the working-time checks, or null if it cannot be read for any reason. Never writes.
    private DayRecord? TryLoadDayQuietly(DateOnly date)
    {
        try
        {
            return Store.Load(date);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    private bool IsOnOrMissing(SettingValues values, string name) =>
        !settings.TryGet(name, out Setting setting) || setting.Kind != SettingKind.OnOff || values.IsOn(name);

    // The current local minute, seconds dropped.
    private TimeOnly Now()
    {
        TimeOnly now = TimeOnly.FromDateTime(time.GetLocalNow().DateTime);
        return new TimeOnly(now.Hour, now.Minute);
    }

    // Parses a typed time against the current minute. On failure, refused holds the red hint.
    private bool TryParseTime(string name, string text, out TimeOnly at, out CommandResult refused)
    {
        refused = CommandResult.None;
        switch (TimeInput.Parse(text, Now(), out at))
        {
            case TimeInputResult.Valid:
                return true;
            case TimeInputResult.CrossesMidnight:
                refused = CrossesMidnight(name, text);
                return false;
            default:
                refused = NotATime(name, text);
                return false;
        }
    }

    private static CommandResult NotATime(string name, string text) =>
        Hint($"{name}: [bold]{Markup.Escape(text)}[/] is not a time. " +
            $"Use [bold]{Markup.Escape(TimeInput.Forms)}[/].");

    private static CommandResult CrossesMidnight(string name, string text) =>
        Hint($"{name}: [bold]{Markup.Escape(text)}[/] crosses midnight. " +
            $"Use a time on this day, from [bold]0:00[/] to [bold]23:59[/].");

    // Adds entry to the day being viewed. Nothing is written if the day's file cannot be read.
    private CommandResult Save(string name, DayEntry entry)
    {
        DateOnly day = dayView.Current;
        DayRecord current;
        DayStamp stamp;
        try
        {
            (current, stamp) = LoadForWrite(day);
        }
        catch (DayFileChangedException)
        {
            return ChangedOnDisk(name, day);
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
            Store.Save(day, updated, stamp);
        }
        catch (DayFileChangedException)
        {
            return ChangedOnDisk(name, day);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Hint($"{name}: could not save the day. {Markup.Escape(ex.Message)}");
        }

        return CommandResult.None;
    }

    // Loads the shown day for a command that writes it, with its stamp. Throws DayFileChangedException when the
    // file is not what the last redraw of that day showed (so the command never writes over a change it did not see).
    private (DayRecord Record, DayStamp Stamp) LoadForWrite(DateOnly day)
    {
        (DayRecord record, DayStamp stamp) = Store.LoadWithStamp(day);
        if (shownDay == day && shownStamp != stamp)
        {
            throw new DayFileChangedException(day);
        }

        return (record, stamp);
    }

    private static CommandResult ChangedOnDisk(string name, DateOnly day) =>
        Hint($"{name}: the file for {ReportDate(day)} changed on disk since it was shown. " +
            "The day is reloaded; check it and run the command again. Nothing was saved.");

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

        // One Times per data folder: taken before anything is read, drawn or started, released on every way out.
        InstanceLockResult locked = instanceLock.TryAcquire();
        if (locked.Status == InstanceLockStatus.Held)
        {
            console.MarkupLine("[red]Times is already running with this data folder " +
                $"[bold]{Markup.Escape(DataFolder.ForDisplay(Store.Folder))}[/]. Close the other copy first, then start Times again.[/]");
            return 3;
        }

        if (locked.Status == InstanceLockStatus.Failed)
        {
            console.MarkupLine($"[red]Times cannot start: {Markup.Escape(DataFolder.ForDisplay(locked.Reason))} " +
                $"in the data folder [bold]{Markup.Escape(DataFolder.ForDisplay(Store.Folder))}[/]. Nothing was saved.[/]");
            return 4;
        }

        try
        {
            return args.Length > 0 && !args[0].StartsWith('-') ? RunOneShot(args) : RunLocked();
        }
        finally
        {
            instanceLock.Dispose();
        }
    }

    // Commands that need the session (a prompt, the panel, another day or the end of the loop).
    private static readonly string[] SessionOnly = ["quit", "settings", "prev", "next", "today", "update", "import"];

    // One command from the shell against today: plain lines, no layout, mouse, clock or update check.
    // Exit code 0 on success, 1 on any error.
    private int RunOneShot(string[] args)
    {
        string text = string.Join(' ', args).Trim();
        dayView.Today();
        IRenderable today = RenderDay();

        if (string.Equals(text, "status", StringComparison.OrdinalIgnoreCase))
        {
            console.Write(today);
            return 0;
        }

        if (SessionOnlyName(text) is { } refused)
        {
            console.MarkupLine($"[red]{Markup.Escape(refused)} needs the interactive session. Run times without arguments.[/]");
            return 1;
        }

        CommandResult result = Dispatch(text);
        foreach (IRenderable? part in new[] { result.Message, result.Hint })
        {
            if (part is not null)
            {
                console.Write(part);
            }
        }

        return result.Failed ? 1 : 0;
    }

    // The name of the session-only command a one-shot line starts with, or null.
    private string? SessionOnlyName(string text)
    {
        string[] words = text.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            return null;
        }

        string token = words[0].StartsWith('/') ? words[0][1..] : words[0];
        if (!registry.TryGet(token, out Command? command))
        {
            return null;
        }

        if (SessionOnly.Contains(command.Name, StringComparer.OrdinalIgnoreCase))
        {
            return command.Name;
        }

        return string.Equals(command.Name, "devops", StringComparison.OrdinalIgnoreCase) && words.Length > 1
            && string.Equals(words[1], "connect", StringComparison.OrdinalIgnoreCase)
            ? "devops connect"
            : null;
    }

    private int RunLocked()
    {
        // The outcome of an update a previous run started, then the one start-up check, outside the loop.
        string? pending = UpdateApplier.ReadOutcome(Store.Folder, updates?.RunningVersion ?? AppVersion.Current);
        if (updates is not null)
        {
            startupCheck = updates.StartBackground(UpdateMode(), UpdateChannel(), Store.Folder);
        }

        // Mouse reporting is on only while the loop runs: off again on every exit, even an exception.
        mouse?.Enable();

        // The live clock ticks only through the background events; it is disposed on every way out.
        LiveClock? clock = events is not null && liveClock ? new LiveClock(time, events, Tick) : null;
        try
        {
            if (clock is not null)
            {
                PrimeAlerts();
            }

            clock?.Start();
            return Loop(pending is null ? null : new Markup(pending));
        }
        finally
        {
            clock?.Dispose();
            mouse?.Disable();
        }
    }

    private int Loop(IRenderable? startHint)
    {
        LineEditor editor = new LineEditor(console, registry, IsDevOpsLineWithArgument, watchResize);
        IRenderable? message = null;
        IRenderable? hint = startHint;
        bool hideQuicklist = false;

        while (true)
        {
            Draw(message, hint, hideQuicklist);

            // The cursor starts right after the prompt, on the placeholder's first character, not after it.
            InputGeometry geometry = Geometry();
            PlaceCursor();
            string? input = editor.ReadLine(geometry.Column, geometry.Line, geometry.Width, geometry.Popup, Placeholder, Scroll,
                ShowPasteCut, events, ApplyEvent, Resize, geometry.Hidden);
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

            if (connectOrganization is { } organization)
            {
                connectOrganization = null;
                if (ReadToken(editor, organization) is not { } connected)
                {
                    return 0;
                }

                result = connected;
            }

            if (pendingImport is { } import)
            {
                pendingImport = null;
                if (AskImportConflicts(editor, import) is not { } imported)
                {
                    return 0;
                }

                result = imported;
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
        shownMessage = message;
        shownHideQuicklist = hideQuicklist;
        SetTop(RenderTop(message, hideQuicklist));
        TakeStartupStaged();
        frameHint = StartupHint(hint) ?? Text.Empty;
        offset = 0;
        DrawFull();
    }

    // The top area's content: the quicklist (unless hidden), the day view, then the message, if any.
    private IRenderable RenderTop(IRenderable? message, bool hideQuicklist)
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

        return new Rows(parts);
    }

    // Called by the line editor after a background event changed the view: renders the top area again
    // (quicklist, day, the same message) and rewrites only its rows in place, like a scroll. The hint
    // rows, the input box and the scroll offset stay (the offset clamped if the content got shorter);
    // the editor then draws the popup and the input line on top. Returns true: rows were rewritten.
    private bool ApplyEvent()
    {
        SetTop(RenderTop(shownMessage, shownHideQuicklist));
        if (TooSmall)
        {
            return false;
        }

        int topHeight = TopHeight;
        offset = Math.Min(offset, TopArea.MaxOffset(topLines.Count, topHeight));
        frame = RenderFrame();
        for (int row = 1; row <= topHeight; row++)
        {
            RestoreRow(row);
        }

        return true;
    }

    private void SetTop(IRenderable top) =>
        topLines = Segment.SplitLines(top.Render(RenderOptions.Create(console, console.Profile.Capabilities), Math.Max(1, console.Profile.Width)));

    // Called by the line editor (and the settings panel) after the terminal size changed: renders what
    // is showing again at the new size (the settings panel with its selection in view, or the top area
    // with the last message and hidden-quicklist flag), keeps the hint (re-wrapped) and the offset
    // (clamped), and redraws the whole screen. Returns where the input line and popup now go.
    private InputGeometry Resize()
    {
        if (shownSettings is { } view)
        {
            SetTop(SettingsPanel.Render(settings, view.Values, view.Selected, view.Unreadable));
            offset = settings.Settings.Count == 0 || TooSmall
                ? offset
                : TopArea.OffsetShowing(SettingsPanel.LineOf(view.Selected, view.Unreadable is not null), topLines.Count, TopHeight, offset);
        }
        else
        {
            SetTop(RenderTop(shownMessage, shownHideQuicklist));
            offset = TooSmall ? offset : Math.Min(offset, TopArea.MaxOffset(topLines.Count, TopHeight));
        }

        DrawFull();
        PlaceCursor();
        return Geometry();
    }

    // Where the input line and the popup go at the current size. Inside the panel: border + 1 padding on
    // the left, then the prompt. The popup ends on the last hint row (just above the input box) and grows
    // upwards over the others. Hidden while the window is too small for the layout.
    private InputGeometry Geometry()
    {
        int height = console.Profile.Height;
        int width = console.Profile.Width;
        PopupArea popup = new PopupArea(height - InputHeight, width, RestoreRow);
        return new InputGeometry(InputColumn, height - 1, width - InputColumn - 2, popup, TooSmall);
    }

    // True while the terminal is below MinWidth × MinHeight: only the too-small line is drawn.
    private bool TooSmall => console.Profile.Width < MinWidth || console.Profile.Height < MinHeight;

    // Puts the cursor after the prompt, unless the window is too small (then nothing is written).
    private void PlaceCursor()
    {
        if (!TooSmall)
        {
            console.Cursor.SetPosition(InputColumn, InputLine);
        }
    }

    // The rows the screen now shows: the whole layout, or none while the window is too small.
    private List<SegmentLine> Relayout() => TooSmall ? [] : RenderFrame();

    // Clears the screen and writes only the grey too-small line on row 1, cut to the width. With a width
    // or height of 0 or less nothing is written at all.
    private void DrawTooSmall()
    {
        int width = console.Profile.Width;
        if (width <= 0 || console.Profile.Height <= 0)
        {
            return;
        }

        console.Clear(home: true);
        string shown = TooSmallText.Length > width ? TooSmallText[..width] : TooSmallText;
        console.Write(new Text(shown, new Style(Color.Grey)));
    }

    // Clears the screen and writes the whole layout for the current top lines, offset and hint (or only
    // the too-small line while the window is below MinWidth × MinHeight).
    private void DrawFull()
    {
        if (TooSmall)
        {
            frame = [];
            DrawTooSmall();
            return;
        }

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
        if (TooSmall)
        {
            return false;
        }

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
        try
        {
            return RunSettingsPanel(editor, selected, unreadable, values);
        }
        finally
        {
            shownSettings = null;
        }
    }

    private bool RunSettingsPanel(LineEditor editor, int selected, string? unreadable, SettingValues values)
    {
        ShowSettings(values, selected, unreadable, null, full: true);
        while (true)
        {
            ConsoleKeyInfo? read = editor.ReadKey(resized: () => Resize());
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
            return editor.ReadValue(InputColumn, InputLine, width - InputColumn - 2, framePlaceholder, out cancelled, ShowPasteCut,
                resized: Resize, hidden: TooSmall);
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
        shownSettings = new SettingsView(values, selected, unreadable);
        SetTop(SettingsPanel.Render(settings, values, selected, unreadable));
        offset = settings.Settings.Count == 0 || TooSmall
            ? 0
            : TopArea.OffsetShowing(SettingsPanel.LineOf(selected, unreadable is not null), topLines.Count, TopHeight, offset);
        if (full)
        {
            DrawFull();
        }
        else
        {
            frame = Relayout();
            for (int row = 1; row <= frame.Count; row++)
            {
                RestoreRow(row);
            }
        }

        PlaceCursor();
    }

    /// <summary>What the open settings panel shows: the values, the selected row and why the file cannot be read (or null).</summary>
    private sealed class SettingsView
    {
        public SettingsView(SettingValues values, int selected, string? unreadable)
        {
            Values = values;
            Selected = selected;
            Unreadable = unreadable;
        }

        public SettingValues Values { get; }

        public int Selected { get; }

        public string? Unreadable { get; }
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
    private TimeSpan DailyTarget() => DailyTarget(LoadSettings(out SettingValues values) is null ? values : null);

    // The daily-target setting from values already read, or the default for null (a broken file).
    private TimeSpan DailyTarget(SettingValues? values)
    {
        int minutes = SettingsRegistry.DailyTargetDefault;
        if (values is not null && settings.TryGet(SettingsRegistry.DailyTargetName, out Setting setting) && setting.Kind == SettingKind.Number)
        {
            int value = values.Number(SettingsRegistry.DailyTargetName);
            if (value >= 0 && value <= SettingsRegistry.DailyTargetMax)
            {
                minutes = value;
            }
        }

        return TimeSpan.FromMinutes(minutes);
    }

    // The vacation-days setting, read from settings.json at each call. A missing or broken file, an
    // out-of-range value or a registry without the setting gives the default (30). Reading never writes the file.
    private int VacationDays()
    {
        int days = SettingsRegistry.VacationDaysDefault;
        if (LoadSettings(out SettingValues values) is null
            && settings.TryGet(SettingsRegistry.VacationDaysName, out Setting setting) && setting.Kind == SettingKind.Number)
        {
            int value = values.Number(SettingsRegistry.VacationDaysName);
            if (value >= 0 && value <= SettingsRegistry.VacationDaysMax)
            {
                days = value;
            }
        }

        return days;
    }

    // A holiday Choice setting from already loaded values, or none when they are missing, broken or it is not registered.
    private string HolidaySetting(SettingValues? values, string name)
    {
        if (values is not null && settings.TryGet(name, out Setting setting) && setting.Kind == SettingKind.Choice)
        {
            return values.Choice(name);
        }

        return PublicHolidays.None;
    }

    private IRenderable RenderDay()
    {
        try
        {
            DateOnly shown = dayView.Current;
            shownDay = shown;
            shownStamp = null;
            (DayRecord day, DayStamp stamp) = Store.LoadWithStamp(shown);
            shownStamp = stamp;
            SettingValues? loaded = LoadSettings(out SettingValues values) is null ? values : null;
            TimeSpan target = DailyTarget(loaded);
            WorkingTimeCheckOptions options = CheckOptions(loaded);

            // Only today is live: one clock read gives both the date and the minute.
            DateTime local = time.GetLocalNow().DateTime;
            TimeOnly? now = shown == DateOnly.FromDateTime(local) ? new TimeOnly(local.Hour, local.Minute) : null;
            DayRecord? previous = shown == DateOnly.MinValue ? null : TryLoadDayQuietly(shown.AddDays(-1));
            WorkingTimeChecks checks = WorkingTimeChecks.Check(day, previous, options, now);
            IReadOnlyList<DateOnly> week = WorkingTimeChecks.WeekWarnings(
                shown, date => date == shown ? day : TryLoadDayQuietly(date), options, now);
            string? holiday = PublicHolidays.NameOf(
                shown, HolidaySetting(loaded, SettingsRegistry.HolidayCountryName), HolidaySetting(loaded, SettingsRegistry.HolidayStateName));
            return dayView.Render(day, target, FlexitimeBalance.Compute(Store, shown, target, now), now, checks, week, holiday);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return dayView.RenderUnreadable(Markup.Escape(ex.Message));
        }
    }

    // Called by the line editor when a paste was cut: shows its hint in place.
    private void ShowPasteCut() => ShowHintInPlace(new Markup(PasteCutHint));

    // Shows a hint on the hint rows in place, without a clear. If the hint keeps the same number of rows,
    // only those are rewritten; otherwise the top area changes size too, so every row is rewritten except
    // the input line, which holds the typed text the editor redraws next (with the popup, if open). The next
    // full redraw shows the hint it is given, as always.
    private void ShowHintInPlace(IRenderable hint)
    {
        if (TooSmall)
        {
            // Nothing is drawn now; the hint shows once the window is big enough again.
            frameHint = hint;
            return;
        }

        int before = HintHeight;
        frameHint = hint;
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
