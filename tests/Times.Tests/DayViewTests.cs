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
        Assert.Matches(@"1 +Arrival +08:15", console.Output);
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
        Assert.Equal(4, lines.Length);
        Assert.Matches(@"^1 +Arrival +08:15 +left +8:00$", lines[1]);
        Assert.Matches(@"^2 +Leaving +12:00 +left +4:15$", lines[2]);
        Assert.Matches(@"^3 +Arrival +13:00 +left +4:15$", lines[3]);
        Assert.DoesNotContain("—", console.Output);
    }

    // AC-5 (017)
    [Fact]
    public void Render_UsesGreyNumbersAndLabels_AndSteelBlueTimes()
    {
        TestConsole console = new TestConsole().EmitAnsiSequences();
        DayView view = new DayView(FixedTimeProvider.At20261027());

        console.Write(view.Render(DayRecord.Empty
            .Add(new DayEntry(EntryKind.Arrival, new TimeOnly(8, 15)))
            .Add(new DayEntry(EntryKind.Leaving, new TimeOnly(17, 30)))));

        // grey = 256-colour 8, SteelBlue1 = 256-colour 75.
        Assert.Contains("\u001b[38;5;8m1", console.Output);
        Assert.Contains("\u001b[38;5;8m2", console.Output);
        Assert.Contains("\u001b[38;5;8mArrival", console.Output);
        Assert.Contains("\u001b[38;5;8mLeaving", console.Output);
        Assert.Contains("\u001b[38;5;75m08:15", console.Output);
        Assert.Contains("\u001b[38;5;75m17:30", console.Output);
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
        Assert.Equal(5, lines.Length);
        Assert.Matches(@"^1 +Arrival +08:00 +left +8:00$", lines[1]);
        Assert.Matches(@"^2 +Leaving +12:00 +left +4:00$", lines[2]);
        Assert.Matches(@"^3 +Arrival +12:30 +left +4:00$", lines[3]);
        Assert.Matches(@"^4 +Leaving +17:00 +left -0:30$", lines[4]);

        // The value column is right-aligned, so every row ends at the same column.
        int[] ends = lines[1..].Select(l => l.Length).Distinct().ToArray();
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

    // AC-8, AC-9 (036)
    [Fact]
    public void Render_AllKinds_NumbersTogetherInTimeOrder_WithLabelsDetailsAndTimeLeft()
    {
        TestConsole console = new TestConsole();
        console.Profile.Width = 120;

        console.Write(new DayView(FixedTimeProvider.At20261027()).Render(AllKinds()));

        string[] lines = console.Lines.Select(l => l.TrimEnd()).Where(l => l.Trim().Length > 0).ToArray();
        Assert.Equal(7, lines.Length);
        Assert.Matches(@"^1 +Arrival +08:00 +left +8:00$", lines[1]);
        Assert.Matches(@"^2 +Checkpoint +09:30 +Standup #5678$", lines[2]);
        Assert.Matches(@"^3 +Break start +12:00$", lines[3]);
        Assert.Matches(@"^4 +Break end +12:30$", lines[4]);
        Assert.Matches(@"^5 +Checkpoint +15:00 +2024 review$", lines[5]);
        Assert.Matches(@"^6 +Leaving +17:00 +left +-1:00$", lines[6]);
        Assert.DoesNotContain("#", lines[5]);
    }

    // AC-8 (036)
    [Fact]
    public void Render_NewKinds_GreyLabels_SteelBlueTimes_GreyWorkItem_DefaultName()
    {
        TestConsole console = new TestConsole().EmitAnsiSequences();
        console.Profile.Width = 120;

        console.Write(new DayView(FixedTimeProvider.At20261027()).Render(AllKinds()));

        // grey = 256-colour 8, SteelBlue1 = 256-colour 75.
        string output = console.Output;
        Assert.Contains("\u001b[38;5;8mBreak start", output);
        Assert.Contains("\u001b[38;5;8mBreak end", output);
        Assert.Contains("\u001b[38;5;8mCheckpoint", output);
        Assert.Contains("\u001b[38;5;75m09:30", output);
        Assert.Contains("\u001b[38;5;75m12:00", output);
        Assert.Contains("\u001b[38;5;75m12:30", output);
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

        Assert.Matches(@"1 +Checkpoint +09:00 +\[red\]Fix\[/\] \[bold\] #1 #42", console.Output);
    }

    // AC-8 (036)
    [Fact]
    public void Render_WithoutCheckpoint_ArrivalAndLeavingRowsLookAsBefore()
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
        Assert.Matches(@"^1 +Arrival +08:00 +left +-?\d+:\d\d$", lines[1]);
        Assert.Matches(@"^2 +Break start +12:00$", lines[2]);
        Assert.Matches(@"^3 +Leaving +17:00 +left +-1:00$", lines[3]);

        // Same single space between the time and "left" as a day with only arrival and leaving.
        TestConsole plain = new TestConsole();
        plain.Write(new DayView(FixedTimeProvider.At20261027()).Render(new DayRecord(
            [new DayEntry(EntryKind.Arrival, new TimeOnly(8, 0)), new DayEntry(EntryKind.Leaving, new TimeOnly(17, 0))])));
        string[] plainLines = plain.Lines.Select(l => l.TrimEnd()).Where(l => l.Trim().Length > 0).ToArray();
        Assert.Equal(plainLines[1][plainLines[1].IndexOf("08:00", StringComparison.Ordinal)..], lines[1][lines[1].IndexOf("08:00", StringComparison.Ordinal)..]);
    }
}
