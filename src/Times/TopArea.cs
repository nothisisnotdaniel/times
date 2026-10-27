using Spectre.Console;
using Spectre.Console.Rendering;

namespace Times;

/// <summary>
/// Fits the top area (day view plus any message) into a fixed number of rows. Content that fits
/// is passed through line for line. Content that is taller keeps its first <c>height - 1</c> lines
/// and ends with a grey <c>… N more lines</c> cue, so the user can see that something is cut off.
/// </summary>
internal sealed class TopArea : IRenderable
{
    private static readonly Style CueStyle = new Style(Color.Grey);

    private readonly IRenderable content;
    private readonly int height;

    private TopArea(IRenderable content, int height)
    {
        this.content = content;
        this.height = height;
    }

    /// <summary>Wraps <paramref name="content"/> so it renders in at most <paramref name="height"/> lines.</summary>
    public static IRenderable Fit(IRenderable content, int height) => new TopArea(content, height);

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

        int shown = height - 1;
        List<SegmentLine> kept = lines.Take(shown).ToList();
        string cue = $"… {lines.Count - shown} more lines";
        if (cue.Length > maxWidth)
        {
            cue = cue[..Math.Max(0, maxWidth)];
        }

        SegmentLine cueLine = new SegmentLine();
        cueLine.Add(new Segment(cue, CueStyle));
        kept.Add(cueLine);
        return Join(kept);
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
