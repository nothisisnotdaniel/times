using System.Net;
using System.Text;
using System.Text.Json;

namespace Times.Tests;

public class DevOpsClientTests
{
    private const string Marker = "PAT-MARKER-c7a1";
    private const string ItemUrl = "https://dev.azure.com/contoso/_apis/wit/workitems/42?api-version=7.1";

    /// <summary>A handler that records each request (with its body) and answers with the next queued response.</summary>
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> answers = new();

        public List<(HttpMethod Method, Uri? Uri, string? Authorization, string? ContentType, string Body)> Requests { get; } = [];

        public void Answer(HttpStatusCode status, string body = "") =>
            answers.Enqueue((_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) }));

        public void Answer(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer) => answers.Enqueue(answer);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method, request.RequestUri, request.Headers.Authorization?.ToString(),
                request.Content?.Headers.ContentType?.MediaType, body));
            return await answers.Dequeue()(request, cancellationToken);
        }
    }

    private static (AzureDevOpsClient Client, FakeHandler Handler) Create(TimeSpan? timeout = null)
    {
        FakeHandler handler = new FakeHandler();
        DevOpsCredential credential = DevOpsCredential.TryCreate("contoso", Marker)!;
        return (new AzureDevOpsClient(credential, handler, timeout), handler);
    }

    private static string Basic => "Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + Marker));

    // AC-7, AC-10
    [Fact]
    public async Task Get_SendsUrlAndBasicAuth_AndReadsFieldAndRev()
    {
        (AzureDevOpsClient client, FakeHandler handler) = Create();
        handler.Answer(HttpStatusCode.OK, "{\"id\":42,\"rev\":7,\"fields\":{\"Microsoft.VSTS.Scheduling.CompletedWork\":2.5}}");

        (double? completed, int rev) = await client.GetCompletedWorkAsync(42);

        Assert.Equal(2.5, completed);
        Assert.Equal(7, rev);
        (HttpMethod method, Uri? uri, string? authorization, _, _) = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, method);
        Assert.Equal(ItemUrl, uri!.AbsoluteUri);
        Assert.Equal(Basic, authorization);
    }

    // AC-7
    [Theory]
    [InlineData("{\"rev\":3,\"fields\":{}}")]
    [InlineData("{\"rev\":3}")]
    [InlineData("{\"rev\":3,\"fields\":{\"Microsoft.VSTS.Scheduling.CompletedWork\":null}}")]
    public async Task Get_MissingField_IsNull(string body)
    {
        (AzureDevOpsClient client, FakeHandler handler) = Create();
        handler.Answer(HttpStatusCode.OK, body);

        (double? completed, int rev) = await client.GetCompletedWorkAsync(42);

        Assert.Null(completed);
        Assert.Equal(3, rev);
    }

    // AC-7
    [Fact]
    public async Task Set_PatchesWithRevTest_AsJsonPatch()
    {
        (AzureDevOpsClient client, FakeHandler handler) = Create();
        handler.Answer(HttpStatusCode.OK, "{}");

        await client.SetCompletedWorkAsync(42, 7, 4.25);

        (HttpMethod method, Uri? uri, string? authorization, string? contentType, string body) = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Patch, method);
        Assert.Equal(ItemUrl, uri!.AbsoluteUri);
        Assert.Equal(Basic, authorization);
        Assert.Equal("application/json-patch+json", contentType);
        Assert.Equal(
            "[{\"op\":\"test\",\"path\":\"/rev\",\"value\":7},"
            + "{\"op\":\"add\",\"path\":\"/fields/Microsoft.VSTS.Scheduling.CompletedWork\",\"value\":4.25}]",
            body);
    }

    // AC-8, AC-10
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, DevOpsException.Refused)]
    [InlineData(HttpStatusCode.Forbidden, DevOpsException.Refused)]
    [InlineData(HttpStatusCode.NotFound, DevOpsException.NotFound)]
    [InlineData(HttpStatusCode.Conflict, DevOpsException.Changed)]
    [InlineData(HttpStatusCode.PreconditionFailed, DevOpsException.Changed)]
    [InlineData(HttpStatusCode.Found, DevOpsException.Redirected)]
    [InlineData(HttpStatusCode.MovedPermanently, DevOpsException.Redirected)]
    [InlineData(HttpStatusCode.InternalServerError, "Azure DevOps answered HTTP 500")]
    public async Task FailingStatus_ThrowsFixedReason_AndNeverEchoesTheBody(HttpStatusCode status, string reason)
    {
        (AzureDevOpsClient client, FakeHandler handler) = Create();
        handler.Answer(status, "secret body " + Marker);
        handler.Answer(status, "secret body " + Marker);

        DevOpsException get = await Assert.ThrowsAsync<DevOpsException>(() => client.GetCompletedWorkAsync(42));
        DevOpsException set = await Assert.ThrowsAsync<DevOpsException>(() => client.SetCompletedWorkAsync(42, 1, 1));

        Assert.Equal(reason, get.Message);
        Assert.Equal(reason, set.Message);
        Assert.DoesNotContain("secret body", get.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Marker, get.ToString(), StringComparison.Ordinal);
    }

    // AC-10: a redirect is never followed.
    [Fact]
    public async Task Redirect_IsNotFollowed()
    {
        (AzureDevOpsClient client, FakeHandler handler) = Create();
        handler.Answer((_, _) =>
        {
            HttpResponseMessage response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("https://evil.example/steal");
            return Task.FromResult(response);
        });

        await Assert.ThrowsAsync<DevOpsException>(() => client.GetCompletedWorkAsync(42));

        Assert.Single(handler.Requests);
    }

    // AC-10
    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("{\"fields\":{}}")]
    [InlineData("{\"rev\":\"7\"}")]
    [InlineData("{\"rev\":7.5}")]
    [InlineData("{\"rev\":7,\"fields\":[]}")]
    [InlineData("{\"rev\":7,\"fields\":{\"Microsoft.VSTS.Scheduling.CompletedWork\":\"2\"}}")]
    [InlineData("{\"rev\":7,\"fields\":{\"Microsoft.VSTS.Scheduling.CompletedWork\":true}}")]
    public async Task UnexpectedAnswer_FailsCleanly(string body)
    {
        (AzureDevOpsClient client, FakeHandler handler) = Create();
        handler.Answer(HttpStatusCode.OK, body);

        DevOpsException ex = await Assert.ThrowsAsync<DevOpsException>(() => client.GetCompletedWorkAsync(42));

        Assert.Equal(DevOpsException.Unexpected, ex.Message);
    }

    // AC-10
    [Fact]
    public async Task OversizedBody_FailsCleanly()
    {
        (AzureDevOpsClient client, FakeHandler handler) = Create();
        string body = "{\"rev\":1,\"x\":\"" + new string('a', (int)AzureDevOpsClient.MaxBodyBytes) + "\"}";
        handler.Answer(HttpStatusCode.OK, body);

        DevOpsException ex = await Assert.ThrowsAsync<DevOpsException>(() => client.GetCompletedWorkAsync(42));

        Assert.Equal(DevOpsException.TooLarge, ex.Message);
    }

    // AC-10: no Content-Length, so only the capped read can catch it.
    [Fact]
    public async Task OversizedStreamedBody_FailsCleanly()
    {
        (AzureDevOpsClient client, FakeHandler handler) = Create();
        handler.Answer((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new UnknownLengthStream(AzureDevOpsClient.MaxBodyBytes + 10)),
        }));

        DevOpsException ex = await Assert.ThrowsAsync<DevOpsException>(() => client.GetCompletedWorkAsync(42));

        Assert.Equal(DevOpsException.TooLarge, ex.Message);
    }

    // AC-10
    [Fact]
    public async Task SlowAnswer_TimesOut()
    {
        (AzureDevOpsClient client, FakeHandler handler) = Create(TimeSpan.FromMilliseconds(50));
        handler.Answer(async (_, token) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        DevOpsException ex = await Assert.ThrowsAsync<DevOpsException>(() => client.GetCompletedWorkAsync(42));

        Assert.Equal(DevOpsException.TimedOut, ex.Message);
    }

    [Fact]
    public async Task NetworkFailure_FailsCleanly()
    {
        (AzureDevOpsClient client, FakeHandler handler) = Create();
        handler.Answer((_, _) => throw new HttpRequestException("connection refused " + Marker));

        DevOpsException ex = await Assert.ThrowsAsync<DevOpsException>(() => client.SetCompletedWorkAsync(42, 1, 1));

        Assert.Equal(DevOpsException.Unreachable, ex.Message);
        Assert.DoesNotContain(Marker, ex.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultTimeout_Is15Seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(15), AzureDevOpsClient.RequestTimeout);
        Assert.Equal(1024 * 1024, AzureDevOpsClient.MaxBodyBytes);
    }

    [Fact]
    public void PatchBody_IsValidJson()
    {
        using JsonDocument document = JsonDocument.Parse(AzureDevOpsClient.PatchBody(3, 0.1 + 0.2));

        Assert.Equal(2, document.RootElement.GetArrayLength());
    }

    /// <summary>A stream of 'a' bytes that reports no length.</summary>
    private sealed class UnknownLengthStream : Stream
    {
        private long left;

        public UnknownLengthStream(long length)
        {
            left = length;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = (int)Math.Min(count, left);
            Array.Fill(buffer, (byte)'a', offset, n);
            left -= n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
