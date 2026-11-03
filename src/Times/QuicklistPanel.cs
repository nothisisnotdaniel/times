using System.Globalization;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Times;

/// <summary>
/// The permanent quicklist panel at the top of the main screen: a rounded <see cref="Panel"/> with a grey
/// border and a bold steelblue1 <c>Projects</c> header, with one line per project (steelblue1 number,
/// grey <c>#work item</c>, title). Every line is
/// cut to the width with a trailing <c>…</c> and never wraps. At most <see cref="MaxRows"/> lines of
/// content are shown; a longer list ends with a grey <c>… N more</c> line. Rendering never reads or
/// writes files: the caller loads the list.
/// </summary>
internal sealed class QuicklistPanel
{
    /// <summary>The most content lines the panel shows, including the <c>… N more</c> line.</summary>
    public const int MaxRows = 5;

    private QuicklistPanel()
    {
    }

    /// <summary>The panel for <paramref name="list"/>, or a grey hint how to add a project if it is empty.</summary>
    public static IRenderable Render(ProjectList list)
    {
        List<IRenderable> lines = [];
        if (list.IsEmpty)
        {
            lines.Add(new OneLine(new Markup("[grey]No projects yet. Add one with addproject <work item> <title>.[/]")));
            return Frame(lines);
        }

        int count = list.Projects.Count;
        int shown = count <= MaxRows ? count : MaxRows - 1;
        for (int i = 0; i < shown; i++)
        {
            Project project = list.Projects[i];
            lines.Add(new OneLine(new Markup(
                $"[steelblue1]{(i + 1).ToString(CultureInfo.InvariantCulture)}[/] " +
                $"[grey]#{project.WorkItem.ToString(CultureInfo.InvariantCulture)}[/] {Markup.Escape(project.Title)}")));
        }

        if (shown < count)
        {
            string more = (count - shown).ToString(CultureInfo.InvariantCulture);
            lines.Add(new OneLine(new Markup($"[grey]… {more} more. Type projects to see all.[/]")));
        }

        return Frame(lines);
    }

    /// <summary>
    /// The panel for a <c>projects.json</c> that cannot be read: a red line with the reason, then a grey line
    /// with what to do about it. Both arguments must already be escaped.
    /// </summary>
    public static IRenderable RenderUnreadable(string escapedReason, string escapedNextStep) =>
        Frame([
            new OneLine(new Markup($"[red]{escapedReason}[/]")),
            new OneLine(new Markup($"[grey]{escapedNextStep}[/]")),
        ]);

    private static IRenderable Frame(List<IRenderable> lines) =>
        new NaturalHeight(new Panel(new Rows(lines))
            .Header("[bold steelblue1]Projects[/]")
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Grey)
            .Expand());
}
