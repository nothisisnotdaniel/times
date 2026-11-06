namespace Times.Tests;

public sealed class FlexitimeBalanceTests
{
    private static readonly TimeSpan EightHours = TimeSpan.FromHours(8);

    private static readonly DateOnly Shown = new(2026, 10, 27);

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static string Day(string arrival, string leaving) =>
        $"{{\"entries\":[{{\"kind\":\"arrival\",\"time\":\"{arrival}\"}},{{\"kind\":\"leaving\",\"time\":\"{leaving}\"}}]}}";

    private static void Write(TempFolder folder, string name, string contents) =>
        File.WriteAllText(Path.Combine(folder.Path, name), contents);

    // Every entry in the folder with its contents (null for anything but a regular file), to check nothing changed.
    private static Dictionary<string, string?> Snapshot(string folder) =>
        Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories).ToDictionary(
            p => p,
            p => File.Exists(p) && !SpecialFiles.IsFifo(p) ? File.ReadAllText(p) : null);

    // AC-5
    [Fact]
    public void Compute_SumsDaysOnOrBeforeTheShownDay()
    {
        using TempFolder temp = new TempFolder();
        Write(temp, "2026-10-20.json", Day("08:00", "17:00"));
        Write(temp, "2026-10-21.json", Day("08:00", "15:30"));
        Write(temp, "2026-10-28.json", Day("08:00", "20:00"));

        FlexitimeBalance balance = FlexitimeBalance.Compute(new DayStore(temp.Path), Shown, EightHours);

        Assert.Equal(TimeSpan.FromMinutes(30), balance.Total);
        Assert.Equal(2, balance.CountedDays);
        Assert.Equal(0, balance.SkippedFiles);
    }

    // AC-5
    [Fact]
    public void Compute_CountsTheShownDayItself()
    {
        using TempFolder temp = new TempFolder();
        Write(temp, "2026-10-20.json", Day("08:00", "17:00"));
        Write(temp, "2026-10-21.json", Day("08:00", "15:30"));
        Write(temp, "2026-10-27.json", Day("08:00", "16:30"));

        FlexitimeBalance balance = FlexitimeBalance.Compute(new DayStore(temp.Path), Shown, EightHours);

        Assert.Equal(TimeSpan.FromHours(1), balance.Total);
        Assert.Equal(3, balance.CountedDays);
    }

    // AC-5
    [Fact]
    public void Compute_DayWithoutArrival_AddsNothing()
    {
        using TempFolder temp = new TempFolder();
        Write(temp, "2026-10-20.json", "{\"entries\":[{\"kind\":\"checkpoint\",\"time\":\"09:00\",\"name\":\"Standup\"}]}");
        Write(temp, "2026-10-21.json", "{\"entries\":[]}");

        FlexitimeBalance balance = FlexitimeBalance.Compute(new DayStore(temp.Path), Shown, EightHours);

        Assert.Equal(TimeSpan.Zero, balance.Total);
        Assert.Equal(0, balance.CountedDays);
        Assert.Equal(0, balance.SkippedFiles);
    }

    // AC-5
    [Fact]
    public void Compute_UsesTheGivenTargetForEveryDay()
    {
        using TempFolder temp = new TempFolder();
        Write(temp, "2026-10-20.json", Day("08:00", "17:00"));
        Write(temp, "2026-10-21.json", Day("08:00", "15:30"));

        FlexitimeBalance balance = FlexitimeBalance.Compute(new DayStore(temp.Path), Shown, TimeSpan.FromMinutes(450));

        Assert.Equal(TimeSpan.FromMinutes(90), balance.Total);
    }

    // AC-7
    [Fact]
    public void Compute_SkipsUnreadableFiles_CountsThem_AndChangesNothing()
    {
        using TempFolder temp = new TempFolder();
        Write(temp, "2026-10-20.json", Day("08:00", "17:00"));
        Write(temp, "2026-10-21.json", "not json");
        Write(temp, "2026-10-22.json", new string(' ', (int)DayStore.MaxFileBytes + 1));
        Write(temp, "2026-10-23.json", "{\"entries\":[{\"kind\":\"checkpoint\",\"time\":\"09:00\",\"name\":\"abc\\ud800def\"}]}");
        Directory.CreateDirectory(Path.Combine(temp.Path, "2026-10-24.json"));
        Write(temp, "2026-10-29.json", "not json");
        Dictionary<string, string?> before = Snapshot(temp.Path);

        FlexitimeBalance balance = FlexitimeBalance.Compute(new DayStore(temp.Path), Shown, EightHours);

        Assert.Equal(TimeSpan.FromHours(1), balance.Total);
        Assert.Equal(1, balance.CountedDays);
        Assert.Equal(4, balance.SkippedFiles);
        Assert.Equal(before, Snapshot(temp.Path));
    }

    // AC-7
    [Fact]
    public void Compute_Fifo_IsSkipped_WithoutBlocking_AndStaysAFifo()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using TempFolder temp = new TempFolder();
        Write(temp, "2026-10-20.json", Day("08:00", "17:00"));
        string fifo = Path.Combine(temp.Path, "2026-10-21.json");
        SpecialFiles.MakeFifo(fifo);
        FlexitimeBalance? balance = null;

        Exception? thrown = SpecialFiles.RunWithin(
            Timeout, () => balance = FlexitimeBalance.Compute(new DayStore(temp.Path), Shown, EightHours));

        Assert.Null(thrown);
        Assert.Equal(1, balance!.SkippedFiles);
        Assert.Equal(1, balance.CountedDays);
        Assert.True(SpecialFiles.IsFifo(fifo));
    }

    // AC-7
    [Fact]
    public void Compute_IgnoresNamesThatAreNotExactlyADay()
    {
        using TempFolder temp = new TempFolder();
        Write(temp, "2026-10-20.json", Day("08:00", "17:00"));
        foreach (string name in new[] { "2026-13-01.json", "settings.json", "projects.json", "x2026-11-01.json", "2026-10-21.json.bak" })
        {
            Write(temp, name, "not json");
        }

        FlexitimeBalance balance = FlexitimeBalance.Compute(new DayStore(temp.Path), Shown, EightHours);

        Assert.Equal(TimeSpan.FromHours(1), balance.Total);
        Assert.Equal(1, balance.CountedDays);
        Assert.Equal(0, balance.SkippedFiles);
    }

    // AC-8
    [Fact]
    public void Compute_MissingFolder_IsZero_AndCreatesNothing()
    {
        using TempFolder temp = new TempFolder(create: false);

        FlexitimeBalance balance = FlexitimeBalance.Compute(new DayStore(temp.Path), Shown, EightHours);

        Assert.Equal(TimeSpan.Zero, balance.Total);
        Assert.Equal(0, balance.CountedDays);
        Assert.Equal(0, balance.SkippedFiles);
        Assert.False(Directory.Exists(temp.Path));
    }

    // AC-8
    [Fact]
    public void Compute_AddsNoFile()
    {
        using TempFolder temp = new TempFolder();
        Write(temp, "2026-10-20.json", Day("08:00", "17:00"));
        Dictionary<string, string?> before = Snapshot(temp.Path);

        FlexitimeBalance.Compute(new DayStore(temp.Path), Shown, EightHours);

        Assert.Equal(before, Snapshot(temp.Path));
    }

    // AC-8, Notes for reviewers: about 1000 files stay fast.
    [Fact]
    public void Compute_ThousandFiles_StaysFast()
    {
        using TempFolder temp = new TempFolder();
        DateOnly first = Shown.AddDays(-999);
        for (int i = 0; i < 1000; i++)
        {
            Write(temp, first.AddDays(i).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) + ".json", Day("08:00", "16:01"));
        }

        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
        FlexitimeBalance balance = FlexitimeBalance.Compute(new DayStore(temp.Path), Shown, EightHours);
        watch.Stop();

        Assert.Equal(1000, balance.CountedDays);
        Assert.Equal(TimeSpan.FromMinutes(1000), balance.Total);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"Took {watch.Elapsed}.");
    }

    // Job 098 ------------------------------------------------------------------------------

    // AC-9
    [Fact]
    public void Compute_AbsenceDay_CountsWithZeroDelta()
    {
        using TempFolder temp = new TempFolder();
        Write(temp, "2026-10-20.json", Day("08:00", "15:30"));
        Write(temp, "2026-10-21.json", "{\"absence\":\"vacation\",\"entries\":[]}");

        FlexitimeBalance balance = FlexitimeBalance.Compute(new DayStore(temp.Path), Shown, EightHours);

        Assert.Equal(TimeSpan.FromMinutes(-30), balance.Total);
        Assert.Equal(2, balance.CountedDays);
        Assert.Equal(0, balance.SkippedFiles);
    }

    // AC-9
    [Fact]
    public void Compute_AbsenceDayAfterShownDay_IsIgnored()
    {
        using TempFolder temp = new TempFolder();
        Write(temp, "2026-10-27.json", "{\"absence\":\"sick\",\"entries\":[]}");
        Write(temp, "2026-10-28.json", "{\"absence\":\"publicHoliday\",\"entries\":[]}");

        FlexitimeBalance balance = FlexitimeBalance.Compute(new DayStore(temp.Path), Shown, EightHours);

        Assert.Equal(TimeSpan.Zero, balance.Total);
        Assert.Equal(1, balance.CountedDays);
    }

    private const string Working = "{\"entries\":[{\"kind\":\"arrival\",\"time\":\"08:00\"}]}";

    // AC-3
    [Fact]
    public void Compute_WithLiveMinute_CountsTheShownDayToNow()
    {
        using TempFolder temp = new TempFolder();
        Write(temp, "2026-10-20.json", Working);
        Write(temp, "2026-10-21.json", Day("08:00", "17:00"));
        Write(temp, "2026-10-27.json", Working);

        FlexitimeBalance plain = FlexitimeBalance.Compute(new DayStore(temp.Path), Shown, EightHours);
        FlexitimeBalance live = FlexitimeBalance.Compute(new DayStore(temp.Path), Shown, EightHours, new TimeOnly(17, 30));

        // Without now: -8:00 (20th, nothing accrued) +1:00 -8:00. With now, only the shown day moves: 9:30 worked.
        Assert.Equal(TimeSpan.FromHours(-15), plain.Total);
        Assert.Equal(TimeSpan.FromHours(-15) + TimeSpan.FromMinutes(570), live.Total);
        Assert.Equal(3, live.CountedDays);
    }

    // AC-3
    [Fact]
    public void Compute_WithLiveMinute_ForAShownDayThatIsNotWorking_IsUnchanged()
    {
        using TempFolder temp = new TempFolder();
        Write(temp, "2026-10-20.json", Working);
        Write(temp, "2026-10-27.json", Day("08:00", "12:00"));

        FlexitimeBalance plain = FlexitimeBalance.Compute(new DayStore(temp.Path), Shown, EightHours);
        FlexitimeBalance live = FlexitimeBalance.Compute(new DayStore(temp.Path), Shown, EightHours, new TimeOnly(17, 30));

        Assert.Equal(plain.Total, live.Total);
    }

    // Job 121 ------------------------------------------------------------------------------

    private const string Corrections = ",\"corrections\":[{\"minutes\":150,\"reason\":\"Start\"},{\"minutes\":-30,\"reason\":\"Paid\"}]}";

    private static string WithCorrections(string day) => day[..^1] + Corrections;

    // AC-7
    [Fact]
    public void Compute_AddsCorrections_OnEveryKindOfDay()
    {
        using TempFolder temp = new TempFolder();
        Write(temp, "2026-10-20.json", WithCorrections(Day("08:00", "17:00")));
        Write(temp, "2026-10-21.json", WithCorrections("{\"absence\":\"vacation\",\"entries\":[]}"));
        Write(temp, "2026-10-22.json", WithCorrections("{\"entries\":[]}"));
        Write(temp, "2026-10-23.json", WithCorrections("{\"entries\":[{\"kind\":\"checkpoint\",\"time\":\"09:00\",\"name\":\"Task\"}]}"));

        FlexitimeBalance balance = FlexitimeBalance.Compute(new DayStore(temp.Path), Shown, EightHours);

        // +1:00 worked over target, plus 4 days of +2:00 corrections.
        Assert.Equal(TimeSpan.FromMinutes(60 + (4 * 120)), balance.Total);
        Assert.Equal(4, balance.CountedDays);
        Assert.Equal(0, balance.SkippedFiles);
    }

    // AC-7
    [Fact]
    public void Compute_IgnoresCorrectionsOnLaterDays_AndCountsTheShownDay()
    {
        using TempFolder temp = new TempFolder();
        Write(temp, "2026-10-27.json", "{\"entries\":[],\"corrections\":[{\"minutes\":-65,\"reason\":\"Paid\"}]}");
        Write(temp, "2026-10-28.json", "{\"entries\":[],\"corrections\":[{\"minutes\":600,\"reason\":\"Later\"}]}");

        FlexitimeBalance balance = FlexitimeBalance.Compute(new DayStore(temp.Path), Shown, EightHours);

        Assert.Equal(TimeSpan.FromMinutes(-65), balance.Total);
        Assert.Equal(1, balance.CountedDays);
    }

    // AC-6, AC-7
    [Fact]
    public void Compute_BrokenCorrections_SkipTheFile()
    {
        using TempFolder temp = new TempFolder();
        Write(temp, "2026-10-20.json", Day("08:00", "17:00")[..^1] + ",\"corrections\":[{\"minutes\":0,\"reason\":\"x\"}]}");
        Write(temp, "2026-10-21.json", Day("08:00", "16:30"));

        FlexitimeBalance balance = FlexitimeBalance.Compute(new DayStore(temp.Path), Shown, EightHours);

        Assert.Equal(TimeSpan.FromMinutes(30), balance.Total);
        Assert.Equal(1, balance.CountedDays);
        Assert.Equal(1, balance.SkippedFiles);
    }

    // AC-7: the live minute still only changes the shown day's work time.
    [Fact]
    public void Compute_Live_KeepsCorrections()
    {
        using TempFolder temp = new TempFolder();
        Write(temp, "2026-10-27.json", "{\"entries\":[{\"kind\":\"arrival\",\"time\":\"08:00\"}],\"corrections\":[{\"minutes\":60,\"reason\":\"x\"}]}");

        FlexitimeBalance balance = FlexitimeBalance.Compute(new DayStore(temp.Path), Shown, EightHours, new TimeOnly(12, 0));

        Assert.Equal(TimeSpan.FromHours(-3), balance.Total);
        Assert.Equal(1, balance.CountedDays);
    }
}
