namespace Times.Tests;

public sealed class LiveClockTests
{
    private static SettableTimeProvider ClockAt(int hour, int minute, int second) =>
        new SettableTimeProvider(new DateTimeOffset(2026, 10, 27, hour, minute, second, TimeSpan.Zero));

    // AC-4
    [Theory]
    [InlineData(18, 30, 20, 40)]
    [InlineData(18, 30, 0, 60)]
    [InlineData(18, 30, 59, 1)]
    [InlineData(23, 59, 30, 30)]
    public void Start_ArmsAOneShotTimer_DueAtTheNextMinute(int hour, int minute, int second, int expectedSeconds)
    {
        SettableTimeProvider time = ClockAt(hour, minute, second);
        using LiveClock clock = new LiveClock(time, new ScriptedEvents(), () => true);

        clock.Start();

        ManualTimer timer = Assert.Single(time.Timers);
        Assert.Equal([TimeSpan.FromSeconds(expectedSeconds)], timer.DueTimes);
        Assert.Equal(Timeout.InfiniteTimeSpan, timer.Period);
    }

    // AC-4
    [Fact]
    public void UntilNextMinute_CountsSubSecondParts()
    {
        DateTimeOffset at = new DateTimeOffset(2026, 10, 27, 18, 30, 20, TimeSpan.Zero).AddMilliseconds(250);

        Assert.Equal(TimeSpan.FromMilliseconds(39750), LiveClock.UntilNextMinute(at));
    }

    // AC-4
    [Fact]
    public void Fire_PostsOneTick_AndReArmsFromTheClock()
    {
        SettableTimeProvider time = ClockAt(18, 30, 20);
        ScriptedEvents events = new ScriptedEvents();
        int ticks = 0;
        using LiveClock clock = new LiveClock(time, events, () =>
        {
            ticks++;
            return true;
        });
        clock.Start();
        ManualTimer timer = time.Timers[0];

        time.UtcNow = new DateTimeOffset(2026, 10, 27, 18, 31, 0, TimeSpan.Zero).AddMilliseconds(5);
        timer.Fire();

        Assert.Single(time.Timers);
        Assert.Equal(TimeSpan.FromMilliseconds(59995), timer.DueTimes[^1]);
        Assert.True(events.TryTake(out BackgroundEvent? tick));
        Assert.False(events.TryTake(out _));
        Assert.True(tick!.Apply());
        Assert.Equal(1, ticks);
    }

    // AC-4
    [Fact]
    public void Dispose_DisposesTheTimer_AndALaterFirePostsNothing()
    {
        SettableTimeProvider time = ClockAt(18, 30, 20);
        ScriptedEvents events = new ScriptedEvents();
        LiveClock clock = new LiveClock(time, events, () => true);
        clock.Start();
        ManualTimer timer = time.Timers[0];

        clock.Dispose();
        timer.Fire();

        Assert.True(timer.Disposed);
        Assert.False(events.TryTake(out _));
        Assert.Single(timer.DueTimes);
    }

    // AC-5
    [Fact]
    public void Fire_WhileATickIsUnapplied_PostsNothing_AndPostsAgainAfterTheApply()
    {
        SettableTimeProvider time = ClockAt(18, 30, 20);
        ScriptedEvents events = new ScriptedEvents();
        using LiveClock clock = new LiveClock(time, events, () => false);
        clock.Start();
        ManualTimer timer = time.Timers[0];

        timer.Fire();
        timer.Fire();
        timer.Fire();

        Assert.True(events.TryTake(out BackgroundEvent? first));
        Assert.False(events.TryTake(out _));
        Assert.Equal(4, timer.DueTimes.Count);

        Assert.False(first!.Apply());
        timer.Fire();

        Assert.True(events.TryTake(out _));
        Assert.False(events.TryTake(out _));
    }

    // AC-5
    [Fact]
    public void ATickThatThrows_StillLetsTheNextFirePost()
    {
        SettableTimeProvider time = ClockAt(18, 30, 20);
        ScriptedEvents events = new ScriptedEvents();
        using LiveClock clock = new LiveClock(time, events, () => throw new IOException("broken"));
        clock.Start();
        ManualTimer timer = time.Timers[0];

        timer.Fire();
        Assert.True(events.TryTake(out BackgroundEvent? tick));
        Assert.Throws<IOException>(() => tick!.Apply());
        timer.Fire();

        Assert.True(events.TryTake(out _));
    }

    // AC-5: the flag holds across threads: many concurrent fires queue one tick.
    [Fact]
    public void ConcurrentFires_QueueOneTick()
    {
        SettableTimeProvider time = ClockAt(18, 30, 20);
        BackgroundEventQueue events = new BackgroundEventQueue();
        using LiveClock clock = new LiveClock(time, events, () => true);
        clock.Start();
        ManualTimer timer = time.Timers[0];

        Parallel.For(0, 64, _ => timer.Fire());

        Assert.True(events.TryTake(out _));
        Assert.False(events.TryTake(out _));
    }

    [Fact]
    public void Start_Twice_ArmsOneTimer()
    {
        SettableTimeProvider time = ClockAt(18, 30, 20);
        using LiveClock clock = new LiveClock(time, new ScriptedEvents(), () => true);

        clock.Start();
        clock.Start();

        Assert.Single(time.Timers);
    }
}
