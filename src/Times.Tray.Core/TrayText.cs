using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Times.Tray;

/// <summary>Cleans untrusted text from <c>tm</c> before the menu shows it, and formats times.</summary>
internal static partial class TrayText
{
    /// <summary>The most characters a checkpoint name keeps in the menu (with the <c>…</c>).</summary>
    public const int MaxNameLength = 40;

    /// <summary>The most characters an error line from <c>tm</c> keeps (with the <c>…</c>).</summary>
    public const int MaxErrorLength = 120;

    /// <summary>The most characters a Windows notification-area tooltip may hold (<c>NotifyIcon.Text</c>).</summary>
    public const int MaxWindowsTooltipLength = 127;

    /// <summary>
    /// Drops ANSI escape sequences, control characters (line breaks included) and bidi marks, overrides and
    /// isolates, trims, then cuts to <paramref name="max"/> characters ending in <c>…</c> (never half a surrogate pair).
    /// </summary>
    public static string Clean(string text, int max)
    {
        string plain = AnsiSequence().Replace(text, string.Empty);
        StringBuilder kept = new StringBuilder(plain.Length);
        foreach (char c in plain)
        {
            if (!char.IsControl(c) && !IsBidi(c))
            {
                kept.Append(c);
            }
        }

        return Cut(kept.ToString().Trim(), max);
    }

    /// <summary>A menu text for Windows: <c>&amp;</c> marks an access key there, so each one is doubled to show as itself.</summary>
    public static string WindowsMenuText(string text) => text.Replace("&", "&&", StringComparison.Ordinal);

    /// <summary>The tooltip cut to <see cref="MaxWindowsTooltipLength"/> characters, ending in <c>…</c> when cut.</summary>
    public static string WindowsTooltip(string text) => Cut(text, MaxWindowsTooltipLength);

    // Cuts to max characters ending in "…", never between the two halves of a surrogate pair.
    private static string Cut(string text, int max)
    {
        if (text.Length <= max)
        {
            return text;
        }

        int cut = max - 1;
        if (char.IsHighSurrogate(text[cut - 1]))
        {
            cut--;
        }

        return text[..cut] + "…";
    }

    /// <summary>A time of day as <c>H:mm</c> (<c>8:15</c>), invariant.</summary>
    public static string Time(TimeOnly time) => time.ToString("H:mm", CultureInfo.InvariantCulture);

    /// <summary>A length in minutes, without its sign, as <c>H:mm</c> (<c>3:12</c>, hours may pass 23).</summary>
    public static string Duration(int minutes)
    {
        long value = Math.Abs((long)minutes);
        return string.Create(CultureInfo.InvariantCulture, $"{value / 60}:{value % 60:00}");
    }

    // U+061C, LRM/RLM, the embeddings and overrides U+202A-U+202E, and the isolates U+2066-U+2069.
    private static bool IsBidi(char c) =>
        c is '\u061C' or '\u200E' or '\u200F' or (>= '\u202A' and <= '\u202E') or (>= '\u2066' and <= '\u2069');

    [GeneratedRegex(@"\x1B(\[[0-?]*[ -/]*[@-~]|\][^\x07\x1B]*(\x07|\x1B\\)?|[@-Z\\-_])")]
    private static partial Regex AnsiSequence();
}
