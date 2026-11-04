using System.Net;
using System.Text;

namespace Times.Tests;

public sealed class ReleaseSourceTests
{
    private const string ListJson =
        """
        [
          {"tag_name":"v1.2.0","prerelease":false,"draft":false,"assets":[
            {"name":"Times.1.2.0.nupkg","browser_download_url":"https://github.com/nothisisnotdaniel/times/releases/download/v1.2.0/Times.1.2.0.nupkg","digest":"sha256:ab"},
            {"name":"Times.1.2.0.nupkg.sig","browser_download_url":"https://github.com/nothisisnotdaniel/times/releases/download/v1.2.0/Times.1.2.0.nupkg.sig","digest":null}]},
          {"tag_name":"v1.3.0-develop.4","prerelease":true,"draft":false,"assets":[]},
          {"tag_name":"v9.0.0","prerelease":"no","draft":false,"assets":[]},
          {"tag_name":5,"prerelease":false,"draft":false},
          "junk"
        ]
        """;

    /// <summary>Answers every request with <c>respond</c> and records what was asked.</summary>
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond;

        public FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        {
            this.respond = respond;
        }

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return respond(request, cancellationToken);
        }
    }

    /// <summary>A body stream with no length, so only the capped read can stop it.</summary>
    private sealed class EndlessStream : Stream
    {
        public long Served { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => Served; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Array.Fill(buffer, (byte)'a', offset, count);
            Served += count;
            return count;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static FakeHandler Answer(HttpStatusCode status, string body) =>
        new((_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8) }));

    private static FakeHandler AnswerBytes(byte[] body) =>
        new((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }));

    private static FakeHandler Endless(EndlessStream stream) =>
        new((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) }));

    private static FakeHandler Hang() =>
        new(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

    // AC-4: one GET of the release list (never /releases/latest), with a User-Agent and the GitHub JSON type.
    [Fact]
    public async Task ListAsync_RequestsOnlyTheListUrl_WithUserAgent()
    {
        FakeHandler handler = Answer(HttpStatusCode.OK, ListJson);
        GitHubReleaseSource source = new GitHubReleaseSource("1.0.0", handler);

        IReadOnlyList<ReleaseInfo> releases = await source.ListAsync(CancellationToken.None);

        HttpRequestMessage request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https://api.github.com/repos/nothisisnotdaniel/times/releases?per_page=100", request.RequestUri!.AbsoluteUri);
        Assert.Equal(GitHubReleaseSource.ListUrl, request.RequestUri.AbsoluteUri);
        Assert.DoesNotContain("latest", request.RequestUri.AbsoluteUri, StringComparison.Ordinal);
        Assert.Equal("Times/1.0.0", request.Headers.UserAgent.ToString());
        Assert.Contains(request.Headers.Accept, a => a.MediaType == "application/vnd.github+json");

        // Wrongly typed entries are skipped; the rest keep every field.
        Assert.Equal(["v1.2.0", "v1.3.0-develop.4"], releases.Select(r => r.Tag));
        ReleaseInfo first = releases[0];
        Assert.False(first.Prerelease);
        Assert.False(first.Draft);
        Assert.Equal(["Times.1.2.0.nupkg", "Times.1.2.0.nupkg.sig"], first.Assets.Select(a => a.Name));
        Assert.Equal("sha256:ab", first.Assets[0].Digest);
        Assert.Null(first.Assets[1].Digest);
        Assert.True(releases[1].Prerelease);
    }

    // AC-4: an unknown running version still sends a User-Agent.
    [Fact]
    public async Task ListAsync_UnknownVersion_StillSendsUserAgent()
    {
        FakeHandler handler = Answer(HttpStatusCode.OK, "[]");

        await new GitHubReleaseSource("unknown", handler).ListAsync(CancellationToken.None);

        Assert.Equal("Times/unknown", Assert.Single(handler.Requests).Headers.UserAgent.ToString());
    }

    // AC-4: the timeouts and caps.
    [Fact]
    public void Limits_AreTheSpecifiedOnes()
    {
        Assert.Equal(TimeSpan.FromSeconds(15), GitHubReleaseSource.ListTimeout);
        Assert.Equal(TimeSpan.FromSeconds(120), GitHubReleaseSource.DownloadTimeout);
        Assert.Equal(4L * 1024 * 1024, GitHubReleaseSource.MaxListBytes);
        Assert.Equal(20L * 1024 * 1024, UpdateDownloader.MaxPackageBytes);
        Assert.Equal(1024L, UpdateDownloader.MaxSignatureBytes);
    }

    // AC-4: a list over 4 MiB is cut off and refused, with or without a Content-Length.
    [Fact]
    public async Task ListAsync_BodyOverCap_IsCutOffAndRefused()
    {
        EndlessStream endless = new EndlessStream();
        GitHubReleaseSource streamed = new GitHubReleaseSource("1.0.0", Endless(endless));

        UpdateException cut = await Assert.ThrowsAsync<UpdateException>(() => streamed.ListAsync(CancellationToken.None));
        Assert.Contains("too large", cut.Message, StringComparison.Ordinal);
        Assert.True(endless.Served <= GitHubReleaseSource.MaxListBytes + 81920 + 1, $"read {endless.Served} bytes");

        byte[] big = new byte[GitHubReleaseSource.MaxListBytes + 1];
        GitHubReleaseSource sized = new GitHubReleaseSource("1.0.0", AnswerBytes(big));
        UpdateException tooLarge = await Assert.ThrowsAsync<UpdateException>(() => sized.ListAsync(CancellationToken.None));
        Assert.Contains("too large", tooLarge.Message, StringComparison.Ordinal);
    }

    // AC-4: a download over its cap is refused; one at the cap is kept.
    [Fact]
    public async Task DownloadAsync_CapsTheBody()
    {
        string url = TestSigner.Prefix + "v1.0.0/Times.1.0.0.nupkg.sig";

        byte[] exact = await new GitHubReleaseSource("1.0.0", AnswerBytes(new byte[1024])).DownloadAsync(url, 1024, CancellationToken.None);
        Assert.Equal(1024, exact.Length);

        await Assert.ThrowsAsync<UpdateException>(() =>
            new GitHubReleaseSource("1.0.0", AnswerBytes(new byte[1025])).DownloadAsync(url, 1024, CancellationToken.None));
        EndlessStream endless = new EndlessStream();
        await Assert.ThrowsAsync<UpdateException>(() =>
            new GitHubReleaseSource("1.0.0", Endless(endless)).DownloadAsync(url, 1024, CancellationToken.None));
        Assert.True(endless.Served <= 1025, $"read {endless.Served} bytes");
    }

    // AC-4: only asset URLs under the repository's download prefix are requested.
    [Fact]
    public async Task DownloadAsync_AllowedUrl_IsRequestedWithUserAgent()
    {
        FakeHandler handler = AnswerBytes([1, 2, 3]);
        string url = TestSigner.Prefix + "v1.2.0/Times.1.2.0.nupkg";

        byte[] bytes = await new GitHubReleaseSource("1.0.0", handler).DownloadAsync(url, 100, CancellationToken.None);

        Assert.Equal([1, 2, 3], bytes);
        HttpRequestMessage request = Assert.Single(handler.Requests);
        Assert.Equal(url, request.RequestUri!.AbsoluteUri);
        Assert.Equal("Times/1.0.0", request.Headers.UserAgent.ToString());
    }

    // AC-4
    [Theory]
    [InlineData("http://github.com/nothisisnotdaniel/times/releases/download/v1/Times.1.nupkg")]
    [InlineData("https://evil.example/nothisisnotdaniel/times/releases/download/v1/Times.1.nupkg")]
    [InlineData("https://github.com/someoneelse/times/releases/download/v1/Times.1.nupkg")]
    [InlineData("https://github.com/nothisisnotdaniel/times/releases/download/../../../evil/x.nupkg")]
    [InlineData("https://github.com/nothisisnotdaniel/times/releases/download/%2e%2e/%2e%2e/%2e%2e/evil/x")]
    [InlineData("https://user@github.com/nothisisnotdaniel/times/releases/download/v1/Times.1.nupkg")]
    [InlineData("https://github.com:8443/nothisisnotdaniel/times/releases/download/v1/Times.1.nupkg")]
    [InlineData("https://github.com.evil.example/nothisisnotdaniel/times/releases/download/v1/x")]
    [InlineData("https://api.github.com/repos/nothisisnotdaniel/times/releases/latest")]
    [InlineData("https://github.com/nothisisnotdaniel/times/releases/download/")]
    [InlineData("file:///etc/passwd")]
    [InlineData("not a url")]
    [InlineData("")]
    public async Task DownloadAsync_OtherUrls_AreRefusedWithoutARequest(string url)
    {
        FakeHandler handler = AnswerBytes([1]);

        await Assert.ThrowsAsync<UpdateException>(() => new GitHubReleaseSource("1.0.0", handler).DownloadAsync(url, 100, CancellationToken.None));

        Assert.Empty(handler.Requests);
    }

    // AC-4: rate limits and HTTP errors end the check with a short reason.
    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "rate limit")]
    [InlineData(HttpStatusCode.TooManyRequests, "rate limit")]
    [InlineData(HttpStatusCode.InternalServerError, "HTTP 500")]
    [InlineData(HttpStatusCode.NotFound, "HTTP 404")]
    public async Task ListAsync_HttpErrors_ThrowUpdateException(HttpStatusCode status, string reason)
    {
        UpdateException ex = await Assert.ThrowsAsync<UpdateException>(() =>
            new GitHubReleaseSource("1.0.0", Answer(status, "{}")).ListAsync(CancellationToken.None));

        Assert.Contains(reason, ex.Message, StringComparison.Ordinal);
    }

    // AC-4: offline.
    [Fact]
    public async Task ListAsync_Offline_ThrowsUpdateException()
    {
        FakeHandler handler = new FakeHandler((_, _) => throw new HttpRequestException("No such host is known."));

        UpdateException ex = await Assert.ThrowsAsync<UpdateException>(() => new GitHubReleaseSource("1.0.0", handler).ListAsync(CancellationToken.None));

        Assert.Contains("offline", ex.Message, StringComparison.Ordinal);
    }

    // AC-4: bad JSON.
    [Theory]
    [InlineData("not json")]
    [InlineData("{\"message\":\"Not Found\"}")]
    [InlineData("[")]
    [InlineData("[{\"tag_name\":\"\\ud800\",\"prerelease\":false,\"draft\":false}]")]
    public async Task ListAsync_BadJson_ThrowsUpdateException(string body)
    {
        await Assert.ThrowsAsync<UpdateException>(() =>
            new GitHubReleaseSource("1.0.0", Answer(HttpStatusCode.OK, body)).ListAsync(CancellationToken.None));
    }

    // AC-4: the list and the download each have their timeout.
    [Fact]
    public async Task Timeouts_EndTheRequest()
    {
        GitHubReleaseSource source = new GitHubReleaseSource("1.0.0", Hang(), TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50));

        UpdateException list = await Assert.ThrowsAsync<UpdateException>(() => source.ListAsync(CancellationToken.None));
        UpdateException download = await Assert.ThrowsAsync<UpdateException>(() =>
            source.DownloadAsync(TestSigner.Prefix + "v1/Times.1.nupkg", 100, CancellationToken.None));

        Assert.Contains("timed out", list.Message, StringComparison.Ordinal);
        Assert.Contains("timed out", download.Message, StringComparison.Ordinal);
    }
}
