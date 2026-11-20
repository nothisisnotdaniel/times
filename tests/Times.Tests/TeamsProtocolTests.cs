namespace Times.Tests;

// Job 327: the wire details of Teams' local API (protocol 2.0.0).
public sealed class TeamsProtocolTests
{
    // AC-2
    [Fact]
    public void BuildUri_IsLoopbackOnly_WithTheTokenEscaped_AndTimesAsTheApp()
    {
        Uri uri = TeamsProtocol.BuildUri("a+b/c=d&e f", "1.2.3-develop.4");

        Assert.Equal("ws", uri.Scheme);
        Assert.Equal("127.0.0.1", uri.Host);
        Assert.Equal(UriHostNameType.IPv4, uri.HostNameType);
        Assert.Equal(8124, uri.Port);
        Assert.Equal("/", uri.AbsolutePath);
        Assert.Equal(
            "?token=a%2Bb%2Fc%3Dd%26e%20f&protocol-version=2.0.0&manufacturer=Times&device=Times&app=Times&app-version=1.2.3-develop.4",
            uri.Query);
        Assert.True(TeamsProtocol.IsAllowed(uri));
    }

    // AC-2: not paired yet: an empty token.
    [Fact]
    public void BuildUri_WithoutAToken_SendsAnEmptyToken()
    {
        Uri uri = TeamsProtocol.BuildUri(null, "0.1.0");

        Assert.StartsWith("?token=&protocol-version=2.0.0&", uri.Query, StringComparison.Ordinal);
    }

    // AC-2
    [Theory]
    [InlineData("ws://localhost:8124/")]
    [InlineData("ws://127.0.0.2:8124/")]
    [InlineData("ws://127.0.0.1:8125/")]
    [InlineData("wss://127.0.0.1:8124/")]
    [InlineData("ws://127.0.0.1:8124/other")]
    [InlineData("ws://[::1]:8124/")]
    public void IsAllowed_RefusesEveryOtherAddress(string address)
    {
        Assert.False(TeamsProtocol.IsAllowed(new Uri(address)));
    }

    // AC-2: the real socket has no proxy and refuses any other address before connecting.
    [Fact]
    public async Task ClientWebSocketTeamsSocket_HasNoProxy_AndRefusesAnotherHost()
    {
        using ClientWebSocketTeamsSocket socket = new ClientWebSocketTeamsSocket();

        Assert.Null(socket.Options.Proxy);
        Assert.Equal("127.0.0.1", TeamsProtocol.BuildUri(null, "0.1.0").Host);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => socket.ConnectAsync(new Uri("ws://example.com:8124/"), CancellationToken.None));
    }

    // AC-3
    [Fact]
    public void Parse_ReadsAMeetingUpdate_AndIgnoresUnknownProperties()
    {
        TeamsMessage? message = TeamsProtocol.Parse(
            "{\"meetingUpdate\":{\"meetingState\":{\"isMuted\":false,\"isInMeeting\":true,\"isRecordingOn\":false}," +
            "\"meetingPermissions\":{\"canToggleMute\":true,\"canPair\":true},\"extra\":[1,2]},\"other\":{\"x\":1}}");

        Assert.NotNull(message);
        Assert.True(message.InMeeting);
        Assert.True(message.CanPair);
        Assert.Null(message.Token);
    }

    // AC-3
    [Fact]
    public void Parse_ReadsATokenRefresh()
    {
        TeamsMessage? message = TeamsProtocol.Parse("{\"tokenRefresh\":\"abc-123\"}");

        Assert.NotNull(message);
        Assert.Equal("abc-123", message.Token);
        Assert.Null(message.InMeeting);
        Assert.DoesNotContain("abc-123", message.ToString(), StringComparison.Ordinal);
    }

    // AC-3: a reply or anything else with no known part is a message with nothing in it.
    [Fact]
    public void Parse_AReplyWithNoKnownPart_HoldsNothing()
    {
        TeamsMessage? message = TeamsProtocol.Parse("{\"requestId\":1,\"response\":\"Success\"}");

        Assert.NotNull(message);
        Assert.Null(message.InMeeting);
        Assert.Null(message.CanPair);
        Assert.Null(message.Token);
    }

    // AC-3
    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("\"text\"")]
    [InlineData("42")]
    [InlineData("null")]
    [InlineData("{\"meetingUpdate\":{}} trailing")]
    [InlineData("{\"tokenRefresh\":42}")]
    [InlineData("{\"tokenRefresh\":null}")]
    [InlineData("{\"meetingUpdate\":[]}")]
    [InlineData("{\"meetingUpdate\":{\"meetingState\":\"on\"}}")]
    [InlineData("{\"meetingUpdate\":{\"meetingState\":{\"isInMeeting\":\"true\"}}}")]
    [InlineData("{\"meetingUpdate\":{\"meetingState\":{\"isInMeeting\":1}}}")]
    [InlineData("{\"meetingUpdate\":{\"meetingPermissions\":{\"canPair\":null}}}")]
    [InlineData("{\"tokenRefresh\":\"a\",\"tokenRefresh\":\"b\"}")]
    [InlineData("{\"meetingUpdate\":{\"meetingState\":{\"isInMeeting\":true,\"isInMeeting\":false}}}")]
    [InlineData("{\"other\":{\"x\":1,\"x\":2}}")]
    public void Parse_RefusesWhatIsNotAMessage(string text)
    {
        Assert.Null(TeamsProtocol.Parse(text));
    }

    // AC-3: deep nesting is refused, never thrown.
    [Fact]
    public void Parse_DeepNesting_IsRefused()
    {
        string deep = "{\"x\":" + new string('[', 200) + new string(']', 200) + "}";

        Assert.Null(TeamsProtocol.Parse(deep));
    }

    // AC-4
    [Theory]
    [InlineData("a", true)]
    [InlineData("!~", true)]
    [InlineData("", false)]
    [InlineData("has space", false)]
    [InlineData("tab\t", false)]
    [InlineData("line\n", false)]
    [InlineData("umlaut-ä", false)]
    [InlineData("del\u007f", false)]
    public void IsValidToken_TakesPrintableAscii(string token, bool valid)
    {
        Assert.Equal(valid, TeamsProtocol.IsValidToken(token));
    }

    // AC-4
    [Fact]
    public void IsValidToken_TakesAtMost512Characters()
    {
        Assert.True(TeamsProtocol.IsValidToken(new string('x', 512)));
        Assert.False(TeamsProtocol.IsValidToken(new string('x', 513)));
        Assert.False(TeamsProtocol.IsValidToken(null));
    }

    // AC-5: the only text Times ever sends asks for pairing, nothing that changes Teams.
    [Fact]
    public void PairRequest_IsThePairAction()
    {
        Assert.Equal("{\"action\":\"pair\",\"parameters\":{},\"requestId\":1}", TeamsProtocol.PairRequest);
    }
}
