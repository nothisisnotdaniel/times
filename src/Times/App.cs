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

    private readonly IAnsiConsole console;
    private readonly TimeProvider time;
    private readonly CommandRegistry registry;
    private readonly DayView dayView;

    // The last drawn frame, one entry per screen row, so rows the command popup covered can be restored.
    private List<SegmentLine> frame = [];

    public App(IAnsiConsole console, TimeProvider time, DayStore store, CommandRegistry? registry = null)
    {
        this.console = console;
        this.time = time;
        Store = store;
        dayView = new DayView(time);
        this.registry = registry ?? CommandRegistry.CreateDefault();
        RegisterNavigation();
        RegisterBookings();
    }

    /// <summary>Where the day files live. Booking features read and write through it.</summary>
    public DayStore Store { get; }

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
            Book("/arrive", argument, (day, at) => day with { Arrival = at }));
        registry.Register("/leave", $"Book your leaving: now, or at an optional {TimeInput.Format}", argument =>
            Book("/leave", argument, (day, at) => day with { Leaving = at }));
    }

    // Books a time on the day being viewed. Nothing is written if the time is invalid, the day's
    // file cannot be read, or leaving would not be after arrival.
    private CommandResult Book(string name, string argument, Func<DayRecord, TimeOnly, DayRecord> apply)
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

        DayRecord updated = apply(current, at);
        if (updated.Arrival is { } arrival && updated.Leaving is { } leaving && leaving <= arrival)
        {
            return Hint($"{name}: leaving ([bold]{Clock(leaving)}[/]) must be after arrival ([bold]{Clock(arrival)}[/]).");
        }

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

    private static string Clock(TimeOnly value) => value.ToString("HH:mm", CultureInfo.InvariantCulture);

    private static Func<string, CommandResult> NoArguments(string name, Func<CommandResult> handler) =>
        argument => argument.Length == 0
            ? handler()
            : Hint($"[bold]{Markup.Escape(name)}[/] takes no arguments.");

    public int Run(string[] args)
    {
        // Before anything is drawn, read or written: never fall back to the working directory.
        if (!DayStore.IsValidFolder(Store.Folder))
        {
            string reason = string.IsNullOrWhiteSpace(Store.Folder)
                ? "the app-data folder could not be found (the path is empty)"
                : $"the data folder [bold]{Markup.Escape(Store.Folder)}[/] is not an absolute path";
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
            // Inside the panel: border + 1 padding on the left, then the prompt.
            int column = 3 + Prompt.Length;
            int line = height - 1;
            console.Cursor.SetPosition(column, line);

            // The popup ends on the hint row (just above the input box) and grows upwards.
            PopupArea popup = new PopupArea(height - InputHeight, width, RestoreRow);
            string? input = editor.ReadLine(column, line, width - column - 2, popup);
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

    private void Draw(IRenderable? message, IRenderable? hint)
    {
        IRenderable day = RenderDay();
        IRenderable top = message is null
            ? day
            : new Rows(day, Text.Empty, message);

        Panel input = new Panel(new Markup($"[steelblue1]{Markup.Escape(Prompt)}[/]"))
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
