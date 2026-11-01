using System.Globalization;

namespace Times;

/// <summary>What the keys after an Esc turned out to be.</summary>
internal enum MouseInputKind
{
    /// <summary>The keys do not start with <c>[</c>: the Esc is a plain Esc key.</summary>
    NotASequence,

    /// <summary>A mouse wheel notch up (with or without Shift, Alt or Ctrl).</summary>
    WheelUp,

    /// <summary>A mouse wheel notch down (with or without Shift, Alt or Ctrl).</summary>
    WheelDown,

    /// <summary>A click, release, drag or horizontal wheel: ignored.</summary>
    OtherMouse,

    /// <summary>Some other <c>ESC [</c> sequence, or one cut off at <see cref="MouseInput.MaxKeys"/>: ignored.</summary>
    Discard,
}

/// <summary>
/// Decodes the keys that follow an Esc into a mouse event. Pure: it only looks at the characters it
/// is given (the <c>KeyChar</c> of each key), never reads or writes anything. Supports SGR
/// (<c>ESC [ &lt; b ; x ; y M|m</c>, mode 1006) only. The pointer position is never used.
/// </summary>
internal static class MouseInput
{
    /// <summary>The most keys (after the Esc, starting with <c>[</c>) read for one sequence.</summary>
    public const int MaxKeys = 32;

    // Button-code bits: 4 Shift, 8 Alt/Meta, 16 Ctrl. They do not change what the wheel does.
    private const int ModifierBits = 4 | 8 | 16;
    private const int WheelUpCode = 64;
    private const int WheelDownCode = 65;

    /// <summary>
    /// True once <paramref name="keys"/> (everything after the Esc) is a whole sequence: a <c>[…</c>
    /// at its final character (<c>@</c>–<c>~</c>), or at <see cref="MaxKeys"/> keys. Also true for
    /// keys that do not start with <c>[</c>.
    /// </summary>
    public static bool IsComplete(string keys)
    {
        if (keys.Length == 0)
        {
            return false;
        }

        if (keys[0] != '[' || keys.Length >= MaxKeys)
        {
            return true;
        }

        return keys.Length >= 2 && IsFinal(keys[^1]);
    }

    /// <summary>Decodes everything after the Esc. Incomplete or unknown sequences are <see cref="MouseInputKind.Discard"/>.</summary>
    public static MouseInputKind Decode(string keys)
    {
        if (keys.Length == 0 || keys[0] != '[')
        {
            return MouseInputKind.NotASequence;
        }

        if (keys.Length < 3 || keys[1] != '<' || keys.Length > MaxKeys || (keys[^1] != 'M' && keys[^1] != 'm'))
        {
            return MouseInputKind.Discard;
        }

        string[] parameters = keys[2..^1].Split(';');
        if (parameters.Length != 3)
        {
            return MouseInputKind.Discard;
        }

        if (!int.TryParse(parameters[0], NumberStyles.None, CultureInfo.InvariantCulture, out int button)
            || !int.TryParse(parameters[1], NumberStyles.None, CultureInfo.InvariantCulture, out _)
            || !int.TryParse(parameters[2], NumberStyles.None, CultureInfo.InvariantCulture, out _))
        {
            return MouseInputKind.Discard;
        }

        return Button(button, release: keys[^1] == 'm');
    }

    // A wheel notch is button 64 (up) or 65 (down), modifiers aside, and never a release. Everything
    // else that decoded (clicks, releases, drags with bit 32, horizontal 66/67) is another mouse event.
    private static MouseInputKind Button(int button, bool release)
    {
        if (button < 0)
        {
            return MouseInputKind.OtherMouse;
        }

        int code = button & ~ModifierBits;
        if (!release && code == WheelUpCode)
        {
            return MouseInputKind.WheelUp;
        }

        if (!release && code == WheelDownCode)
        {
            return MouseInputKind.WheelDown;
        }

        return MouseInputKind.OtherMouse;
    }

    private static bool IsFinal(char c) => c is >= '@' and <= '~';
}
