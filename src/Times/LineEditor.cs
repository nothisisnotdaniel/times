using Spectre.Console;

namespace Times;

/// <summary>
/// A small line editor for the input box. It reads keys with <c>ReadKey(intercept: true)</c> and,
/// after each key, redraws only the command popup's rows and the input line via
/// <c>Cursor.SetPosition</c>, never the whole screen. Supports printable characters, Backspace,
/// Enter, Esc (close the popup if it is open, otherwise clear the line), Tab (complete a
/// <c>/command</c>) and Up/Down (session history).
/// <para>
/// PageUp/PageDown scroll the screen above the input box when a scroll callback is given:
/// Shift+PageUp/PageDown and Ctrl+Up/Down do the same (macOS Terminal keeps plain PageUp/PageDown
/// for its own scrollback). The editor calls the callback with -1 (up) or +1 (down); when it
/// reports that it redrew something, the editor draws the popup (if open) again on top and then
/// the input line, so the cursor ends after the typed text. Scroll keys never change the line, the
/// history position or the popup state. Without a callback they do nothing. Plain Up/Down stay
/// history.
/// </para>
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
/// <para>
/// When a placeholder is given, it is shown in grey whenever the line is empty and blanked as soon
/// as the line holds text. It is never part of the returned line, and the cursor stays at the
/// start of the line while it is shown.
/// </para>
/// <para>
/// A line wider than the room shows only its tail, behind a grey leading <c>…</c> in the first
/// cell that tells the user the start is scrolled out of view. The cue goes away as soon as the
/// whole line fits again, and it is never part of the returned line or the history.
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
    /// <paramref name="placeholder"/> is grey text shown while the line is empty, or <c>null</c> for
    /// none. The editor assumes the caller has already drawn it at <paramref name="column"/> and
    /// redraws it (cropped to the room) whenever the line becomes empty again.
    /// <paramref name="scroll"/> is called with -1 or +1 for a scroll key and returns <c>true</c> if
    /// it redrew anything, or is <c>null</c> to ignore scroll keys.
    /// Returns <c>null</c> if the input has no more keys.
    /// </summary>
    public string? ReadLine(int column, int line, int width, PopupArea? popup = null, string? placeholder = null, Func<int, bool>? scroll = null)
    {
        string buffer = string.Empty;
        int historyIndex = history.Count;
        int drawn = placeholder is null ? 0 : Crop(placeholder, Room(width)).Length;
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

            ConsoleKeyInfo info = key.Value;
            if (ScrollDirection(info) is { } direction)
            {
                if (scroll is not null && scroll(direction))
                {
                    if (popupRows > 0)
                    {
                        popupRows = DrawPopup(popup!, buffer, popupRows, tabList);
                    }

                    drawn = Redraw(buffer, column, line, width, drawn, placeholder);
                }

                continue;
            }

            tabList = false;

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
            drawn = Redraw(buffer, column, line, width, drawn, placeholder);
        }
    }

    // -1 for PageUp, Shift+PageUp and Ctrl+Up; +1 for PageDown, Shift+PageDown and Ctrl+Down;
    // null for every other key, including plain Up/Down (history).
    private static int? ScrollDirection(ConsoleKeyInfo info)
    {
        bool control = (info.Modifiers & ConsoleModifiers.Control) != 0;
        return info.Key switch
        {
            ConsoleKey.PageUp => -1,
            ConsoleKey.PageDown => 1,
            ConsoleKey.UpArrow when control => -1,
            ConsoleKey.DownArrow when control => 1,
            _ => null,
        };
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

    // Rewrites the visible part of the buffer and blanks whatever was drawn before. A buffer wider
    // than the room (of at least 2 cells) shows a grey leading … followed by its last room - 1
    // characters, filling the whole room; with a room of 1 only the last character is shown. An
    // empty buffer shows the placeholder in grey, if there is one, with the cursor at the start of
    // the line. Returns the number of cells now drawn.
    private int Redraw(string buffer, int column, int line, int width, int previouslyDrawn, string? placeholder)
    {
        int room = Room(width);
        console.Cursor.SetPosition(column, line);

        if (buffer.Length == 0 && placeholder is not null)
        {
            string shown = Crop(placeholder, room);
            console.Write(new Text(shown, new Style(Color.Grey)));
            if (previouslyDrawn > shown.Length)
            {
                console.Write(new Text(new string(' ', previouslyDrawn - shown.Length)));
            }

            console.Cursor.SetPosition(column, line);
            return shown.Length;
        }

        if (buffer.Length > room && room >= 2)
        {
            console.Write(new Text("\u2026", new Style(Color.Grey)));
            console.Write(new Text(buffer[^(room - 1)..]));
            console.Cursor.SetPosition(column + room, line);
            return room;
        }

        string visible = buffer.Length > room ? buffer[^room..] : buffer;
        console.Write(new Text(visible.PadRight(previouslyDrawn)));
        console.Cursor.SetPosition(column + visible.Length, line);
        return visible.Length;
    }

    private static int Room(int width) => Math.Max(1, width - 1);

    private static string Crop(string text, int room) => text.Length > room ? text[..room] : text;
}
