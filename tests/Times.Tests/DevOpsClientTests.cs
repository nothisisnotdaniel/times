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

    // Job 105 ------------------------------------------------------------------------------

    private const string WiqlUrl = "https://dev.azure.com/contoso/_apis/wit/wiql?$top=200&api-version=7.1";

    private static string TitlesUrl(string ids) =>
        "https://dev.azure.com/contoso/_apis/wit/workitems?ids=" + ids + "&fields=System.Title&errorPolicy=omit&api-version=7.1";

    private static string WiqlAnswer(params int[] ids) =>
        "{\"queryType\":\"flat\",\"workItems\":[" + string.Join(",", ids.Select(id => "{\"id\":" + id + ",\"url\":\"x\"}")) + "]}";

    // AC-1
    [Fact]
    public async Task Assigned_PostsTheWiql_ThenGetsTheTitlesForThoseIds()
    {
        (AzureDevOpsClient client, FakeHandler handler) = Create();
        handler.Answer(HttpStatusCode.OK, WiqlAnswer(7, 3, 12));
        handler.Answer(HttpStatusCode.OK,
            "{\"count\":3,\"value\":[{\"id\":7,\"fields\":{\"System.Title\":\"Seven\"}},{\"id\":3,\"fields\":{\"System.Title\":\"Three\"}},"
            + "{\"id\":12,\"fields\":{\"System.Title\":\"Twelve\"}}]}");

        IReadOnlyList<(int Id, string? Title)> items = await client.GetAssignedWorkItemsAsync();

        Assert.Equal([(7, "Seven"), (3, "Three"), (12, "Twelve")], items);
        Assert.Equal(2, handler.Requests.Count);
        (HttpMethod method, Uri? uri, string? authorization, string? contentType, string body) = handler.Requests[0];
        Assert.Equal(HttpMethod.Post, method);
        Assert.Equal(WiqlUrl, uri!.AbsoluteUri);
        Assert.Equal(Basic, authorization);
        Assert.Equal("application/json", contentType);
        using (JsonDocument document = JsonDocument.Parse(body))
        {
            Assert.Equal(AzureDevOpsClient.Wiql, document.RootElement.GetProperty("query").GetString());
            Assert.Single(document.RootElement.EnumerateObject());
        }

        Assert.Equal(
            "SELECT [System.Id] FROM WorkItems WHERE [System.AssignedTo] = @Me AND [System.State] NOT IN ('Closed', 'Done', 'Removed') ORDER BY [System.Id]",
            AzureDevOpsClient.Wiql);
        (HttpMethod getMethod, Uri? getUri, string? getAuthorization, _, string getBody) = handler.Requests[1];
        Assert.Equal(HttpMethod.Get, getMethod);
        Assert.Equal(TitlesUrl("7,3,12"), getUri!.AbsoluteUri);
        Assert.Equal(Basic, getAuthorization);
        Assert.Equal(string.Empty, getBody);
    }

    // AC-1
    [Fact]
    public async Task Assigned_NoIds_SendsNoSecondRequest()
    {
        (AzureDevOpsClient client, FakeHandler handler) = Create();
        handler.Answer(HttpStatusCode.OK, WiqlAnswer());

        IReadOnlyList<(int Id, string? Title)> items = await client.GetAssignedWorkItemsAsync();

        Assert.Empty(items);
        Assert.Single(handler.Requests);
    }

    // AC-1: at most the first 200 ids, in order.
    [Fact]
    public async Task Assigned_AsksForAtMostTheFirst200Ids()
    {
        (AzureDevOpsClient client, FakeHandler handler) = Create();
        int[] ids = Enumerable.Range(1, 250).ToArray();
        handler.Answer(HttpStatusCode.OK, WiqlAnswer(ids));
        handler.Answer(HttpStatusCode.OK, "{\"value\":[]}");

        await client.GetAssignedWorkItemsAsync();

        Assert.Equal(TitlesUrl(string.Join(",", ids.Take(200))), handler.Requests[1].Uri!.AbsoluteUri);
    }

    // AC-2
    [Fact]
    public void ParseWiqlIds_ReadsIdsInOrder_EachOnce()
    {
        Assert.Equal([5, 2, 9], AzureDevOpsClient.ParseWiqlIds(Encoding.UTF8.GetBytes(WiqlAnswer(5, 2, 5, 9, 2))));
        Assert.Empty(AzureDevOpsClient.ParseWiqlIds(Encoding.UTF8.GetBytes("{\"workItems\":[]}")));
    }

    // AC-2
    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"workItems\":{}}")]
    [InlineData("{\"workItems\":[1]}")]
    [InlineData("{\"workItems\":[null]}")]
    [InlineData("{\"workItems\":[{}]}")]
    [InlineData("{\"workItems\":[{\"id\":\"7\"}]}")]
    [InlineData("{\"workItems\":[{\"id\":7.5}]}")]
    [InlineData("{\"workItems\":[{\"id\":0}]}")]
    [InlineData("{\"workItems\":[{\"id\":-3}]}")]
    [InlineData("{\"workItems\":[{\"id\":2147483648}]}")]
    [InlineData("{\"workItems\":[{\"id\":1e3}]}")]
    public void ParseWiqlIds_Malformed_ThrowsUnexpected(string body)
    {
        DevOpsException ex = Assert.Throws<DevOpsException>(() => AzureDevOpsClient.ParseWiqlIds(Encoding.UTF8.GetBytes(body)));

        Assert.Equal(DevOpsException.Unexpected, ex.Message);
    }

    // AC-3
    [Fact]
    public void ParseTitles_ReadsEntries_SkipsNulls_AndGivesNullForMissingTitles()
    {
        string body = "{\"value\":[{\"id\":1,\"fields\":{\"System.Title\":\"One\"}},null,{\"id\":2,\"fields\":{}},{\"id\":3},"
            + "{\"id\":4,\"fields\":{\"System.Title\":42}},{\"id\":5,\"fields\":{\"System.Title\":null}},"
            + "{\"id\":6,\"fields\":{\"System.Title\":\"bad \\ud800 half\"}}]}";

        IReadOnlyList<(int Id, string? Title)> items = AzureDevOpsClient.ParseTitles(Encoding.UTF8.GetBytes(body));

        Assert.Equal([(1, "One"), (2, null), (3, null), (4, null), (5, null), (6, null)], items);
    }

    // AC-3
    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"value\":{}}")]
    [InlineData("{\"value\":[7]}")]
    [InlineData("{\"value\":[{\"fields\":{}}]}")]
    [InlineData("{\"value\":[{\"id\":-1}]}")]
    [InlineData("{\"value\":[{\"id\":1,\"fields\":[]}]}")]
    public void ParseTitles_Malformed_ThrowsUnexpected(string body)
    {
        DevOpsException ex = Assert.Throws<DevOpsException>(() => AzureDevOpsClient.ParseTitles(Encoding.UTF8.GetBytes(body)));

        Assert.Equal(DevOpsException.Unexpected, ex.Message);
    }

    // AC-3: entries that were not asked for are dropped, and each work item comes back once.
    [Fact]
    public async Task Assigned_DropsUnrequestedAndRepeatedEntries()
    {
        (AzureDevOpsClient client, FakeHandler handler) = Create();
        handler.Answer(HttpStatusCode.OK, WiqlAnswer(1, 2));
        handler.Answer(HttpStatusCode.OK,
            "{\"value\":[{\"id\":1,\"fields\":{\"System.Title\":\"One\"}},{\"id\":99,\"fields\":{\"System.Title\":\"Sneaky\"}},"
            + "{\"id\":1,\"fields\":{\"System.Title\":\"Again\"}},null]}");

        IReadOnlyList<(int Id, string? Title)> items = await client.GetAssignedWorkItemsAsync();

        Assert.Equal([(1, "One")], items);
    }

    // AC-4: either request failing gives the fixed sync reasons, never the token or the body.
    [Theory]
    [InlineData(0, HttpStatusCode.Unauthorized, DevOpsException.Refused)]
    [InlineData(0, HttpStatusCode.Found, DevOpsException.Redirected)]
    [InlineData(0, HttpStatusCode.InternalServerError, "Azure DevOps answered HTTP 500")]
    [InlineData(1, HttpStatusCode.Forbidden, DevOpsException.Refused)]
    [InlineData(1, HttpStatusCode.MovedPermanently, DevOpsException.Redirected)]
    [InlineData(1, HttpStatusCode.BadGateway, "Azure DevOps answered HTTP 502")]
    public async Task Assigned_FailingStatus_ThrowsFixedReason(int failingRequest, HttpStatusCode status, string reason)
    {
        (AzureDevOpsClient client, FakeHandler handler) = Create();
        if (failingRequest == 1)
        {
            handler.Answer(HttpStatusCode.OK, WiqlAnswer(4));
        }

        handler.Answer((_, _) =>
        {
            HttpResponseMessage response = new HttpResponseMessage(status) { Content = new StringContent("secret body " + Marker) };
            response.Headers.Location = new Uri("https://evil.example/steal");
            return Task.FromResult(response);
        });

        DevOpsException ex = await Assert.ThrowsAsync<DevOpsException>(() => client.GetAssignedWorkItemsAsync());

        Assert.Equal(reason, ex.Message);
        Assert.Equal(failingRequest + 1, handler.Requests.Count);
        Assert.DoesNotContain(Marker, ex.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("secret body", ex.ToString(), StringComparison.Ordinal);
    }

    // AC-4. Only the slow answer uses a short timeout; the other cases run with the default
    // RequestTimeout, so a slow runner cannot turn them into TimedOut.
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Assigned_TimeoutOversizeAndNetworkFailures_FailCleanly(int failingRequest)
    {
        TimeSpan shortTimeout = TimeSpan.FromMilliseconds(50);
        TimeSpan defaultTimeout = AzureDevOpsClient.RequestTimeout;
        foreach ((Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer, TimeSpan timeout, string reason) in new (Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>, TimeSpan, string)[]
        {
            (async (_, token) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30), token);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }, shortTimeout, DevOpsException.TimedOut),
            ((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new UnknownLengthStream(AzureDevOpsClient.MaxBodyBytes + 10)),
            }), defaultTimeout, DevOpsException.TooLarge),
            ((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(new string('a', (int)AzureDevOpsClient.MaxBodyBytes + 1)),
            }), defaultTimeout, DevOpsException.TooLarge),
            ((_, _) => throw new HttpRequestException("connection refused " + Marker), defaultTimeout, DevOpsException.Unreachable),
        })
        {
            (AzureDevOpsClient client, FakeHandler handler) = Create(timeout);
            if (failingRequest == 1)
            {
                handler.Answer(HttpStatusCode.OK, WiqlAnswer(4));
            }

            handler.Answer(answer);

            DevOpsException ex = await Assert.ThrowsAsync<DevOpsException>(() => client.GetAssignedWorkItemsAsync());

            Assert.Equal(reason, ex.Message);
            Assert.DoesNotContain(Marker, ex.ToString(), StringComparison.Ordinal);
            Assert.Equal(failingRequest + 1, handler.Requests.Count);
        }
    }

    // AC-4: a malformed answer to either request.
    [Fact]
    public async Task Assigned_UnexpectedAnswers_FailCleanly()
    {
        (AzureDevOpsClient client, FakeHandler handler) = Create();
        handler.Answer(HttpStatusCode.OK, "{\"workItems\":[{\"id\":\"1 OR 1=1\"}]}");
        DevOpsException first = await Assert.ThrowsAsync<DevOpsException>(() => client.GetAssignedWorkItemsAsync());

        handler.Answer(HttpStatusCode.OK, WiqlAnswer(4));
        handler.Answer(HttpStatusCode.OK, "<html>");
        DevOpsException second = await Assert.ThrowsAsync<DevOpsException>(() => client.GetAssignedWorkItemsAsync());

        Assert.Equal(DevOpsException.Unexpected, first.Message);
        Assert.Equal(DevOpsException.Unexpected, second.Message);
        Assert.Equal(3, handler.Requests.Count);
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
