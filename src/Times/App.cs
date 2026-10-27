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
    private readonly CommandRegistry registry;
    private readonly DayView dayView;

    public App(IAnsiConsole console, TimeProvider time, DayStore store, CommandRegistry? registry = null)
    {
        this.console = console;
        Store = store;
        dayView = new DayView(time);
        this.registry = registry ?? CommandRegistry.CreateDefault();
        RegisterNavigation();
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

    private static Func<string, CommandResult> NoArguments(string name, Func<CommandResult> handler) =>
        argument => argument.Length == 0
            ? handler()
            : Hint($"[bold]{Markup.Escape(name)}[/] takes no arguments.");

    public int Run(string[] args)
    {
        var editor = new LineEditor(console, registry);
        IRenderable? message = null;
        IRenderable? hint = null;

        while (true)
        {
            Draw(message, hint);

            var height = console.Profile.Height;
            var width = console.Profile.Width;
            // Inside the panel: border + 1 padding on the left, then the prompt.
            var column = 3 + Prompt.Length;
            var line = height - 1;
            console.Cursor.SetPosition(column, line);

            var input = editor.ReadLine(column, line, width - column - 2);
            if (input is null)
            {
                return 0;
            }

            var result = Dispatch(input);
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
        var text = input.Trim();
        if (text.Length == 0)
        {
            return CommandResult.None;
        }

        if (!text.StartsWith('/'))
        {
            return Hint("Commands start with [bold]/[/]. Type [bold]/help[/] to see all commands.");
        }

        var split = text.IndexOfAny([' ', '\t']);
        var name = split < 0 ? text : text[..split];
        var argument = split < 0 ? string.Empty : text[(split + 1)..].Trim();

        if (!registry.TryGet(name, out var command))
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
        IRenderable top = message is null
            ? dayView.Render()
            : new Rows(dayView.Render(), Text.Empty, message);

        var input = new Panel(new Markup($"[steelblue1]{Markup.Escape(Prompt)}[/]"))
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.SteelBlue1)
            .Expand();

        var layout = new Layout("Root").SplitRows(
            new Layout("Day", top),
            new Layout("Hint", hint ?? Text.Empty).Size(HintHeight),
            new Layout("Input", input).Size(InputHeight));

        console.Clear(home: true);
        console.Write(layout);
    }
}
