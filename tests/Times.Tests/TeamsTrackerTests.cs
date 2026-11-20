using System.Net.WebSockets;
using System.Text;

namespace Times.Tests;

// Job 327: the background loop over a scripted Teams. No network: FakeTeams answers every connect; the retry timers
// fire by hand through SettableTimeProvider; every wait is on an observable count, bounded by FakeTeams.Bound.
public sealed class TeamsTrackerTests : IDisposable
{
    private const string Token = "SECRET-token-327";
    private const string Version = "1.2.3";

    private readonly TempFolder data = new();
    private readonly TeamsPairing pairing;
    private readonly FakeTeams teams = new();
    private readonly SettableTimeProvider time = new(new DateTimeOffset(2026, 10, 27, 10, 0, 0, TimeSpan.Zero));
    private readonly List<bool> changes = [];
    private int hints;
    private CountingEvents events = new(new BackgroundEventQueue());
    private TeamsTracker? tracker;

    public TeamsTrackerTests()
    {
        pairing = new TeamsPairing(data.Path);
        pairing.Create();
    }

    public void Dispose()
    {
        tracker?.Dispose();
        data.Dispose();
    }

    private static string State(bool inMeeting, bool canPair = false) =>
        $"{{\"meetingUpdate\":{{\"meetingState\":{{\"isInMeeting\":{(inMeeting ? "true" : "false")}}}," +
        $"\"meetingPermissions\":{{\"canPair\":{(canPair ? "true" : "false")}}}}}}}";

    private TeamsTracker Start()
    {
        tracker = new TeamsTracker(time, events, pairing, teams.Factory, Version, inMeeting =>
        {
            changes.Add(inMeeting);
            return true;
        }, () =>
        {
            hints++;
            return true;
        });
        tracker.Start();
        return tracker;
    }

    // Sends a message and waits until the tracker has handled it (it asks for the next one).
    private void Send(string message, int receivesAfter)
    {
        teams.Send(message);
        teams.WaitForReceives(receivesAfter);
    }

    // AC-2
    [Fact]
    public void Connects_ToLoopback_WithTheEscapedToken_AndTheVersion()
    {
        pairing.SaveToken("a+b/c=");
        Start();

        teams.WaitForConnects(1);

        Uri uri = Assert.Single(teams.Uris);
        Assert.Equal(TeamsProtocol.BuildUri("a+b/c=", Version), uri);
        Assert.Contains("token=a%2Bb%2Fc%3D&", uri.Query, StringComparison.Ordinal);
        Assert.Contains("app-version=1.2.3", uri.Query, StringComparison.Ordinal);
    }

    // AC-8: the first state only sets the baseline; then false -> true starts and true -> false ends.
    [Fact]
    public void FirstState_IsTheBaseline_ThenStartAndEndAreApplied()
    {
        Start();
        teams.WaitForConnects(1);

        Send(State(false), 2);
        events.WaitForPosts(1);
        Assert.Equal([false], events.ApplyAll());
        Assert.Empty(changes);

        Send(State(true), 3);
        events.WaitForPosts(2);
        Assert.Equal([true], events.ApplyAll());
        Assert.Equal([true], changes);

        Send(State(false), 4);
        events.WaitForPosts(3);
        Assert.Equal([true], events.ApplyAll());
        Assert.Equal([true, false], changes);
    }

    // AC-8: a meeting already running at start is not booked.
    [Fact]
    public void FirstState_InAMeeting_BooksNothing()
    {
        Start();
        teams.WaitForConnects(1);

        Send(State(true), 2);
        events.WaitForPosts(1);
        events.ApplyAll();

        Assert.Empty(changes);
    }

    // AC-8: the same state again posts nothing.
    [Fact]
    public void TheSameState_PostsNothing()
    {
        Start();
        teams.WaitForConnects(1);
        Send(State(false), 2);
        Send(State(false), 3);
        Send("{\"requestId\":1,\"response\":\"Success\"}", 4);
        Send("not json", 5);

        Assert.Equal(1, events.Posts);
    }

    // AC-10: one queued event at most; it reads the latest state when applied.
    [Fact]
    public void ChangesWhileAnEventIsQueued_PostOnce_AndTheEventReadsTheLatestState()
    {
        Start();
        teams.WaitForConnects(1);
        Send(State(false), 2);
        events.WaitForPosts(1);
        events.ApplyAll();

        Send(State(true), 3);
        Send(State(false), 4);
        Send(State(true), 5);

        Assert.Equal(2, events.Posts);
        Assert.Equal([true], events.ApplyAll());
        Assert.Equal([true], changes);
    }

    // AC-10: a refused post clears the pending flag, so the next change posts again.
    [Fact]
    public void ARefusedPost_LetsTheNextChangePost()
    {
        BackgroundEventQueue queue = new BackgroundEventQueue(capacity: 1);
        Assert.True(queue.Post(new BackgroundEvent(() => false)));
        events = new CountingEvents(queue);
        Start();
        teams.WaitForConnects(1);

        Send(State(false), 2);
        events.WaitForPosts(1);
        Assert.Equal([false], events.ApplyAll());

        // The refused state was never applied, so this one is the baseline.
        Send(State(true), 3);
        events.WaitForPosts(2);
        Assert.Equal([false], events.ApplyAll());
        Assert.Empty(changes);

        Send(State(false), 4);
        events.WaitForPosts(3);
        Assert.Equal([true], events.ApplyAll());
        Assert.Equal([false], changes);
    }

    // AC-8, AC-9: a drop keeps the meeting state: after reconnecting the same state books nothing again.
    [Fact]
    public void ADrop_KeepsTheState_AndShowsTheHintOnce()
    {
        Start();
        teams.WaitForConnects(1);
        Send(State(false), 2);
        events.WaitForPosts(1);
        events.ApplyAll();
        Send(State(true), 3);
        events.WaitForPosts(2);
        events.ApplyAll();
        Assert.Equal([true], changes);

        teams.Drop();
        ManualTimer retry = time.WaitForTimer(1, FakeTeams.Bound);
        Assert.Equal([TimeSpan.FromSeconds(5)], retry.DueTimes);
        events.WaitForPosts(3);
        Assert.Equal([true], events.ApplyAll());
        Assert.Equal(1, hints);

        retry.Fire();
        teams.WaitForConnects(2);
        teams.WaitForReceives(4);
        Send(State(true), 5);

        Assert.Equal(3, events.Posts);
        Assert.Empty(events.ApplyAll());
        Assert.Equal([true], changes);
        Assert.Equal(1, hints);
    }

    // AC-9: retries wait 5 s, 15 s, then 60 s each; one hint for the whole outage; a later drop shows it again.
    [Fact]
    public void Unreachable_RetriesAfter5_15_60_60_WithOneHint()
    {
        teams.FailNextConnects(4);
        Start();

        List<TimeSpan> delays = [];
        for (int i = 1; i <= 4; i++)
        {
            ManualTimer timer = time.WaitForTimer(i, FakeTeams.Bound);
            delays.Add(timer.DueTimes[0]);
            timer.Fire();
            teams.WaitForConnects(i + 1);
        }

        Assert.Equal([TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60)], delays);
        teams.WaitForReceives(1);
        Assert.Equal(1, events.Posts);
        events.ApplyAll();
        Assert.Equal(1, hints);

        // Connected again: the next drop starts at 5 s and shows the hint once more.
        teams.Drop();
        Assert.Equal([TimeSpan.FromSeconds(5)], time.WaitForTimer(5, FakeTeams.Bound).DueTimes);
        events.WaitForPosts(2);
        events.ApplyAll();
        Assert.Equal(2, hints);
    }

    // AC-2, AC-9: a receive error (a message over the cap) closes the connection and retries.
    [Fact]
    public void AReceiveError_ClosesAndRetries()
    {
        Start();
        teams.WaitForConnects(1);
        teams.WaitForReceives(1);

        teams.Fail(new TeamsMessageTooLargeException());
        time.WaitForTimer(1, FakeTeams.Bound).Fire();

        teams.WaitForConnects(2);
        events.WaitForPosts(1);
        events.ApplyAll();
        Assert.Equal(1, hints);
    }

    // AC-5: while not paired, one pair request per meeting, only with canPair, only in a meeting.
    [Fact]
    public void NotPaired_SendsOnePairRequestPerMeeting()
    {
        Start();
        teams.WaitForConnects(1);

        Send(State(false, canPair: true), 2);
        Send(State(true, canPair: false), 3);
        Assert.Empty(teams.Sent);

        Send(State(true, canPair: true), 4);
        Send(State(true, canPair: true), 5);
        Assert.Equal([TeamsProtocol.PairRequest], teams.Sent);

        Send(State(false, canPair: true), 6);
        Send(State(true, canPair: true), 7);
        Assert.Equal([TeamsProtocol.PairRequest, TeamsProtocol.PairRequest], teams.Sent);
    }

    // AC-5
    [Fact]
    public void Paired_NeverSendsAnything()
    {
        pairing.SaveToken(Token);
        Start();
        teams.WaitForConnects(1);

        Send(State(true, canPair: true), 2);
        Send(State(false, canPair: true), 3);
        Send(State(true, canPair: true), 4);

        Assert.Empty(teams.Sent);
    }

    // AC-4: a valid token is saved and used for the next connect; then no more pair requests.
    [Fact]
    public void TokenRefresh_IsSaved_AndUsedOnTheNextConnect()
    {
        Start();
        teams.WaitForConnects(1);

        Send($"{{\"tokenRefresh\":\"{Token}\"}}", 2);
        Assert.Equal(TeamsPairingKind.Paired, pairing.Read(out string? saved));
        Assert.Equal(Token, saved);
        Send(State(true, canPair: true), 3);
        Assert.Empty(teams.Sent);

        teams.Drop();
        time.WaitForTimer(1, FakeTeams.Bound).Fire();
        teams.WaitForConnects(2);
        Assert.Equal(TeamsProtocol.BuildUri(Token, Version), teams.Uris[1]);
    }

    // AC-4
    [Theory]
    [InlineData("\"\"")]
    [InlineData("\"with space\"")]
    [InlineData("\"ctrl\\u0001\"")]
    [InlineData("42")]
    public void TokenRefresh_Invalid_IsIgnored(string value)
    {
        Start();
        teams.WaitForConnects(1);

        Send($"{{\"tokenRefresh\":{value}}}", 2);

        Assert.Equal("{\"token\":null}", File.ReadAllText(pairing.FilePath));
    }

    // AC-4
    [Fact]
    public void TokenRefresh_Overlong_IsIgnored()
    {
        Start();
        teams.WaitForConnects(1);

        Send($"{{\"tokenRefresh\":\"{new string('x', 513)}\"}}", 2);

        Assert.Equal(TeamsPairingKind.NotPaired, pairing.Read(out _));
    }

    // AC-11: dispose cancels a pending connect; a later event does nothing.
    [Fact]
    public void Dispose_CancelsAPendingConnect_AndLaterEventsDoNothing()
    {
        teams.HangConnect = true;
        TeamsTracker started = Start();
        teams.WaitForConnects(1);

        started.Dispose();

        teams.WaitForConnectCancelled(1);
        Assert.False(started.Connected);
    }

    // AC-10: an event applied after dispose books nothing.
    [Fact]
    public void AnEventAppliedAfterDispose_DoesNothing()
    {
        TeamsTracker started = Start();
        teams.WaitForConnects(1);
        Send(State(false), 2);
        events.WaitForPosts(1);
        events.ApplyAll();
        Send(State(true), 3);
        events.WaitForPosts(2);

        started.Dispose();

        Assert.Equal([false], events.ApplyAll());
        Assert.Empty(changes);
    }

    [Fact]
    public void Connected_IsTrueWhileTheConnectionIsOpen()
    {
        TeamsTracker started = Start();
        teams.WaitForConnects(1);
        teams.WaitForReceives(1);

        Assert.True(started.Connected);
    }

    // The production receive loop over an in-memory WebSocket: a text message, a skipped binary one, the cap, close.

    private static async Task<byte[]> ServerFrames(Func<WebSocket, Task> send)
    {
        using MemoryStream frames = new MemoryStream();
        using (WebSocket server = WebSocket.CreateFromStream(frames, isServer: true, subProtocol: null, keepAliveInterval: TimeSpan.Zero))
        {
            await send(server);
        }

        return frames.ToArray();
    }

    private static WebSocket Client(byte[] frames) =>
        WebSocket.CreateFromStream(new ReadOnlyDuplex(frames), isServer: false, subProtocol: null, keepAliveInterval: TimeSpan.Zero);

    // AC-2
    [Fact]
    public async Task ReceiveText_ReadsText_SkipsBinary_AndReturnsNullOnClose()
    {
        byte[] frames = await ServerFrames(async server =>
        {
            await server.SendAsync(new byte[] { 1, 2, 3 }, WebSocketMessageType.Binary, true, CancellationToken.None);
            await server.SendAsync(Encoding.UTF8.GetBytes("{\"a\":"), WebSocketMessageType.Text, false, CancellationToken.None);
            await server.SendAsync(Encoding.UTF8.GetBytes("1}"), WebSocketMessageType.Text, true, CancellationToken.None);
            await server.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        });
        using WebSocket client = Client(frames);

        Assert.Equal("{\"a\":1}", await ClientWebSocketTeamsSocket.ReceiveTextAsync(client, 64, CancellationToken.None));
        Assert.Null(await ClientWebSocketTeamsSocket.ReceiveTextAsync(client, 64, CancellationToken.None));
    }

    // AC-2: a message over the cap throws (the tracker then closes and retries).
    [Fact]
    public async Task ReceiveText_OverTheCap_Throws()
    {
        byte[] frames = await ServerFrames(server =>
            server.SendAsync(new byte[TeamsProtocol.MaxMessageBytes + 1], WebSocketMessageType.Text, true, CancellationToken.None));
        using WebSocket client = Client(frames);

        await Assert.ThrowsAsync<TeamsMessageTooLargeException>(
            () => ClientWebSocketTeamsSocket.ReceiveTextAsync(client, TeamsProtocol.MaxMessageBytes, CancellationToken.None));
    }

    // AC-2: exactly the cap is read.
    [Fact]
    public async Task ReceiveText_AtTheCap_IsRead()
    {
        byte[] frames = await ServerFrames(server =>
            server.SendAsync(Encoding.UTF8.GetBytes(new string('x', 100)), WebSocketMessageType.Text, true, CancellationToken.None));
        using WebSocket client = Client(frames);

        Assert.Equal(100, (await ClientWebSocketTeamsSocket.ReceiveTextAsync(client, 100, CancellationToken.None))!.Length);
    }

    /// <summary>Reads given bytes; writes go nowhere.</summary>
    private sealed class ReadOnlyDuplex : Stream
    {
        private readonly MemoryStream input;

        public ReadOnlyDuplex(byte[] bytes)
        {
            input = new MemoryStream(bytes, writable: false);
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => input.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
        }
    }
}
