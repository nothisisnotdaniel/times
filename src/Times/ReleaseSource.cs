using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Times;

/// <summary>Why an update check, download or staging step stopped. The message is short and safe to show (escape it).</summary>
internal sealed class UpdateException : Exception
{
    public UpdateException(string message)
        : base(message)
    {
    }
}

/// <summary>One file attached to a release.</summary>
internal sealed record ReleaseAsset
{
    public ReleaseAsset(string name, string url, string? digest)
    {
        Name = name;
        Url = url;
        Digest = digest;
    }

    public string Name { get; }

    /// <summary>The <c>browser_download_url</c>.</summary>
    public string Url { get; }

    /// <summary>The <c>digest</c> GitHub reports (<c>sha256:&lt;hex&gt;</c>), or <c>null</c> if it has none.</summary>
    public string? Digest { get; }
}

/// <summary>One entry of the release list, as GitHub reports it (nothing is trusted yet).</summary>
internal sealed record ReleaseInfo
{
    public ReleaseInfo(string tag, bool prerelease, bool draft, IReadOnlyList<ReleaseAsset> assets)
    {
        Tag = tag;
        Prerelease = prerelease;
        Draft = draft;
        Assets = assets;
    }

    public string Tag { get; }

    public bool Prerelease { get; }

    public bool Draft { get; }

    public IReadOnlyList<ReleaseAsset> Assets { get; }
}

/// <summary>Where releases come from. Tests inject a fake, so no test touches the network.</summary>
internal interface IReleaseSource
{
    /// <summary>The releases, in whatever order. Throws <see cref="UpdateException"/> on any failure.</summary>
    public Task<IReadOnlyList<ReleaseInfo>> ListAsync(CancellationToken cancellation);

    /// <summary>
    /// The bytes at <paramref name="url"/>, at most <paramref name="maxBytes"/>. Throws
    /// <see cref="UpdateException"/> on any failure, a refused URL or a body over the cap.
    /// </summary>
    public Task<byte[]> DownloadAsync(string url, long maxBytes, CancellationToken cancellation);
}

/// <summary>
/// The GitHub releases of <c>nothisisnotdaniel/times</c>. It requests only the release list
/// (<see cref="ListUrl"/>, never <c>/releases/latest</c>, which is 404 while no proper release exists) and
/// asset URLs under <see cref="DownloadPrefix"/>. Every request sends a <c>User-Agent</c>, has a timeout
/// and reads at most its cap; a longer body is cut off and refused. Nothing here is a trust anchor: the
/// signature check in <see cref="UpdateDownloader"/> is.
/// </summary>
internal sealed class GitHubReleaseSource : IReleaseSource
{
    public const string ListUrl = "https://api.github.com/repos/nothisisnotdaniel/times/releases?per_page=100";

    public const string DownloadPrefix = "https://github.com/nothisisnotdaniel/times/releases/download/";

    /// <summary>The release list is read up to 4 MiB.</summary>
    public const long MaxListBytes = 4 * 1024 * 1024;

    public static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(15);

    public static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(120);

    private readonly HttpClient client;
    private readonly TimeSpan listTimeout;
    private readonly TimeSpan downloadTimeout;
    private readonly string userAgent;

    /// <param name="handler">The HTTP handler, or <c>null</c> for the default one. Tests pass a fake.</param>
    /// <param name="listTimeout">Defaults to <see cref="ListTimeout"/>; tests pass a short one.</param>
    /// <param name="downloadTimeout">Defaults to <see cref="DownloadTimeout"/>; tests pass a short one.</param>
    public GitHubReleaseSource(string version, HttpMessageHandler? handler = null, TimeSpan? listTimeout = null, TimeSpan? downloadTimeout = null)
    {
        client = new HttpClient(handler ?? new SocketsHttpHandler(), disposeHandler: handler is null)
        {
            // Each request has its own timeout through a cancellation token.
            Timeout = Timeout.InfiniteTimeSpan,
        };
        this.listTimeout = listTimeout ?? ListTimeout;
        this.downloadTimeout = downloadTimeout ?? DownloadTimeout;
        userAgent = "Times/" + (SemVersion.TryParse(version, out SemVersion parsed) ? parsed.ToString() : "unknown");
    }

    public async Task<IReadOnlyList<ReleaseInfo>> ListAsync(CancellationToken cancellation)
    {
        byte[] body = await GetAsync(new Uri(ListUrl), MaxListBytes, listTimeout, "application/vnd.github+json", cancellation).ConfigureAwait(false);
        return ParseList(body);
    }

    public Task<byte[]> DownloadAsync(string url, long maxBytes, CancellationToken cancellation)
    {
        if (!IsAllowedAssetUrl(url, out Uri? uri))
        {
            throw new UpdateException("the release points to a download outside the Times repository");
        }

        return GetAsync(uri, maxBytes, downloadTimeout, "application/octet-stream", cancellation);
    }

    /// <summary>
    /// True if <paramref name="url"/> is an absolute https URL that, once normalised (so <c>..</c> cannot
    /// climb out), starts with <see cref="DownloadPrefix"/>.
    /// </summary>
    internal static bool IsAllowedAssetUrl(string url, out Uri uri)
    {
        uri = null!;
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed) || parsed.Scheme != Uri.UriSchemeHttps
            || parsed.UserInfo.Length > 0 || !parsed.IsDefaultPort)
        {
            return false;
        }

        if (!parsed.AbsoluteUri.StartsWith(DownloadPrefix, StringComparison.Ordinal) || parsed.AbsoluteUri.Length == DownloadPrefix.Length)
        {
            return false;
        }

        uri = parsed;
        return true;
    }

    private async Task<byte[]> GetAsync(Uri uri, long maxBytes, TimeSpan timeout, string accept, CancellationToken cancellation)
    {
        using CancellationTokenSource timer = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timer.CancelAfter(timeout);
        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd(userAgent);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        try
        {
            using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timer.Token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            {
                throw new UpdateException("GitHub's rate limit was reached");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new UpdateException($"GitHub answered HTTP {((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            }

            if (response.Content.Headers.ContentLength is long length && length > maxBytes)
            {
                throw new UpdateException("the response is too large");
            }

            using Stream stream = await response.Content.ReadAsStreamAsync(timer.Token).ConfigureAwait(false);
            return await ReadCappedAsync(stream, maxBytes, timer.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            throw new UpdateException("the request timed out");
        }
        catch (HttpRequestException)
        {
            throw new UpdateException("GitHub cannot be reached (offline?)");
        }
        catch (IOException)
        {
            throw new UpdateException("the connection was interrupted");
        }
    }

    /// <summary>Reads at most <paramref name="maxBytes"/>; one byte more means the body is too large and is refused.</summary>
    internal static async Task<byte[]> ReadCappedAsync(Stream stream, long maxBytes, CancellationToken cancellation)
    {
        using MemoryStream result = new MemoryStream();
        byte[] buffer = new byte[81920];
        while (true)
        {
            int wanted = (int)Math.Min(buffer.Length, maxBytes + 1 - result.Length);
            int read = await stream.ReadAsync(buffer.AsMemory(0, wanted), cancellation).ConfigureAwait(false);
            if (read == 0)
            {
                return result.ToArray();
            }

            result.Write(buffer, 0, read);
            if (result.Length > maxBytes)
            {
                throw new UpdateException("the response is too large");
            }
        }
    }

    /// <summary>
    /// The release list from GitHub's JSON. A root that is not an array, or JSON that does not parse,
    /// throws <see cref="UpdateException"/>; an entry with missing or wrongly typed fields is skipped.
    /// </summary>
    internal static IReadOnlyList<ReleaseInfo> ParseList(byte[] body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 16 });
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new UpdateException("GitHub sent an unexpected answer");
            }

            List<ReleaseInfo> releases = [];
            foreach (JsonElement item in document.RootElement.EnumerateArray())
            {
                if (TryRelease(item, out ReleaseInfo release))
                {
                    releases.Add(release);
                }
            }

            return releases;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            throw new UpdateException("GitHub sent an unreadable answer");
        }
    }

    private static bool TryRelease(JsonElement item, out ReleaseInfo release)
    {
        release = null!;
        if (item.ValueKind != JsonValueKind.Object
            || !TryString(item, "tag_name", out string tag)
            || !TryBool(item, "prerelease", out bool prerelease)
            || !TryBool(item, "draft", out bool draft))
        {
            return false;
        }

        List<ReleaseAsset> assets = [];
        if (item.TryGetProperty("assets", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement asset in list.EnumerateArray())
            {
                if (asset.ValueKind == JsonValueKind.Object
                    && TryString(asset, "name", out string name)
                    && TryString(asset, "browser_download_url", out string url))
                {
                    string? digest = TryString(asset, "digest", out string d) ? d : null;
                    assets.Add(new ReleaseAsset(name, url, digest));
                }
            }
        }

        release = new ReleaseInfo(tag, prerelease, draft, assets);
        return true;
    }

    private static bool TryString(JsonElement item, string name, out string value)
    {
        value = string.Empty;
        if (!item.TryGetProperty(name, out JsonElement element) || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString() ?? string.Empty;
        return true;
    }

    private static bool TryBool(JsonElement item, string name, out bool value)
    {
        value = false;
        if (!item.TryGetProperty(name, out JsonElement element) || element.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        value = element.GetBoolean();
        return true;
    }
}
