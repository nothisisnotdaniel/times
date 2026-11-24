using System.Buffers;
using System.Buffers.Text;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;
using Times.LicenceIssuer;
using Xunit.Abstractions;

namespace Times.Tests;

public sealed partial class MacKeychainMigrationTests
{
    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    private const string Service = MacKeychainSigningKeyStore.Service;
    private const string Account = "maker";

    private const string MigrationFailed = "Signing-key migration failed. Check Keychain write access and retry.";
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private readonly ITestOutputHelper log;

    public MacKeychainMigrationTests(ITestOutputHelper log)
    {
        this.log = log;
    }

    private void ReportFailure(string operation, int status) => log.WriteLine("{0} returned {1}", operation, status);

    [MacKeychainTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void LegacyMigration_PreservesNativeRecordAndReaders(bool extraReader, bool oneTimeRead)
    {
        using Fixture fixture = new();
        using TempFolder folder = new();
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] der = key.ExportPkcs8PrivateKey();
        try
        {
            fixture.AddLegacy(der);
            string[] before = fixture.Readers(extraReader, oneTimeRead);
            byte[] identity = fixture.Identity();
            LicencePublicKeys original = LicencePublicKeys.FromTable([(Account, key.ExportSubjectPublicKeyInfoPem())]);
            ReadGrant grant = new(ReportFailure);
            TestKeychainStore store = new(fixture.Reference, grant);
            string ledger = Path.Combine(folder.Path, "ledger.tsv");
            for (int i = 0; i < 2; i++)
            {
                if (oneTimeRead)
                {
                    grant.AllowOnce(fixture, i == 0 ? fixture.Legacy! : der);
                }
                AssertIssued(store, ledger, original);
                Assert.False(grant.HasOutstandingGrant);
                fixture.AssertData(der);
                Assert.Equal(identity, fixture.Identity());
                Assert.Equal(before, AccessEntries(fixture.Path));
            }

            Assert.Equal(2, File.ReadAllLines(ledger).Length);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(der);
        }
    }

    [MacKeychainTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeniedMigration_PreservesNativeRecordAccessAndLedgerForRetry(bool extraReader)
    {
        if (!OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException();
        }

        using Fixture fixture = new();
        using TempFolder folder = new();
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] der = key.ExportPkcs8PrivateKey();
        try
        {
            fixture.AddLegacy(der);
            string ledger = Path.Combine(folder.Path, "ledger.tsv");
            File.WriteAllText(ledger, "previous entry\n");
            string[] before = fixture.Readers(extraReader);
            byte[] identity = fixture.Identity();
            TestKeychainStore store = new(fixture.Reference, new MacKeychainItem.NativeCalls(ReportFailure));
            File.SetUnixFileMode(fixture.Directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            try
            {
                using StringWriter output = new();
                using StringWriter error = new();
                Assert.Equal(1, RunIssue(store, ledger, output, error));
                Assert.Empty(output.ToString());
                Assert.Equal(MigrationFailed + error.NewLine, error.ToString());
            }
            finally
            {
                File.SetUnixFileMode(fixture.Directory, OwnerOnly);
            }

            fixture.AssertData(fixture.Legacy!);
            Assert.Equal(before, AccessEntries(fixture.Path));
            Assert.Equal(identity, fixture.Identity());
            Assert.Equal("previous entry\n", File.ReadAllText(ledger));
            LicencePublicKeys original = LicencePublicKeys.FromTable([(Account, key.ExportSubjectPublicKeyInfoPem())]);
            AssertIssued(store, ledger, original);
            AssertIssued(store, ledger, original);
            fixture.AssertData(der);
            Assert.Equal(before, AccessEntries(fixture.Path));
            Assert.Equal(identity, fixture.Identity());
            Assert.Equal(3, File.ReadAllLines(ledger).Length);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(der);
        }
    }

    [MacKeychainTheory]
    [InlineData("regular")]
    [InlineData("symlink")]
    [InlineData("fifo")]
    public void LookupGuard_UsesWriterLockAndRefusesUnsafeFiles(string kind)
    {
        using Fixture first = new();
        using Fixture second = new();
        (Fixture target, Fixture fixture) = string.CompareOrdinal(first.Path, second.Path) < 0
            ? (first, second) : (second, first);
        MacKeychainItem.NativeCalls calls = new();
        AssertLookupLocksTheSameFileAsAWriter(fixture.Reference, fixture.Directory);
        if (kind == "regular")
        {
            return;
        }

        string lockPath = Assert.Single(Directory.GetFiles(fixture.Directory, ".fl*"));
        string saved = lockPath + ".original";
        File.Move(lockPath, saved);
        try
        {
            if (kind == "symlink")
            {
                File.CreateSymbolicLink(lockPath, saved);
            }
            else
            {
                Assert.Equal(0, mkfifo(lockPath, 0x180)); // 0600
            }

            // The target writer sorts first. Refusing the secondary must release it.
            Assert.Throws<TokenStoreException>(() => MacKeychainItem.Default.AddWithSearchList(
                target.Reference, [fixture.Reference], Service, Account, [], Environment.ProcessPath!, IssuerApphost));
            Assert.Empty(BusyLockFiles(target.Directory));
            Assert.Throws<TokenStoreException>(() => calls.BeginTransaction(fixture.Reference));
        }
        finally
        {
            File.Delete(lockPath);
            File.Move(saved, lockPath);
        }
    }

    private static string IssuerApphost => Path.Combine(AppContext.BaseDirectory, "Times.LicenceIssuer");

    [MacKeychainFact]
    public void SeparateReadGrant_CannotAuthorizeAnExcludedWriter()
    {
        using Fixture fixture = new();
        using TempFolder folder = new();
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] der = key.ExportPkcs8PrivateKey();
        try
        {
            fixture.AddLegacy(der);
            string[] access = fixture.Readers(extra: true, oneTimeRead: true, allowWriter: false);
            byte[] identity = fixture.Identity();
            List<string> denied = [];
            ReadGrant grant = new((operation, status) =>
            {
                ReportFailure(operation, status);
                denied.Add(operation);
            });
            grant.AllowOnce(fixture, fixture.Legacy!);
            string ledger = Path.Combine(folder.Path, "ledger.tsv");
            File.WriteAllText(ledger, "previous entry\n");
            using StringWriter output = new();
            using StringWriter error = new();
            Assert.Equal(1, RunIssue(new TestKeychainStore(fixture.Reference, grant), ledger, output, error));
            Assert.False(grant.HasOutstandingGrant);
            Assert.Empty(output.ToString());
            Assert.Equal(MigrationFailed + error.NewLine, error.ToString());
            Assert.Contains(denied, stage => stage is "create encryption context" or "initialize encryption"
                or "encrypt update" or "encrypt final");
            fixture.AssertData(fixture.Legacy!);
            Assert.Equal(identity, fixture.Identity());
            Assert.Equal(access, AccessEntries(fixture.Path));
            Assert.Equal("previous entry\n", File.ReadAllText(ledger));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(der);
        }
    }

    [MacKeychainFact]
    public void CreateKey_SecondaryWithoutLock_CreatesAndReleasesFileLock()
    {
        using Fixture target = new();
        using Fixture secondary = new();
        File.Delete(Assert.Single(Directory.GetFiles(secondary.Directory, ".fl*")));

        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] data = key.ExportPkcs8PrivateKey();
        try
        {
            Assert.True(MacKeychainItem.Default.AddWithSearchList(target.Reference, [secondary.Reference],
                Service, Account, data, Environment.ProcessPath!, IssuerApphost));
            target.AssertData(data);
            Assert.Single(Directory.GetFiles(secondary.Directory, ".fl*"));
            Assert.Empty(BusyLockFiles(target.Directory));
            Assert.Empty(BusyLockFiles(secondary.Directory));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(data);
        }
    }

    [MacKeychainFact]
    public void ExplicitAdd_RefusesWhenTheDisposableDatabaseDisappears()
    {
        using Fixture target = new();
        using Fixture other = new();
        string saved = target.Path + ".saved";
        File.Move(target.Path, saved);
        try
        {
            Assert.Throws<TokenStoreException>(() =>
                MacKeychainItem.Default.TryAddToKeychain(target.Reference, Service, Account, [1], Environment.ProcessPath!));
            Assert.False(File.Exists(target.Path));
            Assert.False(MacKeychainItem.Default.Exists(other.Reference, Service, Account));
        }
        finally
        {
            File.Move(saved, target.Path);
        }
        Assert.False(MacKeychainItem.Default.Exists(target.Reference, Service, Account));
    }

    [MacKeychainTheory]
    [InlineData("invalid id")]
    [InlineData("maker")]
    public void Probe_RejectsInvalidIdOrNonDatabase(string account)
    {
        using Fixture fixture = new();
        string candidate = fixture.Path;
        if (account == Account)
        {
            candidate = Path.Combine(fixture.Directory, "fixture.fifo");
            Assert.Equal(0, mkfifo(candidate, 0x180));
            Assert.Equal(1, MacKeychainItem.NativeCalls.Probe(candidate, Service, account));
            candidate = Path.Combine(fixture.Directory, "fixture.txt");
            File.WriteAllText(candidate, "kych");
        }

        Assert.Equal(1, MacKeychainItem.NativeCalls.Probe(candidate, Service, account));
    }

    [MacKeychainTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void CreateKey_GuardedProbeSeesCommittedSecondary(bool competingInsert)
    {
        using Fixture target = new();
        using Fixture secondary = new();
        using ECDsa sample = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] der = sample.ExportPkcs8PrivateKey();
        MacKeychainItem.NativeCalls calls = new();
        try
        {
            Assert.False(MacKeychainItem.Default.Exists(secondary.Reference, Service, Account));
            byte[]? id = null;
            string[]? entries = null;
            if (competingInsert)
            {
                secondary.AddLegacy(der);
                id = secondary.Identity();
                entries = secondary.Readers(false);
            }

            bool created = MacKeychainItem.Default.AddWithSearchList(target.Reference, [secondary.Reference],
                Service, Account, der, Environment.ProcessPath!, IssuerApphost);
            Assert.Equal(!competingInsert, created);
            Assert.Equal(created, MacKeychainItem.Default.Exists(target.Reference, Service, Account));
            if (competingInsert)
            {
                secondary.AssertData(secondary.Legacy!);
                Assert.Equal(id, secondary.Identity());
                Assert.Equal(entries, AccessEntries(secondary.Path));
            }
            else
            {
                Assert.Equal(0, calls.Find(target.Reference, Service, Account, out IntPtr marked, createdOnly: true));
                calls.Release(marked);
            }

            Assert.Empty(BusyLockFiles(target.Directory));
            Assert.Empty(BusyLockFiles(secondary.Directory));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(der);
        }
    }

    private static int RunIssue(ISigningKeyStore store, string ledger, TextWriter output, TextWriter error)
    {
        using LicenceIssuerTests.CountingRandom random = new();
        Issuer issuer = new(output, error, store, random, FixedTimeProvider.At20261027(), ledger);
        return issuer.Run(["issue", "--kid", Account, "--name", "Ada", "--until", "2027-12-31"]);
    }

    private static void AssertIssued(ISigningKeyStore store, string ledger, LicencePublicKeys original)
    {
        using StringWriter output = new();
        using StringWriter error = new();
        int result = RunIssue(store, ledger, output, error);
        Assert.True(result == 0, error.ToString());
        Assert.Empty(error.ToString());
        Assert.True(LicenceKey.TryVerify(output.ToString().Trim(), original, LicenceRevocations.None).IsValid);
    }

    private sealed class ReadGrant : MacKeychainItem.NativeCalls
    {
        private Fixture? source;
        private byte[]? sample;
        private IntPtr grantedData;

        public ReadGrant(Action<string, int> reportFailure) : base(reportFailure)
        {
        }

        public bool HasOutstandingGrant => sample is not null;

        public void AllowOnce(Fixture fixture, byte[] expected)
        {
            source = fixture;
            sample = expected;
        }

        public override int CopyContent(IntPtr item, out uint length, out IntPtr data)
        {
            int status = base.CopyContent(item, out length, out data);
            if (source is null)
            {
                return status;
            }

            ClearAndFree(data, length);
            data = IntPtr.Zero;
            length = 0;
            Assert.True(status != 0, "The issuer unexpectedly had permanent decrypt access.");
            Assert.True(status is -25308 or -25293 or -25243 or -60005 or -128, $"Unexpected read denial {status}.");
            Assert.NotNull(sample);
            // The hosted session has no SecurityAgent UI. A separately trusted reader
            // confirms the exact generated sample on the real item for this single grant.
            source.VerifyReaders(sample);
            length = (uint)sample.Length;
            grantedData = data = Marshal.AllocHGlobal(sample.Length);
            Marshal.Copy(sample, 0, data, sample.Length);
            sample = null;
            return 0;
        }

        private void ClearAndFree(IntPtr data, uint length)
        {
            if (data != IntPtr.Zero)
            {
                Marshal.Copy(new byte[length], 0, data, checked((int)length));
                base.FreeContent(data);
            }
        }

        public override void FreeContent(IntPtr data)
        {
            if (data == grantedData && data != IntPtr.Zero)
            {
                // ReadContent has already cleared the complete allocation.
                Marshal.FreeHGlobal(data);
                grantedData = IntPtr.Zero;
                return;
            }

            base.FreeContent(data);
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly byte previousInteraction;
        private readonly string password = Guid.NewGuid().ToString("N");
        private readonly List<string> readers = [];
        public string Directory { get; }
        public string Path { get; }
        public IntPtr Reference { get; }
        public byte[]? Legacy { get; private set; }

        public Fixture()
        {
            if (!OperatingSystem.IsMacOS())
            {
                throw new PlatformNotSupportedException();
            }

            string root = Environment.GetEnvironmentVariable("TIMES_TEST_KEYCHAIN_ROOT")!;
            string parent = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "Keychains");
            Assert.True(System.IO.Path.IsPathFullyQualified(root));
            root = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(root));
            Assert.StartsWith(parent + "/", root, StringComparison.Ordinal);
            Assert.Equal(OwnerOnly, File.GetUnixFileMode(root));
            for (DirectoryInfo? entry = new(root); entry is not null && entry.FullName != parent; entry = entry.Parent)
            {
                Assert.Null(entry.LinkTarget);
            }

            Directory = System.IO.Path.Combine(root, Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Directory, OwnerOnly);
            Path = System.IO.Path.Combine(Directory, "issuer.keychain-db");
            Assert.Equal(0, SecKeychainGetUserInteractionAllowed(out previousInteraction));
            Assert.Equal(0, SecKeychainSetUserInteractionAllowed(0));
            byte[] encodedPassword = Encoding.UTF8.GetBytes(password);
            try
            {
                Assert.Equal(0, SecKeychainCreate(Path, (uint)encodedPassword.Length, encodedPassword, 0, 0, out IntPtr reference));
                Reference = reference;
                Assert.NotEqual(IntPtr.Zero, Reference);
                Assert.Equal(0, SecKeychainGetKeychainVersion(Reference, out uint version));
                Assert.True(version >= 0x200, $"Fixture file Keychain version 0x{version:x} lacks partition support.");
                Assert.Equal(Path, Encoding.UTF8.GetString(new MacKeychainItem.NativeCalls().KeychainPath(Reference)));
                const string CheckService = "Times fixture partition check";
                Assert.True(MacKeychainItem.Default.TryAddToKeychain(Reference, CheckService, Account, [1], Environment.ProcessPath!));
                Assert.NotEmpty(PartitionIds(AccessEntries(Path, CheckService)));
            }
            catch
            {
                Dispose();
                throw;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(encodedPassword);
            }
        }

        public void AddLegacy(byte[] der)
        {
            Legacy = new byte[Base64.GetMaxEncodedToUtf8Length(der.Length)];
            Assert.Equal(OperationStatus.Done, Base64.EncodeToUtf8(der, Legacy, out _, out _));
            Assert.True(MacKeychainItem.Default.TryAddToKeychain(Reference, Service, Account, Legacy, Environment.ProcessPath!));
        }

        public string[] Readers(bool extra, bool oneTimeRead = false, bool allowWriter = true)
        {
            string[] before = AccessEntries(Path);
            string[] partitions = PartitionIds(before);
            Assert.Contains(before, entry => entry.Contains(" decrypt", StringComparison.Ordinal));
            if (!extra)
            {
                return before;
            }

            string first = System.IO.Path.Combine(Directory, "reader-one");
            string second = System.IO.Path.Combine(Directory, "reader-two");
            string source = System.IO.Path.Combine(Directory, "reader.c");
            using (Stream embedded = typeof(Fixture).Assembly.GetManifestResourceStream("Times.Tests.MacKeychainFixtureReader.c")!)
            using (Stream copy = File.Create(source))
            {
                embedded.CopyTo(copy);
            }

            List<string> readerPartitions = [];
            foreach (string reader in new[] { first, second })
            {
                string marker = "Times fixture " + System.IO.Path.GetFileName(reader);
                RunFixtureProcess("/usr/bin/xcrun", ["clang", "-std=c11", "-Wall", "-Werror",
                    "-Wno-deprecated-declarations", "-framework", "Security", "-framework", "CoreFoundation",
                    $"-DFIXTURE_DB=\"{Path}\"", $"-DFIRST_READER=\"{first}\"", $"-DSECOND_READER=\"{second}\"",
                    $"-DREADER_SERVICE=\"{marker}\"", $"-DFIXTURE_SERVICE=\"{Service}\"", source, "-o", reader]);
                RunFixtureProcess(reader, ["register"]);
                readerPartitions.Add(Assert.Single(PartitionIds(AccessEntries(Path, marker))));
                readers.Add(reader);
            }

            Assert.Equal(2, readerPartitions.Distinct(StringComparer.Ordinal).Count());
            Assert.All(readerPartitions, partition => Assert.DoesNotContain(partition, partitions));
            string[] expected = allowWriter ? [.. partitions, .. readerPartitions] : [.. readerPartitions];
            RunSecurity("set-generic-password-partition-list", "-s", Service, "-a", Account, "-S",
                string.Join(',', expected), "-k", password, Path);
            byte[] pass = Encoding.UTF8.GetBytes(password);
            try
            {
                RunFixtureProcess(first, [oneTimeRead ? "readers-only" : "include-issuer"], pass);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pass);
            }

            before = AccessEntries(Path);
            Assert.Equal(expected.Order(StringComparer.Ordinal), PartitionIds(before).Order(StringComparer.Ordinal));
            VerifyReaders(Legacy!);
            return before;
        }

        public void VerifyReaders(byte[] expected)
        {
            Assert.Equal(2, readers.Count);
            byte[] digest = SHA256.HashData(expected);
            foreach (string reader in readers)
            {
                RunFixtureProcess(reader, ["verify"], digest);
            }
        }

        private static string[] PartitionIds(string[] entries)
        {
            string partition = Assert.Single(entries, entry => entry.Contains(" partition_id", StringComparison.Ordinal));
            string description = partition.Split('\n').Select(line => line.Trim())
                .Single(line => line.StartsWith("description:", StringComparison.Ordinal))["description:".Length..].Trim();
            Assert.DoesNotContain("invalid partition ID", description, StringComparison.Ordinal);
            Assert.NotEqual("<NULL>", description);
            string[] ids = description.Split(',', StringSplitOptions.TrimEntries);
            Assert.All(ids, id => Assert.NotEmpty(id));
            return ids;
        }

        public byte[] Identity()
        {
            MacKeychainItem.NativeCalls calls = new();
            Assert.Equal(0, calls.Find(Reference, Service, Account, out IntPtr item));
            try
            {
                Assert.Equal(0, calls.CopyRecordId(item, out IntPtr id));
                try
                {
                    byte[] bytes = new byte[checked((int)CFDataGetLength(id))];
                    Assert.NotEmpty(bytes);
                    Marshal.Copy(CFDataGetBytePtr(id), bytes, 0, bytes.Length);
                    return bytes;
                }
                finally
                {
                    calls.Release(id);
                }
            }
            finally
            {
                calls.Release(item);
            }
        }

        public void AssertData(byte[] expected)
        {
            if (readers.Count != 0)
            {
                VerifyReaders(expected);
                return;
            }

            byte[] actual = Assert.IsType<byte[]>(MacKeychainItem.Default.Read(Reference, Service, Account, 16384,
                static (_, data) => data.ToArray()));
            try
            {
                Assert.True(CryptographicOperations.FixedTimeEquals(expected, actual));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(actual);
            }
        }

        public void Dispose()
        {
            try
            {
                if (Reference != IntPtr.Zero)
                {
                    int result = SecKeychainDelete(Reference);
                    CFRelease(Reference);
                    Assert.Equal(0, result);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(Legacy);
                SecKeychainSetUserInteractionAllowed(previousInteraction);
                System.IO.Directory.Delete(Directory, recursive: true);
            }
        }
    }

    private static void AssertLookupLocksTheSameFileAsAWriter(IntPtr keychain, string directory)
    {
        MacKeychainItem.NativeCalls native = new MacKeychainItem.NativeCalls();
        string writerLock;
        using (native.BeginTransaction(keychain))
        {
            writerLock = Assert.Single(BusyLockFiles(directory));
        }

        using (IDisposable guard = native.LockForLookup(keychain, native.KeychainPath(keychain)))
        {
            Assert.IsType<SafeFileHandle>(guard); // a read-only guard on the local temporary volume
            Assert.Equal([writerLock], BusyLockFiles(directory));
        }

        Assert.Empty(BusyLockFiles(directory));
    }

    private static string[] BusyLockFiles(string directory)
    {
        List<string> busy = [];
        foreach (string path in Directory.EnumerateFiles(directory, ".fl*"))
        {
            int fd = OpenReadOnly(path, 0);
            Assert.True(fd >= 0);
            using SafeFileHandle handle = new SafeFileHandle(fd, ownsHandle: true);
            if (flock(handle, 2 | 4) != 0) // LOCK_EX | LOCK_NB
            {
                Assert.Equal(35, Marshal.GetLastPInvokeError()); // EWOULDBLOCK
                busy.Add(path);
            }
        }

        return busy.ToArray();
    }

    // Bind the issuer's lookup to the fixture's Keychain without changing the user's search order.
    private sealed class TestKeychainStore : ISigningKeyStore
    {
        private readonly MacKeychainSigningKeyStore store;
        private readonly IntPtr keychain;

        public TestKeychainStore(IntPtr keychain, MacKeychainItem.NativeCalls? calls = null)
        {
            store = new MacKeychainSigningKeyStore(IssuerApphost, new MacKeychainItem(calls ?? new MacKeychainItem.NativeCalls()));
            Assert.Null(store.WriteRefusal);
            this.keychain = keychain;
        }

        public bool IsSupported => store.IsSupported;
        public string? WriteRefusal => store.WriteRefusal;
        public byte[]? Read(string keyId) => store.Read(keychain, keyId);
        public bool Exists(string keyId) => throw new NotSupportedException();
        public bool TryCreate(string keyId, byte[] pkcs8) => throw new NotSupportedException();
    }

    private static string[] AccessEntries(string path, string service = Service)
    {
        string record = RunSecurity("dump-keychain", "-a", path).Split("keychain:")
            .Single(item => item.Contains(service, StringComparison.Ordinal));
        // Compare every ACL, including the integrity entry. Enumeration order is immaterial.
        return Regex.Split(record, @"(?m)^    entry [0-9]+:\s*$").Skip(1)
            .Select(entry => entry.Trim()).Order(StringComparer.Ordinal).ToArray();
    }

    private static void RunFixtureProcess(string file, string[] args, byte[]? input = null)
    {
        ProcessStartInfo info = new(file) { UseShellExecute = false, RedirectStandardInput = true };
        foreach (string arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        using Process child = Process.Start(info)!;
        if (input is not null)
        {
            child.StandardInput.BaseStream.Write(input);
        }
        child.StandardInput.Close();
        if (!child.WaitForExit(TimeSpan.FromSeconds(30)))
        {
            child.Kill(entireProcessTree: true);
            Assert.Fail("The disposable Keychain reader timed out.");
        }
        Assert.Equal(0, child.ExitCode);
    }

    private static string RunSecurity(params string[] arguments)
    {
        ProcessStartInfo info = new ProcessStartInfo("/usr/bin/security") { RedirectStandardOutput = true };
        foreach (string argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(info)!;
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        return output;
    }

    private static string? NativeSkip => !OperatingSystem.IsMacOS() ? "Requires the macOS file Keychain." :
        string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TIMES_TEST_KEYCHAIN_ROOT"))
            ? "Requires an isolated TIMES_TEST_KEYCHAIN_ROOT." : null;

    private sealed class MacKeychainFactAttribute : FactAttribute
    {
        public MacKeychainFactAttribute() => Skip = NativeSkip;
    }

    private sealed class MacKeychainTheoryAttribute : TheoryAttribute
    {
        public MacKeychainTheoryAttribute() => Skip = NativeSkip;
    }

    [DllImport(Security)]
    private static extern int SecKeychainCreate([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint passwordLength,
        byte[] password, byte prompt, IntPtr initialAccess, out IntPtr keychain);

    [DllImport(Security)]
    private static extern int SecKeychainGetUserInteractionAllowed(out byte state);

    [DllImport(Security)]
    private static extern int SecKeychainSetUserInteractionAllowed(byte state);

    [DllImport(Security)]
    private static extern int SecKeychainGetKeychainVersion(IntPtr keychain, out uint version);

    [DllImport("libc")]
    private static extern int mkfifo([MarshalAs(UnmanagedType.LPUTF8Str)] string path, ushort mode);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern nint CFDataGetLength(IntPtr data);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern IntPtr CFDataGetBytePtr(IntPtr data);

    [DllImport(Security)]
    private static extern int SecKeychainDelete(IntPtr keychain);

    [DllImport("libc", EntryPoint = "open")]
    private static extern int OpenReadOnly([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int flock(SafeFileHandle file, int operation);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern void CFRelease(IntPtr reference);
}
