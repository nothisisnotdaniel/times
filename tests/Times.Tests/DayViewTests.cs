using Spectre.Console;
using Spectre.Console.Testing;

namespace Times.Tests;

public class DayViewTests
{
    [Fact]
    public void Current_StartsAtToday()
    {
        DayView view = new DayView(FixedTimeProvider.At20261027());

        Assert.Equal(new DateOnly(2026, 10, 27), view.Current);
    }

    [Fact]
    public void Current_UsesLocalTimeZone()
    {
        // 23:30 UTC on the 27th is already the 28th in UTC+2.
        TimeZoneInfo zone = TimeZoneInfo.CreateCustomTimeZone("Plus2", TimeSpan.FromHours(2), "Plus2", "Plus2");
        DayView view =
            new DayView(new FixedTimeProvider(new DateTimeOffset(2026, 10, 27, 23, 30, 0, TimeSpan.Zero), zone));

        Assert.Equal(new DateOnly(2026, 10, 28), view.Current);
    }

    // AC-1
    [Fact]
    public void Render_ShowsHeadingAndEmptyState()
    {
        TestConsole console = new TestConsole();

        console.Write(new DayView(FixedTimeProvider.At20261027()).Render());

        Assert.Contains("Tuesday, 2026-10-27", console.Output);
        Assert.Contains("No entries yet.", console.Output);
    }

    [Fact]
    public void Heading_UsesInvariantCulture()
    {
        System.Globalization.CultureInfo previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Equal("Tuesday, 2026-10-27", DayView.Heading(new DateOnly(2026, 10, 27)));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void PreviousAndNext_MoveOneDay()
    {
        DayView view = new DayView(FixedTimeProvider.At20261027());

        Assert.True(view.Previous());
        Assert.Equal(new DateOnly(2026, 10, 26), view.Current);
        Assert.True(view.Next());
        Assert.True(view.Next());
        Assert.Equal(new DateOnly(2026, 10, 28), view.Current);
    }

    // AC-4
    [Theory]
    [InlineData(2026, 1, 1, -1, "Wednesday, 2025-12-31")]
    [InlineData(2026, 2, 28, 1, "Sunday, 2026-03-01")]
    [InlineData(2028, 2, 28, 1, "Tuesday, 2028-02-29")]
    public void Navigation_CrossesMonthAndYearBoundaries(int year, int month, int day, int step, string expected)
    {
        DayView view =
            new DayView(new SettableTimeProvider(new DateTimeOffset(year, month, day, 12, 0, 0, TimeSpan.Zero)));

        Assert.True(step < 0 ? view.Previous() : view.Next());

        Assert.Equal(expected, DayView.Heading(view.Current));
    }

    // AC-3, AC-5
    [Fact]
    public void Today_RereadsTheClock()
    {
        SettableTimeProvider clock =
            new SettableTimeProvider(new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero));
        DayView view = new DayView(clock);
        view.Previous();
        view.Previous();

        view.Today();
        Assert.Equal(new DateOnly(2026, 10, 27), view.Current);

        clock.UtcNow = clock.UtcNow.AddDays(1);
        view.Today();
        Assert.Equal(new DateOnly(2026, 10, 28), view.Current);
    }

    // AC-10
    [Fact]
    public void Previous_AtMinValue_StaysPut()
    {
        DayView view = new DayView(new SettableTimeProvider(DateTimeOffset.MinValue));
        Assert.Equal(DateOnly.MinValue, view.Current);

        Assert.False(view.Previous());
        Assert.Equal(DateOnly.MinValue, view.Current);
    }

    // AC-10
    [Fact]
    public void Next_AtMaxValue_StaysPut()
    {
        DayView view = new DayView(new SettableTimeProvider(DateTimeOffset.MaxValue));
        Assert.Equal(DateOnly.MaxValue, view.Current);

        Assert.False(view.Next());
        Assert.Equal(DateOnly.MaxValue, view.Current);
    }

    // AC-6
    [Fact]
    public void Render_AfterNavigation_ShowsNewHeadingAndEmptyState()
    {
        TestConsole console = new TestConsole();
        DayView view = new DayView(FixedTimeProvider.At20261027());
        view.Next();

        console.Write(view.Render());

        Assert.Contains("Wednesday, 2026-10-28", console.Output);
        Assert.Contains("No entries yet.", console.Output);
    }

    // AC-5 (017)
    [Fact]
    public void Render_OneEntry_ShowsNumberedRow()
    {
        TestConsole console = new TestConsole();
        DayView view = new DayView(FixedTimeProvider.At20261027());

        console.Write(view.Render(DayRecord.Empty.Add(new DayEntry(EntryKind.Arrival, new TimeOnly(8, 15)))));

        Assert.Contains("Tuesday, 2026-10-27", console.Output);
        Assert.Matches(@"1 +AR +08:15", console.Output);
        Assert.DoesNotContain("Leaving", console.Output);
        Assert.DoesNotContain("—", console.Output);
        Assert.DoesNotContain("No entries yet.", console.Output);
    }

    // AC-5 (017)
    [Fact]
    public void Render_ThreeEntries_ShowsRowsInTimeOrder_NumberedFromOne()
    {
        TestConsole console = new TestConsole();
        DayView view = new DayView(FixedTimeProvider.At20261027());
        DayRecord record = DayRecord.Empty
            .Add(new DayEntry(EntryKind.Leaving, new TimeOnly(12, 0)))
            .Add(new DayEntry(EntryKind.Arrival, new TimeOnly(13, 0)))
            .Add(new DayEntry(EntryKind.Arrival, new TimeOnly(8, 15)));

        console.Write(view.Render(record));

        string[] lines = console.Lines.Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
        Assert.Equal(5, lines.Length);
        Assert.Matches(@"^1 +AR +08:15 +left +8:00$", lines[1]);
        Assert.Matches(@"^2 +LE +12:00 +left +4:15$", lines[2]);
        Assert.Matches(@"^3 +AR +13:00 +left +4:15$", lines[3]);
        Assert.Equal("Breaks 0:00 taken · 0:00 required", lines[4]);
        Assert.DoesNotContain("—", console.Output);
    }

    // AC-5 (017)
    [Fact]
    public void Render_UsesGreyNumbersAndCodes_AndKindColouredTimes()
    {
        TestConsole console = new TestConsole().EmitAnsiSequences();
        DayView view = new DayView(FixedTimeProvider.At20261027());

        console.Write(view.Render(DayRecord.Empty
            .Add(new DayEntry(EntryKind.Arrival, new TimeOnly(8, 15)))
            .Add(new DayEntry(EntryKind.Leaving, new TimeOnly(17, 30)))));

        // grey = 256-colour 8, SteelBlue1 = 256-colour 75.
        Assert.Contains("\u001b[38;5;8m1", console.Output);
        Assert.Contains("\u001b[38;5;8m2", console.Output);
        Assert.Contains("\u001b[38;5;8mAR", console.Output);
        Assert.Contains("\u001b[38;5;8mLE", console.Output);
        Assert.Contains("\u001b[38;5;34m08:15", console.Output);
        Assert.Contains("\u001b[38;5;134m17:30", console.Output);
        Assert.DoesNotContain("—", console.Output);
    }

    // AC-1, AC-5 (017)
    [Fact]
    public void Render_EmptyRecord_KeepsEmptyState()
    {
        TestConsole console = new TestConsole();

        console.Write(new DayView(FixedTimeProvider.At20261027()).Render(DayRecord.Empty));

        Assert.Contains("No entries yet.", console.Output);
        Assert.DoesNotContain("Arrival", console.Output);
    }

    // AC-4 (019)
    [Theory]
    [InlineData(8, 0, "8:00")]
    [InlineData(0, 5, "0:05")]
    [InlineData(0, 0, "0:00")]
    [InlineData(0, -30, "-0:30")]
    [InlineData(-15, -59, "-15:59")]
    [InlineData(-1, 0, "-1:00")]
    [InlineData(12, 34, "12:34")]
    public void FormatTimeLeft_UsesHoursAndMinutes_WithoutLeadingZero(int hours, int minutes, string expected)
    {
        Assert.Equal(expected, DayView.FormatTimeLeft(new TimeSpan(hours, minutes, 0)));
    }

    // AC-4 (019)
    [Fact]
    public void FormatTimeLeft_ZeroAndSubMinuteNegative_HaveNoMinusSign()
    {
        Assert.Equal("0:00", DayView.FormatTimeLeft(TimeSpan.Zero));
        Assert.Equal("0:00", DayView.FormatTimeLeft(TimeSpan.FromSeconds(-30)));
    }

    // AC-4 (019)
    [Fact]
    public void FormatTimeLeft_IsInvariant_UnderGermanCulture()
    {
        System.Globalization.CultureInfo previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Equal("8:00", DayView.FormatTimeLeft(TimeSpan.FromHours(8)));
            Assert.Equal("-0:30", DayView.FormatTimeLeft(TimeSpan.FromMinutes(-30)));
            Assert.Equal("-15:59", DayView.FormatTimeLeft(new TimeSpan(-15, -59, 0)));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }

    // AC-5 (019)
    [Fact]
    public void Render_ShowsTimeLeftOnEachEntryRow()
    {
        TestConsole console = new TestConsole();
        DayView view = new DayView(FixedTimeProvider.At20261027());
        DayRecord record = DayRecord.Empty
            .Add(new DayEntry(EntryKind.Arrival, new TimeOnly(8, 0)))
            .Add(new DayEntry(EntryKind.Leaving, new TimeOnly(12, 0)))
            .Add(new DayEntry(EntryKind.Arrival, new TimeOnly(12, 30)))
            .Add(new DayEntry(EntryKind.Leaving, new TimeOnly(17, 0)));

        console.Write(view.Render(record));

        string[] lines = console.Lines.Select(l => l.TrimEnd()).Where(l => l.Trim().Length > 0).ToArray();
        Assert.Equal(6, lines.Length);
        Assert.Matches(@"^1 +AR +08:00 +left +8:00$", lines[1]);
        Assert.Matches(@"^2 +LE +12:00 +left +4:00$", lines[2]);
        Assert.Matches(@"^3 +AR +12:30 +left +4:00$", lines[3]);
        Assert.Matches(@"^4 +LE +17:00 +left -0:30$", lines[4]);
        Assert.Equal("Breaks 0:00 taken · 0:30 required  ! Breaks too short: 0:30 missing", lines[5].Trim());

        // The value column is right-aligned, so every entry row ends at the same column.
        int[] ends = lines[1..5].Select(l => l.Length).Distinct().ToArray();
        Assert.Single(ends);
    }

    // AC-5 (019)
    [Fact]
    public void Render_TimeLeft_GreyLabel_AndNegativeValueNotRed()
    {
        TestConsole console = new TestConsole().EmitAnsiSequences();
        DayView view = new DayView(FixedTimeProvider.At20261027());

        console.Write(view.Render(DayRecord.Empty
            .Add(new DayEntry(EntryKind.Arrival, new TimeOnly(8, 0)))
            .Add(new DayEntry(EntryKind.Leaving, new TimeOnly(17, 0)))));

        // grey = 256-colour 8; red is 4-bit 31/91 or 256-colour 9.
        Assert.Contains("\u001b[38;5;8mleft", console.Output);
        Assert.Contains("-1:00", console.Output);
        Assert.DoesNotContain("\u001b[31m", console.Output);
        Assert.DoesNotContain("\u001b[91m", console.Output);
        Assert.DoesNotContain("\u001b[38;5;9m", console.Output);

        // Between the grey label and the value there is no colour sequence: the value uses the default style.
        string output = console.Output;
        int value = output.IndexOf("-1:00", StringComparison.Ordinal);
        int label = output.LastIndexOf("left", value, StringComparison.Ordinal);
        string between = output[(label + "left".Length)..value];
        Assert.DoesNotContain("38;", between);
        Assert.DoesNotMatch(@"\u001b\[(3[0-7]|9[0-7])m", between);
    }

    // AC-5 (019)
    [Fact]
    public void Render_EmptyRecord_ShowsNoTimeLeft()
    {
        TestConsole console = new TestConsole();

        console.Write(new DayView(FixedTimeProvider.At20261027()).Render(DayRecord.Empty));

        Assert.Contains("No entries yet.", console.Output);
        Assert.DoesNotContain("left", console.Output);
    }

    // Breaks and checkpoints (job 036) ----------------------------------------------------

    private static DayRecord AllKinds() => new DayRecord(
    [
        new DayEntry(EntryKind.Arrival, new TimeOnly(8, 0)),
        new DayEntry(EntryKind.Checkpoint, new TimeOnly(9, 30), "Standup", 5678),
        new DayEntry(EntryKind.BreakStart, new TimeOnly(12, 0)),
        new DayEntry(EntryKind.BreakEnd, new TimeOnly(12, 30)),
        new DayEntry(EntryKind.Checkpoint, new TimeOnly(15, 0), "2024 review", null),
        new DayEntry(EntryKind.Leaving, new TimeOnly(17, 0)),
    ]);

    // AC-8, AC-9 (036); AC-5, AC-7 (045)
    [Fact]
    public void Render_AllKinds_NumbersTogetherInTimeOrder_WithLabelsDetailsAndTimeLeftOnEveryRow()
    {
        TestConsole console = new TestConsole();
        console.Profile.Width = 120;

        console.Write(new DayView(FixedTimeProvider.At20261027()).Render(AllKinds()));

        string[] lines = console.Lines.Select(l => l.TrimEnd()).Where(l => l.Trim().Length > 0).ToArray();
        Assert.Equal(8, lines.Length);
        Assert.Matches(@"^1 +AR +08:00 +left +8:00$", lines[1]);
        Assert.Matches(@"^2 +CP +09:30 +Standup #5678 +left +6:30$", lines[2]);
        Assert.Matches(@"^3 +BS +12:00 +left +4:00$", lines[3]);
        Assert.Matches(@"^4 +BE +12:30 +left +4:00$", lines[4]);
        Assert.Matches(@"^5 +CP +15:00 +2024 review +left +1:30$", lines[5]);
        Assert.Matches(@"^6 +LE +17:00 +left +-0:30$", lines[6]);
        Assert.DoesNotContain("#", lines[5]);
        Assert.Equal("Breaks 0:30 taken · 0:30 required", lines[7].Trim());

        // The left cell is the right-aligned last column, so every entry row ends at the same column.
        Assert.Single(lines[1..7].Select(l => l.Length).Distinct());
    }

    // AC-5 (045)
    [Fact]
    public void Render_BreakRows_HaveEmptyDetailsCell_BeforeTheLeftCell()
    {
        TestConsole console = new TestConsole();
        console.Profile.Width = 120;

        console.Write(new DayView(FixedTimeProvider.At20261027()).Render(AllKinds()));

        string[] lines = console.Lines.Select(l => l.TrimEnd()).Where(l => l.Trim().Length > 0).ToArray();

        // The details column starts where the checkpoint name does; break rows hold only spaces there.
        int detailsStart = lines[2].IndexOf("Standup", StringComparison.Ordinal);
        int leftStart = lines[3].IndexOf("left", StringComparison.Ordinal);
        Assert.True(leftStart > detailsStart);
        Assert.Equal(string.Empty, lines[3][detailsStart..leftStart].Trim());
        Assert.Equal(string.Empty, lines[4][detailsStart..lines[4].IndexOf("left", StringComparison.Ordinal)].Trim());
    }

    // AC-5 (045)
    [Fact]
    public void Render_AllKinds_LeftIsGrey_AndNegativeValueNotRed()
    {
        TestConsole console = new TestConsole().EmitAnsiSequences();
        console.Profile.Width = 120;

        console.Write(new DayView(FixedTimeProvider.At20261027()).Render(AllKinds()));

        string output = console.Output;
        Assert.Equal(6, CountOf(output, GreyLeft()));
        Assert.Contains("-0:30", output);
        Assert.DoesNotContain("\u001b[31m", output);
        Assert.DoesNotContain("\u001b[91m", output);
        Assert.DoesNotContain("\u001b[38;5;9m", output);
    }

    // AC-8 (036)
    [Fact]
    public void Render_NewKinds_GreyCodes_KindColouredTimes_GreyWorkItem_DefaultName()
    {
        TestConsole console = new TestConsole().EmitAnsiSequences();
        console.Profile.Width = 120;

        console.Write(new DayView(FixedTimeProvider.At20261027()).Render(AllKinds()));

        // grey = 256-colour 8, SteelBlue1 = 256-colour 75.
        string output = console.Output;
        Assert.Contains("\u001b[38;5;8mBS", output);
        Assert.Contains("\u001b[38;5;8mBE", output);
        Assert.Contains("\u001b[38;5;8mCP", output);
        Assert.Contains("\u001b[38;5;75m09:30", output);
        Assert.Contains("\u001b[38;5;36m12:00", output);
        Assert.Contains("\u001b[38;5;36m12:30", output);
        Assert.Contains("\u001b[38;5;8m#5678", output);
        Assert.Matches(@"(^|\s)Standup", output);
        Assert.DoesNotContain("\u001b[38;5;8mStandup", output);
    }

    // AC-8 (036)
    [Fact]
    public void Render_CheckpointName_IsEscaped()
    {
        TestConsole console = new TestConsole();
        console.Profile.Width = 120;
        DayRecord record = DayRecord.Empty.Add(new DayEntry(EntryKind.Checkpoint, new TimeOnly(9, 0), "[red]Fix[/] [bold] #1", 42));

        console.Write(new DayView(FixedTimeProvider.At20261027()).Render(record));

        Assert.Matches(@"1 +CP +09:00 +\[red\]Fix\[/\] \[bold\] #1 #42", console.Output);
    }

    // AC-8 (036); AC-5, AC-7 (045)
    [Fact]
    public void Render_WithoutCheckpoint_NoDetailsColumn_AndEveryRowShowsTimeLeft()
    {
        TestConsole console = new TestConsole();
        DayRecord record = new DayRecord(
        [
            new DayEntry(EntryKind.Arrival, new TimeOnly(8, 0)),
            new DayEntry(EntryKind.BreakStart, new TimeOnly(12, 0)),
            new DayEntry(EntryKind.Leaving, new TimeOnly(17, 0)),
        ]);

        console.Write(new DayView(FixedTimeProvider.At20261027()).Render(record));

        string[] lines = console.Lines.Select(l => l.TrimEnd()).Where(l => l.Trim().Length > 0).ToArray();
        Assert.Matches(@"^1 +AR +08:00 +left +8:00$", lines[1]);
        Assert.Matches(@"^2 +BS +12:00 +left +4:00$", lines[2]);
        Assert.Matches(@"^3 +LE +17:00 +left +4:00$", lines[3]);
        Assert.Equal("Breaks 5:00 taken · 0:00 required", lines[4].Trim());
        Assert.Single(lines[1..4].Select(l => l.Length).Distinct());

        // Same single space between the time and "left" as a day with only arrival and leaving.
        TestConsole plain = new TestConsole();
        plain.Write(new DayView(FixedTimeProvider.At20261027()).Render(new DayRecord(
            [new DayEntry(EntryKind.Arrival, new TimeOnly(8, 0)), new DayEntry(EntryKind.Leaving, new TimeOnly(12, 0))])));
        string[] plainLines = plain.Lines.Select(l => l.TrimEnd()).Where(l => l.Trim().Length > 0).ToArray();
        Assert.Equal(plainLines[1][plainLines[1].IndexOf("08:00", StringComparison.Ordinal)..], lines[1][lines[1].IndexOf("08:00", StringComparison.Ordinal)..]);
    }

    private static string[] BreakLines(DayRecord record)
    {
        TestConsole console = new TestConsole();
        console.Profile.Width = 120;
        console.Write(new DayView(FixedTimeProvider.At20261027()).Render(record));
        return console.Lines.Select(l => l.TrimEnd()).Where(l => l.Trim().Length > 0).ToArray();
    }

    private static string BreakAnsi(DayRecord record)
    {
        TestConsole console = new TestConsole().EmitAnsiSequences();
        console.Profile.Width = 120;
        console.Write(new DayView(FixedTimeProvider.At20261027()).Render(record));
        return console.Output;
    }

    private static DayRecord ShortBreakDay() => new DayRecord(
    [
        new DayEntry(EntryKind.Arrival, new TimeOnly(8, 0)),
        new DayEntry(EntryKind.BreakStart, new TimeOnly(12, 0)),
        new DayEntry(EntryKind.BreakEnd, new TimeOnly(12, 20)),
        new DayEntry(EntryKind.Leaving, new TimeOnly(17, 0)),
    ]);

    private static DayRecord LongBreakDay() => new DayRecord(
    [
        new DayEntry(EntryKind.Arrival, new TimeOnly(8, 0)),
        new DayEntry(EntryKind.BreakStart, new TimeOnly(12, 0)),
        new DayEntry(EntryKind.BreakEnd, new TimeOnly(12, 40)),
        new DayEntry(EntryKind.Leaving, new TimeOnly(17, 0)),
    ]);

    // AC-4, AC-5 (047)
    [Fact]
    public void Render_BreakLine_FollowsLastEntryRow_WithWarningWhenShort()
    {
        string[] lines = BreakLines(ShortBreakDay());

        Assert.Equal(6, lines.Length);
        Assert.Matches(@"^4 +LE +17:00 +left", lines[4]);
        Assert.Equal("Breaks 0:20 taken · 0:30 required  ! Breaks too short: 0:10 missing", lines[5].Trim());
    }

    // AC-4 (047)
    [Fact]
    public void Render_BreakLine_GreyWords_SteelBlueValues()
    {
        string output = BreakAnsi(ShortBreakDay());

        Assert.Contains("\u001b[38;5;8mBreaks\u001b[0m \u001b[38;5;75m0:20\u001b[0m \u001b[38;5;8mtaken · \u001b[0m\u001b[38;5;75m0:30\u001b[0m \u001b[38;5;8mrequired", output);
    }

    // AC-5 (047)
    [Fact]
    public void Render_ShortBreak_WarningIsYellow()
    {
        string output = BreakAnsi(ShortBreakDay());

        Assert.Contains("\u001b[38;5;11m! Breaks too short: 0:10 missing", output);
    }

    // AC-5 (047)
    [Fact]
    public void Render_LongerBreakThanRequired_NoWarningAndNoYellow()
    {
        string[] lines = BreakLines(LongBreakDay());
        string output = BreakAnsi(LongBreakDay());

        Assert.Equal("Breaks 0:40 taken · 0:30 required", lines[^1].Trim());
        Assert.DoesNotContain("too short", output);
        Assert.DoesNotContain("\u001b[93m", output);
        Assert.DoesNotContain("\u001b[33m", output);
        Assert.DoesNotContain("\u001b[38;5;11m", output);
    }

    // AC-5 (047)
    [Fact]
    public void Render_BreakEqualToRequired_OrNoneRequired_NoWarning()
    {
        DayRecord equal = new DayRecord(
        [
            new DayEntry(EntryKind.Arrival, new TimeOnly(8, 0)),
            new DayEntry(EntryKind.BreakStart, new TimeOnly(12, 0)),
            new DayEntry(EntryKind.BreakEnd, new TimeOnly(12, 30)),
            new DayEntry(EntryKind.Leaving, new TimeOnly(17, 0)),
        ]);
        DayRecord none = new DayRecord(
            [new DayEntry(EntryKind.Arrival, new TimeOnly(8, 0)), new DayEntry(EntryKind.Leaving, new TimeOnly(14, 0))]);

        Assert.Equal("Breaks 0:30 taken · 0:30 required", BreakLines(equal)[^1].Trim());
        Assert.Equal("Breaks 0:00 taken · 0:00 required", BreakLines(none)[^1].Trim());
        foreach (string output in new[] { BreakAnsi(equal), BreakAnsi(none) })
        {
            Assert.DoesNotContain("too short", output);
            Assert.DoesNotContain("\u001b[93m", output);
            Assert.DoesNotContain("\u001b[33m", output);
            Assert.DoesNotContain("\u001b[38;5;11m", output);
        }
    }

    // AC-6 (047)
    [Fact]
    public void Render_EmptyDay_HasNoBreakLine()
    {
        TestConsole console = new TestConsole();

        console.Write(new DayView(FixedTimeProvider.At20261027()).Render(DayRecord.Empty));

        string[] lines = console.Lines.Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
        Assert.Equal(2, lines.Length);
        Assert.Equal("No entries yet.", lines[1]);
        Assert.DoesNotContain("Breaks", console.Output);
    }

    // AC-6 (047)
    [Fact]
    public void RenderUnreadable_HasNoBreakLine()
    {
        TestConsole console = new TestConsole();

        console.Write(new DayView(FixedTimeProvider.At20261027()).RenderUnreadable("Could not read the day."));

        string[] lines = console.Lines.Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
        Assert.Equal(2, lines.Length);
        Assert.Equal("Could not read the day.", lines[1]);
        Assert.DoesNotContain("Breaks", console.Output);
    }

    // Day view polish (job 076) ------------------------------------------------------------

    private static string[] RawLines(DayRecord record)
    {
        TestConsole console = new TestConsole();
        console.Profile.Width = 120;
        console.Write(new DayView(FixedTimeProvider.At20261027()).Render(record));
        return console.Output.Split('\n').Select(l => l.TrimEnd()).ToArray();
    }

    private static readonly System.Text.RegularExpressions.Regex EntryRow =
        new System.Text.RegularExpressions.Regex(@"^ *\d+ +(?<code>AR|LE|BS|BE|CP) +(?<time>\d\d:\d\d)");

    private static DayRecord Arrivals(int count) =>
        new DayRecord(Enumerable.Range(0, count).Select(i => new DayEntry(EntryKind.Arrival, new TimeOnly(8 + i, 0))).ToList());

    // AC-1, AC-4 (076)
    [Fact]
    public void Render_BlankLineAfterHeading_AndOneBlankLineBeforeBreaks()
    {
        foreach (DayRecord record in new[] { AllKinds(), ShortBreakDay(), Arrivals(10) })
        {
            string[] lines = RawLines(record);

            Assert.Contains("Tuesday, 2026-10-27", lines[0]);
            Assert.Equal(string.Empty, lines[1]);
            Assert.Matches(EntryRow, lines[2]);
            Assert.StartsWith("1 ", lines[2].TrimStart());

            int breaks = Array.FindIndex(lines, l => l.TrimStart().StartsWith("Breaks ", StringComparison.Ordinal));
            Assert.True(breaks > 3);
            Assert.Equal(string.Empty, lines[breaks - 1]);
            Assert.Matches(EntryRow, lines[breaks - 2]);
            Assert.StartsWith(record.Entries.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " ", lines[breaks - 2].TrimStart());
        }
    }

    // AC-2 (076)
    [Fact]
    public void Render_AllKinds_ShowsTwoLetterCodes_NoWords_TimesInOneColumn()
    {
        string[] lines = RawLines(AllKinds());
        string output = string.Join('\n', lines);

        foreach (string code in new[] { "AR", "LE", "BS", "BE", "CP" })
        {
            Assert.Matches(@"^ *\d+ +" + code + @" +\d\d:\d\d", lines.First(l => EntryRow.Match(l).Groups["code"].Value == code));
        }

        foreach (string word in new[] { "Arrival", "Leaving", "Break start", "Break end", "Checkpoint" })
        {
            Assert.DoesNotContain(word, output);
        }

        int[] timeColumns = lines.Select(l => EntryRow.Match(l)).Where(m => m.Success).Select(m => m.Groups["time"].Index).ToArray();
        Assert.Equal(6, timeColumns.Length);
        Assert.Single(timeColumns.Distinct());
    }

    // AC-3 (076)
    [Fact]
    public void Render_TimesColouredByKind_NeverYellowOrRed_OtherCellsUnchanged()
    {
        string output = BreakAnsi(AllKinds());

        Assert.Contains("\u001b[38;5;34m08:00", output);
        Assert.Contains("\u001b[38;5;75m09:30", output);
        Assert.Contains("\u001b[38;5;36m12:00", output);
        Assert.Contains("\u001b[38;5;36m12:30", output);
        Assert.Contains("\u001b[38;5;75m15:00", output);
        Assert.Contains("\u001b[38;5;134m17:00", output);
        Assert.Contains("\u001b[38;5;8m1", output);
        Assert.Contains("\u001b[38;5;8mAR", output);
        Assert.Contains("\u001b[38;5;8m#5678", output);
        Assert.Equal(6, CountOf(output, GreyLeft()));

        foreach (string ansi in new[] { output, BreakAnsi(ShortBreakDay()) })
        {
            Assert.DoesNotMatch(@"\x1b\[(38;5;(11|3|9|1)|33|31|93|91)m\d\d:\d\d", ansi);
        }
    }

    // AC-5 (076)
    [Fact]
    public void Render_ShortBreak_WarningSitsOnTheSummaryLine_WithBangInYellow()
    {
        string[] lines = RawLines(ShortBreakDay());

        string[] summary = lines.Where(l => l.Contains("Breaks too short", StringComparison.Ordinal)).ToArray();
        Assert.Single(summary);
        Assert.Equal("Breaks 0:20 taken · 0:30 required  ! Breaks too short: 0:10 missing", summary[0].Trim());
        // No separate warning line: the summary is the last non-empty line.
        Assert.Equal(summary[0], lines.Last(l => l.Length > 0));

        string ansi = BreakAnsi(ShortBreakDay());
        Assert.Contains("\u001b[38;5;8mrequired\u001b[0m  \u001b[38;5;11m! Breaks too short: 0:10 missing\u001b[0m", ansi);
    }

    // AC-5 (076)
    [Fact]
    public void Render_EnoughBreak_SummaryEndsAfterRequired_WithoutBang()
    {
        foreach (DayRecord record in new[] { LongBreakDay(), AllKinds() })
        {
            string summary = RawLines(record).Single(l => l.TrimStart().StartsWith("Breaks ", StringComparison.Ordinal));

            Assert.EndsWith("required", summary);
            Assert.DoesNotContain("!", summary);
        }
    }

    // AC-6 (076)
    [Theory]
    [InlineData(3)]
    [InlineData(10)]
    public void Render_BreaksStartsInTheCodeColumn(int count)
    {
        string[] lines = RawLines(Arrivals(count));

        int breaks = lines.Single(l => l.TrimStart().StartsWith("Breaks ", StringComparison.Ordinal)).IndexOf('B');
        int[] codes = lines.Select(l => EntryRow.Match(l)).Where(m => m.Success).Select(m => m.Groups["code"].Index).ToArray();
        Assert.Equal(count, codes.Length);
        Assert.All(codes, code => Assert.Equal(breaks, code));
    }

    /// <summary>The ANSI text Spectre writes for a grey <c>left</c>, taken from a grey <see cref="Text"/> instead of a raw colour code.</summary>
    private static string GreyLeft()
    {
        TestConsole console = new TestConsole().EmitAnsiSequences();
        console.Write(new Text("left", new Style(Color.Grey)));
        return console.Output.TrimEnd('\n').Replace("\u001b[0m", string.Empty);
    }

    private static int CountOf(string text, string value)
    {
        int count = 0;
        for (int at = text.IndexOf(value, StringComparison.Ordinal); at >= 0; at = text.IndexOf(value, at + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
