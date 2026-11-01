using Spectre.Console;
using Spectre.Console.Rendering;

namespace Times;

/// <summary>
/// Fits the top area (quicklist panel, day view plus any message) into a fixed number of rows, as a
/// scrollable window. Content that fits is passed through line for line, whatever the offset. Taller
/// content shows a window that starts at the given offset: a grey
/// <c>↑ N more lines above · PgUp / Ctrl+↑</c> cue on the first row when lines are hidden above,
/// and a grey <c>↓ N more lines below · PgDn / Ctrl+↓</c> cue on the last row when lines are hidden
/// below (<c>1 more line</c> when only one line is hidden). Scrolling needs at least 3 rows; with 1
/// or 2 rows the window always starts at the top and only the below cue is shown.
/// </summary>
internal sealed class TopArea : IRenderable
{
    private static readonly Style CueStyle = new Style(Color.Grey);

    private readonly IRenderable content;
    private readonly int height;
    private readonly int offset;

    private TopArea(IRenderable content, int height, int offset)
    {
        this.content = content;
        this.height = height;
        this.offset = offset;
    }

    /// <summary>
    /// Wraps <paramref name="content"/> so it renders in at most <paramref name="height"/> lines,
    /// starting at line <paramref name="offset"/> (0-based, clamped to 0..<see cref="MaxOffset"/>).
    /// </summary>
    public static IRenderable Fit(IRenderable content, int height, int offset = 0) => new TopArea(content, height, offset);

    /// <summary>
    /// The largest offset for <paramref name="lineCount"/> content lines in <paramref name="height"/>
    /// rows: <c>lineCount - (height - 1)</c> when the content is taller and there are at least 3 rows,
    /// otherwise 0 (no scrolling).
    /// </summary>
    public static int MaxOffset(int lineCount, int height) =>
        lineCount > height && height >= 3 ? lineCount - (height - 1) : 0;

    /// <summary>How many lines one scroll step moves: the rows between the two cues, at least 1.</summary>
    public static int PageSize(int height) => Math.Max(1, height - 2);

    public Measurement Measure(RenderOptions options, int maxWidth) => content.Measure(options, maxWidth);

    public IEnumerable<Segment> Render(RenderOptions options, int maxWidth)
    {
        if (height <= 0)
        {
            return [];
        }

        List<SegmentLine> lines = Segment.SplitLines(content.Render(options, maxWidth));
        if (lines.Count <= height)
        {
            return Join(lines);
        }

        int max = MaxOffset(lines.Count, height);
        int start = Math.Clamp(offset, 0, max);
        List<SegmentLine> kept = [];
        if (start > 0)
        {
            kept.Add(Cue(CueText(start, above: true), maxWidth));
        }

        // Rows left for content once the cues have their rows.
        int room = height - (start > 0 ? 1 : 0) - (start < max || max == 0 ? 1 : 0);
        kept.AddRange(lines.Skip(start).Take(room));
        int below = lines.Count - start - room;
        if (below > 0)
        {
            kept.Add(Cue(CueText(below, above: false), maxWidth));
        }

        return Join(kept);
    }

    /// <summary>
    /// The text of a scroll cue for <paramref name="count"/> hidden lines: the above cue
    /// (<c>↑ N more lines above · PgUp / Ctrl+↑</c>) or the below cue
    /// (<c>↓ N more lines below · PgDn / Ctrl+↓</c>), with <c>line</c> when the count is 1.
    /// </summary>
    internal static string CueText(int count, bool above)
    {
        string noun = count == 1 ? "line" : "lines";
        return above
            ? $"↑ {count} more {noun} above · PgUp / Ctrl+↑"
            : $"↓ {count} more {noun} below · PgDn / Ctrl+↓";
    }

    private static SegmentLine Cue(string text, int maxWidth)
    {
        if (text.Length > maxWidth)
        {
            text = text[..Math.Max(0, maxWidth)];
        }

        SegmentLine line = new SegmentLine();
        line.Add(new Segment(text, CueStyle));
        return line;
    }

    private static IEnumerable<Segment> Join(List<SegmentLine> lines)
    {
        List<Segment> segments = [];
        for (int i = 0; i < lines.Count; i++)
        {
            if (i > 0)
            {
                segments.Add(Segment.LineBreak);
            }

            segments.AddRange(lines[i]);
        }

        return segments;
    }
}
