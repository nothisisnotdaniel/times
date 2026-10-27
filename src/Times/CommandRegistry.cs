using Spectre.Console;
using Spectre.Console.Rendering;

namespace Times;

/// <summary>What a command handler hands back to the loop.</summary>
/// <param name="Message">Shown in the top area until the next command, or <c>null</c> for nothing.</param>
/// <param name="Quit">True to end the loop with exit code 0.</param>
internal sealed record CommandResult(IRenderable? Message = null, bool Quit = false)
{
    public static CommandResult None { get; } = new();
}

/// <summary>A registered command. <see cref="Handler"/> receives the text after the name, trimmed.</summary>
internal sealed record Command(string Name, string Description, Func<string, CommandResult> Handler);

/// <summary>
/// The single registry of <c>/</c> commands. Every feature adds its commands here, and
/// <c>/help</c> lists whatever is registered. Names are matched case-insensitively.
/// </summary>
internal sealed class CommandRegistry
{
    private readonly Dictionary<string, Command> _commands = new(StringComparer.OrdinalIgnoreCase);

    public IEnumerable<Command> Commands => _commands.Values.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates a registry with the built-in <c>/help</c> and <c>/quit</c> commands.</summary>
    public static CommandRegistry CreateDefault()
    {
        var registry = new CommandRegistry();
        registry.Register("/help", "List all commands", _ => new CommandResult(registry.RenderHelp()));
        registry.Register("/quit", "Exit Times", _ => new CommandResult(Quit: true));
        return registry;
    }

    public void Register(string name, string description, Func<string, CommandResult> handler)
    {
        if (!name.StartsWith('/') || name.Length < 2 || name.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException($"Command name must start with '/' and contain no spaces: '{name}'.", nameof(name));
        }

        if (!_commands.TryAdd(name, new Command(name, description, handler)))
        {
            throw new ArgumentException($"Command '{name}' is already registered.", nameof(name));
        }
    }

    public bool TryGet(string name, out Command command) => _commands.TryGetValue(name, out command!);

    /// <summary>
    /// Completes a command prefix. Returns the full name if exactly one command starts with
    /// <paramref name="prefix"/>, otherwise <c>null</c>.
    /// </summary>
    public string? Complete(string prefix)
    {
        if (!prefix.StartsWith('/'))
        {
            return null;
        }

        var matches = _commands.Keys.Where(n => n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    public IRenderable RenderHelp()
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .AddColumn("[grey]Command[/]")
            .AddColumn("[grey]Description[/]");

        foreach (var command in Commands)
        {
            table.AddRow(
                new Markup($"[steelblue1]{Markup.Escape(command.Name)}[/]"),
                new Text(command.Description));
        }

        return table;
    }
}
