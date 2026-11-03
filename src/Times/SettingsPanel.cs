using System.Globalization;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Times;

/// <summary>
/// The <c>settings</c> panel that replaces the quicklist and day view while it is open: a rounded
/// <see cref="Panel"/> with a grey border and a bold steelblue1 <c>Settings</c> header, one line per
/// setting (name in steelblue1, value, grey description; the selected one marked with <c>›</c>), and a
/// grey key line. Every line is cut to the width with a trailing <c>…</c> and never wraps. Rendering
/// never reads or writes files: the caller loads the values.
/// </summary>
internal static class SettingsPanel
{
    /// <summary>The grey key line under the settings.</summary>
    public const string KeyLine = "↑/↓ move · Enter/Space change · Esc close";

    /// <summary>The grey line shown when no setting is registered.</summary>
    public const string NoSettings = "No settings exist yet.";

    /// <summary>The key line when no setting is registered: only Esc does anything.</summary>
    public const string EmptyKeyLine = "Esc close";

    /// <summary>What to do when <c>settings.json</c> cannot be read.</summary>
    public const string UnreadableNextStep = "Fix or delete settings.json in the data folder (help shows where).";

    /// <summary>
    /// The panel for <paramref name="values"/> of <paramref name="registry"/>'s settings with row
    /// <paramref name="selected"/> marked. When <paramref name="unreadableReason"/> (plain text, escaped
    /// here) is not null, a red <c>The settings cannot be read: …</c> line and a grey line with what to
    /// do come first.
    /// </summary>
    public static IRenderable Render(SettingsRegistry registry, SettingValues values, int selected, string? unreadableReason)
    {
        List<IRenderable> lines = [];
        if (unreadableReason is not null)
        {
            lines.Add(new OneLine(new Markup($"[red]The settings cannot be read: {Markup.Escape(unreadableReason)}[/]")));
            lines.Add(new OneLine(new Markup($"[grey]{Markup.Escape(UnreadableNextStep)}[/]")));
        }

        IReadOnlyList<Setting> settings = registry.Settings;
        if (settings.Count == 0)
        {
            lines.Add(new OneLine(new Markup($"[grey]{Markup.Escape(NoSettings)}[/]")));
            lines.Add(new OneLine(new Markup($"[grey]{Markup.Escape(EmptyKeyLine)}[/]")));
            return Frame(lines);
        }

        List<string> shown = settings.Select(s => FormatValue(values.Get(s.Name))).ToList();
        int nameWidth = settings.Max(s => Cells(s.Name));
        int valueWidth = shown.Max(Cells);
        for (int i = 0; i < settings.Count; i++)
        {
            Setting setting = settings[i];
            string marker = i == selected ? "[bold steelblue1]›[/]" : " ";
            string name = Markup.Escape(Pad(setting.Name, nameWidth));
            string value = Markup.Escape(Pad(shown[i], valueWidth));
            lines.Add(new OneLine(new Markup(
                $"{marker} [steelblue1]{name}[/]  {value}  [grey]{Markup.Escape(setting.Description)}[/]")));
        }

        lines.Add(new OneLine(new Markup($"[grey]{Markup.Escape(KeyLine)}[/]")));
        return Frame(lines);
    }

    /// <summary>
    /// The panel line (0-based, the top border is line 0) that shows setting <paramref name="index"/>,
    /// so the caller can keep the selected row in view.
    /// </summary>
    public static int LineOf(int index, bool unreadable) => 1 + (unreadable ? 2 : 0) + index;

    /// <summary>How a value is shown: <c>on</c>/<c>off</c>, an invariant number, or the option's text.</summary>
    public static string FormatValue(object value) => value switch
    {
        bool on => on ? "on" : "off",
        int number => number.ToString(CultureInfo.InvariantCulture),
        string option => option,
        _ => string.Empty,
    };

    private static int Cells(string text) => new Segment(text).CellCount();

    private static string Pad(string text, int width) => text + new string(' ', Math.Max(0, width - Cells(text)));

    private static IRenderable Frame(List<IRenderable> lines) =>
        new NaturalHeight(new Panel(new Rows(lines))
            .Header("[bold steelblue1]Settings[/]")
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Grey)
            .Expand());
}
