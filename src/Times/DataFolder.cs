using System.Globalization;
using System.Text;

namespace Times;

/// <summary>
/// Where the day files live: the <see cref="VariableName"/> override if it is set, otherwise
/// <c>&lt;ApplicationData&gt;/Times</c>. Resolving reads no environment variables itself, so it can be
/// tested without touching process-wide state. The value is never trimmed or changed: validation,
/// including refusing a value that starts or ends with whitespace, is left to <see cref="DayStore.IsValidFolder"/>.
/// </summary>
internal sealed class DataFolder
{
    /// <summary>The environment variable that overrides the default data folder.</summary>
    internal const string VariableName = "TIMES_DATA_DIR";

    private DataFolder(string path, bool fromEnvironment)
    {
        Path = path;
        FromEnvironment = fromEnvironment;
    }

    /// <summary>The folder to use, exactly as resolved (it may still be invalid).</summary>
    public string Path { get; }

    /// <summary>True if <see cref="Path"/> came from <see cref="VariableName"/>.</summary>
    public bool FromEnvironment { get; }

    /// <summary>
    /// A null or empty <paramref name="environmentValue"/> means the default: <c>""</c> if
    /// <paramref name="appData"/> is null or empty (so the startup check rejects it), otherwise
    /// <c>Path.Combine(appData, "Times")</c>. Any other value is used exactly as given: no trimming,
    /// no <c>~</c> expansion and no resolving against the working directory. A value with leading or
    /// trailing whitespace is kept as it is, so <see cref="DayStore.IsValidFolder"/> can reject it.
    /// </summary>
    internal static DataFolder Resolve(string? environmentValue, string? appData)
    {
        if (!string.IsNullOrEmpty(environmentValue))
        {
            return new DataFolder(environmentValue, fromEnvironment: true);
        }

        string folder = string.IsNullOrEmpty(appData) ? string.Empty : System.IO.Path.Combine(appData, "Times");
        return new DataFolder(folder, fromEnvironment: false);
    }

    /// <summary>
    /// <paramref name="value"/> without control characters (C0, DEL, C1) and line or paragraph
    /// separators, so it can be shown on one terminal line without sending anything to the terminal.
    /// Every other character, spaces included, is kept. Escape the result before it goes into markup.
    /// </summary>
    internal static string ForDisplay(string value)
    {
        StringBuilder shown = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            if (char.IsControl(c)
                || CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
            {
                continue;
            }

            shown.Append(c);
        }

        return shown.ToString();
    }
}
