using System.Threading.Channels;

namespace Times.Tests;

/// <summary>
/// A scripted Teams for <see cref="TeamsTracker"/>: no network. Every connect makes a <see cref="FakeTeamsSocket"/>
/// that records its URI; the test then sends messages to the open connection, drops it, or makes connects fail or
/// hang. Every wait is on an observable count, bounded by <see cref="Bound"/>, so a broken tracker fails instead of
/// hanging.
/// </summary>
internal sealed class FakeTeams
{
    /// <summary>How long any wait for the tracker's background work may take before the test fails.</summary>
    public static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private readonly object gate = new object();
    private readonly List<Uri> uris = [];
    private readonly List<string> sent = [];
    private readonly List<CancellationToken> connectTokens = [];
    private int failNext;
    private int receives;
    private FakeTeamsSocket? current;

    /// <summary>True to make every connect wait until it is cancelled.</summary>
    public bool HangConnect { get; set; }

    /// <summary>Makes the factory the tracker takes.</summary>
    public Func<ITeamsSocket> Factory => () => new FakeTeamsSocket(this);

    public IReadOnlyList<Uri> Uris
    {
        get
        {
            lock (gate)
            {
                return uris.ToList();
            }
        }
    }

    public IReadOnlyList<string> Sent
    {
        get
        {
            lock (gate)
            {
                return sent.ToList();
            }
        }
    }

    /// <summary>The cancellation token of each connect, in order.</summary>
    public IReadOnlyList<CancellationToken> ConnectTokens
    {
        get
        {
            lock (gate)
            {
                return connectTokens.ToList();
            }
        }
    }

    /// <summary>The next <paramref name="count"/> connects throw (Teams not running).</summary>
    public void FailNextConnects(int count)
    {
        lock (gate)
        {
            failNext = count;
        }
    }

    /// <summary>Sends one text message on the open connection.</summary>
    public void Send(string message) => Current().Push(message);

    /// <summary>Closes the open connection from Teams' side.</summary>
    public void Drop() => Current().Push(null);

    /// <summary>Makes the open connection's pending receive throw <paramref name="error"/>.</summary>
    public void Fail(Exception error) => Current().Push(error);

    /// <summary>Waits until the <paramref name="count"/>-th connect has been made (failed or not).</summary>
    public void WaitForConnects(int count) => WaitUntil(() => uris.Count >= count, $"connect {count}");

    /// <summary>Waits until the tracker asked for its <paramref name="count"/>-th message, over all connections.</summary>
    public void WaitForReceives(int count) => WaitUntil(() => receives >= count, $"receive {count}");

    /// <summary>Waits until the tracker sent <paramref name="count"/> texts.</summary>
    public void WaitForSent(int count) => WaitUntil(() => sent.Count >= count, $"sent text {count}");

    /// <summary>Waits until the <paramref name="count"/>-th connect's token was cancelled.</summary>
    public void WaitForConnectCancelled(int count) =>
        WaitUntil(() => connectTokens.Count >= count && connectTokens[count - 1].IsCancellationRequested, $"connect {count} cancelled");

    private FakeTeamsSocket Current()
    {
        lock (gate)
        {
            return current ?? throw new InvalidOperationException("No connection is open.");
        }
    }

    private void WaitUntil(Func<bool> condition, string what)
    {
        DateTime until = DateTime.UtcNow + Bound;
        lock (gate)
        {
            while (!condition())
            {
                TimeSpan left = until - DateTime.UtcNow;
                Assert.True(left > TimeSpan.Zero, $"{what} did not happen within {Bound.TotalSeconds} s");
                Monitor.Wait(gate, TimeSpan.FromMilliseconds(Math.Min(left.TotalMilliseconds, 500)));
            }
        }
    }

    private void Changed() => Monitor.PulseAll(gate);

    /// <summary>One connection. Receives come from a channel the test fills.</summary>
    private sealed class FakeTeamsSocket : ITeamsSocket
    {
        private readonly FakeTeams teams;
        private readonly Channel<object?> incoming = Channel.CreateUnbounded<object?>();

        public FakeTeamsSocket(FakeTeams teams)
        {
            this.teams = teams;
        }

        public void Push(object? item) => incoming.Writer.TryWrite(item);

        public async Task ConnectAsync(Uri uri, CancellationToken cancellationToken)
        {
            bool fail;
            bool hang;
            lock (teams.gate)
            {
                teams.uris.Add(uri);
                teams.connectTokens.Add(cancellationToken);
                fail = teams.failNext > 0;
                if (fail)
                {
                    teams.failNext--;
                }
                else if (!teams.HangConnect)
                {
                    teams.current = this;
                }

                hang = !fail && teams.HangConnect;
                teams.Changed();
            }

            if (fail)
            {
                throw new System.Net.WebSockets.WebSocketException("Unable to connect.");
            }

            if (hang)
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }
                finally
                {
                    lock (teams.gate)
                    {
                        teams.Changed();
                    }
                }
            }
        }

        public async Task<string?> ReceiveAsync(int maxBytes, CancellationToken cancellationToken)
        {
            lock (teams.gate)
            {
                teams.receives++;
                teams.Changed();
            }

            object? item = await incoming.Reader.ReadAsync(cancellationToken);
            if (item is Exception error)
            {
                throw error;
            }

            return (string?)item;
        }

        public Task SendAsync(string text, CancellationToken cancellationToken)
        {
            lock (teams.gate)
            {
                teams.sent.Add(text);
                teams.Changed();
            }

            return Task.CompletedTask;
        }

        public void Dispose()
        {
            lock (teams.gate)
            {
                if (teams.current == this)
                {
                    teams.current = null;
                }

                teams.Changed();
            }
        }
    }
}

/// <summary>
/// Wraps a queue for events posted from the tracker's thread: posts and takes are serialised, and every post
/// attempt (queued or refused) is counted, so a test can wait for "the tracker posted its state event".
/// </summary>
internal sealed class CountingEvents : IBackgroundEvents
{
    private readonly IBackgroundEvents inner;
    private readonly object gate = new object();
    private int posts;

    public CountingEvents(IBackgroundEvents inner)
    {
        this.inner = inner;
    }

    public int Posts
    {
        get
        {
            lock (gate)
            {
                return posts;
            }
        }
    }

    public bool Post(BackgroundEvent e)
    {
        lock (gate)
        {
            bool queued = inner.Post(e);
            posts++;
            Monitor.PulseAll(gate);
            return queued;
        }
    }

    public bool TryTake(out BackgroundEvent? e)
    {
        lock (gate)
        {
            return inner.TryTake(out e);
        }
    }

    public void WaitForKeyOrEvent(Func<bool> keyAvailable) => inner.WaitForKeyOrEvent(keyAvailable);

    /// <summary>Waits until <paramref name="count"/> posts were attempted.</summary>
    public void WaitForPosts(int count)
    {
        DateTime until = DateTime.UtcNow + FakeTeams.Bound;
        lock (gate)
        {
            while (posts < count)
            {
                TimeSpan left = until - DateTime.UtcNow;
                Assert.True(left > TimeSpan.Zero, $"post {count} did not happen within {FakeTeams.Bound.TotalSeconds} s");
                Monitor.Wait(gate, left);
            }
        }
    }

    /// <summary>Takes and applies every queued event; returns what each returned.</summary>
    public List<bool> ApplyAll()
    {
        List<bool> results = [];
        while (TryTake(out BackgroundEvent? e))
        {
            results.Add(e!.Apply());
        }

        return results;
    }
}
