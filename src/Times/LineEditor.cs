using Spectre.Console;

namespace Times;

/// <summary>
/// A small line editor for the input box. It reads keys with <c>ReadKey(intercept: true)</c> and
/// redraws only the input line via <c>Cursor.SetPosition</c>. Supports printable characters,
/// Backspace, Enter, Esc (clear), Tab (complete a <c>/command</c>) and Up/Down (session history).
/// </summary>
internal sealed class LineEditor
{
    private readonly IAnsiConsole console;
    private readonly CommandRegistry registry;
    private readonly List<string> history = [];

    public LineEditor(IAnsiConsole console, CommandRegistry registry)
    {
        this.console = console;
        this.registry = registry;
    }

    public IReadOnlyList<string> History => history;

    /// <summary>
    /// Reads one line. <paramref name="column"/> and <paramref name="line"/> are the 1-based screen
    /// position where the text starts, and <paramref name="width"/> is the room available for it.
    /// Returns <c>null</c> if the input has no more keys.
    /// </summary>
    public string? ReadLine(int column, int line, int width)
    {
        string buffer = string.Empty;
        int historyIndex = history.Count;
        int drawn = 0;

        while (true)
        {
            ConsoleKeyInfo? key = console.Input.ReadKey(intercept: true);
            if (key is null)
            {
                return null;
            }

            ConsoleKeyInfo info = key.Value;
            switch (info.Key)
            {
                case ConsoleKey.Enter:
                    if (buffer.Trim().Length > 0 && (history.Count == 0 || history[^1] != buffer))
                    {
                        history.Add(buffer);
                    }

                    return buffer;

                case ConsoleKey.Backspace:
                    if (buffer.Length > 0)
                    {
                        buffer = buffer[..^1];
                    }

                    break;

                case ConsoleKey.Escape:
                    buffer = string.Empty;
                    historyIndex = history.Count;
                    break;

                case ConsoleKey.Tab:
                    if (!buffer.Any(char.IsWhiteSpace) && registry.Complete(buffer) is { } completed)
                    {
                        buffer = completed;
                    }

                    break;

                case ConsoleKey.UpArrow:
                    if (history.Count > 0)
                    {
                        historyIndex = Math.Max(0, historyIndex - 1);
                        buffer = history[historyIndex];
                    }

                    break;

                case ConsoleKey.DownArrow:
                    if (historyIndex < history.Count)
                    {
                        historyIndex++;
                        buffer = historyIndex == history.Count ? string.Empty : history[historyIndex];
                    }

                    break;

                default:
                    if (info.KeyChar != '\0' && !char.IsControl(info.KeyChar))
                    {
                        buffer += info.KeyChar;
                    }

                    break;
            }

            drawn = Redraw(buffer, column, line, width, drawn);
        }
    }

    // Rewrites the visible part of the buffer (its tail, if it is wider than the box) and blanks
    // whatever was drawn before. Returns the number of characters now drawn.
    private int Redraw(string buffer, int column, int line, int width, int previouslyDrawn)
    {
        int room = Math.Max(1, width - 1);
        string visible = buffer.Length > room ? buffer[^room..] : buffer;

        console.Cursor.SetPosition(column, line);
        console.Write(new Text(visible.PadRight(previouslyDrawn)));
        console.Cursor.SetPosition(column + visible.Length, line);
        return visible.Length;
    }
}
