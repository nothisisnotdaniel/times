using System.Globalization;
using System.Text;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Times;

/// <summary>
/// Renders its content without a fixed height. Inside a <see cref="Layout"/> the options carry the
/// region's height, which would stretch the panel to fill it; the panel only takes the rows it needs.
/// </summary>
internal sealed class NaturalHeight : IRenderable
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
internal sealed class OneLine : IRenderable
{
    /// <summary>Widest width a line is laid out at before it is cut to the real width.</summary>
    private const int LayoutWidth = 4096;

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
