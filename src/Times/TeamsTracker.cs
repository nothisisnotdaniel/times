using System.Net.WebSockets;
using System.Text;

namespace Times;

/// <summary>Thrown by a <see cref="ITeamsSocket"/> for a message over the cap; the tracker then closes and retries.</summary>
internal sealed class TeamsMessageTooLargeException : IOException
{
    public TeamsMessageTooLargeException()
        : base("The Teams message is too large.")
    {
    }
}

/// <summary>One connection to Teams' local API. Created fresh for every connect.</summary>
internal interface ITeamsSocket : IDisposable
{
    /// <summary>Connects to <paramref name="uri"/>; throws when Teams cannot be reached.</summary>
    public Task ConnectAsync(Uri uri, CancellationToken cancellationToken);

    /// <summary>
    /// The next whole text message, or <c>null</c> when the connection was closed. Throws
    /// <see cref="TeamsMessageTooLargeException"/> for a message over <paramref name="maxBytes"/>.
    /// </summary>
    public Task<string?> ReceiveAsync(int maxBytes, CancellationToken cancellationToken);

    /// <summary>Sends one text message.</summary>
    public Task SendAsync(string text, CancellationToken cancellationToken);
}

/// <summary>
/// The production socket: the BCL <see cref="ClientWebSocket"/> with no proxy, refusing any address but
/// <c>ws://127.0.0.1:8124/</c>, with a 10 s connect timeout.
/// </summary>
internal sealed class ClientWebSocketTeamsSocket : ITeamsSocket
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    private readonly ClientWebSocket socket = new ClientWebSocket();

    public ClientWebSocketTeamsSocket()
    {
        socket.Options.Proxy = null;
        socket.Options.UseDefaultCredentials = false;
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
    }

    /// <summary>The socket's options, so a test can check them without connecting.</summary>
    internal ClientWebSocketOptions Options => socket.Options;

    public async Task ConnectAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (!TeamsProtocol.IsAllowed(uri))
        {
            throw new InvalidOperationException("Times connects to Teams on 127.0.0.1 only.");
        }

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ConnectTimeout);
        await socket.ConnectAsync(uri, timeout.Token).ConfigureAwait(false);
    }

    public Task<string?> ReceiveAsync(int maxBytes, CancellationToken cancellationToken) =>
        ReceiveTextAsync(socket, maxBytes, cancellationToken);

    /// <summary>
    /// The next whole text message on <paramref name="socket"/> (binary ones skipped, one that is not UTF-8 read as
    /// empty), or <c>null</c> on close. Throws <see cref="TeamsMessageTooLargeException"/> as soon as a message
    /// passes <paramref name="maxBytes"/>, without reading the rest.
    /// </summary>
    internal static async Task<string?> ReceiveTextAsync(WebSocket socket, int maxBytes, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[4096];
        using MemoryStream message = new MemoryStream();
        while (true)
        {
            WebSocketReceiveResult result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            if (message.Length + result.Count > maxBytes)
            {
                throw new TeamsMessageTooLargeException();
            }

            message.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage)
            {
                continue;
            }

            if (result.MessageType != WebSocketMessageType.Text)
            {
                // Binary messages mean nothing here: skipped.
                message.SetLength(0);
                continue;
            }

            try
            {
                return new UTF8Encoding(false, true).GetString(message.GetBuffer(), 0, (int)message.Length);
            }
            catch (DecoderFallbackException)
            {
                // Not UTF-8: an empty text, which Parse ignores.
                return string.Empty;
            }
        }
    }

    public Task SendAsync(string text, CancellationToken cancellationToken) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, endOfMessage: true, cancellationToken);

    public void Dispose() => socket.Dispose();
}

/// <summary>
/// Follows Teams' meeting state in the background: connects to the local API, reads messages, asks for pairing
/// once per meeting while not paired, saves a token Teams sends, and retries after 5 s, 15 s, then every 60 s
/// (from the injected <see cref="TimeProvider"/>) when Teams cannot be reached or the connection drops. It never
/// books or draws itself: it posts at most one queued state event, and that event (on the loop thread) reads the
/// latest state and calls <c>meetingChanged</c> for a start or an end and <c>notReachable</c> once per drop. The
/// first state after start only sets the baseline, and a reconnect keeps the last state, so nothing is booked twice.
/// </summary>
internal sealed class TeamsTracker : IDisposable
{
    /// <summary>How long to wait before each retry: the first, the second, then every later one.</summary>
    public static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(60)];

    private readonly TimeProvider time;
    private readonly IBackgroundEvents events;
    private readonly TeamsPairing pairing;
    private readonly Func<ITeamsSocket> socketFactory;
    private readonly string appVersion;
    private readonly Func<bool, bool> meetingChanged;
    private readonly Func<bool> notReachable;
    private readonly CancellationTokenSource stop = new CancellationTokenSource();
    private readonly object gate = new object();
    private Task? loop;
    private bool disposed;

    // The latest state from Teams (tracker thread writes, loop thread reads; under gate).
    private bool? latestInMeeting;
    private int drops;

    // Loop thread only: the state the last applied event acted on (null: no baseline yet), and drops shown.
    private bool? appliedInMeeting;
    private int dropsShown;

    // 1 while a posted state event has not been applied yet.
    private int pending;

    private volatile bool connected;

    /// <param name="meetingChanged">On the loop thread, for a meeting start (true) or end (false); returns true if the view changed.</param>
    /// <param name="notReachable">On the loop thread, once per drop; returns true if something was drawn.</param>
    public TeamsTracker(TimeProvider time, IBackgroundEvents events, TeamsPairing pairing, Func<ITeamsSocket> socketFactory,
        string appVersion, Func<bool, bool> meetingChanged, Func<bool> notReachable)
    {
        this.time = time;
        this.events = events;
        this.pairing = pairing;
        this.socketFactory = socketFactory;
        this.appVersion = appVersion;
        this.meetingChanged = meetingChanged;
        this.notReachable = notReachable;
    }

    /// <summary>True while a connection to Teams is open.</summary>
    public bool Connected => connected;

    /// <summary>Starts the background loop. Does nothing if already started or disposed.</summary>
    public void Start()
    {
        lock (gate)
        {
            if (disposed || loop is not null)
            {
                return;
            }

            loop = Task.Run(() => RunAsync(stop.Token));
        }
    }

    /// <summary>
    /// Stops the loop without waiting for it: a pending connect or receive is cancelled, no event posted after this
    /// does anything, and no token is saved after this returns.
    /// </summary>
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
        }

        stop.Cancel();
    }

    private async Task RunAsync(CancellationToken cancel)
    {
        int failures = 0;
        bool dropReported = false;
        bool pairSent = false;
        while (!cancel.IsCancellationRequested)
        {
            ITeamsSocket? socket = null;
            try
            {
                pairing.Read(out string? token);
                bool paired = token is not null;
                socket = socketFactory();
                await socket.ConnectAsync(TeamsProtocol.BuildUri(token, appVersion), cancel).ConfigureAwait(false);
                connected = true;
                failures = 0;
                dropReported = false;
                while (true)
                {
                    string? text = await socket.ReceiveAsync(TeamsProtocol.MaxMessageBytes, cancel).ConfigureAwait(false);
                    if (text is null)
                    {
                        break;
                    }

                    if (TeamsProtocol.Parse(text) is not { } message)
                    {
                        continue;
                    }

                    if (message.Token is not null && SaveToken(message.Token))
                    {
                        paired = true;
                    }

                    if (message.InMeeting == false)
                    {
                        pairSent = false;
                    }

                    if (!paired && !pairSent && message.InMeeting == true && message.CanPair == true)
                    {
                        pairSent = true;
                        await socket.SendAsync(TeamsProtocol.PairRequest, cancel).ConfigureAwait(false);
                    }

                    if (message.InMeeting is { } inMeeting)
                    {
                        SetState(inMeeting);
                    }
                }
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                // Teams not running, refused, dropped or sent too much: retried quietly below.
            }
            finally
            {
                connected = false;
                socket?.Dispose();
            }

            if (cancel.IsCancellationRequested)
            {
                return;
            }

            if (!dropReported)
            {
                dropReported = true;
                lock (gate)
                {
                    drops++;
                }

                PostState();
            }

            TimeSpan delay = RetryDelays[Math.Min(failures, RetryDelays.Length - 1)];
            failures++;
            try
            {
                await Task.Delay(delay, time, cancel).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    // Saves a token Teams sent, unless the tracker was disposed (so a disconnect's delete is never undone).
    private bool SaveToken(string token)
    {
        lock (gate)
        {
            return !disposed && pairing.SaveToken(token);
        }
    }

    private void SetState(bool inMeeting)
    {
        lock (gate)
        {
            if (latestInMeeting == inMeeting)
            {
                return;
            }

            latestInMeeting = inMeeting;
        }

        PostState();
    }

    // Posts one state event unless one is still queued; a refused post clears the flag, so the next change posts.
    private void PostState()
    {
        if (Interlocked.CompareExchange(ref pending, 1, 0) == 0 && !events.Post(new BackgroundEvent(Apply)))
        {
            Interlocked.Exchange(ref pending, 0);
        }
    }

    // On the loop thread: reads the latest state, shows the drop hint once per drop and books a start or an end.
    private bool Apply()
    {
        Interlocked.Exchange(ref pending, 0);
        bool? inMeeting;
        int dropCount;
        lock (gate)
        {
            if (disposed)
            {
                return false;
            }

            inMeeting = latestInMeeting;
            dropCount = drops;
        }

        bool changed = false;
        if (dropCount > dropsShown)
        {
            dropsShown = dropCount;
            changed |= notReachable();
        }

        if (inMeeting is { } now)
        {
            bool? before = appliedInMeeting;
            appliedInMeeting = now;
            if (before is { } was && was != now)
            {
                changed |= meetingChanged(now);
            }
        }

        return changed;
    }
}
