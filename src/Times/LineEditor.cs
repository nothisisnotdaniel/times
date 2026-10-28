using Spectre.Console;

namespace Times;

/// <summary>
/// A small line editor for the input box. It reads keys with <c>ReadKey(intercept: true)</c> and,
/// after each key, redraws only the command popup's rows and the input line via
/// <c>Cursor.SetPosition</c>, never the whole screen. Supports printable characters, Backspace,
/// Enter, Esc (close the popup if it is open, otherwise clear the line), Tab (complete a
/// <c>/command</c>) and Up/Down (session history).
/// <para>
/// Tab completes a unique command prefix. When 2 or more names start with the typed prefix, Tab
/// extends the line to their longest common prefix and enters the "Tab list" state: the popup
/// (reopened even after Esc) lists only <see cref="CommandRegistry.StartingWith"/> for the line,
/// with a grey footer <c>N commands start with …</c>. The next key of any kind leaves that state,
/// and the popup goes back to <see cref="CommandRegistry.Match"/>. Without a popup area only the
/// line is extended.
/// </para>
/// <para>
/// When a <see cref="PopupArea"/> is given, the popup opens while the line starts with <c>/</c>
/// and lists <see cref="CommandRegistry.Match"/> for it. Esc closes it and keeps it closed until
/// the line stops starting with <c>/</c>. Rows the popup stops covering are handed back to
/// <see cref="PopupArea.RestoreRow"/>, and the cursor always ends at the end of the input line.
/// </para>
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
    /// <paramref name="popup"/> says where the command popup may draw, or <c>null</c> for no popup.
    /// Returns <c>null</c> if the input has no more keys.
    /// </summary>
    public string? ReadLine(int column, int line, int width, PopupArea? popup = null)
    {
        string buffer = string.Empty;
        int historyIndex = history.Count;
        int drawn = 0;
        int popupRows = 0;
        bool popupDismissed = false;
        bool tabList = false;

        while (true)
        {
            ConsoleKeyInfo? key = console.Input.ReadKey(intercept: true);
            if (key is null)
            {
                ClosePopup(popup, popupRows);
                return null;
            }

            tabList = false;

            ConsoleKeyInfo info = key.Value;
            switch (info.Key)
            {
                case ConsoleKey.Enter:
                    if (buffer.Trim().Length > 0 && (history.Count == 0 || history[^1] != buffer))
                    {
                        history.Add(buffer);
                    }

                    if (popupRows > 0)
                    {
                        ClosePopup(popup, popupRows);
                        console.Cursor.SetPosition(column + drawn, line);
                    }

                    return buffer;

                case ConsoleKey.Backspace:
                    if (buffer.Length > 0)
                    {
                        buffer = buffer[..^1];
                    }

                    break;

                case ConsoleKey.Escape:
                    if (popupRows > 0)
                    {
                        popupDismissed = true;
                    }
                    else
                    {
                        buffer = string.Empty;
                        historyIndex = history.Count;
                    }

                    break;

                case ConsoleKey.Tab:
                    if (buffer.Any(char.IsWhiteSpace))
                    {
                        break;
                    }

                    if (registry.StartingWith(buffer).Count >= 2)
                    {
                        buffer = registry.CommonPrefix(buffer);
                        popupDismissed = false;
                        tabList = true;
                    }
                    else if (registry.Complete(buffer) is { } completed)
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

            if (!buffer.StartsWith('/'))
            {
                popupDismissed = false;
            }

            bool showPopup = popup is not null && buffer.StartsWith('/') && !popupDismissed;
            popupRows = showPopup ? DrawPopup(popup!, buffer, popupRows, tabList) : ClosePopup(popup, popupRows);
            drawn = Redraw(buffer, column, line, width, drawn);
        }
    }

    // Draws the popup for the current line so that it ends on the bottom row, and restores the rows
    // above it that the previous, taller popup covered. In the Tab list it shows the prefix matches
    // with a count footer instead of the fuzzy match. Returns the number of rows now drawn.
    private int DrawPopup(PopupArea popup, string buffer, int previousRows, bool tabList)
    {
        IReadOnlyList<string> rows;
        if (tabList)
        {
            IReadOnlyList<Command> matches = registry.StartingWith(buffer);
            rows = CommandPopup.Rows(matches, popup.Width, popup.MaxRows, $"{matches.Count} commands start with {buffer}");
        }
        else
        {
            rows = CommandPopup.Rows(registry.Match(buffer), popup.Width, popup.MaxRows);
        }

        int top = popup.BottomRow - rows.Count + 1;
        for (int row = popup.BottomRow - previousRows + 1; row < top; row++)
        {
            popup.RestoreRow(row);
        }

        for (int i = 0; i < rows.Count; i++)
        {
            console.Cursor.SetPosition(1, top + i);
            console.Markup(rows[i]);
        }

        return rows.Count;
    }

    // Restores every row the popup covered. Returns 0, the number of popup rows left.
    private static int ClosePopup(PopupArea? popup, int previousRows)
    {
        if (popup is null)
        {
            return 0;
        }

        for (int row = popup.BottomRow - previousRows + 1; row <= popup.BottomRow; row++)
        {
            popup.RestoreRow(row);
        }

        return 0;
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
