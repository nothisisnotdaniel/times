using System.Globalization;
using System.Text;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Times;

/// <summary>
/// The permanent quicklist panel at the top of the main screen: a grey rounded <see cref="Panel"/>
/// headed <c>Projects</c> with one line per project (number, <c>#work item</c>, title). Every line is
/// cut to the width with a trailing <c>…</c> and never wraps. At most <see cref="MaxRows"/> lines of
/// content are shown; a longer list ends with a grey <c>… N more</c> line. Rendering never reads or
/// writes files: the caller loads the list.
/// </summary>
internal sealed class QuicklistPanel
{
    /// <summary>The most content lines the panel shows, including the <c>… N more</c> line.</summary>
    public const int MaxRows = 5;

    /// <summary>Widest width a line is laid out at before it is cut to the real width.</summary>
    private const int LayoutWidth = 4096;

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
                $"#{project.WorkItem.ToString(CultureInfo.InvariantCulture)} {Markup.Escape(project.Title)}")));
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
            .Header("[grey]Projects[/]")
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Grey)
            .Expand());

    /// <summary>
    /// Renders its content without a fixed height. Inside a <see cref="Layout"/> the options carry the
    /// region's height, which would stretch the panel to fill it; the panel only takes the rows it needs.
    /// </summary>
    private sealed class NaturalHeight : IRenderable
    {
        private readonly IRenderable content;

        public NaturalHeight(IRenderable content)
        {
            this.content = content;
        }

        public Measurement Measure(RenderOptions options, int maxWidth) =>
            content.Measure(options with { Height = null }, maxWidth);

        public IEnumerable<Segment> Render(RenderOptions options, int maxWidth) =>
            content.Render(options with { Height = null }, maxWidth);
    }

    /// <summary>Renders the first line of its content, cut to the width with a trailing <c>…</c> if it is too wide.</summary>
    private sealed class OneLine : IRenderable
    {
        private readonly IRenderable content;

        public OneLine(IRenderable content)
        {
            this.content = content;
        }

        public Measurement Measure(RenderOptions options, int maxWidth) =>
            new(Math.Min(1, maxWidth), maxWidth);

        public IEnumerable<Segment> Render(RenderOptions options, int maxWidth)
        {
            if (maxWidth <= 0)
            {
                return [];
            }

            List<SegmentLine> lines = Segment.SplitLines(content.Render(options, LayoutWidth));
            if (lines.Count == 0)
            {
                return [];
            }

            SegmentLine line = lines[0];
            bool cut = lines.Count > 1;
            if (!cut && line.CellCount() <= maxWidth)
            {
                return line;
            }

            List<Segment> kept = Cut(line, maxWidth - 1);
            Style style = kept.Count > 0 ? kept[^1].Style : Style.Plain;
            kept.Add(new Segment("…", style));
            return kept;
        }

        // The leading segments of a line that fit in width cells, cutting the last one between characters.
        private static List<Segment> Cut(SegmentLine line, int width)
        {
            List<Segment> kept = [];
            int left = width;
            foreach (Segment segment in line)
            {
                int cells = segment.CellCount();
                if (cells <= left)
                {
                    kept.Add(segment);
                    left -= cells;
                    continue;
                }

                StringBuilder part = new StringBuilder();
                TextElementEnumerator elements = StringInfo.GetTextElementEnumerator(segment.Text);
                while (elements.MoveNext())
                {
                    string element = elements.GetTextElement();
                    int elementCells = new Segment(element).CellCount();
                    if (elementCells > left)
                    {
                        break;
                    }

                    part.Append(element);
                    left -= elementCells;
                }

                if (part.Length > 0)
                {
                    kept.Add(new Segment(part.ToString(), segment.Style));
                }

                break;
            }

            return kept;
        }
    }
}
