namespace Times.Tests;

/// <summary>
/// A clock that stays put until a test moves it. Its timers never run by themselves: each
/// <see cref="CreateTimer"/> is recorded as a <see cref="ManualTimer"/> the test fires by hand.
/// </summary>
internal sealed class SettableTimeProvider : TimeProvider
{
    public SettableTimeProvider(DateTimeOffset utcNow)
    {
        UtcNow = utcNow;
    }

    public DateTimeOffset UtcNow { get; set; }

    /// <summary>Every timer created, in order.</summary>
    public List<ManualTimer> Timers { get; } = [];

    public override DateTimeOffset GetUtcNow() => UtcNow;

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ManualTimer timer = new ManualTimer(callback, state, dueTime, period);
        Timers.Add(timer);
        return timer;
    }
}

/// <summary>A timer that records its due time and period (also after <see cref="Change"/>) and fires only through <see cref="Fire"/>.</summary>
internal sealed class ManualTimer : ITimer
{
    private readonly TimerCallback callback;
    private readonly object? state;

    public ManualTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        this.callback = callback;
        this.state = state;
        DueTimes.Add(dueTime);
        Period = period;
    }

    /// <summary>The due time given at creation, then the one of every <see cref="Change"/>.</summary>
    public List<TimeSpan> DueTimes { get; } = [];

    public TimeSpan Period { get; private set; }

    public bool Disposed { get; private set; }

    /// <summary>Runs the callback, as the timer would when due (also after dispose, like a late fire).</summary>
    public void Fire() => callback(state);

    public bool Change(TimeSpan dueTime, TimeSpan period)
    {
        if (Disposed)
        {
            return false;
        }

        DueTimes.Add(dueTime);
        Period = period;
        return true;
    }

    public void Dispose() => Disposed = true;

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}
