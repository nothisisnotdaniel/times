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
    /// <summary>
    /// Queues an event. Safe to call from any thread. Returns <c>true</c> if it was queued, or <c>false</c> if the
    /// queue is full: the event is then dropped and never applied.
    /// </summary>
    public bool Post(BackgroundEvent e);

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
/// <c>keyAvailable</c> every <see cref="PollInterval"/>. It holds at most <see cref="Capacity"/> unapplied
/// events: a post to a full queue is refused (the newest event is dropped) and returns <c>false</c>.
/// </summary>
internal sealed class BackgroundEventQueue : IBackgroundEvents
{
    /// <summary>The longest time between two checks of <c>keyAvailable</c> while waiting.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>How many unapplied events the queue holds unless another capacity is given.</summary>
    public const int DefaultCapacity = 64;

    private readonly ConcurrentQueue<BackgroundEvent> queue = new ConcurrentQueue<BackgroundEvent>();
    private readonly object signal = new object();

    // Slots taken: reserved before Enqueue, given back on a refusal and after each successful TryTake, so it
    // never goes over Capacity or below 0 and is never less than the number of events in the queue.
    private int count;

    /// <param name="capacity">The most unapplied events the queue holds; at least 1.</param>
    public BackgroundEventQueue(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Capacity = capacity;
    }

    /// <summary>The most unapplied events the queue holds; a post beyond it is refused.</summary>
    public int Capacity { get; }

    /// <summary>
    /// Queues <paramref name="e"/> and wakes the wait, or returns <c>false</c> without queuing or waking anything
    /// when <see cref="Capacity"/> events are already queued.
    /// </summary>
    public bool Post(BackgroundEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (Interlocked.Increment(ref count) > Capacity)
        {
            Interlocked.Decrement(ref count);
            return false;
        }

        queue.Enqueue(e);
        lock (signal)
        {
            Monitor.PulseAll(signal);
        }

        return true;
    }

    public bool TryTake(out BackgroundEvent? e)
    {
        bool taken = queue.TryDequeue(out BackgroundEvent? next);
        if (taken)
        {
            Interlocked.Decrement(ref count);
        }

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
