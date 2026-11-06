using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Times;

/// <summary>
/// Why one Azure DevOps request failed. The message is one of a few fixed reasons, short and safe
/// to show (escape it): it never holds the token, a header or a response body.
/// </summary>
internal sealed class DevOpsException : Exception
{
    public const string Refused = "Azure DevOps refused the token (is it valid, with Work Items: Read & write?)";
    public const string NotFound = "the work item was not found, or the token cannot see it";
    public const string Changed = "the work item changed while syncing; run sync again";
    public const string Redirected = "Azure DevOps answered with a redirect (check the organization)";
    public const string TimedOut = "the request timed out";
    public const string Unreachable = "Azure DevOps cannot be reached (offline?)";
    public const string TooLarge = "the response is too large";
    public const string Unexpected = "Azure DevOps sent an unexpected answer";

    public DevOpsException(string message)
        : base(message)
    {
    }

    /// <summary>The reason for an HTTP status that is not a success.</summary>
    public static string ForStatus(int status) => status switch
    {
        401 or 403 => Refused,
        404 => NotFound,
        409 or 412 => Changed,
        >= 300 and < 400 => Redirected,
        _ => "Azure DevOps answered HTTP " + status.ToString(CultureInfo.InvariantCulture),
    };
}

/// <summary>Reads and writes the Completed Work field of Azure DevOps work items. Tests inject a fake.</summary>
internal interface IDevOpsClient
{
    /// <summary>
    /// The work item's Completed Work (<c>null</c> if the field is not set) and its revision. Throws
    /// <see cref="DevOpsException"/> on any failure.
    /// </summary>
    public Task<(double? Completed, int Rev)> GetCompletedWorkAsync(int id);

    /// <summary>
    /// Sets Completed Work to <paramref name="value"/>, only if the work item is still at
    /// <paramref name="rev"/>. Returns after a 2xx answer; throws <see cref="DevOpsException"/> otherwise.
    /// </summary>
    public Task SetCompletedWorkAsync(int id, int rev, double value);
}

/// <summary>
/// The Azure DevOps Services REST API (<c>https://dev.azure.com/&lt;org&gt;</c>, api-version 7.1) with
/// Basic auth from a PAT. Requests go only to that host over HTTPS, never follow a redirect, time out
/// after <see cref="RequestTimeout"/> each and read at most <see cref="MaxBodyBytes"/> of a body.
/// </summary>
internal sealed class AzureDevOpsClient : IDevOpsClient, IDisposable
{
    public const string Host = "https://dev.azure.com/";

    public const string CompletedWorkField = "Microsoft.VSTS.Scheduling.CompletedWork";

    /// <summary>Response bodies are read up to 1 MiB.</summary>
    public const long MaxBodyBytes = 1024 * 1024;

    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    private readonly HttpClient client;
    private readonly string organization;
    private readonly AuthenticationHeaderValue authorization;
    private readonly TimeSpan timeout;

    /// <param name="handler">The HTTP handler, or <c>null</c> for one that never follows redirects. Tests pass a fake.</param>
    /// <param name="timeout">Defaults to <see cref="RequestTimeout"/>; tests pass a short one.</param>
    public AzureDevOpsClient(DevOpsCredential credential, HttpMessageHandler? handler = null, TimeSpan? timeout = null)
    {
        client = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false }, disposeHandler: handler is null)
        {
            // Each request has its own timeout through a cancellation token.
            Timeout = Timeout.InfiniteTimeSpan,
        };
        organization = credential.Organization;
        byte[] raw = Encoding.ASCII.GetBytes(":" + credential.Pat);
        authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(raw));
        Array.Clear(raw);
        this.timeout = timeout ?? RequestTimeout;
    }

    public void Dispose() => client.Dispose();

    /// <summary>The work item's URL. The organisation was validated, so it cannot change the host or path.</summary>
    internal Uri WorkItemUri(int id) =>
        new(Host + organization + "/_apis/wit/workitems/" + id.ToString(CultureInfo.InvariantCulture) + "?api-version=7.1");

    public async Task<(double? Completed, int Rev)> GetCompletedWorkAsync(int id)
    {
        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, WorkItemUri(id));
        byte[] body = await SendAsync(request, readBody: true).ConfigureAwait(false);
        return ParseWorkItem(body);
    }

    public async Task SetCompletedWorkAsync(int id, int rev, double value)
    {
        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Patch, WorkItemUri(id));
        ByteArrayContent content = new ByteArrayContent(PatchBody(rev, value));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json-patch+json");
        request.Content = content;
        await SendAsync(request, readBody: false).ConfigureAwait(false);
    }

    /// <summary>The JSON patch: a <c>test</c> on <c>/rev</c>, then <c>add</c> on Completed Work.</summary>
    internal static byte[] PatchBody(int rev, double value)
    {
        using MemoryStream stream = new MemoryStream();
        using (Utf8JsonWriter writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            writer.WriteStartObject();
            writer.WriteString("op", "test");
            writer.WriteString("path", "/rev");
            writer.WriteNumber("value", rev);
            writer.WriteEndObject();
            writer.WriteStartObject();
            writer.WriteString("op", "add");
            writer.WriteString("path", "/fields/" + CompletedWorkField);
            writer.WriteNumber("value", value);
            writer.WriteEndObject();
            writer.WriteEndArray();
        }

        return stream.ToArray();
    }

    /// <summary>
    /// Completed Work (<c>null</c> when missing or JSON <c>null</c>) and <c>rev</c> from a work item.
    /// Anything else (not JSON, no whole-number <c>rev</c>, a field that is not a number) throws
    /// <see cref="DevOpsException"/>.
    /// </summary>
    internal static (double? Completed, int Rev) ParseWorkItem(byte[] body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 64 });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("rev", out JsonElement revElement)
                || revElement.ValueKind != JsonValueKind.Number
                || !revElement.TryGetInt32(out int rev))
            {
                throw new DevOpsException(DevOpsException.Unexpected);
            }

            double? completed = null;
            if (root.TryGetProperty("fields", out JsonElement fields))
            {
                if (fields.ValueKind != JsonValueKind.Object)
                {
                    throw new DevOpsException(DevOpsException.Unexpected);
                }

                if (fields.TryGetProperty(CompletedWorkField, out JsonElement field) && field.ValueKind != JsonValueKind.Null)
                {
                    if (field.ValueKind != JsonValueKind.Number || !field.TryGetDouble(out double value)
                        || !double.IsFinite(value))
                    {
                        throw new DevOpsException(DevOpsException.Unexpected);
                    }

                    completed = value;
                }
            }

            return (completed, rev);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            throw new DevOpsException(DevOpsException.Unexpected);
        }
    }

    private async Task<byte[]> SendAsync(HttpRequestMessage request, bool readBody)
    {
        request.Headers.Authorization = authorization;
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("Times");
        using CancellationTokenSource timer = new CancellationTokenSource(timeout);
        try
        {
            using HttpResponseMessage response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timer.Token).ConfigureAwait(false);
            int status = (int)response.StatusCode;
            if (status < 200 || status > 299)
            {
                throw new DevOpsException(DevOpsException.ForStatus(status));
            }

            if (!readBody)
            {
                return [];
            }

            if (response.Content.Headers.ContentLength is long length && length > MaxBodyBytes)
            {
                throw new DevOpsException(DevOpsException.TooLarge);
            }

            using Stream stream = await response.Content.ReadAsStreamAsync(timer.Token).ConfigureAwait(false);
            return await GitHubReleaseSource.ReadCappedAsync(stream, MaxBodyBytes, timer.Token).ConfigureAwait(false);
        }
        catch (UpdateException)
        {
            // ReadCappedAsync's only failure: one byte over the cap.
            throw new DevOpsException(DevOpsException.TooLarge);
        }
        catch (OperationCanceledException)
        {
            throw new DevOpsException(DevOpsException.TimedOut);
        }
        catch (HttpRequestException)
        {
            throw new DevOpsException(DevOpsException.Unreachable);
        }
        catch (IOException)
        {
            throw new DevOpsException(DevOpsException.Unreachable);
        }
    }
}
