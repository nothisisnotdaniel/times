namespace Times.Tests;

public sealed class UpdateDownloaderTests : IDisposable
{
    private readonly TempFolder temp = new();
    private readonly TestSigner signer = new();
    private readonly List<string> created = [];

    public void Dispose()
    {
        signer.Dispose();
        temp.Dispose();
    }

    private DirectoryInfo CreateFolder()
    {
        DirectoryInfo folder = Directory.CreateDirectory(Path.Combine(temp.Path, "times-update-" + created.Count));
        created.Add(folder.FullName);
        return folder;
    }

    private UpdateDownloader Downloader(FakeReleaseSource source, UpdateSignature? signature = null) =>
        new(source, signature ?? signer.Signature, CreateFolder);

    private static UpdateCandidate Candidate(ReleaseInfo release)
    {
        Assert.True(SemVersion.TryParseTag(release.Tag, out SemVersion version));
        return new UpdateCandidate(version, release);
    }

    private void AssertNoFolderLeft()
    {
        Assert.All(created, folder => Assert.False(Directory.Exists(folder), folder + " was left behind"));
    }

    // AC-5: a signed package with a matching digest and nuspec is staged in the new folder.
    [Theory]
    [InlineData("1.2.0", true)]
    [InlineData("1.2.0-develop.7", false)]
    public async Task DownloadAsync_ValidPackage_IsStaged(string version, bool digest)
    {
        FakeReleaseSource source = new FakeReleaseSource([]);
        ReleaseInfo release = signer.SignedRelease(source, version, digest: digest);

        StagedUpdate staged = await Downloader(source).DownloadAsync(Candidate(release), CancellationToken.None);

        Assert.Equal(version, staged.Version.ToString());
        Assert.Equal(Assert.Single(created), staged.Folder);
        Assert.Equal(Path.Combine(staged.Folder, $"Times.{version}.nupkg"), staged.PackagePath);
        Assert.Equal([$"Times.{version}.nupkg"], Directory.GetFiles(staged.Folder).Select(Path.GetFileName));
        Assert.Equal(
            [(release.Assets[0].Url, UpdateDownloader.MaxPackageBytes), (release.Assets[1].Url, UpdateDownloader.MaxSignatureBytes)],
            source.Downloads);
    }

    // AC-5: a flipped byte in the package or the signature is refused, and the folder is deleted.
    [Theory]
    [InlineData("package")]
    [InlineData("signature")]
    public async Task DownloadAsync_FlippedByte_IsRefused(string which)
    {
        FakeReleaseSource source = new FakeReleaseSource([]);
        ReleaseInfo release = signer.SignedRelease(source, "1.2.0", digest: false);
        string url = which == "package" ? release.Assets[0].Url : release.Assets[1].Url;
        byte[] bytes = (await source.DownloadAsync(url, long.MaxValue, CancellationToken.None)).ToArray();
        bytes[bytes.Length / 2] ^= 0x01;
        source.Add(url, bytes);

        UpdateException ex = await Assert.ThrowsAsync<UpdateException>(() => Downloader(source).DownloadAsync(Candidate(release), CancellationToken.None));

        Assert.Contains("signature", ex.Message, StringComparison.Ordinal);
        AssertNoFolderLeft();
    }

    // AC-5: a digest that does not match (or is not sha256:) is refused, even with a valid signature.
    [Theory]
    [InlineData("sha256:0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("sha512:abc")]
    [InlineData("")]
    public async Task DownloadAsync_WrongDigest_IsRefused(string digest)
    {
        FakeReleaseSource source = new FakeReleaseSource([]);
        ReleaseInfo signed = signer.SignedRelease(source, "1.2.0");
        ReleaseInfo release = new ReleaseInfo(signed.Tag, false, false,
            [new ReleaseAsset(signed.Assets[0].Name, signed.Assets[0].Url, digest), signed.Assets[1]]);

        UpdateException ex = await Assert.ThrowsAsync<UpdateException>(() => Downloader(source).DownloadAsync(Candidate(release), CancellationToken.None));

        Assert.Contains("digest", ex.Message, StringComparison.Ordinal);
        AssertNoFolderLeft();
    }

    // AC-5: the digest compares with any hex case.
    [Fact]
    public void DigestMatches_IgnoresHexCase()
    {
        byte[] bytes = [9, 8, 7];

        Assert.True(UpdateDownloader.DigestMatches(TestSigner.Digest(bytes), bytes));
        Assert.True(UpdateDownloader.DigestMatches("sha256:" + TestSigner.Digest(bytes)[7..].ToUpperInvariant(), bytes));
        Assert.False(UpdateDownloader.DigestMatches("SHA256:" + TestSigner.Digest(bytes)[7..], bytes));
    }

    // AC-5: a signed package whose nuspec says another id or version is refused.
    [Theory]
    [InlineData("Times", "1.2.1")]
    [InlineData("Times", "1.2.0-develop.1")]
    [InlineData("Times", "1.2.0.0")]
    [InlineData("Evil", "1.2.0")]
    [InlineData("times", "1.2.0")]
    public async Task DownloadAsync_WrongNuspec_IsRefused(string id, string version)
    {
        FakeReleaseSource source = new FakeReleaseSource([]);
        ReleaseInfo release = SignedWith(source, TestSigner.Package(id, version), "1.2.0");

        UpdateException ex = await Assert.ThrowsAsync<UpdateException>(() => Downloader(source).DownloadAsync(Candidate(release), CancellationToken.None));

        Assert.Contains("not Times 1.2.0", ex.Message, StringComparison.Ordinal);
        AssertNoFolderLeft();
    }

    // AC-5: a signed file that is not a zip, or has no nuspec, or one with a DTD, is refused.
    [Theory]
    [InlineData("not a zip")]
    [InlineData("dtd")]
    [InlineData("nested")]
    public async Task DownloadAsync_UnreadableNuspec_IsRefused(string kind)
    {
        FakeReleaseSource source = new FakeReleaseSource([]);
        byte[] package = kind switch
        {
            "not a zip" => [1, 2, 3],
            "dtd" => TestSigner.Package("Times", "1.2.0",
                "<?xml version=\"1.0\"?><!DOCTYPE package [<!ENTITY v \"1.2.0\">]><package><metadata><id>Times</id><version>&v;</version></metadata></package>"),
            _ => NestedNuspec(),
        };
        ReleaseInfo release = SignedWith(source, package, "1.2.0");

        await Assert.ThrowsAsync<UpdateException>(() => Downloader(source).DownloadAsync(Candidate(release), CancellationToken.None));

        AssertNoFolderLeft();
    }

    // AC-5: either asset missing (or not named exactly) refuses before any download.
    [Theory]
    [InlineData("Times.1.2.0.nupkg", null)]
    [InlineData(null, "Times.1.2.0.nupkg.sig")]
    [InlineData("times.1.2.0.nupkg", "times.1.2.0.nupkg.sig")]
    [InlineData("Times.1.2.0.nupkg.zip", "Times.1.2.0.nupkg.sig")]
    [InlineData("Times.v1.2.0.nupkg", "Times.v1.2.0.nupkg.sig")]
    public async Task DownloadAsync_MissingAsset_IsRefused(string? package, string? sig)
    {
        FakeReleaseSource source = new FakeReleaseSource([]);
        ReleaseInfo signed = signer.SignedRelease(source, "1.2.0");
        List<ReleaseAsset> assets = [];
        if (package is not null)
        {
            assets.Add(new ReleaseAsset(package, signed.Assets[0].Url, null));
        }

        if (sig is not null)
        {
            assets.Add(new ReleaseAsset(sig, signed.Assets[1].Url, null));
        }

        ReleaseInfo release = new ReleaseInfo(signed.Tag, false, false, assets);

        UpdateException ex = await Assert.ThrowsAsync<UpdateException>(() => Downloader(source).DownloadAsync(Candidate(release), CancellationToken.None));

        Assert.Contains("Times.1.2.0.nupkg", ex.Message, StringComparison.Ordinal);
        Assert.Empty(source.Downloads);
        AssertNoFolderLeft();
    }

    // AC-5: a duplicated asset name is refused too.
    [Fact]
    public async Task DownloadAsync_DuplicateAsset_IsRefused()
    {
        FakeReleaseSource source = new FakeReleaseSource([]);
        ReleaseInfo signed = signer.SignedRelease(source, "1.2.0");
        ReleaseInfo release = new ReleaseInfo(signed.Tag, false, false, [signed.Assets[0], signed.Assets[0], signed.Assets[1]]);

        await Assert.ThrowsAsync<UpdateException>(() => Downloader(source).DownloadAsync(Candidate(release), CancellationToken.None));

        AssertNoFolderLeft();
    }

    // AC-5: the caps passed to the source refuse an oversized package or signature, and the folder is deleted.
    [Theory]
    [InlineData("package")]
    [InlineData("signature")]
    public async Task DownloadAsync_OverCap_IsRefused(string which)
    {
        FakeReleaseSource source = new FakeReleaseSource([]);
        ReleaseInfo release = signer.SignedRelease(source, "1.2.0");
        long size = which == "package" ? UpdateDownloader.MaxPackageBytes + 1 : UpdateDownloader.MaxSignatureBytes + 1;
        source.Add(which == "package" ? release.Assets[0].Url : release.Assets[1].Url, new byte[size]);

        UpdateException ex = await Assert.ThrowsAsync<UpdateException>(() => Downloader(source).DownloadAsync(Candidate(release), CancellationToken.None));

        Assert.Contains("too large", ex.Message, StringComparison.Ordinal);
        AssertNoFolderLeft();
    }

    // AC-5: a signature by another key is refused.
    [Fact]
    public async Task DownloadAsync_OtherKey_IsRefused()
    {
        FakeReleaseSource source = new FakeReleaseSource([]);
        ReleaseInfo release = signer.SignedRelease(source, "1.2.0");
        using TestSigner other = new TestSigner();

        await Assert.ThrowsAsync<UpdateException>(() => Downloader(source, other.Signature).DownloadAsync(Candidate(release), CancellationToken.None));

        AssertNoFolderLeft();
    }

    // AC-13: with no key nothing is downloaded or staged.
    [Fact]
    public async Task DownloadAsync_NoKey_StagesNothing()
    {
        FakeReleaseSource source = new FakeReleaseSource([]);
        ReleaseInfo release = signer.SignedRelease(source, "1.2.0");

        UpdateException ex = await Assert.ThrowsAsync<UpdateException>(() =>
            Downloader(source, UpdateSignature.None).DownloadAsync(Candidate(release), CancellationToken.None));

        Assert.Equal("this build has no signing key", ex.Message);
        Assert.Empty(source.Downloads);
        Assert.Empty(created);
    }

    // AC-5: a failed download deletes the folder too.
    [Fact]
    public async Task DownloadAsync_DownloadFails_DeletesTheFolder()
    {
        FakeReleaseSource source = new FakeReleaseSource([]);
        ReleaseInfo signed = signer.SignedRelease(source, "1.2.0");
        ReleaseInfo release = new ReleaseInfo(signed.Tag, false, false,
            [signed.Assets[0], new ReleaseAsset(signed.Assets[1].Name, TestSigner.Prefix + "missing", null)]);

        await Assert.ThrowsAsync<UpdateException>(() => Downloader(source).DownloadAsync(Candidate(release), CancellationToken.None));

        AssertNoFolderLeft();
    }

    // AC-5: the default staging folder is a new times-update-* folder, private on Unix.
    [Fact]
    public void CreateStagingFolder_IsNewAndPrivate()
    {
        DirectoryInfo first = UpdateDownloader.CreateStagingFolder();
        DirectoryInfo second = UpdateDownloader.CreateStagingFolder();
        try
        {
            Assert.StartsWith("times-update-", first.Name, StringComparison.Ordinal);
            Assert.NotEqual(first.FullName, second.FullName);
            Assert.Empty(first.GetFileSystemInfos());
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, first.UnixFileMode);
            }
        }
        finally
        {
            first.Delete();
            second.Delete();
        }
    }

    private ReleaseInfo SignedWith(FakeReleaseSource source, byte[] package, string version)
    {
        string url = $"{TestSigner.Prefix}v{version}/Times.{version}.nupkg";
        source.Add(url, package);
        source.Add(url + ".sig", signer.Sign(package));
        return new ReleaseInfo("v" + version, false, false,
            [new ReleaseAsset($"Times.{version}.nupkg", url, TestSigner.Digest(package)), new ReleaseAsset($"Times.{version}.nupkg.sig", url + ".sig", null)]);
    }

    // The nuspec only in a subfolder: no root nuspec.
    private static byte[] NestedNuspec()
    {
        using MemoryStream memory = new MemoryStream();
        using (System.IO.Compression.ZipArchive zip = new System.IO.Compression.ZipArchive(memory, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            System.IO.Compression.ZipArchiveEntry entry = zip.CreateEntry("sub/Times.nuspec");
            using StreamWriter writer = new StreamWriter(entry.Open());
            writer.Write(TestSigner.Nuspec("Times", "1.2.0"));
        }

        return memory.ToArray();
    }
}
