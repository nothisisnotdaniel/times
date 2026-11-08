using System.Globalization;
using System.Text;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Times;

/// <summary>What a command handler hands back to the loop.</summary>
internal sealed record CommandResult
{
    /// <param name="message">
    /// Shown in the top area (under the day view) until the next command, or <c>null</c> for
    /// nothing.
    /// </param>
    /// <param name="quit">True to end the loop with exit code 0.</param>
    /// <param name="hint">
    /// Shown on the hint rows (up to 3, wrapped) just above the input box until the next command, or
    /// <c>null</c> for nothing.
    /// </param>
    /// <param name="hideQuicklist">
    /// True to leave out the quicklist panel while <paramref name="message"/> is shown, so long
    /// command output such as help has room.
    /// </param>
    public CommandResult(
        IRenderable? message = null,
        bool quit = false,
        IRenderable? hint = null,
        bool hideQuicklist = false,
        bool failed = false)
    {
        Failed = failed;
        Message = message;
        Quit = quit;
        Hint = hint;
        HideQuicklist = hideQuicklist;
    }

    public static CommandResult None { get; } = new();

    /// <summary>
    /// A red hint for validation errors. <paramref name="markup"/> is wrapped in
    /// <c>[red]…[/]</c>; any user text in it must already be escaped with
    /// <see cref="Markup.Escape"/>.
    /// </summary>
    internal static CommandResult Error(string markup) => new(hint: new Markup($"[red]{markup}[/]"), failed: true);

    /// <summary>
    /// Shown in the top area (under the day view) until the next command, or <c>null</c> for
    /// nothing.
    /// </summary>
    public IRenderable? Message { get; }

    /// <summary>
    /// Shown on the hint rows (up to 3, wrapped) just above the input box until the next command, or
    /// <c>null</c> for nothing.
    /// </summary>
    public IRenderable? Hint { get; }

    /// <summary>True to end the loop with exit code 0.</summary>
    public bool Quit { get; }

    /// <summary>
    /// True to leave out the quicklist panel until the next command, while <see cref="Message"/> is
    /// shown.
    /// </summary>
    public bool HideQuicklist { get; }

    /// <summary>True for an error result (<see cref="Error"/>); a one-shot command then exits with 1.</summary>
    public bool Failed { get; }
}

/// <summary>
/// A registered command. <see cref="Handler"/> receives the text after the name (or alias),
/// trimmed.
/// </summary>
internal sealed record Command
{
    public Command(string name, string alias, string description, Func<string, CommandResult> handler)
    {
        Name = name;
        Alias = alias;
        Description = description;
        Handler = handler;
    }

    public string Name { get; }

    /// <summary>A short name of one or two ASCII letters that runs the same handler.</summary>
    public string Alias { get; }

    public string Description { get; }

    public Func<string, CommandResult> Handler { get; }
}

/// <summary>
/// The single registry of commands. Every feature adds its commands here, and <c>help</c> lists
/// whatever is registered. Names are stored without a leading <c>/</c> and matched
/// case-insensitively. Every command also has a unique alias of one or two ASCII letters, which
/// <see cref="TryGet"/> and <see cref="Match"/> accept too. The lookup helpers accept a prefix
/// with or without one leading <c>/</c> and keep that <c>/</c> in what they return.
/// </summary>
internal sealed class CommandRegistry
{
    private readonly Dictionary<string, Command> commands = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Command> aliases = new(StringComparer.OrdinalIgnoreCase);

    public IEnumerable<Command> Commands => commands.Values.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Creates a registry with the built-in <c>help</c> and <c>quit</c> commands.
    /// </summary>
    public static CommandRegistry CreateDefault()
    {
        CommandRegistry registry = new CommandRegistry();
        // App.Dispatch renders help itself so it can add the data-folder footer; keep both in step.
        registry.Register(
            "help",
            "h",
            "List all commands",
            _ => new CommandResult(registry.RenderHelp(), hideQuicklist: true));
        registry.Register("quit", "q", "Exit Times", _ => new CommandResult(quit: true));
        return registry;
    }

    /// <summary>
    /// Adds a command. Throws <see cref="ArgumentException"/> (and adds nothing) when the name is
    /// empty, starts with <c>/</c> or holds whitespace; when the alias is not one or two ASCII
    /// letters; or when the name or alias equals (ignoring case) any registered name or alias, or
    /// the alias equals the command's own name. The name and alias quoted in the exception message
    /// have their control characters, Unicode Format (Cf) code points and unpaired surrogates
    /// removed and are then escaped with <see cref="Markup.Escape"/>.
    /// </summary>
    public void Register(string name, string alias, string description, Func<string, CommandResult> handler)
    {
        if (name.Length == 0 || name.StartsWith('/') || name.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException(
                $"Command name must not be empty, start with '/' or contain spaces: '{Quote(name)}'.",
                nameof(name));
        }

        if (alias.Length is < 1 or > 2 || !alias.All(char.IsAsciiLetter))
        {
            throw new ArgumentException(
                $"Alias of '{Quote(name)}' must be one or two ASCII letters: '{Quote(alias)}'.",
                nameof(alias));
        }

        if (commands.ContainsKey(name) || aliases.ContainsKey(name))
        {
            throw new ArgumentException($"Command '{Quote(name)}' is already registered.", nameof(name));
        }

        if (string.Equals(alias, name, StringComparison.OrdinalIgnoreCase)
            || commands.ContainsKey(alias)
            || aliases.ContainsKey(alias))
        {
            throw new ArgumentException($"Alias '{Quote(alias)}' of '{Quote(name)}' is already taken.", nameof(alias));
        }

        Command command = new Command(name, alias, description, handler);
        commands.Add(name, command);
        aliases.Add(alias, command);
    }

    /// <summary>
    /// Makes <paramref name="text"/> safe to quote in an exception message: removes control
    /// characters, Unicode Format (Cf) code points and unpaired surrogates, then escapes the rest
    /// with <see cref="Markup.Escape"/> (after the removal, so removal cannot leave an unescaped
    /// bracket).
    /// </summary>
    private static string Quote(string text)
    {
        StringBuilder kept = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    if (CharUnicodeInfo.GetUnicodeCategory(text, i) != UnicodeCategory.Format)
                    {
                        kept.Append(c).Append(text[i + 1]);
                    }

                    i++;
                }

                continue;
            }

            if (char.IsLowSurrogate(c)
                || char.IsControl(c)
                || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format)
            {
                continue;
            }

            kept.Append(c);
        }

        return Markup.Escape(kept.ToString());
    }

    /// <summary>
    /// Finds a command by its name or its alias, ignoring case. Neither may carry a leading
    /// <c>/</c>.
    /// </summary>
    public bool TryGet(string name, out Command command) =>
        commands.TryGetValue(name, out command!) || aliases.TryGetValue(name, out command!);

    /// <summary>
    /// Completes a command prefix, typed with or without one leading <c>/</c>. Returns the full
    /// name (with the <c>/</c> if one was typed) if exactly one command starts with the bare
    /// prefix, otherwise <c>null</c>. An empty prefix gives <c>null</c>.
    /// </summary>
    public string? Complete(string prefix)
    {
        if (prefix.Length == 0)
        {
            return null;
        }

        string bare = Bare(prefix);
        List<string> matches = commands.Keys
            .Where(n => n.StartsWith(bare, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToList();
        return matches.Count == 1 ? Slash(prefix) + matches[0] : null;
    }

    /// <summary>
    /// The commands whose name starts with <paramref name="prefix"/> (ordinal, ignoring case, one
    /// leading <c>/</c> ignored), in name order. Empty for an empty prefix; a lone <c>/</c> gives
    /// every command.
    /// </summary>
    public IReadOnlyList<Command> StartingWith(string prefix)
    {
        if (prefix.Length == 0)
        {
            return [];
        }

        string bare = Bare(prefix);
        return Commands.Where(c => c.Name.StartsWith(bare, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>
    /// The longest common prefix of the names in <see cref="StartingWith"/>, in the registered
    /// casing and with the <c>/</c> if one was typed, or <paramref name="prefix"/> unchanged when
    /// fewer than 2 names match.
    /// </summary>
    public string CommonPrefix(string prefix)
    {
        IReadOnlyList<Command> matches = StartingWith(prefix);
        if (matches.Count < 2)
        {
            return prefix;
        }

        string common = matches[0].Name;
        foreach (Command command in matches.Skip(1))
        {
            int length = 0;
            int max = Math.Min(common.Length, command.Name.Length);
            while (length < max && char.ToLowerInvariant(common[length]) == char.ToLowerInvariant(command.Name[length]))
            {
                length++;
            }

            common = common[..length];
        }

        return Slash(prefix) + common;
    }

    // The prefix without its one leading '/', if it has one.
    private static string Bare(string prefix) => prefix.StartsWith('/') ? prefix[1..] : prefix;

    // "/" if the user typed one in front of the prefix, otherwise empty.
    private static string Slash(string prefix) => prefix.StartsWith('/') ? "/" : string.Empty;

    /// <summary>
    /// The commands that match the command token of <paramref name="line"/> (the text up to the
    /// first whitespace, one leading <c>/</c> ignored), best first. An empty token (empty line or
    /// just <c>/</c>) returns every command in name order. Otherwise each name scores the smaller
    /// Levenshtein distance to the whole name or to its prefix of the token's length, so a prefix
    /// scores 0 and typos still match. A command is kept if its score is 0 for a 1-letter token, at
    /// most 1 for 2–3 letters and at most 2 for longer tokens. A command is also kept (with
    /// score 0) when its alias starts with the token, ignoring case; aliases get no typo matching.
    /// A command whose alias equals the token is listed first, then the rest sorted by score, then
    /// by name.
    /// </summary>
    public IReadOnlyList<Command> Match(string line)
    {
        int end = 0;
        while (end < line.Length && !char.IsWhiteSpace(line[end]))
        {
            end++;
        }

        string token = line[..end];
        if (token.StartsWith('/'))
        {
            token = token[1..];
        }

        token = token.ToLowerInvariant();
        if (token.Length == 0)
        {
            return Commands.ToList();
        }

        int limit = token.Length switch
        {
            1 => 0,
            <= 3 => 1,
            _ => 2,
        };

        List<(Command Command, int Score)> scored = [];
        foreach (Command command in commands.Values)
        {
            string name = command.Name.ToLowerInvariant();
            string prefix = name[..Math.Min(token.Length, name.Length)];
            int score = Math.Min(Levenshtein(token, name), Levenshtein(token, prefix));
            if (command.Alias.StartsWith(token, StringComparison.OrdinalIgnoreCase))
            {
                score = 0;
            }

            if (score <= limit)
            {
                scored.Add((command, score));
            }
        }

        return scored
            .OrderBy(s => string.Equals(s.Command.Alias, token, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(s => s.Score)
            .ThenBy(s => s.Command.Name, StringComparer.OrdinalIgnoreCase)
            .Select(s => s.Command)
            .ToList();
    }

    /// <summary>
    /// The Levenshtein (edit) distance between two strings: inserts, deletes and substitutions.
    /// </summary>
    internal static int Levenshtein(string a, string b)
    {
        int[] previous = new int[b.Length + 1];
        int[] current = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (int i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }

    /// <summary>
    /// Renders the <c>help</c> table, with an optional <paramref name="footer"/> below it.
    /// </summary>
    public IRenderable RenderHelp(IRenderable? footer = null)
    {
        Table table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .AddColumn("[grey]Command[/]")
            .AddColumn("[grey]Alias[/]")
            .AddColumn("[grey]Description[/]");

        foreach (Command command in Commands)
        {
            table.AddRow(
                new Markup($"[steelblue1]{Markup.Escape(command.Name)}[/]"),
                new Markup($"[steelblue1]{Markup.Escape(command.Alias)}[/]"),
                new Text(command.Description));
        }

        return footer is null ? table : new Rows(table, footer);
    }
}
