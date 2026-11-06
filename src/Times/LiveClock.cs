namespace Times;

/// <summary>
/// Posts one tick per local minute to the background events, so the day view on today can move on by
/// itself. The timer is a one-shot timer from the injected <see cref="TimeProvider"/>, due at the next local
/// minute boundary and re-armed from the clock after every fire, so it never drifts. The timer callback only
/// posts: the tick's work (<c>tick</c>) runs on the loop thread when the line editor applies the event.
/// At most one tick is ever queued: a fire while a tick is still unapplied posts nothing.
/// </summary>
internal sealed class LiveClock : IDisposable
{
    private readonly TimeProvider time;
    private readonly IBackgroundEvents events;
    private readonly Func<bool> tick;
    private readonly object gate = new object();
    private ITimer? timer;
    private bool disposed;

    // 1 while a posted tick has not been applied yet; set by the timer thread, cleared on the loop thread.
    private int pending;

    /// <param name="tick">Runs on the loop thread for each applied tick and returns <c>true</c> if the view must be redrawn.</param>
    public LiveClock(TimeProvider time, IBackgroundEvents events, Func<bool> tick)
    {
        this.time = time;
        this.events = events;
        this.tick = tick;
    }

    /// <summary>Arms the timer for the next local minute boundary. Does nothing if already started or disposed.</summary>
    public void Start()
    {
        lock (gate)
        {
            if (disposed || timer is not null)
            {
                return;
            }

            timer = time.CreateTimer(Fire, null, UntilNextMinute(time.GetLocalNow()), Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// The time from <paramref name="local"/> to the next local minute boundary: more than zero and at most
    /// one minute (18:30:20 gives 40 s, 18:30:00 gives 60 s).
    /// </summary>
    public static TimeSpan UntilNextMinute(DateTimeOffset local)
    {
        long into = local.DateTime.Ticks % TimeSpan.TicksPerMinute;
        return TimeSpan.FromTicks(TimeSpan.TicksPerMinute - into);
    }

    /// <summary>Disposes the timer; no fire after this posts anything.</summary>
    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            timer?.Dispose();
            timer = null;
        }
    }

    // On the timer's thread: post a tick unless one is still queued, then re-arm for the next boundary.
    private void Fire(object? state)
    {
        lock (gate)
        {
            if (disposed || timer is null)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref pending, 1, 0) == 0)
            {
                events.Post(new BackgroundEvent(Apply));
            }

            timer.Change(UntilNextMinute(time.GetLocalNow()), Timeout.InfiniteTimeSpan);
        }
    }

    // On the loop thread. The flag is cleared first, so a tick that throws still lets the next fire post.
    private bool Apply()
    {
        Interlocked.Exchange(ref pending, 0);
        return tick();
    }
}
