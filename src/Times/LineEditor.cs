using Spectre.Console;

namespace Times;

/// <summary>How far one scroll request moves: a page (scroll keys) or a wheel notch.</summary>
internal enum ScrollStep
{
    Page,
    Wheel,
}

/// <summary>
/// A small line editor for the input box. It reads keys with <c>ReadKey(intercept: true)</c> and,
/// after each key, redraws only the command popup's rows and the input line via
/// <c>Cursor.SetPosition</c>, never the whole screen. Supports printable characters, Backspace,
/// Enter, Esc (close the popup if it is open, otherwise clear the line), Tab (complete a
/// command name, with or without a leading <c>/</c>) and Up/Down (session history).
/// <para>
/// PageUp/PageDown scroll the screen above the input box when a scroll callback is given:
/// Shift+PageUp/PageDown and Ctrl+Up/Down do the same (macOS Terminal keeps plain PageUp/PageDown
/// for its own scrollback). The editor calls the callback with -1 (up) or +1 (down) and
/// <see cref="ScrollStep.Page"/>; when it reports that it redrew something, the editor draws the
/// popup (if open) again on top and then the input line, so the cursor ends after the typed text.
/// Scroll keys never change the line, the history position or the popup state. Without a callback
/// they do nothing. Plain Up/Down stay history.
/// </para>
/// <para>
/// Mouse wheel: after an Esc key (<c>Key == Escape</c> or <c>KeyChar == '\u001b'</c>), if another
/// key is already waiting and it is <c>[</c>, the editor reads the rest of the escape sequence
/// (blocking, up to its final character <c>@</c>–<c>~</c>) and decodes it with
/// <see cref="MouseInput"/>. A sequence longer than <see cref="MouseInput.MaxKeys"/> keys is still
/// read to its end, keeping at most that many keys, and then dropped whole: none of it is typed. A
/// wheel notch calls the scroll callback with -1 or +1 and <see cref="ScrollStep.Wheel"/>, exactly
/// like a scroll key; clicks, horizontal wheel and unknown
/// sequences are dropped silently. Neither touches the line, the history position, the popup or the
/// Tab list. A lone Esc (nothing waiting) is a plain Esc, and an Esc followed by any other key is a
/// plain Esc and then that key.
/// </para>
/// <para>
/// Bracketed paste: everything between <c>ESC[200~</c> and <c>ESC[201~</c> is appended to the
/// line as text in one step, never submitted. Each line break (<c>\r</c>, <c>\n</c> or
/// <c>\r\n</c>, as an Enter key or as a <c>KeyChar</c>) and each Tab becomes one space; other
/// control characters, and any other <c>ESC [</c> sequence inside the paste, are dropped. The
/// popup and the line are then drawn as for the same text typed key by key.
/// </para>
/// <para>
/// Tab completes a unique command prefix; on an empty line it does nothing. When 2 or more names
/// start with the typed prefix, Tab extends the line to their longest common prefix and
/// enters the "Tab list" state: the popup (reopened even after Esc) lists only
/// <see cref="CommandRegistry.StartingWith"/> for the line, with a grey footer <c>N commands start
/// with …</c>. The next key of any kind leaves that state, and the popup goes back to
/// <see cref="CommandRegistry.Match"/>. Without a popup area only the line is extended.
/// </para>
/// <para>
/// When a <see cref="PopupArea"/> is given, the popup opens while the line is not empty and does
/// not start with whitespace, and lists <see cref="CommandRegistry.Match"/> for it. Esc closes it
/// and keeps it closed until the line is empty again. Rows the popup stops covering are handed back
/// to <see cref="PopupArea.RestoreRow"/>, and the cursor always ends at the end of the input line.
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

    // A key read after a plain Esc in ReadKey, handed to the next read so it is not lost.
    private ConsoleKeyInfo? carried;

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
    /// <paramref name="placeholder"/> is grey text shown while the line is empty, or <c>null</c>
    /// for none. The editor assumes the caller has already drawn it at <paramref name="column"/>
    /// and redraws it (cropped to the room) whenever the line becomes empty again.
    /// <paramref name="scroll"/> is called with -1 or +1 and the kind of step for a scroll key or a
    /// wheel notch and returns <c>true</c> if it redrew anything, or is <c>null</c> to ignore both.
    /// Returns <c>null</c> if the input has no more keys, also in the middle of an escape sequence
    /// or a paste.
    /// </summary>
    public string? ReadLine(int column, int line, int width, PopupArea? popup = null, string? placeholder = null, Func<int, ScrollStep, bool>? scroll = null) =>
        Read(column, line, width, popup, placeholder, scroll, value: false, out _);

    /// <summary>
    /// Reads a value (not a command) at the same place as <see cref="ReadLine"/>: no popup, no
    /// scrolling, no Tab completion and no history (↑/↓ do nothing and the value is not added).
    /// Esc cancels: <paramref name="cancelled"/> is set and an empty string is returned. Mouse and
    /// paste sequences behave as in <see cref="ReadLine"/>. Returns <c>null</c> if the input has no
    /// more keys.
    /// </summary>
    public string? ReadValue(int column, int line, int width, string? placeholder, out bool cancelled) =>
        Read(column, line, width, null, placeholder, null, value: true, out cancelled);

    /// <summary>
    /// Reads one key for a dialog such as the settings panel. A mouse sequence (an Esc with a
    /// <c>[</c> waiting) is read to its end and dropped, and a bracketed paste is read to its end
    /// and dropped, so neither reaches the dialog as keys. Any Esc comes back as
    /// <see cref="ConsoleKey.Escape"/>; a key that followed a plain Esc is kept for the next read.
    /// Returns <c>null</c> if the input has no more keys.
    /// </summary>
    public ConsoleKeyInfo? ReadKey()
    {
        while (true)
        {
            ConsoleKeyInfo? key = carried ?? console.Input.ReadKey(intercept: true);
            carried = null;
            if (key is null)
            {
                return null;
            }

            if (!IsEscape(key.Value))
            {
                return key;
            }

            ConsoleKeyInfo escape = new ConsoleKeyInfo('\u001b', ConsoleKey.Escape, false, false, false);
            if (!console.Input.IsKeyAvailable())
            {
                return escape;
            }

            ConsoleKeyInfo? next = console.Input.ReadKey(intercept: true);
            if (next is null)
            {
                return null;
            }

            if (next.Value.KeyChar != '[')
            {
                carried = next;
                return escape;
            }

            MouseInputKind? kind = ReadSequence();
            if (kind is null || (kind == MouseInputKind.PasteStart && ReadPaste() is null))
            {
                return null;
            }
        }
    }

    private string? Read(int column, int line, int width, PopupArea? popup, string? placeholder,
        Func<int, ScrollStep, bool>? scroll, bool value, out bool cancelled)
    {
        cancelled = false;
        string buffer = string.Empty;
        int historyIndex = history.Count;
        int drawn = placeholder is null ? 0 : Crop(placeholder, Room(width)).Length;
        int popupRows = 0;
        bool popupDismissed = false;
        bool tabList = false;
        ConsoleKeyInfo? pending = carried;
        carried = null;

        while (true)
        {
            ConsoleKeyInfo? key = pending ?? console.Input.ReadKey(intercept: true);
            pending = null;
            if (key is null)
            {
                ClosePopup(popup, popupRows);
                return null;
            }

            ConsoleKeyInfo info = key.Value;
            ScrollStep step = ScrollStep.Page;
            int? direction = ScrollDirection(info);
            string? pasted = null;
            if (IsEscape(info))
            {
                info = new ConsoleKeyInfo('\u001b', ConsoleKey.Escape, false, false, false);
                if (console.Input.IsKeyAvailable())
                {
                    ConsoleKeyInfo? next = console.Input.ReadKey(intercept: true);
                    if (next is { KeyChar: '[' })
                    {
                        MouseInputKind? kind = ReadSequence();
                        if (kind == MouseInputKind.PasteStart)
                        {
                            pasted = ReadPaste();
                        }

                        if (kind is null || (kind == MouseInputKind.PasteStart && pasted is null))
                        {
                            ClosePopup(popup, popupRows);
                            return null;
                        }

                        if (kind is MouseInputKind.WheelUp or MouseInputKind.WheelDown)
                        {
                            direction = kind == MouseInputKind.WheelUp ? -1 : 1;
                            step = ScrollStep.Wheel;
                        }
                        else if (pasted is null)
                        {
                            continue;
                        }
                    }
                    else
                    {
                        // A plain Esc now, then that key on the next turn.
                        if (next is null)
                        {
                            ClosePopup(popup, popupRows);
                            return null;
                        }

                        pending = next;
                    }
                }
            }

            if (direction is { } scrollDirection)
            {
                if (scroll is not null && scroll(scrollDirection, step))
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

            if (pasted is not null)
            {
                buffer += pasted;
            }
            else
            {
                switch (info.Key)
                {
                    case ConsoleKey.Enter:
                        if (!value && buffer.Trim().Length > 0 && (history.Count == 0 || history[^1] != buffer))
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
                        if (value)
                        {
                            // A key that came right after the Esc belongs to whoever reads next.
                            carried = pending;
                            cancelled = true;
                            return string.Empty;
                        }

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
                        if (value || buffer.Any(char.IsWhiteSpace))
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
                        if (!value && history.Count > 0)
                        {
                            historyIndex = Math.Max(0, historyIndex - 1);
                            buffer = history[historyIndex];
                        }

                        break;

                    case ConsoleKey.DownArrow:
                        if (!value && historyIndex < history.Count)
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
            }

            if (buffer.Length == 0)
            {
                popupDismissed = false;
            }

            bool showPopup = popup is not null && OpensPopup(buffer) && !popupDismissed;
            popupRows = showPopup ? DrawPopup(popup!, buffer, popupRows, tabList) : ClosePopup(popup, popupRows);
            drawn = Redraw(buffer, column, line, width, drawn, placeholder);
        }
    }

    // The popup is for a command being typed: a non-empty line whose first character is not whitespace.
    private static bool OpensPopup(string buffer) => buffer.Length > 0 && !char.IsWhiteSpace(buffer[0]);

    private static bool IsEscape(ConsoleKeyInfo info) => info.Key == ConsoleKey.Escape || info.KeyChar == '\u001b';

    // Reads the rest of an escape sequence whose ESC and '[' were already read, up to its final
    // character, and decodes it. Waits for the keys that are still on their way (a slow connection
    // may split a sequence). Keeps at most MouseInput.MaxKeys keys; a longer sequence is read to its
    // end and then discarded, so none of its tail is typed. Returns null if the input runs out.
    private MouseInputKind? ReadSequence()
    {
        string keys = "[";
        bool tooLong = false;
        while (!MouseInput.IsComplete(keys))
        {
            ConsoleKeyInfo? next = console.Input.ReadKey(intercept: true);
            if (next is null)
            {
                return null;
            }

            char c = next.Value.KeyChar;
            if (keys.Length < MouseInput.MaxKeys)
            {
                keys += c;
            }
            else if (MouseInput.IsFinal(c))
            {
                return MouseInputKind.Discard;
            }
            else
            {
                tooLong = true;
            }
        }

        return tooLong ? MouseInputKind.Discard : MouseInput.Decode(keys);
    }

    // Reads a bracketed paste whose ESC[200~ was already read, up to ESC[201~, and returns its text:
    // line breaks (\r, \n, \r\n) and Tabs as one space each, other control characters and other
    // ESC [ sequences dropped. Returns null if the input runs out first.
    private string? ReadPaste()
    {
        System.Text.StringBuilder text = new System.Text.StringBuilder();
        bool afterCarriageReturn = false;
        ConsoleKeyInfo? pending = null;
        while (true)
        {
            ConsoleKeyInfo? key = pending ?? console.Input.ReadKey(intercept: true);
            pending = null;
            if (key is null)
            {
                return null;
            }

            if (IsEscape(key.Value))
            {
                ConsoleKeyInfo? next = console.Input.ReadKey(intercept: true);
                if (next is { KeyChar: '[' })
                {
                    MouseInputKind? kind = ReadSequence();
                    if (kind is null)
                    {
                        return null;
                    }

                    if (kind == MouseInputKind.PasteEnd)
                    {
                        return text.ToString();
                    }
                }
                else
                {
                    // A stray Esc is a control character: dropped. The key after it is pasted as usual.
                    if (next is null)
                    {
                        return null;
                    }

                    pending = next;
                }

                afterCarriageReturn = false;
                continue;
            }

            char c = PastedChar(key.Value);
            if (c == '\n' && afterCarriageReturn)
            {
                afterCarriageReturn = false;
                continue;
            }

            afterCarriageReturn = c == '\r';
            if (c is '\r' or '\n' or '\t')
            {
                text.Append(' ');
            }
            else if (c != '\0' && !char.IsControl(c))
            {
                text.Append(c);
            }
        }
    }

    // The character a pasted key stands for: Enter is a line break (\n if its KeyChar says so,
    // otherwise \r) and Tab a tab, whatever their KeyChar; every other key is its KeyChar.
    private static char PastedChar(ConsoleKeyInfo info) => info.Key switch
    {
        ConsoleKey.Enter => info.KeyChar == '\n' ? '\n' : '\r',
        ConsoleKey.Tab => '\t',
        _ => info.KeyChar,
    };

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
