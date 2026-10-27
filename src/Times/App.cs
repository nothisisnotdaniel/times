using Spectre.Console;
using Spectre.Console.Rendering;

namespace Times;

/// <summary>
/// The interactive main screen. Each iteration clears the screen, draws the full-terminal layout
/// (day view on top, a 3-line input box at the bottom), reads one line and dispatches it through the
/// <see cref="CommandRegistry"/>. All output goes through <see cref="IAnsiConsole"/> and all clock
/// reads through <see cref="TimeProvider"/>, so both can be faked in tests.
/// </summary>
internal sealed class App(IAnsiConsole console, TimeProvider time, CommandRegistry? registry = null)
{
    private const int InputHeight = 3;
    private const string Prompt = "> ";

    private readonly CommandRegistry _registry = registry ?? CommandRegistry.CreateDefault();
    private readonly DayView _dayView = new(time);

    public int Run(string[] args)
    {
        var editor = new LineEditor(console, _registry);
        IRenderable? message = null;

        while (true)
        {
            Draw(message);

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

        if (!_registry.TryGet(name, out var command))
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

    private static CommandResult Hint(string markup) => new(new Markup($"[red]{markup}[/]"));

    private void Draw(IRenderable? message)
    {
        IRenderable top = message is null
            ? _dayView.Render()
            : new Rows(_dayView.Render(), Text.Empty, message);

        var input = new Panel(new Markup($"[steelblue1]{Markup.Escape(Prompt)}[/]"))
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.SteelBlue1)
            .Expand();

        var layout = new Layout("Root").SplitRows(
            new Layout("Day", top),
            new Layout("Input", input).Size(InputHeight));

        console.Clear(home: true);
        console.Write(layout);
    }
}
