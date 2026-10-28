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
        Assert.Matches(@"^1 +Arrival +08:15$", lines[1]);
        Assert.Matches(@"^2 +Leaving +12:00$", lines[2]);
        Assert.Matches(@"^3 +Arrival +13:00$", lines[3]);
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
}
