using System.IO.Compression;
using System.Security.Cryptography;
using System.Xml;

namespace Times;

/// <summary>A verified package waiting in its own private folder until Times quits.</summary>
internal sealed record StagedUpdate
{
    public StagedUpdate(SemVersion version, string folder, string packagePath)
    {
        Version = version;
        Folder = folder;
        PackagePath = packagePath;
    }

    public SemVersion Version { get; }

    /// <summary>The staging folder (<c>--add-source</c>), deleted by the helper after the install.</summary>
    public string Folder { get; }

    public string PackagePath { get; }
}

/// <summary>
/// Downloads <c>Times.&lt;version&gt;.nupkg</c> and <c>Times.&lt;version&gt;.nupkg.sig</c> of a release into a new
/// private temp folder and keeps the package only if every check passes: both assets exist with exactly
/// those names, neither is over its cap, a reported <c>digest</c> matches the SHA-256, the signature
/// verifies with an embedded key, and the nuspec inside says id <c>Times</c> and exactly that version.
/// On any refusal the folder is deleted and <see cref="UpdateException"/> says why.
/// </summary>
internal sealed class UpdateDownloader
{
    /// <summary>The package is read up to 20 MiB.</summary>
    public const long MaxPackageBytes = 20 * 1024 * 1024;

    /// <summary>The signature is read up to 1 KiB (a DER P-256 signature is at most 72 bytes).</summary>
    public const long MaxSignatureBytes = 1024;

    /// <summary>The nuspec is read up to 64 KiB.</summary>
    private const long MaxNuspecBytes = 64 * 1024;

    /// <summary>What <see cref="UpdateException"/> says when this build has no key, so nothing can be verified.</summary>
    public const string NoKeyMessage = "this build has no signing key";

    private readonly IReleaseSource source;
    private readonly UpdateSignature signature;
    private readonly Func<DirectoryInfo> createFolder;

    /// <param name="createFolder">Makes the staging folder; <c>null</c> for <see cref="CreateStagingFolder"/>. Tests pass one under their temp folder.</param>
    public UpdateDownloader(IReleaseSource source, UpdateSignature signature, Func<DirectoryInfo>? createFolder = null)
    {
        this.source = source;
        this.signature = signature;
        this.createFolder = createFolder ?? CreateStagingFolder;
    }

    /// <summary>A new <c>times-update-*</c> folder under the temp folder (mode 0700 on Unix).</summary>
    public static DirectoryInfo CreateStagingFolder() => Directory.CreateTempSubdirectory("times-update-");

    public static string PackageName(SemVersion version) => $"Times.{version}.nupkg";

    public static string SignatureName(SemVersion version) => PackageName(version) + ".sig";

    public async Task<StagedUpdate> DownloadAsync(UpdateCandidate candidate, CancellationToken cancellation)
    {
        if (!signature.HasKeys)
        {
            throw new UpdateException(NoKeyMessage);
        }

        string packageName = PackageName(candidate.Version);
        ReleaseAsset? package = Single(candidate.Release, packageName);
        ReleaseAsset? sig = Single(candidate.Release, SignatureName(candidate.Version));
        DirectoryInfo folder = createFolder();
        try
        {
            if (package is null || sig is null)
            {
                throw new UpdateException($"the release has no {packageName} with its .sig");
            }

            byte[] packageBytes = await source.DownloadAsync(package.Url, MaxPackageBytes, cancellation).ConfigureAwait(false);
            byte[] signatureBytes = await source.DownloadAsync(sig.Url, MaxSignatureBytes, cancellation).ConfigureAwait(false);
            if (packageBytes.Length > MaxPackageBytes || signatureBytes.Length > MaxSignatureBytes)
            {
                throw new UpdateException("a download is too large");
            }

            if (package.Digest is not null && !DigestMatches(package.Digest, packageBytes))
            {
                throw new UpdateException("the package does not match its digest");
            }

            if (!signature.Verify(packageBytes, signatureBytes))
            {
                throw new UpdateException("the package signature is not valid");
            }

            CheckNuspec(packageBytes, candidate.Version);

            string path = Path.Combine(folder.FullName, packageName);
            using (FileStream file = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
            {
                file.Write(packageBytes);
            }

            return new StagedUpdate(candidate.Version, folder.FullName, path);
        }
        catch
        {
            DeleteFolder(folder.FullName);
            throw;
        }
    }

    /// <summary>Deletes a staging folder; a failure is ignored (it is only a temp folder).</summary>
    public static void DeleteFolder(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left in the temp folder.
        }
    }

    // Exactly one asset of that name (ordinal); two with the same name are refused too.
    private static ReleaseAsset? Single(ReleaseInfo release, string name)
    {
        List<ReleaseAsset> found = release.Assets.Where(a => string.Equals(a.Name, name, StringComparison.Ordinal)).ToList();
        return found.Count == 1 ? found[0] : null;
    }

    /// <summary>True if <paramref name="digest"/> is <c>sha256:&lt;hex&gt;</c> of <paramref name="bytes"/>; any other form is a mismatch.</summary>
    internal static bool DigestMatches(string digest, byte[] bytes)
    {
        const string Prefix = "sha256:";
        if (!digest.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        string expected = Convert.ToHexStringLower(SHA256.HashData(bytes));
        return string.Equals(digest[Prefix.Length..], expected, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Throws unless the zip holds exactly one <c>.nuspec</c> at its root whose metadata says id
    /// <c>Times</c> and exactly <paramref name="version"/>. Only called on a package whose signature verified.
    /// </summary>
    internal static void CheckNuspec(byte[] package, SemVersion version)
    {
        string? id;
        string? found;
        try
        {
            using MemoryStream memory = new MemoryStream(package, writable: false);
            using ZipArchive zip = new ZipArchive(memory, ZipArchiveMode.Read);
            List<ZipArchiveEntry> nuspecs = zip.Entries
                .Where(e => !e.FullName.Contains('/') && !e.FullName.Contains('\\')
                    && e.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (nuspecs.Count != 1 || nuspecs[0].Length > MaxNuspecBytes)
            {
                throw new UpdateException("the package has no readable nuspec");
            }

            byte[] bytes;
            using (Stream stream = nuspecs[0].Open())
            {
                bytes = CappedFile.ReadAtMost(stream, MaxNuspecBytes + 1);
            }

            if (bytes.Length > MaxNuspecBytes)
            {
                throw new UpdateException("the package has no readable nuspec");
            }

            (id, found) = ReadIdAndVersion(bytes);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or XmlException)
        {
            throw new UpdateException("the package has no readable nuspec");
        }

        if (!string.Equals(id, "Times", StringComparison.Ordinal) || !string.Equals(found, version.ToString(), StringComparison.Ordinal))
        {
            throw new UpdateException("the package is not Times " + version);
        }
    }

    // <package><metadata><id/><version/></metadata></package>, in any namespace. No DTDs, no external entities.
    private static (string? Id, string? Version) ReadIdAndVersion(byte[] nuspec)
    {
        XmlReaderSettings settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxNuspecBytes * 2,
        };
        using MemoryStream memory = new MemoryStream(nuspec, writable: false);
        using XmlReader reader = XmlReader.Create(memory, settings);
        string? id = null;
        string? version = null;
        string? section = null;
        bool more = reader.Read();
        while (more)
        {
            if (reader.NodeType == XmlNodeType.Element && reader.Depth == 1)
            {
                section = reader.LocalName;
            }

            if (reader.NodeType == XmlNodeType.Element && reader.Depth == 2 && section == "metadata"
                && reader.LocalName is "id" or "version")
            {
                bool isId = reader.LocalName == "id";
                string value = reader.ReadElementContentAsString().Trim();
                if (isId)
                {
                    id ??= value;
                }
                else
                {
                    version ??= value;
                }

                // ReadElementContentAsString already moved to the next node.
                more = !reader.EOF;
                continue;
            }

            more = reader.Read();
        }

        return (id, version);
    }
}
