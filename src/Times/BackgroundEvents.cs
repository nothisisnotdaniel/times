using System.Collections.Concurrent;

namespace Times;

/// <summary>
/// Something that happened in the background (a call ended, the clock ticked) and changes what the
/// app shows. It is posted from any thread but applied only on the loop thread, between keys.
/// </summary>
internal sealed class BackgroundEvent
{
    private readonly Func<bool> apply;

    /// <param name="apply">Runs on the loop thread and returns <c>true</c> if the shown view changed.</param>
    public BackgroundEvent(Func<bool> apply)
    {
        this.apply = apply;
    }

    /// <summary>Runs the event on the loop thread. Returns <c>true</c> if the shown view changed.</summary>
    public bool Apply() => apply();
}

/// <summary>
/// The queue of background events the line editor drains between keys. Only <see cref="Post"/> may
/// be called off the loop thread.
/// </summary>
internal interface IBackgroundEvents
{
    /// <summary>Queues an event. Safe to call from any thread.</summary>
    public void Post(BackgroundEvent e);

    /// <summary>Takes the oldest queued event (FIFO). Returns <c>false</c> when the queue is empty.</summary>
    public bool TryTake(out BackgroundEvent? e);

    /// <summary>
    /// Blocks until an event is queued or <paramref name="keyAvailable"/> says a key is waiting. Never
    /// applies an event and never touches the console itself.
    /// </summary>
    public void WaitForKeyOrEvent(Func<bool> keyAvailable);
}

/// <summary>
/// The production queue: a <see cref="ConcurrentQueue{T}"/> plus a wake signal that
/// <see cref="Post"/> pulses. Console input has no wait handle, so the wait also polls
/// <c>keyAvailable</c> every <see cref="PollInterval"/>.
/// </summary>
internal sealed class BackgroundEventQueue : IBackgroundEvents
{
    /// <summary>The longest time between two checks of <c>keyAvailable</c> while waiting.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    private readonly ConcurrentQueue<BackgroundEvent> queue = new ConcurrentQueue<BackgroundEvent>();
    private readonly object signal = new object();

    public void Post(BackgroundEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        queue.Enqueue(e);
        lock (signal)
        {
            Monitor.PulseAll(signal);
        }
    }

    public bool TryTake(out BackgroundEvent? e)
    {
        bool taken = queue.TryDequeue(out BackgroundEvent? next);
        e = next;
        return taken;
    }

    /// <summary>
    /// Returns at once if an event is queued; otherwise as soon as one is posted or
    /// <paramref name="keyAvailable"/> returns <c>true</c> (checked at least every
    /// <see cref="PollInterval"/>). If <paramref name="keyAvailable"/> throws
    /// <see cref="InvalidOperationException"/> (redirected input), it returns, so the caller falls back
    /// to a blocking read instead of spinning here.
    /// </summary>
    public void WaitForKeyOrEvent(Func<bool> keyAvailable)
    {
        while (true)
        {
            if (!queue.IsEmpty)
            {
                return;
            }

            try
            {
                if (keyAvailable())
                {
                    return;
                }
            }
            catch (InvalidOperationException)
            {
                return;
            }

            lock (signal)
            {
                // Post enqueues before it takes the lock, so an event posted after the check above is
                // either seen here or pulses the wait below: no wake-up is lost.
                if (!queue.IsEmpty)
                {
                    return;
                }

                Monitor.Wait(signal, PollInterval);
            }
        }
    }
}
