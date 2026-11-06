using Spectre.Console;
using Spectre.Console.Testing;

namespace Times.Tests;

public class DayViewTests
{
    private static readonly TimeSpan EightHours = TimeSpan.FromHours(8);

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

        console.Write(new DayView(FixedTimeProvider.At20261027()).Render(DayRecord.Empty, EightHours, FlexitimeBalance.None));

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

        console.Write(view.Render(DayRecord.Empty, EightHours, FlexitimeBalance.None));

        Assert.Contains("Wednesday, 2026-10-28", console.Output);
        Assert.Contains("No entries yet.", console.Output);
    }

    // AC-5 (017)
    [Fact]
    public void Render_OneEntry_ShowsNumberedRow()
    {
        TestConsole console = new TestConsole();
        DayView view = new DayView(FixedTimeProvider.At20261027());

        console.Write(view.Render(DayRecord.Empty.Add(new DayEntry(EntryKind.Arrival, new TimeOnly(8, 15))), EightHours, FlexitimeBalance.None));

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

        console.Write(view.Render(record, EightHours, FlexitimeBalance.None));

        string[] lines = console.Lines.Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
        Assert.Equal(6, lines.Length);
        Assert.Matches(@"^1 +AR +08:15 +left +8:00$", lines[1]);
        Assert.Matches(@"^2 +LE +12:00 +left +4:15$", lines[2]);
        Assert.Matches(@"^3 +AR +13:00 +left +4:15$", lines[3]);
        Assert.Equal("Breaks 1:00 taken · 0:00 required", lines[4]);
        Assert.Equal("Target 8:00 · day -4:15 · balance 0:00", lines[5]);
        Assert.DoesNotContain("—", console.Output);
    }

    // AC-9 (084)
    [Fact]
    public void Render_Today_AwayAfterLeaving_GapCountsNothingYet()
    {
        TestConsole console = new TestConsole();
        DayView view = new DayView(
            new FixedTimeProvider(new DateTimeOffset(2026, 10, 27, 12, 40, 0, TimeSpan.Zero), TimeZoneInfo.Utc));
        DayRecord record = new DayRecord(
            [new DayEntry(EntryKind.Arrival, new TimeOnly(8, 0)), new DayEntry(EntryKind.Leaving, new TimeOnly(12, 0))]);

        console.Write(view.Render(record, EightHours, FlexitimeBalance.None));

        Assert.Equal(new DateOnly(2026, 10, 27), view.Current);
        Assert.Contains("Breaks 0:00 taken · 0:00 required", console.Output);
    }

    // AC-5 (017)
    [Fact]
    public void Render_UsesGreyNumbersAndCodes_AndKindColouredTimes()
    {
        TestConsole console = new TestConsole().EmitAnsiSequences();
        DayView view = new DayView(FixedTimeProvider.At20261027());

        console.Write(view.Render(DayRecord.Empty
            .Add(new DayEntry(EntryKind.Arrival, new TimeOnly(8, 15)))
            .Add(new DayEntry(EntryKind.Leaving, new TimeOnly(17, 30))), EightHours, FlexitimeBalance.None));

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

        console.Write(new DayView(FixedTimeProvider.At20261027()).Render(DayRecord.Empty, EightHours, FlexitimeBalance.None));

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

        console.Write(view.Render(record, EightHours, FlexitimeBalance.None));

        string[] lines = console.Lines.Select(l => l.TrimEnd()).Where(l => l.Trim().Length > 0).ToArray();
        Assert.Equal(7, lines.Length);
        Assert.Matches(@"^1 +AR +08:00 +left +8:00$", lines[1]);
        Assert.Matches(@"^2 +LE +12:00 +left +4:00$", lines[2]);
        Assert.Matches(@"^3 +AR +12:30 +left +4:00$", lines[3]);
        Assert.Matches(@"^4 +LE +17:00 +left -0:30$", lines[4]);
        Assert.Equal("Breaks 0:30 taken · 0:30 required", lines[5].Trim());
        Assert.DoesNotContain("Breaks too short", console.Output);

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
            .Add(new DayEntry(EntryKind.Leaving, new TimeOnly(17, 0))), EightHours, FlexitimeBalance.None));

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

        console.Write(new DayView(FixedTimeProvider.At20261027()).Render(DayRecord.Empty, EightHours, FlexitimeBalance.None));

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

        console.Write(new DayView(FixedTimeProvider.At20261027()).Render(AllKinds(), EightHours, FlexitimeBalance.None));

        string[] lines = console.Lines.Select(l => l.TrimEnd()).Where(l => l.Trim().Length > 0).ToArray();
        Assert.Equal(9, lines.Length);
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

        console.Write(new DayView(FixedTimeProvider.At20261027()).Render(AllKinds(), EightHours, FlexitimeBalance.None));

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

        console.Write(new DayView(FixedTimeProvider.At20261027()).Render(AllKinds(), EightHours, FlexitimeBalance.None));

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

        console.Write(new DayView(FixedTimeProvider.At20261027()).Render(AllKinds(), EightHours, FlexitimeBalance.None));

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

        console.Write(new DayView(FixedTimeProvider.At20261027()).Render(record, EightHours, FlexitimeBalance.None));

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

        console.Write(new DayView(FixedTimeProvider.At20261027()).Render(record, EightHours, FlexitimeBalance.None));

        string[] lines = console.Lines.Select(l => l.TrimEnd()).Where(l => l.Trim().Length > 0).ToArray();
        Assert.Matches(@"^1 +AR +08:00 +left +8:00$", lines[1]);
        Assert.Matches(@"^2 +BS +12:00 +left +4:00$", lines[2]);
        Assert.Matches(@"^3 +LE +17:00 +left +4:00$", lines[3]);
        Assert.Equal("Breaks 5:00 taken · 0:00 required", lines[4].Trim());
        Assert.Single(lines[1..4].Select(l => l.Length).Distinct());

        // Same single space between the time and "left" as a day with only arrival and leaving.
        TestConsole plain = new TestConsole();
        plain.Write(new DayView(FixedTimeProvider.At20261027()).Render(new DayRecord(
            [new DayEntry(EntryKind.Arrival, new TimeOnly(8, 0)), new DayEntry(EntryKind.Leaving, new TimeOnly(12, 0))]), EightHours, FlexitimeBalance.None));
        string[] plainLines = plain.Lines.Select(l => l.TrimEnd()).Where(l => l.Trim().Length > 0).ToArray();
        Assert.Equal(plainLines[1][plainLines[1].IndexOf("08:00", StringComparison.Ordinal)..], lines[1][lines[1].IndexOf("08:00", StringComparison.Ordinal)..]);
    }

    private static string[] BreakLines(DayRecord record)
    {
        TestConsole console = new TestConsole();
        console.Profile.Width = 120;
        console.Write(new DayView(FixedTimeProvider.At20261027()).Render(record, EightHours, FlexitimeBalance.None));
        return console.Lines.Select(l => l.TrimEnd()).Where(l => l.Trim().Length > 0).ToArray();
    }

    private static string BreakAnsi(DayRecord record)
    {
        TestConsole console = new TestConsole().EmitAnsiSequences();
        console.Profile.Width = 120;
        console.Write(new DayView(FixedTimeProvider.At20261027()).Render(record, EightHours, FlexitimeBalance.None));
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

        Assert.Equal(7, lines.Length);
        Assert.Matches(@"^4 +LE +17:00 +left", lines[4]);
        Assert.Equal("Breaks 0:20 taken · 0:30 required  ! Breaks too short: 0:10 missing", lines[5].Trim());
        Assert.StartsWith("Target 8:00 · day ", lines[6].Trim());
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

        Assert.Equal("Breaks 0:40 taken · 0:30 required", lines[^2].Trim());
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

        Assert.Equal("Breaks 0:30 taken · 0:30 required", BreakLines(equal)[^2].Trim());
        Assert.Equal("Breaks 0:00 taken · 0:00 required", BreakLines(none)[^2].Trim());
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

        console.Write(new DayView(FixedTimeProvider.At20261027()).Render(DayRecord.Empty, EightHours, FlexitimeBalance.None));

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
        console.Write(new DayView(FixedTimeProvider.At20261027()).Render(record, EightHours, FlexitimeBalance.None));
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
        // No separate warning line: only the target line follows the summary.
        string[] nonEmpty = lines.Where(l => l.Length > 0).ToArray();
        Assert.Equal(summary[0], nonEmpty[^2]);
        Assert.StartsWith("Target ", nonEmpty[^1].Trim());

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

    // Booked checkpoints (job 078) --------------------------------------------------------

    private static DayRecord BookedDay() => new DayRecord(
    [
        new DayEntry(EntryKind.Arrival, new TimeOnly(8, 0)),
        new DayEntry(EntryKind.Checkpoint, new TimeOnly(9, 15), "Done [x]", 5678, true),
        new DayEntry(EntryKind.Checkpoint, new TimeOnly(10, 45), "Open", 1234),
        new DayEntry(EntryKind.Leaving, new TimeOnly(12, 0)),
    ]);

    private static string RenderAnsi(DayRecord record)
    {
        TestConsole console = new TestConsole().EmitAnsiSequences();
        console.Profile.Width = 120;
        console.Write(new DayView(FixedTimeProvider.At20261027()).Render(record, EightHours, FlexitimeBalance.None));
        return console.Output;
    }

    // AC-9 (078)
    [Fact]
    public void Render_BookedCheckpoint_GreyTimeAndName_GreyWorkItem_GreyCheckMark()
    {
        string output = RenderAnsi(BookedDay());

        // grey = 256-colour 8, SteelBlue1 = 256-colour 75.
        Assert.Contains("\u001b[38;5;8m09:15\u001b[0m", output);
        Assert.DoesNotContain("\u001b[38;5;75m09:15", output);
        Assert.Contains("\u001b[38;5;8mDone [x]\u001b[0m \u001b[38;5;8m#5678\u001b[0m \u001b[38;5;8m✓\u001b[0m", output);
        Assert.Equal(1, CountOf(output, "✓"));
    }

    // AC-9 (078)
    [Fact]
    public void Render_UnbookedCheckpointAndOtherKinds_LookAsBefore()
    {
        string output = RenderAnsi(BookedDay());
        string unbooked = RenderAnsi(new DayRecord(
        [
            new DayEntry(EntryKind.Arrival, new TimeOnly(8, 0)),
            new DayEntry(EntryKind.Checkpoint, new TimeOnly(9, 15), "Done [x]", 5678),
            new DayEntry(EntryKind.Checkpoint, new TimeOnly(10, 45), "Open", 1234),
            new DayEntry(EntryKind.Leaving, new TimeOnly(12, 0)),
        ]));

        Assert.Contains("\u001b[38;5;75m10:45\u001b[0m", output);
        Assert.Contains("Open \u001b[38;5;8m#1234\u001b[0m", output);
        Assert.Contains("\u001b[38;5;75m09:15\u001b[0m", unbooked);
        Assert.DoesNotContain("✓", unbooked);
        Assert.DoesNotContain("\u001b[38;5;8mOpen", output);
        Assert.Contains("\u001b[38;5;34m08:00", output);
    }

    // AC-9 (078)
    [Fact]
    public void Render_BookedCheckpoint_PlainText_EndsWithCheckMark()
    {
        TestConsole console = new TestConsole();
        console.Profile.Width = 120;
        console.Write(new DayView(FixedTimeProvider.At20261027()).Render(BookedDay(), EightHours, FlexitimeBalance.None));

        Assert.Matches(@"2 +CP +09:15 +Done \[x\] #5678 ✓ +left 6:45", console.Output);
        Assert.Matches(@"3 +CP +10:45 +Open #1234 +left 5:15", console.Output);
    }

    private static DayRecord UnendedDay() => new DayRecord(
    [
        new DayEntry(EntryKind.Arrival, new TimeOnly(8, 0)),
        new DayEntry(EntryKind.BreakStart, new TimeOnly(12, 0)),
        new DayEntry(EntryKind.Leaving, new TimeOnly(17, 0)),
    ]);

    private static DayRecord EarlyBreakDay() => new DayRecord(
    [
        new DayEntry(EntryKind.BreakStart, new TimeOnly(7, 0)),
        new DayEntry(EntryKind.Arrival, new TimeOnly(8, 0)),
        new DayEntry(EntryKind.Leaving, new TimeOnly(17, 0)),
    ]);

    // AC-7, AC-10 (083)
    [Fact]
    public void Render_UnendedBreak_HintDirectlyBelowSummary_SameIndent()
    {
        string[] lines = BreakLines(UnendedDay());

        Assert.Equal(7, lines.Length);
        Assert.Matches(@"^1 +AR +08:00 +left 8:00$", lines[1]);
        Assert.Matches(@"^2 +BS +12:00 +left 4:00$", lines[2]);
        Assert.Matches(@"^3 +LE +17:00 +left 4:00$", lines[3]);
        Assert.Equal("   Breaks 5:00 taken · 0:00 required", lines[4]);
        Assert.Equal("   Target 8:00 · day -4:00 · balance 0:00", lines[5]);
        Assert.Equal("   ! Break not ended: entry 2", lines[6]);
    }

    // AC-7 (083)
    [Fact]
    public void Render_UnendedBreak_HintIsYellow()
    {
        string output = BreakAnsi(UnendedDay());

        Assert.Contains("\u001b[38;5;11m! Break not ended: entry 2\u001b[0m", output);
    }

    // AC-7 (083)
    [Fact]
    public void Render_TwoUnendedBreaks_ListsEntries()
    {
        DayRecord record = new DayRecord(
        [
            new DayEntry(EntryKind.Arrival, new TimeOnly(8, 0)),
            new DayEntry(EntryKind.BreakStart, new TimeOnly(10, 0)),
            new DayEntry(EntryKind.Arrival, new TimeOnly(11, 0)),
            new DayEntry(EntryKind.BreakStart, new TimeOnly(12, 0)),
            new DayEntry(EntryKind.Leaving, new TimeOnly(17, 0)),
        ]);

        string[] lines = BreakLines(record);

        Assert.Equal("   ! Break not ended: entries 2, 4", lines[^1]);
        Assert.StartsWith("   Target ", lines[^2]);
        Assert.StartsWith("   Breaks ", lines[^3]);
    }

    // AC-8, AC-10 (083)
    [Fact]
    public void Render_BreakOutsideSpan_ShowsHintInYellow()
    {
        string[] lines = BreakLines(EarlyBreakDay());
        string output = BreakAnsi(EarlyBreakDay());

        // The break start is also never ended: the next break or leaving entry is the leaving.
        Assert.Equal(8, lines.Length);
        Assert.Matches(@"^1 +BS +07:00 +left 8:00$", lines[1]);
        Assert.Matches(@"^3 +LE +17:00 +left -1:00$", lines[3]);
        Assert.Equal("   Breaks 0:00 taken · 0:30 required  ! Breaks too short: 0:30 missing", lines[4]);
        Assert.Equal("   Target 8:00 · day +1:00 · balance 0:00", lines[5]);
        Assert.Equal("   ! Break not ended: entry 1", lines[6]);
        Assert.Equal("   ! Break outside arrival and leaving: entry 1", lines[7]);
        Assert.Contains("\u001b[38;5;11m! Break outside arrival and leaving: entry 1\u001b[0m", output);
    }

    // AC-8 (083)
    [Fact]
    public void Render_BothHints_NotEndedFirst_ThenOutside()
    {
        DayRecord record = new DayRecord(
        [
            new DayEntry(EntryKind.Arrival, new TimeOnly(8, 0)),
            new DayEntry(EntryKind.BreakStart, new TimeOnly(12, 0)),
            new DayEntry(EntryKind.Leaving, new TimeOnly(17, 0)),
            new DayEntry(EntryKind.BreakStart, new TimeOnly(18, 0)),
            new DayEntry(EntryKind.BreakEnd, new TimeOnly(18, 30)),
        ]);

        string[] lines = BreakLines(record);

        Assert.StartsWith("   Breaks ", lines[^4]);
        Assert.StartsWith("   Target ", lines[^3]);
        Assert.Equal("   ! Break not ended: entry 2", lines[^2]);
        Assert.Equal("   ! Break outside arrival and leaving: entries 4, 5", lines[^1]);
    }

    // AC-4, AC-10 (083)
    [Fact]
    public void Render_BreakEndBetweenSpans_ShowsOutsideHint_ValuesUnchanged()
    {
        DayRecord record = new DayRecord(
        [
            new DayEntry(EntryKind.Arrival, new TimeOnly(8, 0)),
            new DayEntry(EntryKind.Leaving, new TimeOnly(12, 0)),
            new DayEntry(EntryKind.BreakEnd, new TimeOnly(12, 30)),
            new DayEntry(EntryKind.Arrival, new TimeOnly(13, 0)),
            new DayEntry(EntryKind.Leaving, new TimeOnly(17, 0)),
        ]);

        string[] lines = BreakLines(record);

        Assert.Matches(@"^3 +BE +12:30 +left 4:00$", lines[3]);
        Assert.Matches(@"^5 +LE +17:00 +left 0:00$", lines[5]);
        Assert.Equal("   Breaks 1:00 taken · 0:30 required", lines[6]);
        Assert.Equal("   Target 8:00 · day 0:00 · balance 0:00", lines[7]);
        Assert.Equal("   ! Break outside arrival and leaving: entry 3", lines[8]);
    }

    // AC-6, AC-10 (083)
    [Fact]
    public void Render_SameTime_BookingOrderDecidesHints()
    {
        DayEntry arrival = new DayEntry(EntryKind.Arrival, new TimeOnly(8, 0));
        DayEntry breakStart = new DayEntry(EntryKind.BreakStart, new TimeOnly(17, 0));
        DayEntry leaving = new DayEntry(EntryKind.Leaving, new TimeOnly(17, 0));
        string[] breakFirst = BreakLines(DayRecord.Empty.Add(arrival).Add(breakStart).Add(leaving));
        string[] leaveFirst = BreakLines(DayRecord.Empty.Add(arrival).Add(leaving).Add(breakStart));

        Assert.Equal(7, breakFirst.Length);
        Assert.Matches(@"^3 +LE +17:00 +left -1:00$", breakFirst[3]);
        Assert.Equal("   Breaks 0:00 taken · 0:30 required  ! Breaks too short: 0:30 missing", breakFirst[4]);
        Assert.Equal("   Target 8:00 · day +1:00 · balance 0:00", breakFirst[5]);
        Assert.Equal("   ! Break not ended: entry 2", breakFirst[6]);
        Assert.Equal(8, leaveFirst.Length);
        Assert.Matches(@"^3 +BS +17:00 +left -1:00$", leaveFirst[3]);
        Assert.Equal("   Breaks 0:00 taken · 0:30 required  ! Breaks too short: 0:30 missing", leaveFirst[4]);
        Assert.Equal("   Target 8:00 · day +1:00 · balance 0:00", leaveFirst[5]);
        Assert.Equal("   ! Break not ended: entry 3", leaveFirst[6]);
        Assert.Equal("   ! Break outside arrival and leaving: entry 3", leaveFirst[7]);
    }

    // AC-9 (083)
    [Fact]
    public void Render_PairedBreaksInsideSpans_AndEmptyDay_ShowNoHint()
    {
        string paired = BreakAnsi(LongBreakDay());
        string empty = BreakAnsi(DayRecord.Empty);

        foreach (string output in new[] { paired, empty })
        {
            Assert.DoesNotContain("Break not ended", output);
            Assert.DoesNotContain("Break outside", output);
        }

        Assert.Equal("Breaks 0:40 taken · 0:30 required", BreakLines(LongBreakDay())[^2].Trim());
    }

    private static string[] PlainLines(DayRecord record, TimeSpan target, FlexitimeBalance balance)
    {
        TestConsole console = new TestConsole();
        console.Profile.Width = 120;
        console.Write(new DayView(FixedTimeProvider.At20261027()).Render(record, target, balance));
        return console.Lines.Select(l => l.TrimEnd()).Where(l => l.Trim().Length > 0).ToArray();
    }

    private static string AnsiOutput(DayRecord record, TimeSpan target, FlexitimeBalance balance)
    {
        TestConsole console = new TestConsole().EmitAnsiSequences();
        console.Profile.Width = 120;
        console.Write(new DayView(FixedTimeProvider.At20261027()).Render(record, target, balance));
        return console.Output;
    }

    private static DayRecord MorningDay() => new DayRecord(
        [new DayEntry(EntryKind.Arrival, new TimeOnly(8, 0)), new DayEntry(EntryKind.Leaving, new TimeOnly(12, 0))]);

    // AC-2 (091)
    [Fact]
    public void Render_TimeLeft_FollowsTheGivenTarget()
    {
        string[] lines = PlainLines(MorningDay(), TimeSpan.FromMinutes(450), FlexitimeBalance.None);

        Assert.Matches(@"^1 +AR +08:00 +left +7:30$", lines[1]);
        Assert.Matches(@"^2 +LE +12:00 +left +3:30$", lines[2]);
    }

    // AC-3 (091)
    [Fact]
    public void Render_TargetLine_DirectlyBelowBreaks_SameIndent_BeforeHints()
    {
        DayRecord record = new DayRecord(
        [
            new DayEntry(EntryKind.Arrival, new TimeOnly(8, 0)),
            new DayEntry(EntryKind.BreakStart, new TimeOnly(12, 0)),
            new DayEntry(EntryKind.Leaving, new TimeOnly(15, 30)),
        ]);
        FlexitimeBalance balance = new FlexitimeBalance(new TimeSpan(1, 15, 0), 3, 0);

        string[] lines = PlainLines(record, EightHours, balance);

        Assert.Equal("   Breaks 3:30 taken · 0:00 required", lines[4]);
        Assert.Equal("   Target 8:00 · day -4:00 · balance +1:15", lines[5]);
        Assert.Equal("   ! Break not ended: entry 2", lines[6]);
    }

    // AC-3 (091)
    [Fact]
    public void Render_TargetLine_DayIsWorkTimeMinusTarget()
    {
        DayRecord record = new DayRecord(
            [new DayEntry(EntryKind.Arrival, new TimeOnly(8, 0)), new DayEntry(EntryKind.Leaving, new TimeOnly(15, 30))]);

        string[] lines = PlainLines(record, EightHours, new FlexitimeBalance(new TimeSpan(1, 15, 0), 2, 0));

        Assert.Equal("Target 8:00 · day -0:30 · balance +1:15", lines[^1].Trim());
    }

    // AC-3 (091)
    [Fact]
    public void Render_TargetLine_GreyWords_SteelBlueValues()
    {
        string output = AnsiOutput(MorningDay(), EightHours, new FlexitimeBalance(new TimeSpan(1, 15, 0), 2, 0));

        Assert.Contains(
            "\u001b[38;5;8mTarget\u001b[0m \u001b[38;5;75m8:00\u001b[0m \u001b[38;5;8m· day\u001b[0m \u001b[38;5;75m-4:00\u001b[0m " +
            "\u001b[38;5;8m· balance\u001b[0m \u001b[38;5;75m+1:15\u001b[0m",
            output);
    }

    // AC-4 (091)
    [Theory]
    [InlineData(1800, "+0:30")]
    [InlineData(-1800, "-0:30")]
    [InlineData(0, "0:00")]
    [InlineData(59, "0:00")]
    [InlineData(-59, "0:00")]
    [InlineData(61, "+0:01")]
    [InlineData(-61, "-0:01")]
    [InlineData(43500, "+12:05")]
    [InlineData(-43500, "-12:05")]
    [InlineData(329400, "+91:30")]
    public void FormatSigned_SignsAndTruncatesTowardZero(int seconds, string expected)
    {
        Assert.Equal(expected, DayView.FormatSigned(TimeSpan.FromSeconds(seconds)));
    }

    // AC-4 (091)
    [Fact]
    public void TargetLine_ShowsTheTargetWithoutSign()
    {
        Assert.Equal(
            "Target 0:00 · day +4:00 · balance 0:00",
            PlainLines(MorningDay(), TimeSpan.Zero, FlexitimeBalance.None)[^1].Trim());
        Assert.StartsWith("Target 24:00 ", PlainLines(MorningDay(), TimeSpan.FromMinutes(1440), FlexitimeBalance.None)[^1].Trim());
    }

    // AC-6 (091)
    [Fact]
    public void Render_EmptyDay_WithCountedDays_ShowsTargetLineWithoutDay()
    {
        string[] lines = PlainLines(DayRecord.Empty, EightHours, new FlexitimeBalance(TimeSpan.FromMinutes(30), 2, 0));

        Assert.Equal(3, lines.Length);
        Assert.Equal("No entries yet.", lines[1]);
        Assert.Equal("Target 8:00 · balance +0:30", lines[2]);
    }

    // AC-6 (091)
    [Fact]
    public void Render_EmptyDay_WithNoCountedDay_ShowsNoTargetLine()
    {
        string[] lines = PlainLines(DayRecord.Empty, EightHours, FlexitimeBalance.None);

        Assert.Equal(2, lines.Length);
        Assert.Equal("No entries yet.", lines[1]);
        Assert.DoesNotContain(lines, l => l.Contains("Target", StringComparison.Ordinal));
    }

    // AC-7 (091)
    [Fact]
    public void Render_SkippedFiles_EndTheTargetLineInYellow()
    {
        Assert.Equal(
            "Target 8:00 · day -4:00 · balance 0:00  ! 1 day file skipped",
            PlainLines(MorningDay(), EightHours, new FlexitimeBalance(TimeSpan.Zero, 1, 1))[^1].Trim());
        Assert.Equal(
            "Target 8:00 · balance -1:00  ! 2 day files skipped",
            PlainLines(DayRecord.Empty, EightHours, new FlexitimeBalance(TimeSpan.FromHours(-1), 1, 2))[^1].Trim());
        Assert.Contains(
            "\u001b[38;5;11m! 1 day file skipped\u001b[0m",
            AnsiOutput(MorningDay(), EightHours, new FlexitimeBalance(TimeSpan.Zero, 1, 1)));
    }

    // Job 098 ------------------------------------------------------------------------------

    // AC-10
    [Theory]
    [InlineData("Vacation", "Vacation")]
    [InlineData("Sick", "Sick")]
    [InlineData("PublicHoliday", "Public holiday")]
    public void Render_AbsenceDay_ShowsHeadingAbsenceAndTargetLine(string kind, string label)
    {
        AbsenceKind absence = Enum.Parse<AbsenceKind>(kind);
        DayRecord record = DayRecord.Empty.WithAbsence(absence);

        string[] lines = PlainLines(record, EightHours, new FlexitimeBalance(new TimeSpan(1, 15, 0), 3, 0));

        Assert.Equal(3, lines.Length);
        Assert.Contains("Tuesday, 2026-10-27", lines[0]);
        Assert.Equal("Absence: " + label, lines[1]);
        Assert.Equal("Target 8:00 · day +0:00 · balance +1:15", lines[2]);
        Assert.DoesNotContain(lines, l => l.Contains("No entries yet.", StringComparison.Ordinal));
    }

    // AC-10
    [Fact]
    public void Render_AbsenceDay_GreyLabel_SteelBlueValue()
    {
        string output = AnsiOutput(DayRecord.Empty.WithAbsence(AbsenceKind.Sick), EightHours, FlexitimeBalance.None);

        Assert.Contains("\u001b[38;5;8mAbsence:\u001b[0m \u001b[38;5;75mSick\u001b[0m", output);
        Assert.Contains("\u001b[38;5;75m+0:00\u001b[0m", output);
    }

    private static string RenderLive(DayRecord record, TimeOnly? now, FlexitimeBalance? balance = null)
    {
        TestConsole console = new TestConsole();
        console.Profile.Width = 80;
        console.Write(new DayView(FixedTimeProvider.At20261027()).Render(record, EightHours, balance ?? FlexitimeBalance.None, now));
        return console.Output;
    }

    private static string RenderOld(DayRecord record, FlexitimeBalance? balance = null)
    {
        TestConsole console = new TestConsole();
        console.Profile.Width = 80;
        console.Write(new DayView(FixedTimeProvider.At20261027()).Render(record, EightHours, balance ?? FlexitimeBalance.None));
        return console.Output;
    }

    private static readonly DayRecord WorkingDay = DayRecord.Empty
        .Add(new DayEntry(EntryKind.Arrival, new TimeOnly(8, 0)))
        .Add(new DayEntry(EntryKind.BreakStart, new TimeOnly(12, 0)))
        .Add(new DayEntry(EntryKind.BreakEnd, new TimeOnly(12, 30)));

    // AC-2
    [Fact]
    public void Render_WithLiveMinute_OnAWorkingDay_AddsANowRow_AndTheLiveDayDelta()
    {
        string output = RenderLive(WorkingDay, new TimeOnly(18, 30), new FlexitimeBalance(TimeSpan.FromMinutes(30), 2, 0));
        string[] lines = output.Split('\n');

        int nowRow = Array.FindIndex(lines, l => l.Contains("now", StringComparison.Ordinal));
        int lastEntry = Array.FindIndex(lines, l => l.Contains("12:30", StringComparison.Ordinal));
        Assert.Equal(lastEntry + 1, nowRow);
        // Empty number cell, then now, the time and left: 8:00 - 10:00 worked.
        Assert.Matches(@"^ +now +18:30 +left -2:00 *$", lines[nowRow]);
        Assert.Contains("Target 8:00 · day +2:00 · balance +0:30", output);
    }

    // AC-2
    [Fact]
    public void Render_NowRow_UsesGreyLabel_AndSteelBlueTime()
    {
        TestConsole console = new TestConsole().EmitAnsiSequences();
        console.Write(new DayView(FixedTimeProvider.At20261027()).Render(WorkingDay, EightHours, FlexitimeBalance.None, new TimeOnly(18, 30)));

        Assert.Contains("\u001b[38;5;8mnow\u001b[0m", console.Output);
        Assert.Contains("\u001b[38;5;75m18:30\u001b[0m", console.Output);
    }

    // AC-2
    [Fact]
    public void Render_WithLiveMinute_AndACheckpoint_KeepsTheDetailsColumn()
    {
        DayRecord record = WorkingDay.Add(new DayEntry(EntryKind.Checkpoint, new TimeOnly(13, 0), "Review", 42));

        string output = RenderLive(record, new TimeOnly(14, 0));

        Assert.Matches(@"(?m)^ +now +14:00 +left 2:30 *$", output);
        Assert.Contains("day -2:30", output);
    }

    // AC-2
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Render_WithoutLiveMinute_OrNotWorking_IsByteIdentical(bool live)
    {
        DayRecord[] days =
        [
            DayRecord.Empty,
            DayRecord.Empty.WithAbsence(AbsenceKind.Vacation),
            DayRecord.Empty.Add(new DayEntry(EntryKind.Arrival, new TimeOnly(8, 0))).Add(new DayEntry(EntryKind.Leaving, new TimeOnly(17, 0))),
            DayRecord.Empty.Add(new DayEntry(EntryKind.Arrival, new TimeOnly(8, 0))).Add(new DayEntry(EntryKind.BreakStart, new TimeOnly(12, 0))),
            DayRecord.Empty.Add(new DayEntry(EntryKind.Checkpoint, new TimeOnly(9, 0), "Standup", null)),
        ];
        FlexitimeBalance balance = new FlexitimeBalance(TimeSpan.FromMinutes(15), 1, 0);
        foreach (DayRecord day in days)
        {
            Assert.Equal(RenderOld(day, balance), RenderLive(day, live ? new TimeOnly(18, 30) : null, balance));
        }

        // A working day without a live minute is the old render too.
        Assert.Equal(RenderOld(WorkingDay, balance), RenderLive(WorkingDay, null, balance));
        Assert.DoesNotContain("now", RenderLive(WorkingDay, null, balance));
    }

    // AC-2: a minute before the last entry (a booking ahead of the clock) shows no now row.
    [Fact]
    public void Render_WithLiveMinute_BeforeTheLastEntry_IsByteIdentical()
    {
        Assert.Equal(RenderOld(WorkingDay), RenderLive(WorkingDay, new TimeOnly(12, 0)));
    }
}
