using System.Diagnostics;
using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using Times.LicenceIssuer;

namespace Times.Tests;

public sealed class LicenceIssuerTests : IDisposable
{
    private readonly TempFolder folder = new();
    private readonly StringWriter output = new();
    private readonly StringWriter error = new();
    private readonly FakeSigningKeyStore keys = new();

    public void Dispose()
    {
        folder.Dispose();
    }

    private string Ledger => Path.Combine(folder.Path, "issuer", "ledger.tsv");

    private int Run(TimeProvider? time = null, params string[] args) =>
        new Issuer(output, error, keys, new CountingRandom(), time ?? FixedTimeProvider.At20261027(), Ledger).Run(args);

    private string CreateKey(string kid = "k1")
    {
        Assert.Equal(0, Run(null, "create-key", "--kid", kid));
        string pem = output.ToString();
        output.GetStringBuilder().Clear();
        return pem;
    }

    // AC-12
    [Fact]
    public void CreateKey_StoresThePrivateKey_PrintsOnlyThePublicPem()
    {
        string pem = CreateKey();

        Assert.Contains("-----BEGIN PUBLIC KEY-----", pem, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE KEY", pem, StringComparison.Ordinal);
        Assert.Equal(string.Empty, error.ToString());
        byte[] stored = Assert.Single(keys.Keys).Value;
        using ECDsa key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(stored, out _);
        Assert.Equal(key.ExportSubjectPublicKeyInfoPem().Trim(), pem.Trim());
        Assert.False(File.Exists(Ledger));
    }

    // AC-12
    [Fact]
    public void CreateKey_SameKidAgain_Refuses_AndKeepsTheOldKey()
    {
        CreateKey();
        byte[] before = keys.Keys["k1"].ToArray();

        int exit = Run(null, "create-key", "--kid", "k1");

        Assert.NotEqual(0, exit);
        Assert.Equal(before, keys.Keys["k1"]);
        Assert.Equal(1, keys.WriteCount);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Equal("A signing key named k1 already exists; it was left as it is.", error.ToString().Trim());
    }

    // AC-12
    [Theory]
    [InlineData("Upper")]
    [InlineData("seventeen-chars-x")]
    [InlineData("")]
    public void CreateKey_InvalidKid_Refuses(string kid)
    {
        Assert.NotEqual(0, Run(null, "create-key", "--kid", kid));
        Assert.Empty(keys.Keys);
    }

    // AC-12: a store that refuses writes (shared dotnet host) or is missing stores nothing.
    [Fact]
    public void CreateKey_RefusingOrUnsupportedStore_StoresNothing()
    {
        keys.WriteRefusal = "Not through dotnet.";
        Assert.Equal(1, Run(null, "create-key", "--kid", "k1"));
        Assert.Equal("Not through dotnet.", error.ToString().Trim());

        Issuer unsupported = new Issuer(output, error, new UnsupportedSigningKeyStore(), new CountingRandom(), FixedTimeProvider.At20261027(), Ledger);
        Assert.Equal(1, unsupported.Run(["create-key", "--kid", "k1"]));
        Assert.Empty(keys.Keys);
        Assert.Equal(string.Empty, output.ToString());
    }

    // AC-13
    [Fact]
    public void Issue_WithoutAKey_RefusesWithOneLine_NoLedger()
    {
        int exit = Run(null, "issue", "--kid", "k1", "--name", "Ada Lovelace", "--until", "2027-12-31");

        Assert.NotEqual(0, exit);
        Assert.Equal(["There is no signing key named k1. Run create-key --kid k1 first."], Lines(error.ToString()));
        Assert.Equal(string.Empty, output.ToString());
        Assert.False(File.Exists(Ledger));
    }

    // AC-13
    [Fact]
    public void Issue_PrintsATokenTimesVerifies_AndAppendsOneLedgerLine()
    {
        string pem = CreateKey();
        Assert.Null(Issuer.LedgerProblem(Ledger));

        int exit = Run(null, "issue", "--kid", "k1", "--name", "Ada Lovelace", "--until", "2027-12-31");

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, error.ToString());
        string token = Assert.Single(Lines(output.ToString()));
        LicenceCheck check = LicenceKey.TryVerify(token, LicencePublicKeys.FromTable([("k1", pem)]), LicenceRevocations.None);
        Assert.True(check.IsValid, check.Problem);
        Licence licence = check.Licence!;
        Assert.Equal("k1", licence.KeyId);
        Assert.Equal(TestLicences.LicenceId, licence.LicenceId);
        Assert.Equal("Ada Lovelace", licence.Name);
        Assert.Equal(new DateOnly(2027, 12, 31), licence.Until);
        Assert.Equal(new DateOnly(2026, 10, 27), licence.Issued);
        Assert.Equal(TestLicences.LicenceId + "\tAda Lovelace\t2027-12-31\t2026-10-27\tk1\n", File.ReadAllText(Ledger));
    }

    // AC-13: a second issue appends a second line, the first stays.
    [Fact]
    public void Issue_Twice_AppendsTwoLines()
    {
        CreateKey();

        Assert.Equal(0, Run(null, "issue", "--kid", "k1", "--name", "Ada", "--until", "2027-12-31"));
        Assert.Equal(0, Run(null, "issue", "--kid", "k1", "--name", "Grace", "--until", "2028-01-31"));

        string[] lines = File.ReadAllText(Ledger).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.StartsWith(TestLicences.LicenceId + "\tAda\t", lines[0], StringComparison.Ordinal);
        Assert.Contains("\tGrace\t2028-01-31\t", lines[1], StringComparison.Ordinal);
    }

    // AC-13: the issue date is the local date from the injected clock.
    [Fact]
    public void Issue_TakesTheIssueDateFromTheInjectedClock()
    {
        string pem = CreateKey();
        TimeZoneInfo plusTen = TimeZoneInfo.CreateCustomTimeZone("plus-ten", TimeSpan.FromHours(10), "plus-ten", "plus-ten");
        FixedTimeProvider time = new FixedTimeProvider(new DateTimeOffset(2026, 10, 27, 18, 30, 0, TimeSpan.Zero), plusTen);

        Assert.Equal(0, Run(time, "issue", "--kid", "k1", "--name", "Ada", "--until", "2027-12-31"));

        Licence licence = LicenceKey.TryVerify(output.ToString().Trim(), LicencePublicKeys.FromTable([("k1", pem)]), LicenceRevocations.None).Licence!;
        Assert.Equal(new DateOnly(2026, 10, 28), licence.Issued);
    }

    // AC-13
    [Theory]
    [InlineData("--until", "2027-02-30")]
    [InlineData("--until", "31.12.2027")]
    [InlineData("--until", "2027-1-1")]
    [InlineData("--name", "   ")]
    [InlineData("--name", "Ada​Lovelace")]
    [InlineData("--name", "Ada\nLovelace")]
    [InlineData("--kid", "K1")]
    public void Issue_InvalidInput_Refuses_NoLedger(string option, string value)
    {
        CreateKey();
        Dictionary<string, string> options = new Dictionary<string, string>
        {
            ["--kid"] = "k1",
            ["--name"] = "Ada Lovelace",
            ["--until"] = "2027-12-31",
        };
        options[option] = value;

        int exit = Run(null, ["issue", .. options.SelectMany(pair => new[] { pair.Key, pair.Value })]);

        Assert.NotEqual(0, exit);
        Assert.Single(Lines(error.ToString()));
        Assert.Equal(string.Empty, output.ToString());
        Assert.False(File.Exists(Ledger));
    }

    // AC-13
    [Fact]
    public void Issue_NameOf101Characters_Refused()
    {
        CreateKey();

        Assert.NotEqual(0, Run(null, "issue", "--kid", "k1", "--name", new string('a', 101), "--until", "2027-12-31"));
        Assert.False(File.Exists(Ledger));
    }

    // AC-13: a ledger inside a git repository (a .git folder or file above it) is refused.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Issue_LedgerInsideAGitRepository_Refused(bool gitFolder)
    {
        CreateKey();
        string repo = Path.Combine(folder.Path, "repo");
        Directory.CreateDirectory(Path.Combine(repo, "deep"));
        if (gitFolder)
        {
            Directory.CreateDirectory(Path.Combine(repo, ".git"));
        }
        else
        {
            File.WriteAllText(Path.Combine(repo, ".git"), "gitdir: elsewhere");
        }

        string ledger = Path.Combine(repo, "deep", "ledger.tsv");

        int exit = Run(null, "issue", "--kid", "k1", "--name", "Ada", "--until", "2027-12-31", "--ledger", ledger);

        Assert.NotEqual(0, exit);
        Assert.Equal(["The ledger must not be inside a git repository."], Lines(error.ToString()));
        Assert.Equal(string.Empty, output.ToString());
        Assert.False(File.Exists(ledger));
    }

    // AC-13
    [Fact]
    public void Issue_RelativeLedger_Refused()
    {
        CreateKey();

        Assert.NotEqual(0, Run(null, "issue", "--kid", "k1", "--name", "Ada", "--until", "2027-12-31", "--ledger", "ledger.tsv"));
        Assert.Equal(["The ledger path must be absolute."], Lines(error.ToString()));
    }

    [Theory]
    [InlineData]
    [InlineData("sign")]
    [InlineData("issue", "--kid")]
    [InlineData("create-key", "--kid", "k1", "--kid", "k2")]
    [InlineData("create-key", "--kid", "k1", "--name", "x")]
    public void Usage_Errors_ExitTwo(params string[] args)
    {
        Assert.Equal(2, Run(null, args));
        Assert.Empty(keys.Keys);
    }

    // AC-14: the packed Times tool holds nothing of the issuer.
    [Fact]
    public async Task PackedTimes_HoldsNoIssuerFile()
    {
#if DEBUG
        const string Configuration = "Debug";
#else
        const string Configuration = "Release";
#endif
        string packed = Path.Combine(folder.Path, "pack");
        ProcessStartInfo startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = RepoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string argument in new[] { "pack", Path.Combine("src", "Times", "Times.csproj"), "-c", Configuration, "--no-build", "-o", packed, "-nologo", "-nodeReuse:false" })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(startInfo)!;
        Task<string> errors = process.StandardError.ReadToEndAsync();
        string text = await process.StandardOutput.ReadToEndAsync() + await errors;
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, "pack exit " + process.ExitCode + ": " + text);

        string package = Assert.Single(Directory.GetFiles(packed, "*.nupkg"));
        using ZipArchive zip = ZipFile.OpenRead(package);
        Assert.Contains(zip.Entries, entry => entry.FullName.EndsWith("Times.dll", StringComparison.Ordinal));
        Assert.DoesNotContain(zip.Entries, entry => entry.FullName.Contains("LicenceIssuer", StringComparison.OrdinalIgnoreCase));
    }

    // AC-14: Times.dll references no signing or private-key import method, and no issuer assembly.
    [Fact]
    public void TimesAssembly_ReferencesNoSigningMethod()
    {
        string[] banned = ["SignData", "SignHash", "TrySignData", "TrySignHash", "ImportPkcs8PrivateKey", "ImportECPrivateKey", "ImportEncryptedPkcs8PrivateKey"];
        using FileStream stream = File.OpenRead(typeof(LicenceKey).Assembly.Location);
        using PEReader pe = new PEReader(stream);
        MetadataReader metadata = pe.GetMetadataReader();

        List<string> members = metadata.MemberReferences
            .Select(handle => metadata.GetString(metadata.GetMemberReference(handle).Name))
            .ToList();
        List<string> assemblies = metadata.AssemblyReferences
            .Select(handle => metadata.GetString(metadata.GetAssemblyReference(handle).Name))
            .ToList();

        Assert.Contains("VerifyData", members);
        Assert.Empty(members.Intersect(banned));
        Assert.DoesNotContain(assemblies, name => name.Contains("LicenceIssuer", StringComparison.Ordinal));
    }

    // AC-14: and the Times sources hold none either.
    [Fact]
    public void TimesSources_HoldNoSigningCall()
    {
        foreach (string file in Directory.GetFiles(Path.Combine(RepoRoot, "src", "Times"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                continue;
            }

            string source = File.ReadAllText(file);
            Assert.DoesNotContain(".SignData(", source, StringComparison.Ordinal);
            Assert.DoesNotContain(".SignHash(", source, StringComparison.Ordinal);
            Assert.DoesNotContain("ImportPkcs8PrivateKey", source, StringComparison.Ordinal);
        }
    }

    private static List<string> Lines(string text) =>
        text.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();

    private static readonly string RepoRoot = FindRepoRoot();

    private static string FindRepoRoot()
    {
        DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Times.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Times.slnx not found above " + AppContext.BaseDirectory);
    }

    // Fills every request with 0, 1, 2, … so the first licence id is TestLicences.LicenceId.
    private sealed class CountingRandom : RandomNumberGenerator
    {
        public override void GetBytes(byte[] data)
        {
            for (int i = 0; i < data.Length; i++)
            {
                data[i] = (byte)i;
            }
        }
    }

    private sealed class FakeSigningKeyStore : ISigningKeyStore
    {
        public Dictionary<string, byte[]> Keys { get; } = new(StringComparer.Ordinal);

        public int WriteCount { get; private set; }

        public bool IsSupported => true;

        public string? WriteRefusal { get; set; }

        public bool Exists(string keyId) => Keys.ContainsKey(keyId);

        // A copy: the issuer zeroes what it reads.
        public byte[]? Read(string keyId) => Keys.TryGetValue(keyId, out byte[]? key) ? key.ToArray() : null;

        public void Write(string keyId, byte[] pkcs8)
        {
            WriteCount++;
            Keys[keyId] = pkcs8.ToArray();
        }
    }
}
