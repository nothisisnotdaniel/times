using System.Buffers;
using System.Buffers.Text;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;
using Times.LicenceIssuer;

namespace Times.Tests;

public sealed partial class MacKeychainMigrationTests
{
    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    private const string Service = MacKeychainSigningKeyStore.Service;
    private const string Account = "maker";

    private const string MigrationFailed = "Signing-key migration failed. Check Keychain write access and retry.";
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

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
            string[] before = fixture.Readers(extraReader);
            byte[] identity = fixture.Identity();
            LicencePublicKeys original = LicencePublicKeys.FromTable([(Account, key.ExportSubjectPublicKeyInfoPem())]);
            ReadGrant grant = new();
            TestKeychainStore store = new(fixture.Reference, grant);
            string ledger = Path.Combine(folder.Path, "ledger.tsv");
            for (int i = 0; i < 2; i++)
            {
                // Model Allow Once at the read boundary. Each granted read still
                // goes through Security.framework and the database's actual ACL.
                grant.Remaining = oneTimeRead ? 1 : null;
                AssertIssued(store, ledger, original);
                Assert.True(!oneTimeRead || grant.Remaining == 0);
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
            byte[] identity = fixture.Identity();
            string[] before = fixture.Readers(extraReader);
            TestKeychainStore store = new(fixture.Reference);
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
            AssertIssued(store, ledger, LicencePublicKeys.FromTable([(Account, key.ExportSubjectPublicKeyInfoPem())]));
            fixture.AssertData(der);
            Assert.Equal(before, AccessEntries(fixture.Path));
            Assert.Equal(identity, fixture.Identity());
            Assert.Equal(2, File.ReadAllLines(ledger).Length);
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
                target.Reference, [fixture.Reference], Service, Account, [], 0, IssuerApphost));
            Assert.Empty(BusyLockFiles(target.Directory));
        }
        finally
        {
            File.Delete(lockPath);
            File.Move(saved, lockPath);
        }
    }

    private static string IssuerApphost => Path.Combine(AppContext.BaseDirectory, "Times.LicenceIssuer");

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
        IntPtr access = calls.CreateAccess(IssuerApphost, Service);
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
                Service, Account, der, access, IssuerApphost);
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
            calls.Release(access);
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
        public int? Remaining { get; set; }

        public override int CopyContent(IntPtr item, out uint length, out IntPtr data)
        {
            Assert.False(Remaining == 0, "Migration requested another protected read.");
            Remaining--;
            return base.CopyContent(item, out length, out data);
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly byte previousInteraction;
        private readonly string password = Guid.NewGuid().ToString("N");
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
            byte[] service = Encoding.UTF8.GetBytes(Service);
            byte[] account = Encoding.UTF8.GetBytes(Account);
            Assert.Equal(0, SecKeychainAddGenericPassword(Reference, (uint)service.Length, service, (uint)account.Length,
                account, (uint)Legacy.Length, Legacy, 0));
        }

        public string[] Readers(bool extra)
        {
            string[] before = AccessEntries(Path);
            string[] partitions = PartitionIds(before);
            Assert.Contains(before, entry => entry.Contains(" decrypt", StringComparison.Ordinal));
            if (extra)
            {
                string[] expected = [.. partitions, "teamid:TESTREADER"];
                RunSecurity("set-generic-password-partition-list", "-s", Service, "-a", Account, "-S",
                    string.Join(',', expected), "-k", password, Path);
                before = AccessEntries(Path);
                Assert.Equal(expected.Order(StringComparer.Ordinal), PartitionIds(before).Order(StringComparer.Ordinal));
            }

            return before;
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

    private static string[] AccessEntries(string path)
    {
        string record = RunSecurity("dump-keychain", "-a", path).Split("keychain:")
            .Single(item => item.Contains(Service, StringComparison.Ordinal));
        // Compare every ACL, including the integrity entry. Enumeration order is immaterial.
        return Regex.Split(record, @"(?m)^    entry [0-9]+:\s*$").Skip(1)
            .Select(entry => entry.Trim()).Order(StringComparer.Ordinal).ToArray();
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

    private sealed class MacKeychainTheoryAttribute : TheoryAttribute
    {
        public MacKeychainTheoryAttribute()
        {
            if (!OperatingSystem.IsMacOS())
            {
                Skip = "Requires the macOS file Keychain.";
            }
            else if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TIMES_TEST_KEYCHAIN_ROOT")))
            {
                Skip = "Requires an isolated TIMES_TEST_KEYCHAIN_ROOT.";
            }
        }
    }

    [DllImport(Security)]
    private static extern int SecKeychainCreate([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint passwordLength,
        byte[] password, byte prompt, IntPtr initialAccess, out IntPtr keychain);

    [DllImport(Security)]
    private static extern int SecKeychainAddGenericPassword(IntPtr keychain, uint serviceLength, byte[] service,
        uint accountLength, byte[] account, uint length, byte[] data, IntPtr item);

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
