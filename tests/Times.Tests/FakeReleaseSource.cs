using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Times.Tests;

/// <summary>A release list and asset bytes in memory, so no test touches the network.</summary>
internal sealed class FakeReleaseSource : IReleaseSource
{
    private readonly Dictionary<string, byte[]> files = new Dictionary<string, byte[]>(StringComparer.Ordinal);

    public FakeReleaseSource(IReadOnlyList<ReleaseInfo> releases)
    {
        Releases.AddRange(releases);
    }

    /// <summary>The list <see cref="ListAsync"/> returns.</summary>
    public List<ReleaseInfo> Releases { get; } = [];

    /// <summary>Thrown by <see cref="ListAsync"/> instead of returning the list, or <c>null</c>.</summary>
    public Exception? ListFailure { get; set; }

    /// <summary>Returned by <see cref="ListAsync"/> instead of a completed task, or <c>null</c>.</summary>
    public Task<IReadOnlyList<ReleaseInfo>>? ListTask { get; set; }

    public int ListCalls { get; private set; }

    public List<(string Url, long MaxBytes)> Downloads { get; } = [];

    public void Add(string url, byte[] bytes) => files[url] = bytes;

    public Task<IReadOnlyList<ReleaseInfo>> ListAsync(CancellationToken cancellation)
    {
        ListCalls++;
        if (ListTask is not null)
        {
            return ListTask;
        }

        if (ListFailure is not null)
        {
            throw ListFailure;
        }

        return Task.FromResult<IReadOnlyList<ReleaseInfo>>(Releases.ToArray());
    }

    public Task<byte[]> DownloadAsync(string url, long maxBytes, CancellationToken cancellation)
    {
        Downloads.Add((url, maxBytes));
        if (!files.TryGetValue(url, out byte[]? bytes))
        {
            throw new UpdateException("not found");
        }

        if (bytes.LongLength > maxBytes)
        {
            throw new UpdateException("the response is too large");
        }

        return Task.FromResult(bytes);
    }
}

/// <summary>A test key pair and signed packages built in memory. No test reads the real update-key.pem.</summary>
internal sealed class TestSigner : IDisposable
{
    public const string Prefix = "https://github.com/nothisisnotdaniel/times/releases/download/";

    private readonly ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public string PublicPem => key.ExportSubjectPublicKeyInfoPem();

    public string PrivatePem => key.ExportPkcs8PrivateKeyPem();

    public UpdateSignature Signature => UpdateSignature.FromPem(PublicPem);

    public byte[] Sign(byte[] data) => key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

    public void Dispose() => key.Dispose();

    /// <summary>A nupkg-like zip with a root <c>&lt;id&gt;.nuspec</c>.</summary>
    public static byte[] Package(string id, string version, string? nuspec = null)
    {
        using MemoryStream memory = new MemoryStream();
        using (ZipArchive zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            ZipArchiveEntry entry = zip.CreateEntry(id + ".nuspec");
            using (StreamWriter writer = new StreamWriter(entry.Open(), new UTF8Encoding(false)))
            {
                writer.Write(nuspec ?? Nuspec(id, version));
            }

            ZipArchiveEntry tool = zip.CreateEntry("tools/net10.0/any/Times.dll");
            using Stream stream = tool.Open();
            stream.Write([1, 2, 3, 4]);
        }

        return memory.ToArray();
    }

    public static string Nuspec(string id, string version) =>
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
        "<package xmlns=\"http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd\">\n" +
        $"  <metadata>\n    <id>{id}</id>\n    <version>{version}</version>\n    <authors>Daniel Legien</authors>\n  </metadata>\n" +
        "</package>\n";

    public static string Digest(byte[] bytes) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>
    /// A release <c>v&lt;version&gt;</c> with a signed <c>Times.&lt;version&gt;.nupkg</c> and its <c>.sig</c>,
    /// both added to <paramref name="source"/>.
    /// </summary>
    public ReleaseInfo SignedRelease(FakeReleaseSource source, string version, bool prerelease = false, bool digest = true)
    {
        byte[] package = Package("Times", version);
        string packageUrl = $"{Prefix}v{version}/Times.{version}.nupkg";
        source.Add(packageUrl, package);
        source.Add(packageUrl + ".sig", Sign(package));
        return new ReleaseInfo("v" + version, prerelease, false,
        [
            new ReleaseAsset($"Times.{version}.nupkg", packageUrl, digest ? Digest(package) : null),
            new ReleaseAsset($"Times.{version}.nupkg.sig", packageUrl + ".sig", null),
        ]);
    }
}

/// <summary>Records every helper call instead of starting a process.</summary>
internal sealed class FakeLauncher : IProcessLauncher
{
    public List<ProcessCall> Calls { get; } = [];

    /// <summary>Thrown by <see cref="Start"/>, or <c>null</c>.</summary>
    public Exception? Failure { get; set; }

    public void Start(ProcessCall call)
    {
        Calls.Add(call);
        if (Failure is not null)
        {
            throw Failure;
        }
    }
}
