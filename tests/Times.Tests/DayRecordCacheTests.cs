namespace Times.Tests;

public sealed class DayRecordCacheTests
{
    private static readonly TimeSpan EightHours = TimeSpan.FromHours(8);

    private static readonly DateOnly Shown = new(2026, 10, 27);

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static readonly DateTime Stamp1 = new(2026, 10, 1, 10, 0, 0, DateTimeKind.Utc);

    private static readonly DateTime Stamp2 = new(2026, 10, 1, 11, 0, 0, DateTimeKind.Utc);

    private static string Day(string arrival, string leaving) =>
        $"{{\"entries\":[{{\"kind\":\"arrival\",\"time\":\"{arrival}\"}},{{\"kind\":\"leaving\",\"time\":\"{leaving}\"}}]}}";

    // Writes a day file and sets its last-write time explicitly, so no test waits for the clock to move.
    private static string Write(TempFolder folder, string date, string contents, DateTime lastWriteUtc)
    {
        string path = Path.Combine(folder.Path, date + ".json");
        File.WriteAllText(path, contents);
        File.SetLastWriteTimeUtc(path, lastWriteUtc);
        return path;
    }

    private static (TimeSpan Total, int Counted, int Skipped) Of(FlexitimeBalance balance) =>
        (balance.Total, balance.CountedDays, balance.SkippedFiles);

    private static TempFolder ThreeDays()
    {
        TempFolder temp = new TempFolder();
        Write(temp, "2026-10-20", Day("08:00", "17:00"), Stamp1);
        Write(temp, "2026-10-21", Day("08:00", "15:30"), Stamp1);
        Write(temp, "2026-10-22", Day("08:00", "16:00"), Stamp1);
        return temp;
    }

    // AC-6 (314)
    [Fact]
    public void SecondCompute_OverUnchangedFiles_LoadsNothing_AndMatchesTheUncachedResult()
    {
        using TempFolder temp = ThreeDays();
        DayStore store = new DayStore(temp.Path);
        DayRecordCache cache = new DayRecordCache(store);

        FlexitimeBalance first = FlexitimeBalance.Compute(store, Shown, EightHours, cache: cache);
        int loads = cache.Loads;
        FlexitimeBalance second = FlexitimeBalance.Compute(store, Shown, EightHours, cache: cache);

        Assert.Equal(3, loads);
        Assert.Equal(loads, cache.Loads);
        Assert.Equal((TimeSpan.FromMinutes(30), 3, 0), Of(first));
        Assert.Equal(Of(first), Of(second));
        Assert.Equal(Of(FlexitimeBalance.Compute(store, Shown, EightHours)), Of(second));
    }

    // AC-7 (314): a different length is seen, and only that day is loaded again.
    [Fact]
    public void ChangedLength_IsReloaded_OnlyThatDay()
    {
        using TempFolder temp = ThreeDays();
        DayStore store = new DayStore(temp.Path);
        DayRecordCache cache = new DayRecordCache(store);
        FlexitimeBalance.Compute(store, Shown, EightHours, cache: cache);
        int loads = cache.Loads;

        Write(temp, "2026-10-21", Day("07:00", "15:30") + " ", Stamp1);
        FlexitimeBalance after = FlexitimeBalance.Compute(store, Shown, EightHours, cache: cache);

        Assert.Equal(loads + 1, cache.Loads);
        Assert.Equal((TimeSpan.FromMinutes(90), 3, 0), Of(after));
        Assert.Equal(Of(FlexitimeBalance.Compute(store, Shown, EightHours)), Of(after));
    }

    // AC-7 (314): the same length with a new last-write time is seen too.
    [Fact]
    public void SameLength_NewLastWriteTime_IsReloaded_OnlyThatDay()
    {
        using TempFolder temp = ThreeDays();
        DayStore store = new DayStore(temp.Path);
        DayRecordCache cache = new DayRecordCache(store);
        FlexitimeBalance.Compute(store, Shown, EightHours, cache: cache);
        int loads = cache.Loads;

        Write(temp, "2026-10-22", Day("08:00", "17:00"), Stamp2);
        FlexitimeBalance after = FlexitimeBalance.Compute(store, Shown, EightHours, cache: cache);

        Assert.Equal(loads + 1, cache.Loads);
        Assert.Equal((TimeSpan.FromMinutes(90), 3, 0), Of(after));
    }

    // AC-7 (314): a deleted day drops out without any load, and a new day is loaded on its own.
    [Fact]
    public void DeletedAndAddedDays_AreReflected_LoadingOnlyTheNewDay()
    {
        using TempFolder temp = ThreeDays();
        DayStore store = new DayStore(temp.Path);
        DayRecordCache cache = new DayRecordCache(store);
        FlexitimeBalance.Compute(store, Shown, EightHours, cache: cache);
        int loads = cache.Loads;

        File.Delete(Path.Combine(temp.Path, "2026-10-21.json"));
        FlexitimeBalance deleted = FlexitimeBalance.Compute(store, Shown, EightHours, cache: cache);
        Assert.Equal(loads, cache.Loads);
        Assert.Equal((TimeSpan.FromMinutes(60), 2, 0), Of(deleted));

        Write(temp, "2026-10-23", Day("08:00", "18:00"), Stamp1);
        FlexitimeBalance added = FlexitimeBalance.Compute(store, Shown, EightHours, cache: cache);
        Assert.Equal(loads + 1, cache.Loads);
        Assert.Equal((TimeSpan.FromMinutes(180), 3, 0), Of(added));
    }

    // AC-8 (314): a Save is seen even when the file's length and last-write time are put back to the old values.
    [Fact]
    public void Save_InvalidatesTheDay_EvenWithTheOldLengthAndLastWriteTime()
    {
        using TempFolder temp = new TempFolder();
        DayStore store = new DayStore(temp.Path);
        DateOnly day = new DateOnly(2026, 10, 20);
        DayRecord before = DayRecord.Empty
            .Add(new DayEntry(EntryKind.Arrival, new TimeOnly(8, 0)))
            .Add(new DayEntry(EntryKind.Leaving, new TimeOnly(16, 0)));
        DayRecord after = DayRecord.Empty
            .Add(new DayEntry(EntryKind.Arrival, new TimeOnly(8, 0)))
            .Add(new DayEntry(EntryKind.Leaving, new TimeOnly(17, 0)));
        store.Save(day, before);
        string path = store.PathFor(day);
        File.SetLastWriteTimeUtc(path, Stamp1);
        long length = new FileInfo(path).Length;
        DayRecordCache cache = new DayRecordCache(store);
        Assert.Equal(TimeSpan.Zero, FlexitimeBalance.Compute(store, Shown, EightHours, cache: cache).Total);
        int loads = cache.Loads;

        store.Save(day, after);
        Assert.Equal(length, new FileInfo(path).Length);
        File.SetLastWriteTimeUtc(path, Stamp1);
        FlexitimeBalance balance = FlexitimeBalance.Compute(store, Shown, EightHours, cache: cache);

        Assert.Equal(loads + 1, cache.Loads);
        Assert.Equal(TimeSpan.FromHours(1), balance.Total);
    }

    // AC-9 (314): bad JSON and a too-large file are skipped on every cached Compute, never kept, and count once fixed.
    [Theory]
    [InlineData("not json")]
    [InlineData("too large")]
    public void UnreadableFile_IsSkippedEveryTime_NeverKept_AndCountsOnceFixed(string kind)
    {
        using TempFolder temp = ThreeDays();
        string contents = kind == "not json" ? "not json" : new string(' ', (int)DayStore.MaxFileBytes) + "{}";
        Write(temp, "2026-10-21", contents, Stamp1);
        DayStore store = new DayStore(temp.Path);
        DayRecordCache cache = new DayRecordCache(store);

        FlexitimeBalance first = FlexitimeBalance.Compute(store, Shown, EightHours, cache: cache);
        int loads = cache.Loads;
        FlexitimeBalance second = FlexitimeBalance.Compute(store, Shown, EightHours, cache: cache);

        Assert.Equal((TimeSpan.FromMinutes(60), 2, 1), Of(first));
        Assert.Equal(Of(first), Of(second));
        Assert.Equal(loads + 1, cache.Loads);

        Write(temp, "2026-10-21", Day("08:00", "15:30"), Stamp1);
        Assert.Equal((TimeSpan.FromMinutes(30), 3, 0), Of(FlexitimeBalance.Compute(store, Shown, EightHours, cache: cache)));
    }

    // AC-9 (314): a FIFO with a day's name is skipped on every cached Compute without blocking (Unix only).
    [Fact]
    public void Fifo_IsSkippedEveryTime_WithoutBlocking_AndCountsOnceReplaced()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using TempFolder temp = ThreeDays();
        string fifo = Path.Combine(temp.Path, "2026-10-21.json");
        File.Delete(fifo);
        SpecialFiles.MakeFifo(fifo);
        DayStore store = new DayStore(temp.Path);
        DayRecordCache cache = new DayRecordCache(store);
        FlexitimeBalance? first = null;
        FlexitimeBalance? second = null;

        Exception? thrown = SpecialFiles.RunWithin(Timeout, () =>
        {
            first = FlexitimeBalance.Compute(store, Shown, EightHours, cache: cache);
            second = FlexitimeBalance.Compute(store, Shown, EightHours, cache: cache);
        });

        Assert.Null(thrown);
        Assert.Equal((TimeSpan.FromMinutes(60), 2, 1), Of(first!));
        Assert.Equal(Of(first!), Of(second!));
        Assert.Equal(4, cache.Loads);

        File.Delete(fifo);
        Write(temp, "2026-10-21", Day("08:00", "15:30"), Stamp1);
        Assert.Equal((TimeSpan.FromMinutes(30), 3, 0), Of(FlexitimeBalance.Compute(store, Shown, EightHours, cache: cache)));
    }

    // A missing day is loaded as empty every time and never kept.
    [Fact]
    public void MissingDay_IsEmpty_AndNeverKept()
    {
        using TempFolder temp = new TempFolder();
        DayRecordCache cache = new DayRecordCache(new DayStore(temp.Path));

        Assert.Empty(cache.Load(Shown).Entries);
        Assert.Empty(cache.Load(Shown).Entries);
        Assert.Equal(2, cache.Loads);
    }
}
