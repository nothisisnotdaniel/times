using System.Globalization;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Times;

/// <summary>
/// Renders the current day. The current day starts at today, read from <see cref="TimeProvider"/>,
/// and can be moved one day back or forward, or back to today.
/// </summary>
internal sealed class DayView
{
    private readonly TimeProvider time;

    public DayView(TimeProvider time)
    {
        this.time = time;
        Current = TodayFrom(time);
    }

    public DateOnly Current { get; private set; }

    public static string Heading(DateOnly day) => day.ToString("dddd, yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Moves one day back. Returns false (and stays put) at <see cref="DateOnly.MinValue"/>.</summary>
    public bool Previous()
    {
        if (Current == DateOnly.MinValue)
        {
            return false;
        }

        Current = Current.AddDays(-1);
        return true;
    }

    /// <summary>Moves one day forward. Returns false (and stays put) at <see cref="DateOnly.MaxValue"/>.</summary>
    public bool Next()
    {
        if (Current == DateOnly.MaxValue)
        {
            return false;
        }

        Current = Current.AddDays(1);
        return true;
    }

    /// <summary>Jumps to today, re-reading the clock each time.</summary>
    public void Today() => Current = TodayFrom(time);

    /// <summary>Renders the current day with nothing booked.</summary>
    public IRenderable Render() => Render(DayRecord.Empty);

    /// <summary>
    /// Renders the current day: the heading, then one row per entry in time order (grey number
    /// starting at 1, grey <c>Arrival</c>/<c>Leaving</c> label, <c>SteelBlue1</c> time), or
    /// <c>No entries yet.</c> if nothing is booked. Each row ends with the work time left after
    /// that entry (grey <c>left</c> plus the right-aligned value, see <see cref="DayRecord.TimeLeft"/>).
    /// </summary>
    public IRenderable Render(DayRecord day)
    {
        if (day.IsEmpty)
        {
            return new Rows(HeadingRule(), new Markup("[grey]No entries yet.[/]"));
        }

        Grid grid = new Grid()
            .AddColumn(new GridColumn().RightAligned())
            .AddColumn()
            .AddColumn()
            .AddColumn(new GridColumn().RightAligned());
        IReadOnlyList<TimeSpan> timeLeft = day.TimeLeft();
        for (int i = 0; i < day.Entries.Count; i++)
        {
            DayEntry entry = day.Entries[i];
            string number = (i + 1).ToString(CultureInfo.InvariantCulture);
            string label = entry.Kind == EntryKind.Arrival ? "Arrival" : "Leaving";
            string clock = entry.Time.ToString("HH:mm", CultureInfo.InvariantCulture);
            grid.AddRow(
                new Markup($"[grey]{number}[/]"),
                new Markup($"[grey]{label}[/]"),
                new Markup($"[steelblue1]{clock}[/]"),
                new Markup($"[grey]left[/] {FormatTimeLeft(timeLeft[i])}"));
        }

        return new Rows(HeadingRule(), grid);
    }

    /// <summary>
    /// Formats a time-left value as invariant <c>H:mm</c> with no leading zero on the hours, and a
    /// leading <c>-</c> for negative values (<c>-0:30</c>). Zero is <c>0:00</c>.
    /// </summary>
    public static string FormatTimeLeft(TimeSpan value)
    {
        // Whole minutes, truncated toward zero, so a sub-minute negative value never shows "-0:00".
        long totalMinutes = value.Ticks / TimeSpan.TicksPerMinute;
        string sign = totalMinutes < 0 ? "-" : string.Empty;
        long magnitude = Math.Abs(totalMinutes);
        string hoursText = (magnitude / 60).ToString(CultureInfo.InvariantCulture);
        string minutesText = (magnitude % 60).ToString("00", CultureInfo.InvariantCulture);
        return sign + hoursText + ":" + minutesText;
    }

    /// <summary>Renders the current day when its file could not be read. <paramref name="markup"/> must be escaped.</summary>
    public IRenderable RenderUnreadable(string markup) =>
        new Rows(HeadingRule(), new Markup($"[red]{markup}[/]"));

    private Rule HeadingRule() =>
        new Rule($"[steelblue1]{Markup.Escape(Heading(Current))}[/]").LeftJustified().RuleStyle(Style.Parse("grey"));

    private static DateOnly TodayFrom(TimeProvider time) => DateOnly.FromDateTime(time.GetLocalNow().DateTime);
}
