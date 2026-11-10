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
/// popup and the line are then drawn as for the same text typed key by key. A paste keeps at most
/// <see cref="MaxPasteLength"/> characters after that clean-up (never half a surrogate pair); the
/// rest is still read up to <c>ESC[201~</c> but dropped, never typed or submitted, and the
/// <c>pasteCut</c> callback, if given, is called once for that paste.
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
/// <para>
/// Resize: given a resize callback, the editor compares <c>console.Profile.Width/Height</c>
/// with the size it last saw before each fresh key (where events are applied, never inside an
/// escape sequence or a paste). On a change it calls the callback once, at the latest size,
/// which redraws the screen and hands back the new <see cref="InputGeometry"/>; the editor then
/// draws the popup (if open) and the line on top. The line, the history position, the popup
/// state and the Tab list stay. While the geometry is hidden (a window too small for the
/// layout) keys still edit the line, but nothing is drawn. With events, a size change wakes the
/// wait like a key; without events, the editor polls the size every
/// <see cref="BackgroundEventQueue.PollInterval"/> while it waits, but only when resize
/// watching is on.
/// </para>
/// </summary>
internal sealed class LineEditor
{
    private readonly IAnsiConsole console;
    private readonly CommandRegistry registry;
    private readonly List<string> history = [];

    // Lines this returns true for are never added to the history (a line that may hold a secret).
    private readonly Func<string, bool>? keepOutOfHistory;

    // A key read after a plain Esc in ReadKey, handed to the next read so it is not lost.
    private ConsoleKeyInfo? carried;

    // True to poll the terminal size while waiting for a key (without events), so a resize redraws at once.
    private readonly bool watchResize;

    // True while the current read draws nothing (the window is too small for the layout).
    private bool hidden;

    // The terminal size at the last check. A line or value read starts from the size its caller just drew
    // at; ReadKey goes on from the last check, so a change during the key a read returned is still seen.
    private (int Width, int Height) seen;

    /// <summary>
    /// The most UTF-16 characters one bracketed paste keeps, counted after the clean-up (line
    /// breaks and Tab as a space, other control characters and sequences dropped). The rest is
    /// dropped.
    /// </summary>
    public const int MaxPasteLength = 4096;

    /// <param name="keepOutOfHistory">
    /// Says which submitted lines never enter the history (for example a line that may hold a
    /// token), or <c>null</c> to keep every non-blank line.
    /// </param>
    /// <param name="watchResize">
    /// True to poll the terminal size every <see cref="BackgroundEventQueue.PollInterval"/> while
    /// a read with a resize callback waits for a key, so a resize is redrawn without a key press.
    /// </param>
    public LineEditor(IAnsiConsole console, CommandRegistry registry, Func<string, bool>? keepOutOfHistory = null,
        bool watchResize = false)
    {
        this.watchResize = watchResize;
        this.console = console;
        seen = Size();
        this.registry = registry;
        this.keepOutOfHistory = keepOutOfHistory;
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
    /// <paramref name="pasteCut"/> is called once for each paste that was cut to
    /// <see cref="MaxPasteLength"/> characters, after the kept text was added to the line and
    /// before the line is redrawn, or is <c>null</c> for no notice.
    /// <paramref name="events"/> is the background event queue, or <c>null</c> for none: then
    /// the editor only ever blocks in <c>ReadKey</c>. With a queue, before each fresh key (not
    /// inside an escape sequence or a paste, and not while a key carried from a plain Esc is
    /// pending) the editor waits for a key or an event, applies every queued event in order on
    /// this thread, and repeats until a round finds none. An event whose <c>Apply()</c> throws is
    /// dropped. After an event that changed the view, <paramref name="applied"/> is called; when
    /// it reports that it redrew rows, the editor draws the popup (if open) again on top and then
    /// the input line. The line, the history position, the popup state and the Tab list stay.
    /// <paramref name="resized"/> is called once when the terminal size changed before a fresh
    /// key; it redraws the screen and returns where the line (and popup) now go, or is
    /// <c>null</c> to ignore resizes. <paramref name="hidden"/> starts the read without drawing
    /// anything (the window is too small), until a resize returns a geometry that is not hidden.
    /// Returns <c>null</c> if the input has no more keys, also in the middle of an escape sequence
    /// or a paste.
    /// </summary>
    public string? ReadLine(int column, int line, int width, PopupArea? popup = null, string? placeholder = null,
        Func<int, ScrollStep, bool>? scroll = null, Action? pasteCut = null, IBackgroundEvents? events = null,
        Func<bool>? applied = null, Func<InputGeometry>? resized = null, bool hidden = false) =>
        Read(column, line, width, popup, placeholder, scroll, pasteCut, value: false, out _, masked: false, events, applied,
            resized, hidden);

    /// <summary>
    /// Reads a value (not a command) at the same place as <see cref="ReadLine"/>: no popup, no
    /// scrolling, no Tab completion and no history (↑/↓ do nothing and the value is not added).
    /// Esc cancels: <paramref name="cancelled"/> is set and an empty string is returned. Mouse and
    /// paste sequences behave as in <see cref="ReadLine"/>, including the
    /// <see cref="MaxPasteLength"/> cap and the <paramref name="pasteCut"/> callback. Returns
    /// <c>null</c> if the input has no more keys. With <paramref name="masked"/>, every
    /// character of the value (typed or pasted) is drawn as <see cref="MaskCharacter"/>,
    /// never as itself, for a secret such as a token (also when a resize redraws it).
    /// <paramref name="resized"/> and <paramref name="hidden"/> work as in <see cref="ReadLine"/>.
    /// </summary>
    public string? ReadValue(int column, int line, int width, string? placeholder, out bool cancelled, Action? pasteCut = null,
        bool masked = false, Func<InputGeometry>? resized = null, bool hidden = false) =>
        Read(column, line, width, null, placeholder, null, pasteCut, value: true, out cancelled, masked, null, null, resized, hidden);

    /// <summary>What a masked value shows for each of its characters.</summary>
    public const char MaskCharacter = '\u2022';

    /// <summary>
    /// Reads one key for a dialog such as the settings panel. A mouse sequence (an Esc with a
    /// <c>[</c> waiting) is read to its end and dropped, and a bracketed paste is read to its end
    /// and dropped (keeping at most <see cref="MaxPasteLength"/> characters while it reads; the
    /// <paramref name="pasteCut"/> callback is never called, since nothing of the paste is used),
    /// so neither reaches the dialog as keys. Any Esc comes back as
    /// <see cref="ConsoleKey.Escape"/>; a key that followed a plain Esc is kept for the next read.
    /// <paramref name="resized"/> is called once when the terminal size changed before a fresh key
    /// (it redraws the dialog), or is <c>null</c> to ignore resizes.
    /// Returns <c>null</c> if the input has no more keys.
    /// </summary>
    public ConsoleKeyInfo? ReadKey(Action? pasteCut = null, Action? resized = null)
    {
        while (true)
        {
            if (carried is null && resized is not null)
            {
                if (watchResize)
                {
                    WaitForKeyOrResize();
                }

                if (TakeSize())
                {
                    resized();
                    continue;
                }
            }

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
            if (kind is null || (kind == MouseInputKind.PasteStart && ReadPaste(out _) is null))
            {
                return null;
            }
        }
    }

    private string? Read(int column, int line, int width, PopupArea? popup, string? placeholder,
        Func<int, ScrollStep, bool>? scroll, Action? pasteCut, bool value, out bool cancelled, bool masked,
        IBackgroundEvents? events, Func<bool>? applied, Func<InputGeometry>? resized, bool startHidden)
    {
        cancelled = false;
        hidden = startHidden;
        seen = Size();
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
            while (pending is null)
            {
                if (events is not null)
                {
                    // Events run here, on the loop thread, between keys; only their redraw touches the screen.
                    ApplyEvents(events, applied, () =>
                    {
                        if (popupRows > 0)
                        {
                            popupRows = DrawPopup(popup!, buffer, popupRows, tabList);
                        }

                        drawn = Redraw(masked ? new string(MaskCharacter, buffer.Length) : buffer, column, line, width, drawn, placeholder);
                    }, resized is null ? null : () => Size() != seen);
                }
                else if (resized is not null && watchResize)
                {
                    WaitForKeyOrResize();
                }

                if (resized is null || !TakeSize())
                {
                    break;
                }

                // The callback redrew the whole screen at the latest size: the popup and the line go on top.
                InputGeometry geometry = resized();
                column = geometry.Column;
                line = geometry.Line;
                width = geometry.Width;
                hidden = geometry.Hidden;
                if (popup is not null)
                {
                    popup = geometry.Popup ?? popup;
                }

                drawn = placeholder is null ? 0 : Crop(placeholder, Room(width)).Length;
                if (popupRows > 0)
                {
                    popupRows = DrawPopup(popup!, buffer, 0, tabList);
                }

                drawn = Redraw(masked ? new string(MaskCharacter, buffer.Length) : buffer, column, line, width, drawn, placeholder);
            }

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
            bool cut = false;
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
                            pasted = ReadPaste(out cut);
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

                    drawn = Redraw(masked ? new string(MaskCharacter, buffer.Length) : buffer, column, line, width, drawn, placeholder);
                }

                continue;
            }

            tabList = false;

            if (pasted is not null)
            {
                buffer += pasted;
                if (cut)
                {
                    pasteCut?.Invoke();
                }
            }
            else
            {
                switch (info.Key)
                {
                    case ConsoleKey.Enter:
                        if (!value && buffer.Trim().Length > 0 && (history.Count == 0 || history[^1] != buffer)
                            && keepOutOfHistory?.Invoke(buffer) != true)
                        {
                            history.Add(buffer);
                        }

                        if (popupRows > 0 && !hidden)
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
            drawn = Redraw(masked ? new string(MaskCharacter, buffer.Length) : buffer, column, line, width, drawn, placeholder);
        }
    }

    // Waits for a key or an event, then applies every queued event in FIFO order; after each one that
    // changed the view and whose redraw (applied) wrote rows, redrawOnTop draws the popup and the line
    // again. An event that throws is dropped and the rest still run. Repeats until a round finds no
    // event, so a key is waiting (or the read should block).
    // With sizeChanged, a size change also ends the wait (the caller then redraws for it).
    private void ApplyEvents(IBackgroundEvents events, Func<bool>? applied, Action redrawOnTop, Func<bool>? sizeChanged)
    {
        Func<bool> wake = sizeChanged is null ? console.Input.IsKeyAvailable : () => console.Input.IsKeyAvailable() || sizeChanged();
        while (true)
        {
            events.WaitForKeyOrEvent(wake);
            bool any = false;
            while (events.TryTake(out BackgroundEvent? next))
            {
                any = true;
                bool changed;
                try
                {
                    changed = next?.Apply() == true;
                }
                catch (Exception)
                {
                    // A broken event must never end the session: it is dropped.
                    changed = false;
                }

                if (changed && applied?.Invoke() == true)
                {
                    redrawOnTop();
                }
            }

            if (!any)
            {
                return;
            }
        }
    }

    // The terminal size as Spectre reports it now (the live console size unless a test set one).
    private (int Width, int Height) Size() => (console.Profile.Width, console.Profile.Height);

    // True, once, when the size differs from seen, which then becomes the size now.
    private bool TakeSize()
    {
        (int Width, int Height) now = Size();
        if (now == seen)
        {
            return false;
        }

        seen = now;
        return true;
    }

    // Blocks until a key is waiting or the size differs from the last check, checking every PollInterval. If the
    // input cannot say whether a key is waiting (redirected), it returns, so the read blocks as before.
    private void WaitForKeyOrResize()
    {
        while (true)
        {
            try
            {
                if (console.Input.IsKeyAvailable())
                {
                    return;
                }
            }
            catch (InvalidOperationException)
            {
                return;
            }

            if (Size() != seen)
            {
                return;
            }

            Thread.Sleep(BackgroundEventQueue.PollInterval);
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
    // ESC [ sequences dropped. Keeps at most MaxPasteLength of those characters, never a lone high
    // surrogate at the cut; once full, it reads on to the end marker and stores nothing more, and
    // cut says whether anything was dropped that way. Returns null if the input runs out first.
    private string? ReadPaste(out bool cut)
    {
        cut = false;
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
            char kept;
            if (c is '\r' or '\n' or '\t')
            {
                kept = ' ';
            }
            else if (c != '\0' && !char.IsControl(c))
            {
                kept = c;
            }
            else
            {
                continue;
            }

            if (cut)
            {
                continue;
            }

            int left = MaxPasteLength - text.Length;
            if (left == 0 || (left == 1 && char.IsHighSurrogate(kept)))
            {
                cut = true;
                continue;
            }

            text.Append(kept);
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
    // While hidden nothing is drawn, but the popup counts as open (1 row) so its state stays.
    private int DrawPopup(PopupArea popup, string buffer, int previousRows, bool tabList)
    {
        if (hidden)
        {
            return 1;
        }

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
    private int ClosePopup(PopupArea? popup, int previousRows)
    {
        if (popup is null || hidden)
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
    // While hidden nothing is drawn and 0 is returned.
    private int Redraw(string buffer, int column, int line, int width, int previouslyDrawn, string? placeholder)
    {
        if (hidden)
        {
            return 0;
        }

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

/// <summary>
/// Where the line editor draws after a resize: the 1-based column and line the text starts at, the
/// room for it, the popup area (or <c>null</c> to keep the one the read started with), and whether
/// nothing may be drawn at all (a window too small for the layout).
/// </summary>
internal sealed class InputGeometry
{
    public InputGeometry(int column, int line, int width, PopupArea? popup, bool hidden)
    {
        Column = column;
        Line = line;
        Width = width;
        Popup = popup;
        Hidden = hidden;
    }

    public int Column { get; }

    public int Line { get; }

    public int Width { get; }

    public PopupArea? Popup { get; }

    public bool Hidden { get; }
}
