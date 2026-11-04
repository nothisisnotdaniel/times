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

    /// <summary>
    /// Renders the current day: the heading, a blank line, then one row per entry in time order
    /// (grey number starting at 1, grey two-letter kind code (<c>AR</c> arrival, <c>LE</c> leaving,
    /// <c>BS</c> break start, <c>BE</c> break end, <c>CP</c> checkpoint), and the time coloured by
    /// kind: <c>green3</c> arrival, <c>darkcyan</c> break start and end, <c>mediumorchid</c> leaving,
    /// <c>SteelBlue1</c> checkpoint), or <c>No entries yet.</c> if nothing is booked. If the day has a
    /// checkpoint, a details column follows the time: the checkpoint's escaped name and, if it has
    /// one, its grey work item (<c>#5678</c>). Every row, of every kind, ends with the work time left
    /// at that entry's time (grey <c>left</c> plus the right-aligned value, <paramref name="target"/>
    /// minus the work so far, see <see cref="DayRecord.TimeLeft"/>). A booked checkpoint is muted: its time and name are grey,
    /// and its details cell ends with a grey <c>✓</c>.
    /// After one blank line below the last entry row, a break line, starting in the code column,
    /// shows the break taken and the break required for the day's work time
    /// (<c>Breaks 0:20 taken · 0:30 required</c>, grey words, <c>SteelBlue1</c> values, see
    /// <see cref="DayRecord.BreakTime"/> and <see cref="DayRecord.RequiredBreak"/>). When the break
    /// taken is shorter than required, the same line ends with a yellow
    /// <c>! Breaks too short: 0:10 missing</c> after two spaces. Directly below it, with the same
    /// indent, the target line follows (see <see cref="TargetLine"/>):
    /// <c>Target 8:00 · day -0:30 · balance +1:15</c>, where <c>day</c> is the day's work time minus
    /// <paramref name="target"/> and <c>balance</c> is <paramref name="balance"/>'s total. Below it,
    /// with the same indent, up to two yellow hint lines follow, each on its own row and in this order:
    /// <c>! Break not ended: entry 3</c> (<c>entries 3, 7</c> for more than one, see
    /// <see cref="DayRecord.UnendedBreaks"/>) and <c>! Break outside arrival and leaving: entry 5</c>
    /// (see <see cref="DayRecord.BreaksOutsideSpans"/>); each is left out when it names no entry.
    /// The empty day shows none of these lines, only <c>No entries yet.</c>, followed by the target
    /// line without its <c>day</c> part (<c>Target 8:00 · balance +0:30</c>) when at least one day
    /// counts toward the balance.
    /// </summary>
    public IRenderable Render(DayRecord day, TimeSpan target, FlexitimeBalance balance)
    {
        if (day.IsEmpty)
        {
            if (balance.CountedDays == 0)
            {
                return new Rows(HeadingRule(), new Markup("[grey]No entries yet.[/]"));
            }

            return new Rows(HeadingRule(), new Markup("[grey]No entries yet.[/]"), new Markup(TargetLine(target, null, balance)));
        }

        // Only days with a checkpoint get the details column, so other days look exactly as before.
        bool details = day.Entries.Any(e => e.Kind == EntryKind.Checkpoint);
        Grid grid = new Grid()
            .AddColumn(new GridColumn().RightAligned().Padding(new Padding(0, 0, NumberColumnGap, 0)))
            .AddColumn()
            .AddColumn();
        if (details)
        {
            grid.AddColumn();
        }

        grid.AddColumn(new GridColumn().RightAligned());
        IReadOnlyList<TimeSpan> timeLeft = day.TimeLeft(target);
        for (int i = 0; i < day.Entries.Count; i++)
        {
            DayEntry entry = day.Entries[i];
            string number = (i + 1).ToString(CultureInfo.InvariantCulture);
            string clock = entry.Time.ToString("HH:mm", CultureInfo.InvariantCulture);
            List<IRenderable> cells =
            [
                new Markup($"[grey]{number}[/]"),
                new Markup($"[grey]{Code(entry.Kind)}[/]"),
                new Markup($"[{(entry.Booked ? "grey" : TimeColour(entry.Kind))}]{clock}[/]"),
            ];
            if (details)
            {
                cells.Add(Details(entry));
            }

            cells.Add(new Markup($"[grey]left[/] {FormatTimeLeft(timeLeft[i])}"));
            grid.AddRow(cells.ToArray());
        }

        TimeSpan taken = day.BreakTime();
        TimeSpan required = DayRecord.RequiredBreak(day.WorkTime());
        string summary =
            $"[grey]Breaks[/] [steelblue1]{FormatTimeLeft(taken)}[/] [grey]taken · [/][steelblue1]{FormatTimeLeft(required)}[/] [grey]required[/]";
        if (taken < required)
        {
            summary += $"  [yellow]! Breaks too short: {FormatTimeLeft(required - taken)} missing[/]";
        }

        // Indent the summary to the code column: the widest number plus the number column's gap.
        int indent = day.Entries.Count.ToString(CultureInfo.InvariantCulture).Length + NumberColumnGap;
        List<IRenderable> rows =
        [
            HeadingRule(),
            Text.Empty,
            grid,
            Text.Empty,
            new Padder(new Markup(summary), new Padding(indent, 0, 0, 0)),
            new Padder(new Markup(TargetLine(target, day.WorkTime() - target, balance)), new Padding(indent, 0, 0, 0)),
        ];
        AddHint(rows, "Break not ended", day.UnendedBreaks(), indent);
        AddHint(rows, "Break outside arrival and leaving", day.BreaksOutsideSpans(), indent);
        return new Rows(rows);
    }

    /// <summary>
    /// The target line's markup: grey words and <c>·</c>, <c>SteelBlue1</c> values. The target is
    /// <c>H:mm</c>; the day part (left out when <paramref name="dayDelta"/> is null) and the balance are
    /// signed (see <see cref="FormatSigned"/>). When day files were skipped, a yellow
    /// <c>! 1 day file skipped</c> (<c>2 day files skipped</c>) follows after two spaces. It holds no user text.
    /// </summary>
    internal static string TargetLine(TimeSpan target, TimeSpan? dayDelta, FlexitimeBalance balance)
    {
        string line = $"[grey]Target[/] [steelblue1]{FormatTimeLeft(target)}[/]";
        if (dayDelta is { } delta)
        {
            line += $" [grey]· day[/] [steelblue1]{FormatSigned(delta)}[/]";
        }

        line += $" [grey]· balance[/] [steelblue1]{FormatSigned(balance.Total)}[/]";
        if (balance.SkippedFiles > 0)
        {
            string count = balance.SkippedFiles.ToString(CultureInfo.InvariantCulture);
            string files = balance.SkippedFiles == 1 ? "day file" : "day files";
            line += $"  [yellow]! {count} {files} skipped[/]";
        }

        return line;
    }

    /// <summary>
    /// Formats a signed difference as <c>+H:mm</c> or <c>-H:mm</c>, and zero as <c>0:00</c>. Truncated
    /// toward zero to whole minutes like <see cref="FormatTimeLeft"/>, so a value under one minute is
    /// <c>0:00</c>, never <c>+0:00</c> or <c>-0:00</c>.
    /// </summary>
    public static string FormatSigned(TimeSpan value)
    {
        long totalMinutes = value.Ticks / TimeSpan.TicksPerMinute;
        string text = FormatTimeLeft(TimeSpan.FromMinutes(totalMinutes));
        return totalMinutes > 0 ? "+" + text : text;
    }

    // A yellow hint line under the break summary naming entry numbers; it holds no user text.
    private static void AddHint(List<IRenderable> rows, string text, IReadOnlyList<int> numbers, int indent)
    {
        if (numbers.Count == 0)
        {
            return;
        }

        string label = numbers.Count == 1 ? "entry" : "entries";
        string list = string.Join(", ", numbers.Select(n => n.ToString(CultureInfo.InvariantCulture)));
        rows.Add(new Padder(new Markup($"[yellow]! {text}: {label} {list}[/]"), new Padding(indent, 0, 0, 0)));
    }

    // The spaces after the number column, set on the column itself so the summary indent follows it.
    private const int NumberColumnGap = 2;

    private static string Code(EntryKind kind) => kind switch
    {
        EntryKind.Arrival => "AR",
        EntryKind.Leaving => "LE",
        EntryKind.BreakStart => "BS",
        EntryKind.BreakEnd => "BE",
        EntryKind.Checkpoint => "CP",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static string TimeColour(EntryKind kind) => kind switch
    {
        EntryKind.Arrival => "green3",
        EntryKind.Leaving => "mediumorchid",
        EntryKind.BreakStart or EntryKind.BreakEnd => "darkcyan",
        EntryKind.Checkpoint => "steelblue1",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    // The checkpoint's name (user text, so escaped) and its grey work item; empty for other kinds. A
    // booked checkpoint is muted: its name is grey too, and the cell ends with a grey check mark.
    private static IRenderable Details(DayEntry entry)
    {
        if (entry.Name is not { } name)
        {
            return Text.Empty;
        }

        string workItem = entry.WorkItem is { } w ? $" [grey]#{w.ToString(CultureInfo.InvariantCulture)}[/]" : string.Empty;
        if (entry.Booked)
        {
            return new Markup($"[grey]{Markup.Escape(name)}[/]{workItem} [grey]✓[/]");
        }

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
