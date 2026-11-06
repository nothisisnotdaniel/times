using System.Collections.Concurrent;
using System.Diagnostics;

namespace Times.Tests;

public class BackgroundEventsTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(1);

    [Fact]
    public void BackgroundEvent_Apply_ReturnsWhatTheFunctionReturns()
    {
        Assert.True(new BackgroundEvent(() => true).Apply());
        Assert.False(new BackgroundEvent(() => false).Apply());
    }

    // AC-1
    [Fact]
    public void TryTake_OnAnEmptyQueue_ReturnsFalse()
    {
        BackgroundEventQueue queue = new BackgroundEventQueue();

        Assert.False(queue.TryTake(out BackgroundEvent? e));
        Assert.Null(e);
    }

    // AC-1
    [Fact]
    public void TryTake_ReturnsTheEvents_InTheOrderTheyWerePosted()
    {
        BackgroundEventQueue queue = new BackgroundEventQueue();
        BackgroundEvent first = new BackgroundEvent(() => true);
        BackgroundEvent second = new BackgroundEvent(() => false);
        BackgroundEvent third = new BackgroundEvent(() => true);
        queue.Post(first);
        queue.Post(second);
        queue.Post(third);

        List<BackgroundEvent> taken = [];
        while (queue.TryTake(out BackgroundEvent? e))
        {
            taken.Add(e!);
        }

        Assert.Equal([first, second, third], taken);
        Assert.False(queue.TryTake(out _));
    }

    // AC-1
    [Fact]
    public void Post_FromFourThreads_YieldsAllEvents_OnceEach_KeepingEachThreadsOrder()
    {
        const int Threads = 4;
        const int PerThread = 250;
        BackgroundEventQueue queue = new BackgroundEventQueue();
        Dictionary<BackgroundEvent, (int Thread, int Index)> origin = [];
        BackgroundEvent[][] events = new BackgroundEvent[Threads][];
        for (int t = 0; t < Threads; t++)
        {
            events[t] = new BackgroundEvent[PerThread];
            for (int i = 0; i < PerThread; i++)
            {
                events[t][i] = new BackgroundEvent(() => true);
                origin[events[t][i]] = (t, i);
            }
        }

        using Barrier start = new Barrier(Threads);
        Thread[] threads = new Thread[Threads];
        for (int t = 0; t < Threads; t++)
        {
            BackgroundEvent[] mine = events[t];
            threads[t] = new Thread(() =>
            {
                start.SignalAndWait();
                foreach (BackgroundEvent e in mine)
                {
                    queue.Post(e);
                }
            });
            threads[t].Start();
        }

        foreach (Thread thread in threads)
        {
            thread.Join();
        }

        List<BackgroundEvent> taken = [];
        while (queue.TryTake(out BackgroundEvent? e))
        {
            taken.Add(e!);
        }

        Assert.Equal(Threads * PerThread, taken.Count);
        Assert.Equal(Threads * PerThread, taken.Distinct().Count());
        int[] last = Enumerable.Repeat(-1, Threads).ToArray();
        foreach (BackgroundEvent e in taken)
        {
            (int thread, int index) = origin[e];
            Assert.Equal(last[thread] + 1, index);
            last[thread] = index;
        }
    }

    // AC-1: taking while posting still yields every event once.
    [Fact]
    public void TryTake_WhilePosting_YieldsEveryEventOnce()
    {
        BackgroundEventQueue queue = new BackgroundEventQueue();
        ConcurrentBag<BackgroundEvent> posted = [];
        Thread poster = new Thread(() =>
        {
            for (int i = 0; i < 1000; i++)
            {
                BackgroundEvent e = new BackgroundEvent(() => true);
                posted.Add(e);
                queue.Post(e);
            }
        });
        poster.Start();

        List<BackgroundEvent> taken = [];
        Stopwatch watch = Stopwatch.StartNew();
        while (taken.Count < 1000 && watch.Elapsed < TimeSpan.FromSeconds(10))
        {
            if (queue.TryTake(out BackgroundEvent? e))
            {
                taken.Add(e!);
            }
        }

        poster.Join();
        Assert.Equal(1000, taken.Count);
        Assert.Equal(1000, taken.Distinct().Count());
        Assert.True(posted.ToHashSet().SetEquals(taken));
    }

    // AC-2
    [Fact]
    public void Wait_ReturnsAtOnce_WhenAnEventIsQueued()
    {
        BackgroundEventQueue queue = new BackgroundEventQueue();
        queue.Post(new BackgroundEvent(() => true));
        int keyChecks = 0;

        Stopwatch watch = Stopwatch.StartNew();
        queue.WaitForKeyOrEvent(() =>
        {
            keyChecks++;
            return false;
        });

        Assert.True(watch.Elapsed < Bound);
        Assert.Equal(0, keyChecks);
        Assert.True(queue.TryTake(out _));
    }

    // AC-2
    [Fact]
    public void Wait_Returns_SoonAfterAnotherThreadPosts()
    {
        BackgroundEventQueue queue = new BackgroundEventQueue();
        Stopwatch posted = new Stopwatch();
        Thread poster = new Thread(() =>
        {
            Thread.Sleep(200);
            posted.Start();
            queue.Post(new BackgroundEvent(() => true));
        });
        poster.Start();

        Stopwatch total = Stopwatch.StartNew();
        queue.WaitForKeyOrEvent(() => false);
        TimeSpan afterPost = posted.Elapsed;
        poster.Join();

        Assert.True(afterPost < Bound, $"returned {afterPost} after the post");
        Assert.True(total.Elapsed < TimeSpan.FromSeconds(5));
        Assert.True(queue.TryTake(out _));
    }

    // AC-2
    [Fact]
    public void Wait_Returns_SoonAfterAKeyBecomesAvailable()
    {
        BackgroundEventQueue queue = new BackgroundEventQueue();
        long availableAt = Stopwatch.GetTimestamp() + (Stopwatch.Frequency / 5);

        queue.WaitForKeyOrEvent(() => Stopwatch.GetTimestamp() >= availableAt);

        Assert.True(Stopwatch.GetTimestamp() >= availableAt);
        Assert.True(Stopwatch.GetElapsedTime(availableAt) < Bound, $"returned {Stopwatch.GetElapsedTime(availableAt)} after the key");
    }

    // AC-2: while idle, the key is checked often (at least every 50 ms), not in a busy loop.
    [Fact]
    public void Wait_PollsTheKey_AboutEvery50Milliseconds()
    {
        BackgroundEventQueue queue = new BackgroundEventQueue();
        List<long> checks = [];
        long end = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 2;

        queue.WaitForKeyOrEvent(() =>
        {
            checks.Add(Stopwatch.GetTimestamp());
            return Stopwatch.GetTimestamp() >= end;
        });

        Assert.Equal(TimeSpan.FromMilliseconds(50), BackgroundEventQueue.PollInterval);
        // Half a second at one check per 50 ms is about 10; a busy loop would make thousands.
        Assert.InRange(checks.Count, 3, 100);
    }

    // AC-2
    [Fact]
    public void Wait_Returns_WhenKeyAvailableThrowsInvalidOperation()
    {
        BackgroundEventQueue queue = new BackgroundEventQueue();

        Stopwatch watch = Stopwatch.StartNew();
        queue.WaitForKeyOrEvent(() => throw new InvalidOperationException("Cannot see if a key has been pressed when input is redirected."));

        Assert.True(watch.Elapsed < Bound);
    }

    [Fact]
    public void Post_Null_Throws()
    {
        BackgroundEventQueue queue = new BackgroundEventQueue();

        Assert.Throws<ArgumentNullException>(() => queue.Post(null!));
    }
}
