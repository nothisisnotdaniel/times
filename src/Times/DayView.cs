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
    /// starting at 1, grey label such as <c>Arrival</c> or <c>Checkpoint</c>, <c>SteelBlue1</c> time),
    /// or <c>No entries yet.</c> if nothing is booked. If the day has a checkpoint, a details column
    /// follows the time: the checkpoint's escaped name and, if it has one, its grey work item
    /// (<c>#5678</c>). Every row, of every kind, ends with the work time left at that entry's time
    /// (grey <c>left</c> plus the right-aligned value, see <see cref="DayRecord.TimeLeft"/>).
    /// Below the last entry row, a break line shows the break taken and the break required for the
    /// day's work time (<c>Breaks 0:20 taken · 0:30 required</c>, grey words, <c>SteelBlue1</c>
    /// values, see <see cref="DayRecord.BreakTime"/> and <see cref="DayRecord.RequiredBreak"/>). When
    /// the break taken is shorter than required, a yellow line follows:
    /// <c>Breaks too short: 0:10 missing</c>. The empty day shows neither line.
    /// </summary>
    public IRenderable Render(DayRecord day)
    {
        if (day.IsEmpty)
        {
            return new Rows(HeadingRule(), new Markup("[grey]No entries yet.[/]"));
        }

        // Only days with a checkpoint get the details column, so other days look exactly as before.
        bool details = day.Entries.Any(e => e.Kind == EntryKind.Checkpoint);
        Grid grid = new Grid()
            .AddColumn(new GridColumn().RightAligned())
            .AddColumn()
            .AddColumn();
        if (details)
        {
            grid.AddColumn();
        }

        grid.AddColumn(new GridColumn().RightAligned());
        IReadOnlyList<TimeSpan> timeLeft = day.TimeLeft();
        for (int i = 0; i < day.Entries.Count; i++)
        {
            DayEntry entry = day.Entries[i];
            string number = (i + 1).ToString(CultureInfo.InvariantCulture);
            string clock = entry.Time.ToString("HH:mm", CultureInfo.InvariantCulture);
            List<IRenderable> cells =
            [
                new Markup($"[grey]{number}[/]"),
                new Markup($"[grey]{Label(entry.Kind)}[/]"),
                new Markup($"[steelblue1]{clock}[/]"),
            ];
            if (details)
            {
                cells.Add(Details(entry));
            }

            cells.Add(new Markup($"[grey]left[/] {FormatTimeLeft(timeLeft[i])}"));
            grid.AddRow(cells.ToArray());
        }

        List<IRenderable> rows = [HeadingRule(), grid];
        TimeSpan taken = day.BreakTime();
        TimeSpan required = DayRecord.RequiredBreak(day.WorkTime());
        rows.Add(new Markup(
            $"[grey]Breaks[/] [steelblue1]{FormatTimeLeft(taken)}[/] [grey]taken · [/][steelblue1]{FormatTimeLeft(required)}[/] [grey]required[/]"));
        if (taken < required)
        {
            rows.Add(new Markup($"[yellow]Breaks too short: {FormatTimeLeft(required - taken)} missing[/]"));
        }

        return new Rows(rows);
    }

    private static string Label(EntryKind kind) => kind switch
    {
        EntryKind.Arrival => "Arrival",
        EntryKind.Leaving => "Leaving",
        EntryKind.BreakStart => "Break start",
        EntryKind.BreakEnd => "Break end",
        EntryKind.Checkpoint => "Checkpoint",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    // The checkpoint's name (user text, so escaped) and its grey work item; empty for other kinds.
    private static IRenderable Details(DayEntry entry)
    {
        if (entry.Name is not { } name)
        {
            return Text.Empty;
        }

        string workItem = entry.WorkItem is { } w ? $" [grey]#{w.ToString(CultureInfo.InvariantCulture)}[/]" : string.Empty;
        return new Markup(Markup.Escape(name) + workItem);
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
