using System.Globalization;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Times;

/// <summary>
/// The interactive main screen. Each iteration clears the screen, draws the full-terminal layout
/// (day view on top, a one-line hint row, a 3-line input box at the bottom), reads one line and dispatches it through the
/// <see cref="CommandRegistry"/>. All output goes through <see cref="IAnsiConsole"/> and all clock
/// reads through <see cref="TimeProvider"/>, so both can be faked in tests. Day files live in the
/// injected <see cref="DayStore"/>, so tests use a temp folder.
/// </summary>
internal sealed class App
{
    private const int HintHeight = 1;
    private const int InputHeight = 3;
    private const string Prompt = "> ";
    private const string Placeholder = "Type /help for commands";

    private readonly IAnsiConsole console;
    private readonly TimeProvider time;
    private readonly CommandRegistry registry;
    private readonly DayView dayView;
    private readonly bool folderFromEnvironment;
    private readonly ProjectStore projectStore;

    // The last drawn frame, one entry per screen row, so rows the command popup covered can be restored.
    private List<SegmentLine> frame = [];

    /// <param name="folderFromEnvironment">True if the store's folder came from <see cref="DataFolder.VariableName"/>.</param>
    public App(IAnsiConsole console, TimeProvider time, DayStore store, CommandRegistry? registry = null, bool folderFromEnvironment = false)
    {
        this.console = console;
        this.time = time;
        this.folderFromEnvironment = folderFromEnvironment;
        Store = store;
        projectStore = new ProjectStore(store.Folder);
        dayView = new DayView(time);
        this.registry = registry ?? CommandRegistry.CreateDefault();
        this.registry.HelpFooter = RenderDataFolder();
        RegisterNavigation();
        RegisterBookings();
        RegisterProjects();
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
        registry.Register("/prev", "Go to the previous day", NoArguments("/prev", () =>
            dayView.Previous() ? CommandResult.None : Hint("There is no day before this one.")));
        registry.Register("/next", "Go to the next day", NoArguments("/next", () =>
            dayView.Next() ? CommandResult.None : Hint("There is no day after this one.")));
        registry.Register("/today", "Go back to today", NoArguments("/today", () =>
        {
            dayView.Today();
            return CommandResult.None;
        }));
    }

    private void RegisterBookings()
    {
        registry.Register("/arrive", $"Book your arrival: now, or at an optional {TimeInput.Format}", argument =>
            Book("/arrive", argument, EntryKind.Arrival));
        registry.Register("/leave", $"Book your leaving: now, or at an optional {TimeInput.Format}", argument =>
            Book("/leave", argument, EntryKind.Leaving));
        registry.Register("/remove", "Remove an entry from this day by its number", Remove);
        registry.Register("/change", $"Change the time of an entry on this day: <number> <{TimeInput.Format}>", Change);
    }

    private void RegisterProjects()
    {
        registry.Register("/addproject", "Add a project to the quicklist: <work item> <title>", AddProject);
        registry.Register("/projects", "List the projects on the quicklist", NoArguments("/projects", ListProjects));
    }

    // Adds a project to the end of the quicklist. Nothing is written if the work item or title is
    // invalid, the work item is already listed, or projects.json cannot be read.
    private CommandResult AddProject(string argument)
    {
        const string Name = "/addproject";
        const string Usage = "Use [bold]/addproject <work item> <title>[/].";
        int split = argument.IndexOfAny([' ', '\t']);
        if (argument.Length == 0 || split < 0)
        {
            return Hint($"{Name}: say the work item number and the title. {Usage}");
        }

        string number = argument[..split];
        string title = argument[(split + 1)..].Trim();
        if (!int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out int workItem) || workItem < 1)
        {
            return Hint($"{Name}: [bold]{Markup.Escape(number)}[/] is not a work item number. {Usage}");
        }

        if (title.Length == 0)
        {
            return Hint($"{Name}: say the title. {Usage}");
        }

        if (title.Length > ProjectList.MaxTitleLength)
        {
            return Hint($"{Name}: the title is longer than {ProjectList.MaxTitleLength.ToString(CultureInfo.InvariantCulture)} characters.");
        }

        if (ProjectList.HasControlCharacters(title))
        {
            return Hint($"{Name}: the title must not contain control characters.");
        }

        ProjectList current;
        try
        {
            current = projectStore.Load();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return Hint($"{Name}: {UnreadableProjects(ex)} Nothing was added.");
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
            return Hint($"/projects: {UnreadableProjects(ex)}");
        }

        if (list.IsEmpty)
        {
            return new CommandResult(new Markup("[grey]No projects yet. Add one with /addproject <work item> <title>.[/]"));
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

        return new CommandResult(table);
    }

    private static string UnreadableProjects(Exception ex) =>
        $"the project list cannot be read: {Markup.Escape(ex.Message)}";

    // Removes entry <number> from the day being viewed. Nothing is written if the number is not a
    // plain number, there is no such entry, or the day's file cannot be read.
    private CommandResult Remove(string argument)
    {
        const string Name = "/remove";
        const string Usage = "Use [bold]/remove <number>[/].";
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

    // Changes the time of entry <number> on the day being viewed, keeping its kind. Nothing is
    // written if the argument is not a number and a time, there is no such entry, or the day's file
    // cannot be read.
    private CommandResult Change(string argument)
    {
        const string Name = "/change";
        string usage = $"Use [bold]/change <number> <{TimeInput.Format}>[/].";
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
            TimeOnly now = TimeOnly.FromDateTime(time.GetLocalNow().DateTime);
            at = new TimeOnly(now.Hour, now.Minute);
        }
        else if (!TimeInput.TryParse(argument, out at))
        {
            return Hint($"{name}: [bold]{Markup.Escape(argument)}[/] is not a time. " +
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
            return Hint($"{name}: {Markup.Escape(ex.Message)} Nothing was booked.");
        }

        DayRecord updated = current.Add(new DayEntry(kind, at));

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
            if (folderFromEnvironment)
            {
                reason = $"the data folder [bold]{Markup.Escape(Store.Folder)}[/] from {DataFolder.VariableName} is not an absolute path";
            }
            else if (string.IsNullOrWhiteSpace(Store.Folder))
            {
                reason = "the app-data folder could not be found (the path is empty)";
            }
            else
            {
                reason = $"the data folder [bold]{Markup.Escape(Store.Folder)}[/] is not an absolute path";
            }

            console.MarkupLine($"[red]Times cannot start: {reason}. Nothing was saved.[/]");
            return 2;
        }

        LineEditor editor = new LineEditor(console, registry);
        IRenderable? message = null;
        IRenderable? hint = null;

        while (true)
        {
            Draw(message, hint);

            int height = console.Profile.Height;
            int width = console.Profile.Width;
            // Inside the panel: border + 1 padding on the left, then the prompt. The cursor starts
            // right after the prompt, on the placeholder's first character, not after it.
            int column = 3 + Prompt.Length;
            int line = height - 1;
            console.Cursor.SetPosition(column, line);

            // The popup ends on the hint row (just above the input box) and grows upwards.
            PopupArea popup = new PopupArea(height - InputHeight, width, RestoreRow);
            string? input = editor.ReadLine(column, line, width - column - 2, popup, Placeholder);
            if (input is null)
            {
                return 0;
            }

            CommandResult result = Dispatch(input);
            if (result.Quit)
            {
                console.Clear(home: true);
                return 0;
            }

            message = result.Message;
            hint = result.Hint;
        }
    }

    private CommandResult Dispatch(string input)
    {
        string text = input.Trim();
        if (text.Length == 0)
        {
            return CommandResult.None;
        }

        if (!text.StartsWith('/'))
        {
            return Hint("Commands start with [bold]/[/]. Type [bold]/help[/] to see all commands.");
        }

        int split = text.IndexOfAny([' ', '\t']);
        string name = split < 0 ? text : text[..split];
        string argument = split < 0 ? string.Empty : text[(split + 1)..].Trim();

        if (!registry.TryGet(name, out Command? command))
        {
            return Hint($"Unknown command [bold]{Markup.Escape(name)}[/]. Type [bold]/help[/] to see all commands.");
        }

        try
        {
            return command.Handler(argument);
        }
        catch (Exception ex)
        {
            return Hint($"{Markup.Escape(command.Name)} failed: {Markup.Escape(ex.Message)}");
        }
    }

    private static CommandResult Hint(string markup) => CommandResult.Error(markup);

    // The line under the /help table, so the user can always tell where the day files are.
    private Markup RenderDataFolder()
    {
        string source = folderFromEnvironment ? $" [grey](from {DataFolder.VariableName})[/]" : string.Empty;
        return new Markup($"[grey]Data folder:[/] {Markup.Escape(Store.Folder)}{source}");
    }

    private void Draw(IRenderable? message, IRenderable? hint)
    {
        IRenderable day = RenderDay();
        IRenderable top = message is null
            ? day
            : new Rows(day, Text.Empty, message);

        Panel input = new Panel(new Markup($"[steelblue1]{Markup.Escape(Prompt)}[/][grey]{Markup.Escape(Placeholder)}[/]"))
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.SteelBlue1)
            .Expand();

        int topHeight = console.Profile.Height - HintHeight - InputHeight;

        Layout layout = new Layout("Root").SplitRows(
            new Layout("Day", TopArea.Fit(top, topHeight)),
            new Layout("Hint", hint ?? Text.Empty).Size(HintHeight),
            new Layout("Input", input).Size(InputHeight));

        frame = Segment.SplitLines(layout.GetSegments(console));
        console.Clear(home: true);
        console.Write(new SegmentsRenderable(frame.SelectMany(l => l.Append(Segment.LineBreak)).SkipLast(1)));
    }

    private IRenderable RenderDay()
    {
        try
        {
            return dayView.Render(Store.Load(dayView.Current));
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return dayView.RenderUnreadable(Markup.Escape(ex.Message));
        }
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
